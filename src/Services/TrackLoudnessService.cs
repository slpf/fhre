using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FH6RB.Core;

namespace FH6RB.Services;

public static class TrackLoudnessService
{
    private const int CacheLimit = 512;
    private const string Filter = "aformat=sample_fmts=dbl," +
        "astats@input=measure_perchannel=none:measure_overall=Number_of_samples+Number_of_NaNs+Number_of_Infs," +
        "ebur128=framelog=verbose";
    private static readonly Regex Integrated = new(@"(?m)^\s*I:\s*(\S+)\s+LUFS\s*$", RegexOptions.Compiled);
    private static readonly Regex NonFinite = new(@"Number of (?:NaNs|Infs):\s*(\S+)", RegexOptions.Compiled);
    private static readonly SemaphoreSlim Workers = new(2, 2);
    private static readonly object CacheSync = new();
    private static readonly Dictionary<CacheKey, double?> Cache = new();
    private readonly record struct CacheKey(string Path, long Modified, long Bytes, int? SubIndex);

    public static async Task<double?> MeasureAsync(string source, int? subIndex = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Tools.HasFfmpeg) throw new FileNotFoundException("ffmpeg is required to measure loudness.", Tools.FfmpegPath);
        if (subIndex < 0) throw new ArgumentOutOfRangeException(nameof(subIndex));
        if (subIndex is not null && !Tools.HasVgmstream)
            throw new FileNotFoundException("vgmstream is required to measure bank loudness.", Tools.VgmstreamPath);
        var key = Key(source, subIndex);
        lock (CacheSync)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }
        await Workers.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (CacheSync)
            {
                if (Cache.TryGetValue(key, out var cached)) return cached;
            }
            var timer = Stopwatch.StartNew();
            var result = await Task.Run(() => MeasureCoreAsync(source, subIndex, ct), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (Key(source, subIndex) != key) throw new IOException("Audio source changed during loudness measurement.");
            lock (CacheSync)
            {
                if (Cache.Count >= CacheLimit) Cache.Remove(Cache.Keys.First());
                Cache[key] = result;
            }
            if (Log.IsVerbose)
                Log.Line("ui.loudness.ok " + JsonSerializer.Serialize(new { source, subIndex, lufs = result, elapsedMs = timer.ElapsedMilliseconds }));
            return result;
        }
        finally { Workers.Release(); }
    }

    private static CacheKey Key(string source, int? subIndex)
    {
        var file = new FileInfo(Path.GetFullPath(source));
        if (!file.Exists) throw new FileNotFoundException("Audio source not found.", source);
        var path = OperatingSystem.IsWindows() ? file.FullName.ToUpperInvariant() : file.FullName;
        return new(path, file.LastWriteTimeUtc.Ticks, file.Length, subIndex);
    }

    private static async Task<double?> MeasureCoreAsync(string source, int? subIndex, CancellationToken ct)
    {
        string? extracted = null;
        Process? meter = null;
        Process? decoder = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        using var cancellation = token.Register(() => { Kill(decoder); Kill(meter); });
        try
        {
            var decodeSource = source;
            var decodeIndex = subIndex ?? 0;
            var rawFsb = false;
            if (subIndex is not null)
            {
                using var input = File.OpenRead(source);
                var magic = new byte[4];
                input.ReadExactly(magic);
                rawFsb = magic.AsSpan().SequenceEqual("FSB5"u8);
            }
            if (subIndex is not null && !rawFsb)
            {
                token.ThrowIfCancellationRequested();
                var data = TrackPack.ExtractSampleFsb(source, subIndex.Value);
                token.ThrowIfCancellationRequested();
                var directory = Path.Combine(AppPaths.TempBase, "ui-loudness");
                Directory.CreateDirectory(directory);
                extracted = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".fsb");
                await File.WriteAllBytesAsync(extracted, data, token).ConfigureAwait(false);
                decodeSource = extracted;
                decodeIndex = 0;
            }
            token.ThrowIfCancellationRequested();
            meter = Start(Tools.FfmpegPath, subIndex is not null,
                "-hide_banner", "-nostdin", "-nostats", "-xerror", "-loglevel", "info",
                "-i", subIndex is null ? source : "pipe:0", "-map", "0:a:0", "-af", Filter, "-f", "null", "-");
            var meterError = meter.StandardError.ReadToEndAsync();
            var meterOutput = meter.StandardOutput.ReadToEndAsync();
            Task<string>? decoderError = null;
            if (subIndex is not null)
            {
                token.ThrowIfCancellationRequested();
                decoder = Start(Tools.VgmstreamPath, false, "-i", "-W", "4", "-p", "-s",
                    (decodeIndex + 1).ToString(CultureInfo.InvariantCulture), decodeSource);
                decoderError = decoder.StandardError.ReadToEndAsync();
                try
                {
                    await decoder.StandardOutput.BaseStream.CopyToAsync(meter.StandardInput.BaseStream, token).ConfigureAwait(false);
                }
                finally { meter.StandardInput.Close(); }
                await decoder.WaitForExitAsync(token).ConfigureAwait(false);
            }
            await meter.WaitForExitAsync(token).ConfigureAwait(false);
            var error = await meterError.ConfigureAwait(false);
            await meterOutput.ConfigureAwait(false);
            var decodeError = decoderError is null ? "" : await decoderError.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (decoder is not null && decoder.ExitCode != 0)
                throw new InvalidDataException($"vgmstream exited with code {decoder.ExitCode}: {decodeError.Trim()}");
            if (meter.ExitCode != 0) throw new InvalidDataException($"ffmpeg exited with code {meter.ExitCode}: {error.Trim()}");
            var counts = NonFinite.Matches(error);
            if (counts.Count == 0 || counts.Any(m => !double.TryParse(m.Groups[1].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var count) || count != 0))
                throw new InvalidDataException("Audio contains non-finite samples or sample statistics are missing.");
            var integrated = Integrated.Matches(error).LastOrDefault();
            if (integrated is null) throw new InvalidDataException("Integrated loudness was not reported.");
            var text = integrated.Groups[1].Value;
            if (text.Equals("-inf", StringComparison.OrdinalIgnoreCase)) return null;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var lufs) || !double.IsFinite(lufs))
                throw new InvalidDataException("Invalid integrated loudness measurement.");
            return lufs > -69.9 ? lufs : null;
        }
        catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (Exception) when (timeout.IsCancellationRequested) { throw new TimeoutException("Loudness measurement exceeded two minutes."); }
        finally
        {
            Kill(decoder);
            Kill(meter);
            foreach (var process in new[] { decoder, meter })
            {
                if (process is null) continue;
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (InvalidOperationException) { }
                catch (TimeoutException) { Log.Line("ui.loudness.process.cleanup.timeout"); }
            }
            decoder?.Dispose();
            meter?.Dispose();
            if (extracted is not null)
            {
                try { File.Delete(extracted); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Log.Line($"ui.loudness.cleanup.failed: {ex.Message}"); }
            }
        }
    }

    private static Process Start(string path, bool input, params string[] arguments)
    {
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = input, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException($"Could not start {Path.GetFileName(path)}.");
    }

    private static void Kill(Process? process)
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }
}
