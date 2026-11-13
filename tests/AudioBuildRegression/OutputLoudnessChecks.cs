using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using FH6RB.Core;
using FH6RB.Services;

internal static class OutputLoudnessChecks
{
    public static async Task RunAsync(string temp, Action<string> writeEmptyBank)
    {
        await CheckNativeMeasurementAsync(temp);
        var varying = Path.Combine(temp, "source-varying.wav");
        GainEnvelopeChecks.WriteVaryingFixture(varying);
        await CheckAnalysisReuseAsync(temp, varying);
        await CheckEncodedPeaksAsync(temp, varying, writeEmptyBank);
        Console.WriteLine("Verified shared source measurements, native output loudness, selective encoded peak correction and encoded audio preservation.");
    }

    private static async Task CheckNativeMeasurementAsync(string temp)
    {
        var mono = Path.Combine(temp, "native-meter-mono44100.wav");
        NormalizationChecks.WriteSignal(mono, 44100, 1, 3, limited: false, transients: false);
        var log = new List<string>();
        var measured = await Loudnorm.MeasureOutputAsync(mono, log.Add);
        var independent = NormalizationChecks.Measure(mono);
        Check(measured.Frames == 44100 * 3 && measured.IntegratedLufs is { } integrated
            && Math.Abs(integrated - independent.Integrated) <= 0.11
            && measured.TruePeakDbtp is { } peak && Math.Abs(peak - independent.TruePeak) <= 0.11,
            "Output metering resampled or remixed the native mono source.");
        Check(log.Any(line => line.StartsWith("loudnorm.measure.start ", StringComparison.Ordinal))
            && !log.Any(line => line.Contains("aresample=", StringComparison.Ordinal)),
            "Output measurement applies the encoder conversion graph.");
        var silence = Path.Combine(temp, "native-meter-silence.wav");
        NormalizationChecks.WriteSignal(silence, 44100, 1, 1, limited: false, transients: false, silence: true);
        var silent = await Loudnorm.MeasureOutputAsync(silence);
        Check(silent.Frames == 44100 && silent.IntegratedLufs is null && silent.TruePeakDbtp is null,
            "Silent native output produced fabricated loudness or peak measurements.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var rejected = false;
        try { await Loudnorm.MeasureOutputAsync(mono, ct: cancelled.Token); }
        catch (OperationCanceledException) { rejected = true; }
        Check(rejected, "Output metering ignored cancellation.");
    }

    private static async Task CheckAnalysisReuseAsync(string temp, string source)
    {
        var settings = LimiterSettings(-23, -1);
        var first = Path.Combine(temp, "source-analysis-first.wav");
        var firstLog = new List<string>();
        await Loudnorm.RenderAsync(source, first, settings, log: firstLog.Add);
        Check(CacheStatus(firstLog, "miss"), "A new source was not measured.");
        var meter = NormalizationChecks.Measure(first);
        Check(Math.Abs(meter.Integrated + 23) <= 0.3 && meter.TruePeak <= -1,
            "Constant gain missed an attainable target on a varying-level source.");
        var repeat = Path.Combine(temp, "source-analysis-repeat.wav");
        var repeatLog = new List<string>();
        await Loudnorm.RenderAsync(source, repeat, settings, log: repeatLog.Add);
        Check(CacheStatus(repeatLog, "hit") && !repeatLog.Any(line => line.StartsWith("loudnorm.measure.start ", StringComparison.Ordinal))
            && AudioBuildValidation.Inspect(first).PcmSha256 == AudioBuildValidation.Inspect(repeat).PcmSha256,
            "Repeated rendering remeasured the source or changed its PCM.");
        foreach (var (name, options, trim) in new[]
        {
            ("lufs", LimiterSettings(-26, -1), 0d),
            ("peak", LimiterSettings(-23, -3), 0d),
            ("trim", LimiterSettings(-23, -1), -2d),
        })
        {
            var changed = new List<string>();
            await Loudnorm.RenderAsync(source, Path.Combine(temp, $"source-analysis-{name}.wav"), options, trim, changed.Add);
            Check(CacheStatus(changed, "hit") && !changed.Any(line => line.StartsWith("loudnorm.measure.start ", StringComparison.Ordinal)),
                "Changing render targets duplicated unchanged source measurements.");
        }
    }
    private static async Task CheckEncodedPeaksAsync(string temp, string varying, Action<string> writeEmptyBank)
    {
        var source = Path.Combine(temp, "encoded-peak-source.wav");
        Run(Tools.FfmpegPath, "-y -hide_banner -loglevel error -f lavfi -i " +
            $"\"aevalsrc=sgn(sin(2*PI*12000*t))|sgn(sin(2*PI*11900*t)):s=48000:d=4\" -c:a pcm_f32le \"{source}\"");
        await CheckHotFloatAsync(temp, source);
        var settings = new AppSettings
        {
            LoudnessNormalize = true, TargetLufs = -9, TargetTruePeak = -1,
            VorbisQuality = 95, EncodeParallelism = 1,
        };
        var prepared = Path.Combine(temp, "encoded-peak-prepared.wav");
        await Loudnorm.RenderAsync(source, prepared, settings, gainDb: 20);
        var preparedMeter = NormalizationChecks.Measure(prepared);
        Check(preparedMeter.TruePeak is >= -1.3 and <= -1.1, "The peak-retry fixture does not reach the protected input ceiling.");
        var witnessFsb = Path.Combine(temp, "encoded-peak-witness.fsb");
        var cache = Path.Combine(temp, "encoded-peak-witness-cache");
        Directory.CreateDirectory(cache);
        Run(Tools.FsbankclPath, $"-format vorbis -quality 95 -o \"{witnessFsb}\" \"{prepared}\" -cache_dir \"{cache}\"");
        Check(File.Exists(witnessFsb), "The native peak-retry fixture did not encode.");
        var witnessWav = Path.Combine(temp, "encoded-peak-witness.wav");
        Run(Tools.VgmstreamPath, $"-i -w -s 1 -o \"{witnessWav}\" \"{witnessFsb}\"");
        Check(NormalizationChecks.Measure(witnessWav).TruePeak > -1,
            "The native Vorbis fixture no longer exceeds its prepared input ceiling.");
        var previewSource = Path.Combine(temp, "limiter-preview-build-source.wav");
        File.Copy(varying, previewSource);
        var limiter = LimiterSettings(-23, -1);
        var preview = AudioDecoder.DecodeAdded(previewSource, limiter);
        var items = new[]
        {
            new BuildItem(Naming.MakeSoundName(201), true, source, "Peak retry", null, 20, true),
            new BuildItem(Naming.MakeSoundName(202), true, previewSource, "Cached limiter", null, null, true,
                Normalization: new NormalizationOverride(true, -23, -1, true)),
            new BuildItem(Naming.MakeSoundName(203), true, prepared, "Manual remains unchanged", null, null, true,
                Normalization: new NormalizationOverride(false)),
            new BuildItem(Naming.MakeSoundName(204), true, witnessFsb, "Encoded remains unchanged", null, 20, true,
                Normalization: new NormalizationOverride(true, -9, -9, true)),
        };
        var empty = Path.Combine(temp, "encoded-peaks.empty.bank");
        var output = Path.Combine(temp, "encoded-peaks.bank");
        writeEmptyBank(empty);
        var log = new ConcurrentQueue<string>();
        await BankBuildService.BuildToFileAsync(empty, output, items, settings, log.Enqueue);
        var retryLog = ForTrack(log, 0);
        var corrections = Events(retryLog, "fsb.peak_correction");
        Check(corrections.Count is >= 1 and <= 2, "A real Vorbis overshoot did not trigger bounded correction.");
        double cumulative = 0;
        for (var i = 0; i < corrections.Count; i++)
        {
            var correction = corrections[i];
            var attenuation = correction.GetProperty("attenuationDb").GetDouble();
            cumulative += attenuation;
            Check(correction.GetProperty("attempt").GetInt32() == i + 1 && attenuation < 0
                && Math.Abs(correction.GetProperty("cumulativeGainDb").GetDouble() - cumulative) < 0.0001
                && correction.GetProperty("inputTruePeak").GetDouble() > -1.05,
                "Encoded peak-correction diagnostics lost the ordered attenuation history.");
        }
        Check(EncoderCalls(retryLog) == corrections.Count + 1, "Peak correction did not re-encode exactly the problematic track.");
        for (var i = 1; i < items.Length; i++)
        {
            var trackLog = ForTrack(log, i);
            Check(Events(trackLog, "fsb.peak_correction").Count == 0 && EncoderCalls(trackLog) == (i == 3 ? 0 : 1),
                "Peak correction re-encoded a safe track, a manual track or an encoded FSB import.");
        }
        var accepted = Events(retryLog, "fsb.peak_correction.ok").Single();
        Check(accepted.GetProperty("attempts").GetInt32() == corrections.Count
            && accepted.GetProperty("truePeakDbtp").GetDouble() <= -1,
            "The correction accepted audio above its final protected ceiling.");
        var limiterLog = ForTrack(log, 1);
        Check(CacheStatus(limiterLog, "hit") && !limiterLog.Any(line => line.StartsWith("loudnorm.measure.start ", StringComparison.Ordinal)),
            "Building a previewed track repeated its first analysis pass.");
        var inputHash = Events(limiterLog, "wav.prepared").Single().GetProperty("PcmSha256").GetString();
        Check(inputHash == AudioBuildValidation.Inspect(preview).PcmSha256,
            "The shared source analysis produced different preview and encoder PCM.");
        for (var i = 0; i < items.Length; i++)
        {
            var decoded = AudioDecoder.DecodeBank(output, i);
            var actual = NormalizationChecks.Measure(decoded);
            var reported = Events(ForTrack(log, i), "fsb.loudness").Last();
            Check(Math.Abs(reported.GetProperty("integratedLufs").GetDouble() - actual.Integrated) <= 0.11
                && Math.Abs(reported.GetProperty("truePeakDbtp").GetDouble() - actual.TruePeak) <= 0.11
                && reported.GetProperty("frames").GetInt64() == AudioBuildValidation.Inspect(decoded).Frames,
                "Final FSB loudness diagnostics differ from independently measured assembled audio.");
            if (i < 2) Check(actual.TruePeak <= -1, "A normalized encoded track exceeds its selected true peak ceiling.");
            if (i == 1) Check(Math.Abs(actual.Integrated + 23) <= 0.6, "Encoded normalized audio missed its loudness target.");
            if (i > 1) Check(actual.TruePeak > -1, "Manual or encoded audio was unexpectedly peak corrected.");
        }
        var originalSamples = Fsb5.Parse(new FevBank(File.ReadAllBytes(output)).ExtractFsb5()).Samples;
        Check(Hash(originalSamples[3].Data) == Hash(Fsb5.Parse(File.ReadAllBytes(witnessFsb)).Samples.Single().Data),
            "An encoded import changed compressed packets during peak validation.");
        var repeatedBank = Path.Combine(temp, "encoded-peaks-retained.bank");
        var kept = items.Select(item => item with { SourcePath = null, IsNewCustom = false, GainDb = 30,
            Normalization = new NormalizationOverride(true, -9, -9, true) }).ToArray();
        var retainedLog = new ConcurrentQueue<string>();
        await BankBuildService.BuildToFileAsync(output, repeatedBank, kept, settings, retainedLog.Enqueue);
        var retained = Fsb5.Parse(new FevBank(File.ReadAllBytes(repeatedBank)).ExtractFsb5()).Samples;
        Check(retained.Count == originalSamples.Count && retained.Zip(originalSamples).All(pair => Hash(pair.First.Data) == Hash(pair.Second.Data))
            && EncoderCalls(retainedLog) == 0 && Events(retainedLog, "fsb.peak_correction").Count == 0,
            "Retaining bank audio applied new normalization, gain or peak correction.");
    }

    private static async Task CheckHotFloatAsync(string temp, string source)
    {
        var input = NormalizationChecks.Measure(source);
        Check(input.Integrated > 0 && input.TruePeak > 0, "The hot float fixture does not exceed ordinary full-scale levels.");
        var output = Path.Combine(temp, "hot-float-constant.wav");
        var log = new List<string>();
        await Loudnorm.RenderAsync(source, output, LimiterSettings(-23, -1), log: log.Add);
        var comparison = AudioBuildValidation.Compare(source, output);
        Check(Events(log, "loudnorm.render.plan").Single().GetProperty("mode").GetString() == "constant"
            && comparison.ChannelCorrelation.All(value => value > 0.999999)
            && comparison.GainDb.Max() - comparison.GainDb.Min() < 0.003
            && NormalizationChecks.Measure(output).TruePeak <= -1,
            "A valid hot float source did not preserve a safe common gain.");
        Check(!log.Any(line => line.StartsWith("loudnorm.render.start ", StringComparison.Ordinal)
            && line.Contains("alimiter=", StringComparison.Ordinal)),
            "A safe hot float source unnecessarily activated the limiter.");
    }

    private static AppSettings LimiterSettings(double lufs, double peak) => new()
    {
        LoudnessNormalize = true, PeakLimiter = true, TargetLufs = lufs, TargetTruePeak = peak,
        VorbisQuality = 95, EncodeParallelism = 1,
    };

    private static List<string> ForTrack(IEnumerable<string> log, int index)
        => log.Where(line => line.Contains($"[track={index} ", StringComparison.Ordinal)).ToList();

    private static int EncoderCalls(IEnumerable<string> log)
        => log.Count(line => line.Contains("$ " + Path.GetFileName(Tools.FsbankclPath) + " ", StringComparison.Ordinal));

    private static bool CacheStatus(IEnumerable<string> log, string status)
        => Events(log, "loudnorm.analysis.cache").Any(value => value.GetProperty("status").GetString() == status);

    private static List<JsonElement> Events(IEnumerable<string> log, string stage)
    {
        var result = new List<JsonElement>();
        var marker = stage + " ";
        foreach (var line in log)
        {
            var index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0) continue;
            using var document = JsonDocument.Parse(line[(index + marker.Length)..]);
            result.Add(document.RootElement.Clone());
        }
        return result;
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static void Run(string tool, string arguments)
    {
        var result = Proc.Run(tool, arguments);
        Check(result.ExitCode == 0, $"Fixture tool failed: {tool}\n{result.StdErr}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
