using System.Text;
using System.Text.Json;
using FH6RB.Services;

namespace FH6RB.Core;

public static class LoopFinderTester
{
    public sealed record ManifestEntry(
        string Wav,
        string Role,
        bool AutoTune,
        double MinLoopSeconds,
        double ChromaPeakThresh,
        double VadThresh,
        bool RequireVocalPhrase,
        double PhaseContinuityWeight,
        LoopStage Stages,
        bool UseHarmonicChroma,
        bool UseAutocorrOffset,
        bool UseSsmNomination);

    public sealed record Manifest(string FileName, List<ManifestEntry> Entries);

    public static string ResolveTestsDir()
    {
        var exeDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(exeDir);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FH6RB.csproj")))
        {
            dir = dir.Parent;
        }
        return dir != null
            ? Path.Combine(dir.FullName, "tests")
            : Path.Combine(exeDir, "tests");
    }

    public static string ResolveResultsDir() => Path.Combine(ResolveTestsDir(), "result");

    public static string ResolveProjectRoot()
    {
        var exeDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(exeDir);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FH6RB.csproj")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? exeDir;
    }

    public static string ResolveAudioPath(string wav)
    {
        if (Path.IsPathRooted(wav) && File.Exists(wav)) return wav;

        var root = ResolveProjectRoot();
        var candidates = new[]
        {
            Path.Combine(root, "tests", wav),
            Path.Combine(root, "tests", "audio", Path.GetFileName(wav)),
            Path.Combine(root, "tools", "bench", "audio", Path.GetFileName(wav)),
            Path.Combine(root, wav),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return Path.Combine(root, "tests", wav);
    }

    public static List<Manifest> DiscoverManifests(string testsDir)
    {
        if (!Directory.Exists(testsDir)) return new List<Manifest>();

        var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var manifests = new List<Manifest>();
        foreach (var file in Directory.GetFiles(testsDir, "lftest-*.json").OrderBy(f => f))
        {
            try
            {
                var json = File.ReadAllText(file);
                var entries = JsonSerializer.Deserialize<List<EntryDto>>(json, jsonOpts) ?? new();
                manifests.Add(new Manifest(
                    Path.GetFileName(file),
                    entries.Select(e => new ManifestEntry(
                        e.Wav ?? "",
                        e.Role ?? "Generic",
                        e.AutoTune,
                        e.MinLoopSeconds ?? 20.0,
                        e.ChromaPeakThresh ?? 0.0,
                        e.VadThresh ?? 0.0,
                        e.RequireVocalPhrase,
                        e.PhaseContinuityWeight ?? 0.15,
                        ParseStages(e.Disable),
                        e.UseHarmonicChroma ?? false,
                        e.UseAutocorrOffset ?? true,
                        e.UseSsmNomination ?? false)).ToList()));
            }
            catch
            {
                manifests.Add(new Manifest(Path.GetFileName(file), new List<ManifestEntry>
                {
                    new("<parse error>", "Generic", true, 20.0, 0.0, 0.0, false, 0.15, LoopStage.All, false, true, false)
                }));
            }
        }
        return manifests;
    }

    public static async Task RunAsync(
        string testsDir,
        string resultsDir,
        AppSettings settings,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var runLog = new StringBuilder();
        var decoded = new Dictionary<string, (float[] Mono, int Rate, string Wav)>(StringComparer.OrdinalIgnoreCase);

        void Trace(string s)
        {
            runLog.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}");
            Log.Line(s);
            progress?.Report(s);
        }

        (float[] Mono, int Rate)? LoadMono(string sourcePath, StringBuilder entryLog)
        {
            if (decoded.TryGetValue(sourcePath, out var hit))
            {
                return (hit.Mono, hit.Rate);
            }

            string decodedWav;
            try
            {
                decodedWav = AudioDecoder.DecodeAdded(sourcePath, settings);
            }
            catch (Exception ex)
            {
                entryLog.AppendLine($"# decode error: {ex.Message}");
                Trace($"    decode error: {ex.Message}");
                return null;
            }

            var (rate, _) = WaveformService.Probe(decodedWav);
            var mono = WaveformService.Samples(decodedWav);
            if (rate <= 0 || mono.Length == 0)
            {
                entryLog.AppendLine($"# decode produced no samples: {decodedWav}");
                Trace($"    decode produced no samples");
                return null;
            }

            entryLog.AppendLine($"# decoded: {Path.GetFileName(decodedWav)}  rate={rate}  samples={mono.Length}");
            decoded[sourcePath] = (mono, rate, decodedWav);
            return (mono, rate);
        }

        try
        {
            Trace($"Scanning {testsDir}...");
            var manifests = DiscoverManifests(testsDir);
            Trace($"Found {manifests.Count} manifest(s)");
            if (manifests.Count == 0)
            {
                return;
            }

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var runDir = Path.Combine(resultsDir, timestamp);
            Directory.CreateDirectory(runDir);
            Trace($"Run dir: {runDir}");
            Trace($"Decode = AudioDecoder.DecodeAdded (matches Waveform): TargetLufs={settings.TargetLufs} TruePeak={settings.TargetTruePeak}");

            foreach (var manifest in manifests)
            {
                ct.ThrowIfCancellationRequested();
                Trace($"Manifest: {manifest.FileName} ({manifest.Entries.Count} entries)");

                foreach (var entry in manifest.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    Trace($"  {entry.Wav}");

                    var log = new StringBuilder();
                    log.AppendLine($"# track: {entry.Wav}");
                    log.AppendLine($"# manifest: {manifest.FileName}");
                    log.AppendLine($"# role={entry.Role} autoTune={entry.AutoTune} minLoopSeconds={entry.MinLoopSeconds}");
                    log.AppendLine($"# chromaPeak={entry.ChromaPeakThresh} vad={entry.VadThresh} phrase={entry.RequireVocalPhrase} phase={entry.PhaseContinuityWeight} stages={entry.Stages} hps={entry.UseHarmonicChroma} autoOffset={entry.UseAutocorrOffset} ssm={entry.UseSsmNomination}");

                    try
                    {
                        var sourcePath = ResolveAudioPath(entry.Wav);
                        Trace($"    source: {sourcePath}");
                        log.AppendLine($"# source: {sourcePath}");

                        var loaded = LoadMono(sourcePath, log);
                        if (loaded is null)
                        {
                            continue;
                        }

                        var (samples, rate) = loaded.Value;
                        Trace($"    samples: {samples.Length} @ {rate}Hz -> Find");

                        var opts = new LoopSearchOptions
                        {
                            Role = Enum.TryParse<LoopRole>(entry.Role, out var r) ? r : LoopRole.Generic,
                            AutoTune = entry.AutoTune,
                            MinLoopSeconds = entry.MinLoopSeconds,
                            ChromaPeakThreshold = entry.ChromaPeakThresh,
                            VadThreshold = entry.VadThresh,
                            RequireVocalPhrase = entry.RequireVocalPhrase,
                            PhaseContinuityWeight = entry.PhaseContinuityWeight,
                            Stages = entry.Stages,
                            UseHarmonicChroma = entry.UseHarmonicChroma,
                            UseAutocorrOffset = entry.UseAutocorrOffset,
                            UseSsmNomination = entry.UseSsmNomination,
                        };

                        var sb = new StringBuilder();
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var heart = new System.Timers.Timer(2000) { AutoReset = true };
                        heart.Elapsed += (_, _) => Trace($"    | {entry.Wav} Find running... {sw.ElapsedMilliseconds / 1000}s");
                        heart.Start();
                        List<LoopPair> result;
                        try
                        {
                            result = LoopFinder.Find(samples, rate, opts, cacheKey: null, log: line =>
                            {
                                sb.AppendLine(line);
                                Trace("    | " + line.TrimEnd());
                            });
                        }
                        finally
                        {
                            heart.Stop();
                            heart.Dispose();
                        }
                        sw.Stop();
                        Trace($"    Find: {sw.ElapsedMilliseconds}ms, {result.Count} loop(s)");

                        foreach (var line in sb.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        {
                            log.AppendLine($"# {line.TrimEnd()}");
                        }

                        log.AppendLine();
                        log.AppendLine($"# loops ({result.Count}) — start/end are sample indices into the decoded mono (== Waveform Peaks, rate {rate})");
                        for (var i = 0; i < result.Count; i++)
                        {
                            var p = result[i];
                            var startSec = (double) p.LoopStart / rate;
                            var endSec = (double) p.LoopEnd / rate;
                            var lenSec = (double) (p.LoopEnd - p.LoopStart) / rate;
                            log.AppendLine($"# loop#{i:D2}  score={p.Score:0.000}  start={p.LoopStart} ({startSec:0.000}s)  end={p.LoopEnd} ({endSec:0.000}s)  length={p.LoopEnd - p.LoopStart} ({lenSec:0.000}s)  src={p.Source}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace($"    ERROR: {ex.GetType().Name}: {ex.Message}");
                        log.AppendLine($"# ERROR: {ex.GetType().Name}: {ex.Message}");
                    }

                    var trackName = SafeName(Path.GetFileNameWithoutExtension(entry.Wav));
                    var trackDir = Path.Combine(runDir, trackName);
                    Directory.CreateDirectory(trackDir);
                    var outPath = Path.Combine(trackDir, manifest.FileName + ".log");
                    await File.WriteAllTextAsync(outPath, log.ToString(), ct);
                }
            }

            Trace($"Done. Results: {runDir}");
            await File.WriteAllTextAsync(Path.Combine(runDir, "run.log"), runLog.ToString(), ct);
        }
        catch (Exception ex)
        {
            Trace($"FATAL: {ex.GetType().Name}: {ex.Message}");
            try
            {
                var crashDir = Path.Combine(resultsDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}");
                Directory.CreateDirectory(crashDir);
                await File.WriteAllTextAsync(Path.Combine(crashDir, "run.log"), runLog.ToString(), ct);
            }
            catch { }
            throw;
        }
    }

    private static string SafeName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    private static LoopStage ParseStages(List<string>? disable)
    {
        var stages = LoopStage.All;
        if (disable is null || disable.Count == 0) return stages;
        foreach (var name in disable)
        {
            if (Enum.TryParse<LoopStage>(name, true, out var flag) && flag != LoopStage.All && flag != LoopStage.None)
            {
                stages &= ~flag;
            }
        }
        return stages;
    }

    private sealed class EntryDto
    {
        public string? Wav { get; set; }
        public string? Role { get; set; }
        public bool AutoTune { get; set; }
        public double? MinLoopSeconds { get; set; }
        public double? ChromaPeakThresh { get; set; }
        public double? VadThresh { get; set; }
        public bool RequireVocalPhrase { get; set; }
        public double? PhaseContinuityWeight { get; set; }
        public List<string>? Disable { get; set; }
        public bool? UseHarmonicChroma { get; set; }
        public bool? UseAutocorrOffset { get; set; }
        public bool? UseSsmNomination { get; set; }
    }
}
