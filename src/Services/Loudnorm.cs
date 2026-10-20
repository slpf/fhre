using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FH6RB.Services;

public static class Loudnorm
{
    private const string ConversionFilter = "aresample=48000:osf=dbl:ochl=stereo:rematrix_maxval=1";
    private const string SampleStatisticsFilter =
        "astats=measure_perchannel=none:measure_overall=Number_of_samples+Number_of_NaNs+Number_of_Infs";
    private const string MeasurementFilter = "ebur128=peak=true:framelog=verbose," + SampleStatisticsFilter;
    private const string PreparedScheme = "stereo48k-double-normalized-rematrix-ebur128-v2";
    private const double PeakMarginDb = 0.2;
    private const int CacheLimit = 256;
    private static readonly object CacheSync = new();
    private static readonly Dictionary<AnalysisKey, CacheEntry> AnalysisCache = new();
    private static readonly Regex IntegratedValue = new(@"(?m)^\s*I:\s*(\S+)\s+LUFS\s*$", RegexOptions.Compiled);
    private static readonly Regex TruePeakValue = new(@"True peak:\s*Peak:\s*(\S+)\s+dBFS", RegexOptions.Compiled);
    private static readonly Regex SampleCount = new(@"Number of samples:\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex NanCount = new(@"Number of NaNs:\s*(\S+)", RegexOptions.Compiled);
    private static readonly Regex InfCount = new(@"Number of Infs:\s*(\S+)", RegexOptions.Compiled);

    private readonly record struct AnalysisKey(string Path, long Modified, long Bytes, string Scheme);
    private sealed record Analysis(double InputI, double InputTp, long Frames, bool Silent, bool IntegratedAvailable);
    private sealed record RenderOptions(bool Normalize, bool Dynamic, double TargetI, double TargetTp, double GainDb);
    private sealed class CacheEntry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Analysis? Value { get; set; }
        public long LastUsed { get; set; }
        public int Users { get; set; }
    }

    public static void Render(string source, string wav, AppSettings settings, double gainDb = 0,
        Action<string>? log = null, CancellationToken ct = default)
        => RenderCore(source, wav, Options(settings, gainDb), log, ct);

    public static Task RenderAsync(string source, string wav, AppSettings settings, double gainDb = 0,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var options = Options(settings, gainDb);
        return Task.Run(() => RenderCore(source, wav, options, log, ct), ct);
    }

    public static string Filter(string source, AppSettings settings, Action<string>? log = null)
        => CreateFilter(source, Options(settings, 0), log, default);

    public static Task<string> FilterAsync(string source, AppSettings settings, Action<string>? log = null)
    {
        var options = Options(settings, 0);
        return Task.Run(() => CreateFilter(source, options, log, default));
    }

    public static async Task<string?> MeasureIntegratedAsync(string source)
    {
        try
        {
            var result = await Task.Run(() => Measure(source, false, null, default)).ConfigureAwait(false);
            return result.IntegratedAvailable ? Fmt(result.InputI) : null;
        }
        catch
        {
            return null;
        }
    }

