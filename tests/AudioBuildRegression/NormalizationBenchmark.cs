using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FH6RB.Services;

internal static class NormalizationBenchmark
{
    public static async Task RunAsync(string temp)
    {
        var raw = Path.Combine(temp, "benchmark-180s.wav");
        var source = Path.Combine(temp, "benchmark-180s.mp3");
        NormalizationChecks.WriteSignal(raw, 44100, 2, 180, limited: false, transients: false, seed: 2387);
        RunTool($"-y -hide_banner -loglevel error -i \"{raw}\" -c:a libmp3lame -b:a 256k \"{source}\"");
        File.Delete(raw);
        var settings = new AppSettings { LoudnessNormalize = true, DynamicNormalization = false, TargetLufs = -23, TargetTruePeak = -1 };
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "normalization.benchmark.environment", durationSeconds = 180, sourceBytes = new FileInfo(source).Length,
            sourceSampleRate = 44100, channels = 2, mp3Kbps = 256, runtime = Environment.Version.ToString(), processors = Environment.ProcessorCount,
            settings.TargetLufs, settings.TargetTruePeak, cache = "OS file cache warm; measurement cache tested separately" }));

        RenderOld(source, Path.Combine(temp, "benchmark-old-warmup.wav"), settings);
        var coldWarmupSource = Path.Combine(temp, "benchmark-new-cold-warmup.mp3");
        File.Copy(source, coldWarmupSource);
        await Loudnorm.RenderAsync(coldWarmupSource, Path.Combine(temp, "benchmark-new-cold-warmup.wav"), settings);
        var old = new List<double>();
        var cold = new List<double>();
        var warm = new List<double>();
        for (var index = 0; index < 3; index++)
        {
            var oldOutput = Path.Combine(temp, $"benchmark-old-{index}.wav");
            var oldResult = RenderOld(source, oldOutput, settings);
            old.Add(oldResult.TotalMs);
            var coldSource = Path.Combine(temp, $"benchmark-cold-{index}.mp3");
            File.Copy(source, coldSource);
            var coldOutput = Path.Combine(temp, $"benchmark-cold-{index}.wav");
            var log = new List<string>();
            var timer = Stopwatch.StartNew();
            await Loudnorm.RenderAsync(coldSource, coldOutput, settings, log: log.Add);
            var elapsed = timer.Elapsed.TotalMilliseconds;
            cold.Add(elapsed);
            Check(log.Any(l => l.Contains("\"status\":\"miss\"", StringComparison.Ordinal)), "Cold benchmark reused a cached measurement.");
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "normalization.benchmark.cold", run = index + 1,
                oldTotalMs = oldResult.TotalMs, oldMeasureMs = oldResult.MeasureMs, oldRenderMs = oldResult.RenderMs,
                newTotalMs = elapsed, oldResult.Mode, outputBytes = new FileInfo(coldOutput).Length }));
            File.Delete(oldOutput);
            File.Delete(coldOutput);
        }

        await Loudnorm.RenderAsync(source, Path.Combine(temp, "benchmark-new-warmup.wav"), settings);
        for (var index = 0; index < 3; index++)
        {
            var output = Path.Combine(temp, $"benchmark-warm-{index}.wav");
            var log = new List<string>();
            var timer = Stopwatch.StartNew();
            await Loudnorm.RenderAsync(source, output, settings, log: log.Add);
            var elapsed = timer.Elapsed.TotalMilliseconds;
            warm.Add(elapsed);
            Check(log.Any(l => l.Contains("\"status\":\"hit\"", StringComparison.Ordinal)), "Warm benchmark did not reuse its measurement.");
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "normalization.benchmark.warm", run = index + 1, totalMs = elapsed }));
            File.Delete(output);
        }

        Console.WriteLine(JsonSerializer.Serialize(new { stage = "normalization.benchmark.result", runs = 3,
            oldMedianMs = Median(old), newColdMedianMs = Median(cold), newWarmMedianMs = Median(warm),
            coldChangePercent = (Median(cold) / Median(old) - 1) * 100, warmChangePercent = (Median(warm) / Median(old) - 1) * 100,
            scope = "Audio rendering only; bank encoding, validation and assembly excluded" }));
    }

    private static (double TotalMs, double MeasureMs, double RenderMs, string? Mode) RenderOld(string source, string output, AppSettings settings)
    {
        var timer = Stopwatch.StartNew();
        var target = settings.TargetLufs.ToString(CultureInfo.InvariantCulture);
        var peak = settings.TargetTruePeak.ToString(CultureInfo.InvariantCulture);
        var measured = RunTool($"-hide_banner -nostats -i \"{source}\" -map 0:a:0 -af loudnorm=I={target}:TP={peak}:LRA=11:print_format=json -f null -");
        var measureMs = timer.Elapsed.TotalMilliseconds;
        using var document = ParseStats(measured);
        var stats = document.RootElement;
        string Text(string name) => stats.GetProperty(name).GetString() ?? throw new InvalidDataException("Legacy loudnorm returned no statistics.");
        double Number(string name) => double.Parse(Text(name), CultureInfo.InvariantCulture);
        var safeTarget = Number("input_i") - Number("input_tp") + settings.TargetTruePeak - 0.1;
        var resolvedTarget = Math.Round(Math.Min(safeTarget, settings.TargetLufs), 2).ToString(CultureInfo.InvariantCulture);
        var lra = Math.Round(Math.Clamp(Number("input_lra"), 11, 50), 2).ToString(CultureInfo.InvariantCulture);
        var filter = $"loudnorm=I={resolvedTarget}:TP={peak}:LRA={lra}:measured_I={Text("input_i")}:measured_TP={Text("input_tp")}:measured_LRA={Text("input_lra")}:measured_thresh={Text("input_thresh")}:offset={Text("target_offset")}:linear=true:print_format=json";
        var result = RunTool($"-y -hide_banner -nostats -loglevel info -i \"{source}\" -map 0:a:0 -ar 48000 -ac 2 -c:a pcm_s16le -af {filter} \"{output}\"");
        var totalMs = timer.Elapsed.TotalMilliseconds;
        using var outputStats = ParseStats(result);
        return (totalMs, measureMs, totalMs - measureMs, outputStats.RootElement.GetProperty("normalization_type").GetString());
    }

    private static JsonDocument ParseStats(string text)
    {
        var first = text.LastIndexOf('{');
        var last = text.LastIndexOf('}');
        if (first < 0 || last < first) throw new InvalidDataException("Legacy loudnorm produced no statistics.");
        return JsonDocument.Parse(text[first..(last + 1)]);
    }

    private static string RunTool(string arguments)
    {
        var result = Proc.Run(Tools.FfmpegPath, arguments);
        Check(result.ExitCode == 0, "Benchmark conversion failed: " + result.StdErr);
        return result.StdErr;
    }

    private static double Median(IEnumerable<double> values) => values.Order().ElementAt(1);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
