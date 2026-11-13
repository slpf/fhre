using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using FH6RB.Core;
using FH6RB.Services;
using static TestSupport;

internal static class LoudnessChecks
{
    private sealed record Measurement(int Station, string StationName, string Bank, int Index, string SoundName,
        string? Title, string? Artist, bool Active, long Frames, int SampleRate, int Channels, double Lufs, double TruePeak);

    public static async Task RunAsync(string game, string report, string scratch)
    {
        Check(GameScanner.Scan(game).IsValid && GameScanner.DetectVersion(game) == 3, "Expected a readable FH3 installation.");
        var gameRoot = Path.GetFullPath(game).TrimEnd(Path.DirectorySeparatorChar);
        var reportRoot = Path.GetFullPath(report).TrimEnd(Path.DirectorySeparatorChar);
        Check(!reportRoot.Equals(gameRoot, StringComparison.OrdinalIgnoreCase)
            && !reportRoot.StartsWith(gameRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "The report directory overlaps the game installation.");
        var radio = RadioInfo.Load(GameScanner.RadioInfoPathByFile(game, "RadioInfo_EN.xml")!);
        var stations = RadioCatalog.Build(game, radio);
        var results = new ConcurrentBag<Measurement>();
        var inputs = new List<(StationInfo Station, string Bank, string Path, RadioTrack Track)>();
        var stamps = new Dictionary<string, (long Length, DateTime Modified, string Hash)>();
        foreach (var station in stations)
        {
            foreach (var variant in station.Variants)
            {
                var bank = station.BankName(variant);
                var path = GameScanner.BankPath(game, bank)!;
                var read = BankReader.ReadTracks(path, radio, station.Number);
                Check(read.Error is null && read.Tracks.Count > 0, bank + ": " + read.Error);
                stamps.Add(path, (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path), Hash(path)));
                inputs.AddRange(read.Tracks.Select(track => (station, bank, path, track)));
            }
        }
        var completed = 0;
        await Parallel.ForEachAsync(inputs, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (input, ct) =>
        {
            var track = input.Track;
            var wav = Path.Combine(scratch, input.Bank + "." + track.SubIndex + ".float.wav");
            try
            {
                var decode = await Task.Run(() => Proc.Run(Tools.VgmstreamPath,
                    $"-i -W 4 -s {track.SubIndex + 1} -o \"{wav}\" \"{input.Path}\"", ct), ct);
                Check(decode.ExitCode == 0, input.Bank + ": " + decode.StdErr);
                var info = AudioBuildValidation.Inspect(wav);
                Check(info.Frames == track.SampleLength && info.SampleRate == track.SampleRate && info.BitsPerSample == 32,
                    "Native floating-point decode changed track dimensions.");
                var loudness = await Loudnorm.MeasureOutputAsync(wav, ct: ct);
                Check(loudness.IntegratedLufs is not null && loudness.TruePeakDbtp is not null, "No finite loudness measurement.");
                results.Add(new(input.Station.Number, input.Station.Name, input.Bank, track.SubIndex, track.SoundName,
                    track.DisplayName, track.Artist, track.Enabled, info.Frames, info.SampleRate, info.Channels,
                    loudness.IntegratedLufs!.Value, loudness.TruePeakDbtp!.Value));
                var count = Interlocked.Increment(ref completed);
                if (count % 10 == 0 || count == inputs.Count) Console.WriteLine($"Measured {count}/{inputs.Count} FH3 streams.");
            }
            finally { File.Delete(wav); }
        });
        Check(stamps.All(p => new FileInfo(p.Key).Length == p.Value.Length && File.GetLastWriteTimeUtc(p.Key) == p.Value.Modified
            && Hash(p.Key) == p.Value.Hash), "A source bank changed during measurement.");
        var ordered = results.OrderBy(r => r.Station).ThenBy(r => r.Bank, StringComparer.Ordinal).ThenBy(r => r.Index).ToArray();
        var active = ordered.Where(r => r.Active).ToArray();
        Directory.CreateDirectory(report);
        var stationResults = stations.Select(s => new
        {
            s.Number, s.Name,
            XmlSamples = radio.StationByNumber(s.Number)!.ReadTracks().Count,
            ActiveTracks = radio.StationByNumber(s.Number)!.FreeRoamActiveCount(),
            AudioStreams = ordered.Count(r => r.Station == s.Number),
            Loudness = Statistics(ordered.Where(r => r.Station == s.Number && r.Active).ToArray())
        }).ToArray();
        var data = new
        {
            Game = game, Decoder = Tools.VgmstreamPath, Meter = Tools.FfmpegPath,
            Method = "Native float32 decode; full-track EBU R128 integrated loudness and true peak; no normalization or gain.",
            MinimumActiveTracks = stationResults.Min(s => s.ActiveTracks),
            AllStreams = Statistics(ordered), ActiveTracks = Statistics(active), Stations = stationResults,
            Sources = stamps.Select(p => new { Path = p.Key, p.Value.Length, p.Value.Modified, Sha256 = p.Value.Hash }),
            Tracks = ordered
        };
        File.WriteAllText(Path.Combine(report, "measurements.json"), JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        var csv = new List<string> { "Station,StationName,Bank,Index,SoundName,Title,Artist,Active,Frames,SampleRate,Channels,LUFS,TruePeakDbtp" };
        csv.AddRange(ordered.Select(r => string.Join(",", r.Station.ToString(CultureInfo.InvariantCulture), Quote(r.StationName),
            Quote(r.Bank), r.Index.ToString(CultureInfo.InvariantCulture), Quote(r.SoundName), Quote(r.Title), Quote(r.Artist),
            r.Active, r.Frames.ToString(CultureInfo.InvariantCulture), r.SampleRate.ToString(CultureInfo.InvariantCulture),
            r.Channels.ToString(CultureInfo.InvariantCulture), r.Lufs.ToString("F3", CultureInfo.InvariantCulture),
            r.TruePeak.ToString("F3", CultureInfo.InvariantCulture))));
        File.WriteAllLines(Path.Combine(report, "measurements.csv"), csv);
        Console.WriteLine(JsonSerializer.Serialize(new { data.MinimumActiveTracks, data.AllStreams, data.ActiveTracks, data.Stations }));
    }

    private static object Statistics(Measurement[] tracks)
    {
        Check(tracks.Length > 0, "No audio streams to summarize.");
        var loudness = tracks.Select(t => t.Lufs).Order().ToArray();
        var middle = loudness.Length / 2;
        return new
        {
            Count = tracks.Length, MeanLufs = loudness.Average(),
            MedianLufs = loudness.Length % 2 == 0 ? (loudness[middle - 1] + loudness[middle]) / 2 : loudness[middle],
            MinLufs = loudness[0], MaxLufs = loudness[^1],
            MinTruePeak = tracks.Min(t => t.TruePeak), MaxTruePeak = tracks.Max(t => t.TruePeak)
        };
    }

    private static string Quote(string? value) => "\"" + (value ?? "").Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
