using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FH6RB.Core;

namespace FH6RB.Services;

public sealed record XmaEncoderStatus(bool Available, string Reason);

public static class XmaEncoderService
{
    private const int AlignedProbeFrames = 65536;
    private const int UnalignedProbeFrames = 65537;
    private const int ProbeSampleRate = 48000;
    private const int ProbeChannels = 2;
    private static readonly TimeSpan FailureCacheDuration = TimeSpan.FromSeconds(2);
    private static readonly SemaphoreSlim ProbeGate = new(1, 1);
    private static readonly object CacheSync = new();
    private static CachedResult? _cached;

    private sealed record FileStamp(string Path, long Length, long Modified, long Created);
    private sealed record ProbeTools(string Fsbank, string Vgmstream, FileStamp[] Stamps);
    private sealed record CachedResult(ProbeTools Tools, XmaEncoderStatus Status, long CheckedAt);

    public static bool IsAvailable
    {
        get
        {
            if (!TryReadTools(out var tools, out _)) return false;
            lock (CacheSync)
            {
                return _cached is { Status.Available: true } && SameTools(_cached.Tools, tools!);
            }
        }
    }

    public static async Task<XmaEncoderStatus> CheckAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!TryReadTools(out var tools, out var reason))
        {
            lock (CacheSync) _cached = null;
            return Report(new XmaEncoderStatus(false, reason), "unavailable", log);
        }
        var cached = ReadCache(tools!);
        if (cached is not null) return Report(cached, "cached", log);

        await ProbeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!TryReadTools(out tools, out reason))
            {
                lock (CacheSync) _cached = null;
                return Report(new XmaEncoderStatus(false, reason), "unavailable", log);
            }
            cached = ReadCache(tools!);
            if (cached is not null) return Report(cached, "cached", log);

            XmaEncoderStatus result;
            try
            {
                await ProbeAsync(tools!, log, ct).ConfigureAwait(false);
                result = new XmaEncoderStatus(true, "XMA encoding and decoded audio validation succeeded.");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                WriteLog(log, $"xma.probe.failed {ex}");
                result = new XmaEncoderStatus(false, $"XMA encoder check failed: {ShortReason(ex.Message)}");
            }

            ct.ThrowIfCancellationRequested();
            if (!TryReadTools(out var current, out _) || !SameTools(tools!, current!))
            {
                lock (CacheSync) _cached = null;
                return Report(new XmaEncoderStatus(false, "XMA tools changed during the encoder check. Run the check again."),
                    "changed", log);
            }

            lock (CacheSync) _cached = new CachedResult(tools!, result, Stopwatch.GetTimestamp());
            return Report(result, "checked", log);
        }
        finally
        {
            ProbeGate.Release();
        }
    }

    private static XmaEncoderStatus? ReadCache(ProbeTools tools)
    {
        lock (CacheSync)
        {
            if (_cached is null || !SameTools(_cached.Tools, tools)) return null;
            if (_cached.Status.Available || Stopwatch.GetElapsedTime(_cached.CheckedAt) < FailureCacheDuration)
            {
                return _cached.Status;
            }
            return null;
        }
    }

    private static bool SameTools(ProbeTools first, ProbeTools second) => first.Stamps.SequenceEqual(second.Stamps);

    private static bool TryReadTools(out ProbeTools? tools, out string reason)
    {
        tools = null;
        try
        {
            if (!File.Exists(Tools.XmaEncoderPath))
            {
                reason = $"Optional XMA encoder library is missing: {Tools.XmaEncoderPath}";
                return false;
            }
            if (!File.Exists(Tools.FsbankclPath))
            {
                reason = $"FSBank is missing: {Tools.FsbankclPath}";
                return false;
            }
            if (!File.Exists(Tools.VgmstreamPath))
            {
                reason = $"vgmstream is required to check the XMA encoder: {Tools.VgmstreamPath}";
                return false;
            }

            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var paths = new[] { Tools.XmaEncoderPath, Tools.FsbankclPath, Tools.VgmstreamPath }
                .Concat(Directory.EnumerateFiles(Path.GetDirectoryName(Tools.FsbankclPath)!, "*.dll"))
                .Concat(Directory.EnumerateFiles(Path.GetDirectoryName(Tools.VgmstreamPath)!, "*.dll"))
                .Select(Path.GetFullPath).Distinct(comparer).OrderBy(path => path, comparer);
            var stamps = paths.Select(path =>
            {
                var file = new FileInfo(path);
                return new FileStamp(path, file.Length, file.LastWriteTimeUtc.Ticks, file.CreationTimeUtc.Ticks);
            }).ToArray();
            tools = new ProbeTools(Tools.FsbankclPath, Tools.VgmstreamPath, stamps);
            reason = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            reason = $"Could not inspect the XMA encoder tools: {ShortReason(ex.Message)}";
            return false;
        }
    }

    private static async Task ProbeAsync(ProbeTools tools, Action<string>? log, CancellationToken ct)
    {
        var workDir = Path.Combine(AppPaths.TempBase, "xma-probe", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workDir);
            var cache = Path.Combine(workDir, "cache");
            Directory.CreateDirectory(cache);
            WriteLog(log, "xma.probe.start " + JsonSerializer.Serialize(new
            {
                workDir, frames = new[] { AlignedProbeFrames, UnalignedProbeFrames }, sampleRate = ProbeSampleRate, channels = ProbeChannels,
                quality = 90, stamps = tools.Stamps
            }));
            foreach (var frames in new[] { AlignedProbeFrames, UnalignedProbeFrames })
            {
                var name = frames.ToString(CultureInfo.InvariantCulture);
                var wav = Path.Combine(workDir, $"probe-{name}.wav");
                var fsb = Path.Combine(workDir, $"probe-{name}.fsb");
                var decoded = Path.Combine(workDir, $"decoded-{name}.wav");
                WriteProbeWav(wav, frames);
                await RunAsync(tools.Fsbank,
                    ["-format", "xma", "-quality", "90", "-no_loop", "-o", fsb, wav, "-cache_dir", cache], workDir, log, ct)
                    .ConfigureAwait(false);
                ValidateFsb(fsb, frames);
                await RunAsync(tools.Vgmstream,
                    ["-i", "-W", "4", "-s", "1", "-o", decoded, fsb], workDir, log, ct).ConfigureAwait(false);
                ValidateProbe(wav, fsb, decoded, log);
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                WriteLog(log, $"xma.probe.cleanup.failed {ex.Message}");
            }
        }
    }

    private static void ValidateFsb(string path, long expectedFrames)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 60 or > 8 * 1024 * 1024)
        {
            throw new InvalidDataException("FSBank did not produce a valid XMA probe file.");
        }
        var bytes = File.ReadAllBytes(path);
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)) != 10)
        {
            throw new InvalidDataException("FSBank did not produce XMA codec mode 10.");
        }
        var fsb = Fsb5.Parse(bytes);
        if (fsb.Samples.Count != 1 || fsb.Samples[0].Frames != expectedFrames ||
            fsb.Samples[0].SampleRate != ProbeSampleRate || fsb.Samples[0].Channels != ProbeChannels ||
            fsb.Samples[0].Data.Length == 0)
        {
            throw new InvalidDataException($"XMA probe metadata must preserve exactly {expectedFrames} frames, {ProbeSampleRate} Hz, and {ProbeChannels} channels; encoder padding or stretching is not supported.");
        }
    }

    internal static void ValidateProbe(string sourceWav, string fsbPath, string decodedWav, Action<string>? log = null)
    {
        var expected = AudioBuildValidation.Inspect(sourceWav);
        var actual = AudioBuildValidation.Inspect(decodedWav);
        if (expected.Frames is not (AlignedProbeFrames or UnalignedProbeFrames) || expected.SampleRate != ProbeSampleRate || expected.Channels != ProbeChannels ||
            actual.Frames != expected.Frames || actual.SampleRate != ProbeSampleRate || actual.Channels != ProbeChannels ||
            actual.BitsPerSample != 32)
        {
            throw new InvalidDataException("Decoded XMA probe changed its frame count, sample rate, channels, or float output format.");
        }
        ValidateFsb(fsbPath, expected.Frames);
        var comparison = AudioBuildValidation.Compare(sourceWav, decodedWav);
        for (var channel = 0; channel < ProbeChannels; channel++)
        {
            if (!double.IsFinite(comparison.ChannelCorrelation[channel]) || comparison.ChannelCorrelation[channel] < 0.95 ||
                !double.IsFinite(comparison.GainDb[channel]) || Math.Abs(comparison.GainDb[channel]) > 3)
            {
                throw new InvalidDataException($"Decoded XMA probe changed the signal or level in channel {channel + 1}.");
            }
        }
        WriteLog(log, "xma.probe.audio " + JsonSerializer.Serialize(new
        {
            frames = actual.Frames, sampleRate = actual.SampleRate, channels = actual.Channels,
            actual.RmsDb, actual.PeakDb, comparison.ChannelCorrelation, comparison.GainDb
        }));
    }

    private static void WriteProbeWav(string path, int frames)
    {
        const int bytesPerFrame = ProbeChannels * sizeof(short);
        var dataLength = checked(frames * bytesPerFrame);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short) 1);
        writer.Write((short) ProbeChannels);
        writer.Write(ProbeSampleRate);
        writer.Write(ProbeSampleRate * bytesPerFrame);
        writer.Write((short) bytesPerFrame);
        writer.Write((short) 16);
        writer.Write("data"u8);
        writer.Write(dataLength);
        for (var frame = 0; frame < frames; frame++)
        {
            var time = (double) frame / ProbeSampleRate;
            var envelope = Math.Min(1, Math.Min(frame, frames - 1 - frame) / 512.0);
            var left = envelope * (0.18 * Math.Sin(2 * Math.PI * 311 * time) +
                0.1 * Math.Sin(2 * Math.PI * 997 * time) + 0.06 * Math.Sin(2 * Math.PI * 3011 * time));
            var right = envelope * (0.17 * Math.Sin(2 * Math.PI * 439 * time) +
                0.09 * Math.Sin(2 * Math.PI * 1481 * time) + 0.05 * Math.Sin(2 * Math.PI * 3673 * time));
            if (frame is 2048 or 32768 or 65024)
            {
                left = 0.7;
                right = -0.6;
            }
            writer.Write((short) Math.Round(left * short.MaxValue));
            writer.Write((short) Math.Round(right * short.MaxValue));
        }
    }

    private static async Task RunAsync(string exe, string[] args, string workDir, Action<string>? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDir
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        WriteLog(log, $"xma.probe.command {Path.GetFileName(exe)} {JsonSerializer.Serialize(args)}");
        var timer = Stopwatch.StartNew();
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {Path.GetFileName(exe)}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            await Proc.WaitForCleanupAsync(process).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(exe)} exceeded 45 seconds during the XMA encoder check.");
        }
        var output = (await stdout.ConfigureAwait(false)).Trim();
        var errors = (await stderr.ConfigureAwait(false)).Trim();
        WriteOutput(log, "stdout", output);
        WriteOutput(log, "stderr", errors);
        WriteLog(log, $"xma.probe.exit tool={Path.GetFileName(exe)} code={process.ExitCode} elapsedMs={timer.ElapsedMilliseconds}");
        var combined = output + "\n" + errors;
        if (process.ExitCode != 0 || combined.Contains("Build failed", StringComparison.OrdinalIgnoreCase) ||
            combined.Split('\n').Any(line => line.TrimStart().StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)))
        {
            var detail = errors.Length > 0 ? errors : output;
            throw new InvalidOperationException($"{Path.GetFileName(exe)} failed (exit {process.ExitCode.ToString(CultureInfo.InvariantCulture)}): {ShortReason(detail)}");
        }
    }

    private static void WriteOutput(Action<string>? log, string stream, string value)
    {
        const int limit = 32768;
        if (value.Length == 0) return;
        WriteLog(log, $"xma.probe.{stream} " + (value.Length > limit
            ? value[..limit] + $"\n[truncated {value.Length - limit} characters]"
            : value));
    }

    private static XmaEncoderStatus Report(XmaEncoderStatus status, string stage, Action<string>? log)
    {
        WriteLog(log, "xma.encoder.status " + JsonSerializer.Serialize(new { stage, status.Available, status.Reason }));
        return status;
    }

    private static string ShortReason(string value)
    {
        const int limit = 1024;
        value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length > limit ? value[..limit] + " [truncated]" : value;
    }

    private static void WriteLog(Action<string>? log, string message)
    {
        try { log?.Invoke(message); } catch { }
    }
}
