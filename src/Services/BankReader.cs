using FH6RB.Core;

namespace FH6RB.Services;

public sealed record BankReadResult(List<RadioTrack> Tracks, string? Error);

public static class BankReader
{
    public static BankReadResult ReadTracks(string bankPath, RadioInfo? radio, int stationNumber)
    {
        try
        {
            return new BankReadResult(ReadCore(bankPath, radio, stationNumber), null);
        }
        catch (Exception ex)
        {
            Log.Line($"BankReader ERROR for {bankPath}:");
            Log.Line(ex.ToString());
            return new BankReadResult([], ex.Message);
        }
    }

    private static List<RadioTrack> ReadCore(string bankPath, RadioInfo? radio, int stationNumber)
    {
        Log.Line($"BankReader: {Path.GetFileName(bankPath)} (station #{stationNumber})");

        var tracks = FevBank.ReadTrackInfoFromFile(bankPath);
        Log.Line($"  STBL count = {tracks.Count}");
        if (tracks.Count == 0)
        {
            Log.Line("  -> empty STBL, returning 0 tracks");
            return [];
        }

        var withAudio = tracks.Count(t => t.SampleRate > 0);
        Log.Line($"  FSB5 samples = {withAudio}" + (withAudio == 0 ? " (streamed/empty — durations unavailable)" : ""));

        var ids = tracks.Select(t => t.Id).ToHashSet();
        
        var bakPath = bankPath + ".bak";
        HashSet<ulong> customIds;
        Dictionary<ulong, string> customs;
        if (File.Exists(bakPath))
        {
            HashSet<ulong> originalIds;
            try { originalIds = FevBank.ReadStblIdsFromFile(bakPath); }
            catch (Exception ex) { Log.Line($"  .bak read failed: {ex.Message}"); originalIds = []; }

            if (originalIds.Count > 0)
            {
                customIds = ids.Where(id => !originalIds.Contains(id)).ToHashSet();
                customs = Naming.ResolveCustomNames(customIds);
                Log.Line($"  customs by .bak diff = {customIds.Count}, named = {customs.Count}");
            }
            else
            {
                customs = Naming.ScanCustomTracks(ids).ToDictionary(c => c.Hash, c => c.SoundName);
                customIds = customs.Keys.ToHashSet();
                Log.Line($"  .bak empty -> name-probe customs = {customs.Count}");
            }
        }
        else
        {
            customs = Naming.ScanCustomTracks(ids).ToDictionary(c => c.Hash, c => c.SoundName);
            customIds = customs.Keys.ToHashSet();
            Log.Line($"  no .bak -> name-probe customs = {customs.Count}");
        }

        var meta = new Dictionary<ulong, (string Sn, string? Dn, string? Ar)>();
        var enabled = new HashSet<ulong>();

        if (radio is not null)
        {
            foreach (var s in radio.Document.Descendants("Sample"))
            {
                if ((string?)s.Attribute("SoundName") is { } sn)
                {
                    meta.TryAdd(Lookup.SoundNameToId(sn),
                        (sn, (string?)s.Attribute("DisplayName"), (string?)s.Attribute("Artist")));
                }
            }

            var freeRoam = radio.Document.Descendants("RadioStation")
                .FirstOrDefault(st => (int?)st.Attribute("Number") == stationNumber)
                ?.Elements("PlayList")
                .FirstOrDefault(pl => (string?)pl.Attribute("Type") == "FreeRoam");

            if (freeRoam is not null)
            {
                foreach (var name in freeRoam.Elements("Entry").Select(e => (string?)e.Attribute("Name")))
                {
                    if (name is not null)
                    {
                        enabled.Add(Lookup.SoundNameToId(name));
                    }
                }
            }

            Log.Line($"  XML: meta entries = {meta.Count}, FreeRoam@#{stationNumber} = {enabled.Count}"
                     + (freeRoam is null ? " (FreeRoam NOT FOUND)" : ""));
        }
        else
        {
            Log.Line("  XML: radio is null (no metadata overlay)");
        }

        var inMeta = ids.Count(id => meta.ContainsKey(id));
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
            var isCustom = customIds.Contains(t.Id);
            var hasMeta = meta.TryGetValue(t.Id, out var m);

            var soundName = hasMeta ? m.Sn : customName ?? $"sound_{t.Index}";

            result.Add(new RadioTrack
            {
                SoundName = soundName,
                Origin = isCustom ? TrackOrigin.Custom : TrackOrigin.Original,
                Enabled = enabled.Contains(t.Id),
                DisplayName = hasMeta ? m.Dn ?? soundName : soundName,
                Artist = hasMeta ? m.Ar : null,
                SampleLength = t.Frames,
                SampleRate = t.SampleRate,
                SubIndex = hasAudio ? t.Index : -1,
            });
        }

        var named = result.Count(r => !r.SoundName.StartsWith("sound_"));
        Log.Line($"  -> {result.Count} tracks, {named} named, {result.Count(r => r.Enabled)} enabled");
        return result;
    }
}
