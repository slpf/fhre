using FH6RB.Core;

namespace FH6RB.Services;

public sealed record BankReadResult(List<RadioTrack> Tracks, string? Error)
{
    internal BankFileSnapshot? Snapshot { get; init; }
    internal int Mode { get; init; }
    internal long Overhead { get; init; }
}

internal readonly record struct BankFileStamp(long Length, DateTime LastWriteTime, DateTime CreationTime)
{
    public static BankFileStamp Read(FileStream file) =>
        new(file.Length, File.GetLastWriteTimeUtc(file.SafeFileHandle), File.GetCreationTimeUtc(file.SafeFileHandle));
}

internal sealed record BankFileSnapshot(BankFileStamp Stamp, IReadOnlySet<ulong> Ids);

public static class BankReader
{
    public static BankReadResult ReadTracks(string bankPath, RadioInfo? radio, int stationNumber)
    {
        try
        {
            using var file = File.OpenRead(bankPath);
            var stamp = BankFileStamp.Read(file);
            var tracks = FevBank.ReadTrackInfo(file, out var mode, out var audioBytes);
            var ids = tracks.Select(track => track.Id).ToHashSet();
            return new BankReadResult(ReadCore(bankPath, radio, stationNumber, tracks, ids), null)
            {
                Snapshot = new BankFileSnapshot(stamp, ids),
                Mode = mode,
                Overhead = Math.Max(0, stamp.Length - audioBytes)
            };
        }
        catch (Exception ex)
        {
            Log.Line($"BankReader ERROR for {bankPath}:");
            Log.Line(ex.ToString());
            return new BankReadResult([], ex.Message);
        }
    }

    private static List<RadioTrack> ReadCore(string bankPath, RadioInfo? radio, int stationNumber,
        List<FevBank.BankTrack> tracks, HashSet<ulong> ids)
    {
        Log.Line($"BankReader: {Path.GetFileName(bankPath)} (station #{stationNumber})");

        Log.Line($"  STBL count = {tracks.Count}");
        
        if (tracks.Count == 0)
        {
            Log.Line("  -> empty STBL, returning 0 tracks");
            return [];
        }

        var withAudio = tracks.Count(t => t.SampleRate > 0);
        
        Log.Line($"  FSB5 samples = {withAudio}" + (withAudio == 0 ? " (streamed/empty — durations unavailable)" : ""));

        var meta = new Dictionary<ulong, (string Sn, string? Dn, string? Ar)>();
        var markersById = new Dictionary<ulong, Dictionary<string, long>>();
        var replaced = new HashSet<ulong>();
        var enabled = new HashSet<ulong>();
        var station = radio?.StationByNumber(stationNumber);
        var usesMilliseconds = station?.UsesMilliseconds == true;
        var usesTrackBreakDown = station?.UsesTrackBreakDown == true;

        if (station is not null)
        {
            foreach (var track in station.ReadTracks())
            {
                var id = Lookup.SoundNameToId(track.SoundName);
                meta.TryAdd(id, (track.SoundName, track.DisplayName, track.Artist));
                if (track.Enabled) enabled.Add(id);
                if (track.Replaced) replaced.Add(id);
                if (track.Markers is { Count: > 0 })
                {
                    markersById.TryAdd(id, track.Markers.ToDictionary(m => m.Key, m => m.Value));
                }
            }

            Log.Line($"  XML: meta entries = {meta.Count}, FreeroamTracks@#{stationNumber} = {enabled.Count}");
        }
        else
        {
            Log.Line($"  XML: no metadata for station #{stationNumber}");
        }

        var unresolved = ids.Where(id => !meta.ContainsKey(id)).ToHashSet();
        var customs = Naming.ResolveCustomNames(unresolved);
        Log.Line($"  custom name-probe (for id->name) = {customs.Count}");

        var inMeta = ids.Count - unresolved.Count;
        Log.Line($"  STBL ids matched in XML meta = {inMeta}/{ids.Count}");

        foreach (var t in tracks.Take(3))
        {
            Log.Line($"    sample[{t.Index}] id=0x{t.Id:x16} inMeta={meta.ContainsKey(t.Id)} inCustom={customs.ContainsKey(t.Id)}");
        }

        var result = new List<RadioTrack>(tracks.Count);
        
        foreach (var t in tracks.OrderBy(e => e.Index))
        {
            var hasAudio = t.SampleRate > 0;
            var customName = customs.GetValueOrDefault(t.Id);
            var hasMeta = meta.TryGetValue(t.Id, out var m);
            var soundName = hasMeta ? m.Sn : customName ?? $"sound_{t.Index}";
            var isCustom = soundName.StartsWith(Naming.CustomPrefix, StringComparison.Ordinal);
            IReadOnlyDictionary<string, long>? markers = markersById.GetValueOrDefault(t.Id);
            if (usesMilliseconds && hasAudio && markers is not null)
            {
                markers = markers.ToDictionary(m => m.Key,
                    m => m.Value < 0 ? m.Value : checked((long) decimal.Round(m.Value * (decimal) t.SampleRate / 1000,
                        0, MidpointRounding.AwayFromZero)));
            }

            result.Add(new RadioTrack
            {
                SoundName = soundName,
                BankId = t.Id,
                Origin = isCustom ? TrackOrigin.Custom : TrackOrigin.Original,
                Enabled = enabled.Contains(t.Id),
                DisplayName = hasMeta ? m.Dn ?? soundName : soundName,
                Artist = hasMeta ? m.Ar : null,
                SampleLength = t.Frames,
                SampleRate = t.SampleRate,
                SubIndex = hasAudio ? t.Index : -1,
                BankBytes = t.Bytes,
                Replaced = replaced.Contains(t.Id),
                UsesMilliseconds = usesMilliseconds,
                UsesTrackBreakDown = usesTrackBreakDown,
                Markers = markers,
            });
        }

        var named = result.Count(r => !r.SoundName.StartsWith("sound_"));
        Log.Line($"  -> {result.Count} tracks, {named} named, {result.Count(r => r.Enabled)} enabled");
        
        return result;
    }
}