    private static RenderOptions Options(AppSettings settings, double gainDb)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!double.IsFinite(gainDb)) throw new ArgumentOutOfRangeException(nameof(gainDb), "Audio gain must be finite.");
        return new RenderOptions(settings.LoudnessNormalize, settings.DynamicNormalization,
            AppSettings.ClampTargetLufs(settings.TargetLufs), AppSettings.ClampTargetTruePeak(settings.TargetTruePeak), gainDb);
    }

    private static void RenderCore(string source, string wav, RenderOptions options, Action<string>? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sourceKey = Key(source, PreparedScheme);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(wav), comparison))
        {
            throw new ArgumentException("The output WAV must differ from the audio source.", nameof(wav));
        }
        var filter = CreateFilter(source, options, log, ct);
        if (Key(source, PreparedScheme) != sourceKey) throw new IOException("Audio source changed during loudness analysis.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(wav));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var args = $"-y -hide_banner -nostdin -nostats -xerror -loglevel info -i \"{source}\" -map 0:a:0 " +
            $"-af \"{filter}\" -ar 48000 -ac 2 -c:a pcm_s16le \"{wav}\"";
        try
        {
            var err = Run(args, "loudnorm.render", log, ct);
            if (Key(source, PreparedScheme) != sourceKey) throw new IOException("Audio source changed during rendering.");
            if (!options.Normalize || options.Dynamic) _ = ReadSampleStatistics(err);
            var (rate, frames) = WaveformService.Probe(wav);
            if (rate != 48000 || frames <= 0)
            {
                throw new InvalidDataException("Audio rendering produced no valid 48000 Hz PCM frames.");
            }
            Diagnostic(log, "loudnorm.render.ok", new { wav, bytes = new FileInfo(wav).Length, frames, sampleRate = rate });
            if (options.Normalize && options.Dynamic && ParseSecondPass(err) is { } stats)
            {
                Diagnostic(log, "loudnorm.dynamic.result", stats);
            }
        }
        catch (Exception ex)
        {
            Diagnostic(log, "loudnorm.render.failed", new { source, wav, error = ex.Message });
            try { File.Delete(wav); } catch { }
            throw;
        }
    }

    private static string CreateFilter(string source, RenderOptions options, Action<string>? log, CancellationToken ct)
    {
        if (!options.Normalize)
        {
            Diagnostic(log, "loudnorm.render.plan", new { mode = "manual", source,
                manualGainDb = options.GainDb, requestedGainDb = options.GainDb, effectiveGainDb = options.GainDb,
                peakLimited = false, peakProtection = false });
            return ConversionFilter + "," + SampleStatisticsFilter + Volume(options.GainDb);
        }
        if (options.Dynamic)
        {
            var target = Math.Clamp(options.TargetI + options.GainDb, -70, -5);
            var peak = Math.Max(-9, options.TargetTp - PeakMarginDb);
            Diagnostic(log, "loudnorm.render.plan", new { mode = "dynamic", source,
                targetLufs = options.TargetI, targetTruePeak = options.TargetTp, manualGainDb = options.GainDb,
                dynamicTargetLufs = target, dynamicPeakCeiling = peak, peakMarginDb = options.TargetTp - peak,
                peakCeilingScope = "PCM before codec encoding",
                gainPolicy = "manual trim shifts the loudness target before the fixed true-peak limiter" });
            return ConversionFilter + "," + SampleStatisticsFilter +
                $",loudnorm=I={Fmt(target)}:TP={Fmt(peak)}:LRA=11:linear=false:print_format=json," + ConversionFilter;
        }

        var measured = CachedMeasure(source, log, ct);
        if (measured.Silent)
        {
            Diagnostic(log, "loudnorm.render.plan", new { mode = "silence", source, frames = measured.Frames,
                manualGainDb = options.GainDb, effectiveGainDb = 0, peakMarginDb = PeakMarginDb,
                peakCeilingScope = "PCM before codec encoding" });
            return ConversionFilter;
        }
        var requested = measured.IntegratedAvailable ? options.TargetI - measured.InputI + options.GainDb : options.GainDb;
        var peakGain = options.TargetTp - measured.InputTp - PeakMarginDb;
        var effective = Math.Min(requested, peakGain);
        if (!double.IsFinite(effective)) throw new InvalidDataException("The calculated audio gain is not finite.");
        Diagnostic(log, "loudnorm.render.plan", new { mode = measured.IntegratedAvailable ? "constant" : "peak-only", source,
            inputLufs = measured.IntegratedAvailable ? (double?) measured.InputI : null, inputTruePeak = measured.InputTp,
            frames = measured.Frames, targetLufs = options.TargetI, targetTruePeak = options.TargetTp,
            manualGainDb = options.GainDb, requestedGainDb = requested, peakGainDb = peakGain, effectiveGainDb = effective,
            peakLimited = effective < requested, peakMarginDb = PeakMarginDb, peakCeilingScope = "PCM before codec encoding",
            estimatedOutputLufs = measured.IntegratedAvailable ? (double?) (measured.InputI + effective) : null,
            estimatedOutputTruePeak = measured.InputTp + effective });
        if (!measured.IntegratedAvailable)
        {
            log?.Invoke("loudnorm.integrated.unavailable: short or below-gate audio uses manual gain with the peak ceiling; no dynamic normalization.");
        }
        return ConversionFilter + Volume(effective);
    }

    private static string Volume(double gainDb) => $",volume={Fmt(gainDb)}dB:precision=double";

    private static Analysis CachedMeasure(string source, Action<string>? log, CancellationToken ct)
    {
        var key = Key(source, PreparedScheme);
        CacheEntry? entry;
        var created = false;
        lock (CacheSync)
        {
            if (!AnalysisCache.TryGetValue(key, out entry))
            {
                if (AnalysisCache.Count >= CacheLimit)
                {
                    var oldest = AnalysisCache.Where(item => item.Value.Users == 0)
                        .OrderBy(item => item.Value.LastUsed).FirstOrDefault();
                    if (oldest.Value is not null) AnalysisCache.Remove(oldest.Key);
                }
                if (AnalysisCache.Count < CacheLimit)
                {
                    entry = new CacheEntry();
                    AnalysisCache.Add(key, entry);
                    created = true;
                }
            }
            if (entry is not null)
            {
                entry.LastUsed = Stopwatch.GetTimestamp();
                entry.Users++;
            }
        }
        Diagnostic(log, "loudnorm.analysis.cache", new { source, scheme = PreparedScheme,
            status = entry is null ? "bypass" : created ? "miss" : entry.Gate.CurrentCount == 0 ? "waiting"
                : entry.Value is null ? "miss" : "hit" });
        if (entry is null) return Measure(source, true, log, ct);
        var entered = false;
        try
        {
            entry.Gate.Wait(ct);
            entered = true;
            ct.ThrowIfCancellationRequested();
            if (entry.Value is { } cached) return cached;
            var measured = Measure(source, true, log, ct);
            if (Key(source, PreparedScheme) != key) throw new IOException("Audio source changed during loudness analysis.");
            entry.Value = measured;
            return measured;
        }
        finally
        {
            if (entered) entry.Gate.Release();
            lock (CacheSync)
            {
                entry.Users--;
                entry.LastUsed = Stopwatch.GetTimestamp();
            }
        }
    }

    private static AnalysisKey Key(string source, string scheme)
    {
        var file = new FileInfo(Path.GetFullPath(source));
        if (!file.Exists) throw new FileNotFoundException("Audio source not found.", source);
        var path = OperatingSystem.IsWindows() ? file.FullName.ToUpperInvariant() : file.FullName;
        return new AnalysisKey(path, file.LastWriteTimeUtc.Ticks, file.Length, scheme);
    }

    private static Analysis Measure(string source, bool prepared, Action<string>? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _ = Key(source, prepared ? PreparedScheme : "native-ebur128");
        var filter = (prepared ? ConversionFilter + "," : "") + MeasurementFilter;
        var args = $"-hide_banner -nostdin -nostats -xerror -loglevel info -i \"{source}\" -map 0:a:0 " +
            $"-af \"{filter}\" -f null -";
        var err = Run(args, "loudnorm.measure", log, ct);
        var frames = ReadSampleStatistics(err);
        var inputI = ReadNumber(IntegratedValue, err, "integrated loudness", allowNegativeInfinity: true);
        var inputTp = ReadNumber(TruePeakValue, err, "true peak", allowNegativeInfinity: true);
        var silent = double.IsNegativeInfinity(inputTp);
        var available = !silent && inputI > -69.9 && (!prepared || frames >= 19200);
        Diagnostic(log, "loudnorm.measure.result", new { source, prepared, frames, silent, integratedAvailable = available,
            inputLufs = available ? (double?) inputI : null, inputTruePeak = silent ? (double?) null : inputTp });
        return new Analysis(inputI, inputTp, frames, silent, available);
    }

    private static long ReadSampleStatistics(string text)
    {
        var count = SampleCount.Matches(text).LastOrDefault();
        if (count is null || !long.TryParse(count.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var frames)
            || frames <= 0)
        {
            throw new InvalidDataException("Audio processing produced no decoded audio frames.");
        }
        if (ReadNumber(NanCount, text, "NaN count") != 0 || ReadNumber(InfCount, text, "infinity count") != 0)
        {
            throw new InvalidDataException("Audio contains non-finite samples.");
        }
        return frames;
    }

    private static double ReadNumber(Regex pattern, string text, string label, bool allowNegativeInfinity = false)
    {
        var match = pattern.Matches(text).LastOrDefault();
        if (match is null) throw new InvalidDataException($"Loudness analysis did not report {label}.");
        var value = match.Groups[1].Value;
        if (allowNegativeInfinity && value.Equals("-inf", StringComparison.OrdinalIgnoreCase)) return double.NegativeInfinity;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
        {
            throw new InvalidDataException($"Loudness analysis reported invalid {label}: {value}.");
        }
        return number;
    }

    private static string Run(string args, string stage, Action<string>? log, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        log?.Invoke($"{stage}.start $ ffmpeg {args}");
        try
        {
            var (output, err, code) = Proc.Run(Tools.FfmpegPath, args, ct, timeoutMs: 20 * 60 * 1000);
            LogOutput(log, stage + ".stdout", output);
            LogOutput(log, stage + ".stderr", err);
            Diagnostic(log, stage + ".exit", new { code, elapsedMs = timer.ElapsedMilliseconds });
            if (code != 0)
            {
                var detail = string.IsNullOrWhiteSpace(err) ? output.Trim() : err.Trim();
                throw new InvalidOperationException($"ffmpeg exited with code {code}: {detail}");
            }
            ct.ThrowIfCancellationRequested();
            return err;
        }
        catch (Exception ex)
        {
            Diagnostic(log, stage + ".failed", new { elapsedMs = timer.ElapsedMilliseconds, error = ex.Message });
            throw;
        }
    }

    private static void LogOutput(Action<string>? log, string stage, string output)
    {
        if (log is null || string.IsNullOrWhiteSpace(output)) return;
        const int limit = 32000;
        output = output.Trim();
        if (output.Length > limit) output = output[..limit] + $"\n(truncated, {output.Length} characters total)";
        log($"{stage} {output}");
    }

    private static void Diagnostic(Action<string>? log, string stage, object value)
    {
        if (log is not null) log($"{stage} {JsonSerializer.Serialize(value)}");
    }

    private static string Fmt(double value) => value.ToString("0.############", CultureInfo.InvariantCulture);

    public static SecondPass? ParseSecondPass(string text)
    {
        var open = text.LastIndexOf('{');
        var close = text.LastIndexOf('}');
        if (open < 0 || close <= open) return null;
        try
        {
            using var doc = JsonDocument.Parse(text[open..(close + 1)]);
            var root = doc.RootElement;
            string? Get(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            return new SecondPass(Get("normalization_type"), Get("input_i"), Get("input_lra"),
                Get("output_i"), Get("output_lra"), Get("input_tp"), Get("output_tp"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public sealed record SecondPass(string? NormType, string? InputI, string? InputLra, string? OutputI, string? OutputLra,
        string? InputTp = null, string? OutputTp = null);
}
