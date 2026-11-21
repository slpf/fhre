using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FH6RB.Services;

public sealed record LoudnessMeasurement(double? IntegratedLufs, double? TruePeakDbtp, long Frames);

public static class Loudnorm
{
    private const string ConversionFilter = "aresample=48000:osf=dbl:ochl=stereo:rematrix_maxval=1";
    private const string SampleStatisticsOptions =
        "measure_perchannel=none:measure_overall=Number_of_samples+Peak_level+Number_of_NaNs+Number_of_Infs";
    private const string InputStatisticsFilter = "astats@input=" + SampleStatisticsOptions;
    private const string OutputStatisticsFilter = "astats@output=" + SampleStatisticsOptions;
    private const string PreparedScheme = "stereo48k-double-normalized-rematrix-ebur128-v5";
    private const int TruePeakPaddingFrames = 192;
    private const int LimiterPaddingFrames = 480;
    private const int LimiterAttackMs = 5;
    private const int LimiterReleaseMs = 50;
    private const double PeakMarginDb = 0.2;
    private const int CacheLimit = 256;
    private static readonly object CacheSync = new();
    private static readonly Dictionary<(string Path, string Hash), CacheEntry> AnalysisCache = new();
    private static readonly Regex IntegratedValue = new(@"(?m)^\s*I:\s*(\S+)\s+LUFS\s*$", RegexOptions.Compiled);
    private static readonly Regex TruePeakValue = new(@"True peak:\s*Peak:\s*(\S+)\s+dBFS", RegexOptions.Compiled);
    private static readonly Regex SamplePeakValue = new(@"Peak level dB:\s*(\S+)", RegexOptions.Compiled);
    private static readonly Regex SampleCount = new(@"Number of samples:\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex NanCount = new(@"Number of NaNs:\s*(\S+)", RegexOptions.Compiled);
    private static readonly Regex InfCount = new(@"Number of Infs:\s*(\S+)", RegexOptions.Compiled);

    private readonly record struct AnalysisKey(string Path, long Modified, long Bytes, string Scheme);
    private sealed record Analysis(double InputI, double InputTp, long Frames, bool Silent, bool IntegratedAvailable);
    private sealed record RenderOptions(bool Normalize, bool Limiter, double TargetI, double TargetTp, double GainDb);
    private sealed record RenderPlan(string Filter, bool LimiterActive, long? Frames);
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

    internal static void RenderPreview(string source, string wav, AppSettings settings, double gainDb,
        string contentHash, Action<string>? log, CancellationToken ct)
        => RenderCore(source, wav, Options(settings, gainDb), log, ct, contentHash);

    public static Task RenderAsync(string source, string wav, AppSettings settings, double gainDb = 0,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var options = Options(settings, gainDb);
        return Task.Run(() => RenderCore(source, wav, options, log, ct), ct);
    }

    public static string Filter(string source, AppSettings settings, Action<string>? log = null)
        => CreatePlan(source, Options(settings, 0), log, default).Filter;

    public static Task<string> FilterAsync(string source, AppSettings settings, Action<string>? log = null)
    {
        var options = Options(settings, 0);
        return Task.Run(() => CreatePlan(source, options, log, default).Filter);
    }

    public static async Task<string?> MeasureIntegratedAsync(string source)
    {
        try
        {
            var result = await MeasureOutputAsync(source).ConfigureAwait(false);
            return result.IntegratedLufs is { } value ? Fmt(value) : null;
        }
        catch
        {
            return null;
        }
    }

    public static Task<LoudnessMeasurement> MeasureOutputAsync(string source, Action<string>? log = null,
        CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var key = Key(source, "native-output-ebur128-v2");
            var result = Measure(source, false, log, ct);
            if (Key(source, key.Scheme) != key) throw new IOException("Audio source changed during output measurement.");
            return new LoudnessMeasurement(result.IntegratedAvailable ? result.InputI : null,
                result.Silent ? null : result.InputTp, result.Frames);
        }, ct);

    private static RenderOptions Options(AppSettings settings, double gainDb)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!double.IsFinite(gainDb)) throw new ArgumentOutOfRangeException(nameof(gainDb), "Audio gain must be finite.");
        return new RenderOptions(settings.LoudnessNormalize, settings.PeakLimiter,
            AppSettings.ClampTargetLufs(settings.TargetLufs), AppSettings.ClampTargetTruePeak(settings.TargetTruePeak), gainDb);
    }

    private static void RenderCore(string source, string wav, RenderOptions options, Action<string>? log,
        CancellationToken ct, string? contentHash = null)
    {
        ct.ThrowIfCancellationRequested();
        using var input = File.OpenRead(source);
        var sourceKey = Key(source, PreparedScheme);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(wav), comparison))
        {
            throw new ArgumentException("The output WAV must differ from the audio source.", nameof(wav));
        }
        if (options.Normalize && contentHash is null) contentHash = ContentHash(input, ct);
        var plan = CreatePlan(source, options, log, ct, contentHash);
        if (Key(source, PreparedScheme) != sourceKey) throw new IOException("Audio source changed during loudness analysis.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(wav));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        try
        {
            if (plan.LimiterActive)
            {
                RenderLimited(source, wav, plan, options, sourceKey, log, ct);
            }
            else
            {
                RenderPcm(source, wav, plan.Filter, "loudnorm.render", log, ct);
            }
            if (Key(source, PreparedScheme) != sourceKey) throw new IOException("Audio source changed during rendering.");
            var (rate, frames) = WaveformService.Probe(wav);
            if (rate != 48000 || frames <= 0 || plan.Frames is { } expected && frames != expected)
            {
                throw new InvalidDataException("Audio rendering changed its expected duration or produced no valid 48000 Hz PCM frames.");
            }
            Diagnostic(log, "loudnorm.render.ok", new { wav, bytes = new FileInfo(wav).Length, frames, sampleRate = rate });
        }
        catch (Exception ex)
        {
            Diagnostic(log, "loudnorm.render.failed", new { source, wav, error = ex.Message });
            try { File.Delete(wav); } catch { }
            throw;
        }
    }

    private static void RenderPcm(string source, string wav, string filter, string stage,
        Action<string>? log, CancellationToken ct)
    {
        var args = $"-y -hide_banner -nostdin -nostats -xerror -loglevel info -i \"{source}\" -map 0:a:0 " +
            $"-af \"{filter},{OutputStatisticsFilter}\" -ar 48000 -ac 2 -c:a pcm_s16le \"{wav}\"";
        _ = ReadSampleStatistics(Run(args, stage, log, ct));
    }

    private static void RenderLimited(string source, string wav, RenderPlan plan, RenderOptions options,
        AnalysisKey sourceKey, Action<string>? log, CancellationToken ct)
    {
        var floating = wav + "." + Guid.NewGuid().ToString("N") + ".float.wav";
        try
        {
            var graph = $"[0:a:0]{plan.Filter},{OutputStatisticsFilter},asplit=3[pcm][integrated][peak];" +
                "[integrated]ebur128@integrated=framelog=verbose,anullsink;" + PeakMeter("[peak]", "peak");
            var args = $"-y -hide_banner -nostdin -nostats -xerror -loglevel info -i \"{source}\" " +
                $"-filter_complex \"{graph}\" -map \"[pcm]\" -c:a pcm_f32le \"{floating}\"";
            var err = Run(args, "loudnorm.render", log, ct);
            if (Key(source, PreparedScheme) != sourceKey) throw new IOException("Audio source changed during rendering.");
            _ = ReadSampleStatistics(err);
            var inputFrames = ReadSampleStatistics(FilterLines(err, "astats@input"));
            var measured = ReadMeteredAnalysis(err, "astats@output");
            var (rate, outputFrames) = WaveformService.Probe(floating);
            if (rate != 48000 || outputFrames != inputFrames || measured.Frames != inputFrames
                || plan.Frames is { } expected && inputFrames != expected)
            {
                throw new InvalidDataException("Peak limiting changed the audio duration or output sample rate.");
            }
            var attenuation = measured.Silent ? 0 : Math.Min(0, options.TargetTp - measured.InputTp - PeakMarginDb);
            Diagnostic(log, "loudnorm.pcm.protection", new { source = floating,
                inputLufs = measured.IntegratedAvailable ? (double?) measured.InputI : null,
                inputTruePeak = measured.Silent ? (double?) null : measured.InputTp,
                targetTruePeak = options.TargetTp, attenuationDb = attenuation, marginDb = PeakMarginDb,
                frames = measured.Frames });
            RenderPcm(floating, wav, "anull" + Volume(attenuation), "loudnorm.pcm.convert", log, ct);
            Diagnostic(log, "loudnorm.pcm.result", new { wav, frames = outputFrames, attenuationDb = attenuation });
        }
        finally
        {
            try { File.Delete(floating); } catch { }
        }
    }

    private static RenderPlan CreatePlan(string source, RenderOptions options, Action<string>? log, CancellationToken ct,
        string? contentHash = null)
    {
        var prefix = ConversionFilter + "," + InputStatisticsFilter;
        if (!options.Normalize)
        {
            Diagnostic(log, "loudnorm.render.plan", new { mode = "manual", source,
                manualGainDb = options.GainDb, requestedGainDb = options.GainDb, effectiveGainDb = options.GainDb,
                limiterActive = false, peakLimited = false, peakProtection = false });
            return new RenderPlan(prefix + Volume(options.GainDb), false, null);
        }
        using var opened = contentHash is null ? File.OpenRead(source) : null;
        var input = CachedMeasure(source, contentHash ?? ContentHash(opened!, ct), log, ct);
        if (input.Silent)
        {
            Diagnostic(log, "loudnorm.render.plan", new { mode = "constant", source, silent = true,
                input.Frames, manualGainDb = options.GainDb, requestedGainDb = 0, effectiveGainDb = 0,
                limiterActive = false, peakMarginDb = PeakMarginDb, peakCeilingScope = "PCM before codec encoding" });
            return new RenderPlan(prefix, false, input.Frames);
        }
        var requested = input.IntegratedAvailable ? options.TargetI - input.InputI + options.GainDb : options.GainDb;
        var peakGain = options.TargetTp - input.InputTp - PeakMarginDb;
        var effective = options.Limiter ? requested : Math.Min(requested, peakGain);
        if (!double.IsFinite(effective)) throw new InvalidDataException("The calculated audio gain is not finite.");
        var predictedPeak = input.InputTp + requested;
        var limiterActive = options.Limiter && requested > peakGain;
        Diagnostic(log, "loudnorm.render.plan", new { mode = limiterActive ? "limited" : "constant", source,
            inputLufs = input.IntegratedAvailable ? (double?) input.InputI : null, inputTruePeak = input.InputTp,
            frames = input.Frames, targetLufs = options.TargetI, targetTruePeak = options.TargetTp,
            manualGainDb = options.GainDb, requestedGainDb = requested, effectiveGainDb = effective,
            predictedTruePeak = predictedPeak, limiterActive, peakLimited = !options.Limiter && effective < requested,
            peakMarginDb = PeakMarginDb, peakCeilingScope = "PCM before codec encoding",
            estimatedOutputLufs = !limiterActive && input.IntegratedAvailable ? (double?) (input.InputI + effective) : null,
            estimatedOutputTruePeak = limiterActive ? (double?) null : input.InputTp + effective,
            limiterAttackMs = limiterActive ? (int?) LimiterAttackMs : null,
            limiterReleaseMs = limiterActive ? (int?) LimiterReleaseMs : null,
            limiterPaddingFrames = limiterActive ? LimiterPaddingFrames : 0 });
        if (!input.IntegratedAvailable)
        {
            log?.Invoke("loudnorm.integrated.unavailable: short or below-gate audio uses manual gain with peak protection.");
        }
        var filter = prefix + Volume(effective);
        if (limiterActive)
        {
            var limit = Math.Pow(10, (options.TargetTp - PeakMarginDb) / 20);
            filter += $",apad=pad_len={LimiterPaddingFrames},aresample=192000:osf=dbl," +
                $"alimiter=limit={Fmt(limit)}:attack={LimiterAttackMs}:release={LimiterReleaseMs}:asc=false:level=false:latency=true," +
                ConversionFilter + $",atrim=end_sample={input.Frames}";
        }
        return new RenderPlan(filter, limiterActive, input.Frames);
    }

    private static string Volume(double gainDb) => $",volume={Fmt(gainDb)}dB:precision=double";

    private static Analysis CachedMeasure(string source, string hash, Action<string>? log, CancellationToken ct)
    {
        var stamp = Key(source, PreparedScheme);
        var key = (stamp.Path, hash);
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
            if (Key(source, PreparedScheme) != stamp) throw new IOException("Audio source changed during loudness analysis.");
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

    internal static string ContentHash(Stream input, CancellationToken ct)
    {
        var hash = SHA256.HashDataAsync(input, ct).AsTask().GetAwaiter().GetResult();
        return Convert.ToHexString(hash);
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
        var preparation = prepared ? ConversionFilter + "," : "aformat=sample_fmts=dbl,";
        var graph = $"[0:a:0]{preparation}{InputStatisticsFilter},asplit=2[integrated][peak];" +
            "[integrated]ebur128@integrated=framelog=verbose[measured];" + PeakMeter("[peak]", "peak");
        var args = $"-hide_banner -nostdin -nostats -xerror -loglevel info -i \"{source}\" " +
            $"-filter_complex \"{graph}\" -map \"[measured]\" -f null -";
        var err = Run(args, "loudnorm.measure", log, ct);
        var result = ReadMeteredAnalysis(err, "astats@input");
        var available = result.IntegratedAvailable && (!prepared || result.Frames >= 19200);
        Diagnostic(log, "loudnorm.measure.result", new { source, prepared, frames = result.Frames, silent = result.Silent,
            integratedAvailable = available, inputLufs = available ? (double?) result.InputI : null,
            inputTruePeak = result.Silent ? (double?) null : result.InputTp });
        return result with { IntegratedAvailable = available };
    }

    private static string PeakMeter(string input, string name)
        => $"{input}apad=pad_len={TruePeakPaddingFrames},ebur128@{name}=peak=sample+true:framelog=verbose,anullsink";

    private static Analysis ReadMeteredAnalysis(string text, string statisticsName)
    {
        var statistics = FilterLines(text, statisticsName);
        var frames = ReadSampleStatistics(statistics);
        var inputI = ReadNumber(IntegratedValue, FilterSummary(text, "ebur128@integrated"),
            "integrated loudness", allowNegativeInfinity: true);
        var truePeak = ReadNumber(TruePeakValue, FilterSummary(text, "ebur128@peak"),
            "true peak", allowNegativeInfinity: true);
        var samplePeak = ReadNumber(SamplePeakValue, statistics, "sample peak", allowNegativeInfinity: true);
        var inputTp = Math.Max(truePeak, samplePeak);
        var silent = double.IsNegativeInfinity(inputTp);
        return new Analysis(inputI, inputTp, frames, silent, !silent && inputI > -69.9);
    }

    private static string FilterLines(string text, string name)
        => string.Join("\n", text.Replace("\r", "").Split('\n')
            .Where(line => line.StartsWith("[" + name + " @ ", StringComparison.Ordinal)));

    private static string FilterSummary(string text, string name)
    {
        text = text.Replace("\r", "");
        var start = text.LastIndexOf("[" + name + " @ ", StringComparison.Ordinal);
        if (start < 0) throw new InvalidDataException($"Audio analysis did not report {name}.");
        var end = text.IndexOf("\n[", start, StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..end];
    }

    private static long ReadSampleStatistics(string text)
    {
        var count = SampleCount.Matches(text).LastOrDefault();
        if (count is null || !long.TryParse(count.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var frames)
            || frames <= 0)
        {
            throw new InvalidDataException("Audio processing produced no decoded audio frames.");
        }
        foreach (var (pattern, label) in new[] { (NanCount, "NaN count"), (InfCount, "infinity count") })
        {
            var matches = pattern.Matches(text);
            if (matches.Count == 0) throw new InvalidDataException($"Audio analysis did not report {label}.");
            foreach (Match match in matches)
            {
                if (ParseNumber(match.Groups[1].Value, label, false) != 0)
                {
                    throw new InvalidDataException("Audio contains non-finite samples.");
                }
            }
        }
        return frames;
    }

    private static double ReadNumber(Regex pattern, string text, string label, bool allowNegativeInfinity = false)
    {
        var match = pattern.Matches(text).LastOrDefault();
        if (match is null) throw new InvalidDataException($"Loudness analysis did not report {label}.");
        return ParseNumber(match.Groups[1].Value, label, allowNegativeInfinity);
    }

    private static double ParseNumber(string value, string label, bool allowNegativeInfinity)
    {
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
}
