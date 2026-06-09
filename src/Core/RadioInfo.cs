using System.Xml.Linq;

namespace FH6RB.Core;

public enum TrackOrigin { Original, Custom }

public sealed class RadioTrack
{
    public required string SoundName { get; init; }
    public required TrackOrigin Origin { get; init; }
    public bool Enabled { get; set; }
    public string? DisplayName { get; set; }
    public string? Artist { get; set; }
    public long SampleLength { get; set; }
    public int SampleRate { get; set; }
    public int SubIndex { get; set; } = -1;

    public bool CanDisable => true;
    public bool CanEditInfo => true;
    public bool CanDelete => Origin == TrackOrigin.Custom;
}

public sealed class RadioInfo
{
    private readonly XDocument _doc;
    public XDocument Document => _doc;

    private RadioInfo(XDocument doc) => _doc = doc;

    public static RadioInfo Load(string path) => new(XDocument.Load(path));

    public void Save(string path) => _doc.Save(path);

    public int MaxCustomSeq()
    {
        var max = -1;
        foreach (var s in _doc.Descendants("Sample"))
        {
            var sn = (string?) s.Attribute("SoundName");
            if (sn is null || !sn.StartsWith(Naming.CustomPrefix))
            {
                continue;
            }

            if (int.TryParse(sn[Naming.CustomPrefix.Length..], out var n) && n > max)
            {
                max = n;
            }
        }

        return max;
    }
    
    public RadioStationEditor? StationForBank(string bankName)
    {
        return (from st in _doc.Descendants("RadioStation") where st.Descendants("Bank")
            .Any(b => (string?) b.Attribute("Name") == bankName) select new RadioStationEditor(st))
            .FirstOrDefault();
    }

    public RadioStationEditor? StationByNumber(int number)
    {
        return _doc.Descendants("RadioStation")
            .Where(st => (int?) st.Attribute("Number") == number)
            .Select(st => new RadioStationEditor(st))
            .FirstOrDefault();
    }
}

public sealed class RadioStationEditor(XElement station)
{
    private XElement TrackList =>
        station.Elements("SampleList").First(sl => (string?) sl.Attribute("Type") == "Track");
    
    private IEnumerable<XElement> MusicPlaylists => station.Elements("PlayList")
        .Where(pl => (string?) pl.Attribute("Type") is "FreeRoam" or "Event");

    public void RegisterBank(string bankName)
    {
        var banks = station.Element("Banks");
        if (banks is null)
        {
            banks = new XElement("Banks");
            station.AddFirst(banks);
        }

        if (!banks.Elements("Bank").Any(b => (string?) b.Attribute("Name") == bankName))
        {
            banks.Add(new XElement("Bank", new XAttribute("Name", bankName)));
        }
    }

    public IEnumerable<string> CustomSoundNames()
    {
        return TrackList.Elements("Sample")
            .Select(s => (string?) s.Attribute("SoundName"))
            .Where(sn => sn is not null && sn.StartsWith(Naming.CustomPrefix))
            .Select(sn => sn!);
    }
    
    public List<RadioTrack> ReadTracks()
    {
        var fr = MusicPlaylists.First(pl => (string?) pl.Attribute("Type") == "FreeRoam");
        
        var live = fr.Elements("Entry")
            .Select(e => (string?)e.Attribute("Name"))
            .ToHashSet();

        var result = new List<RadioTrack>();
        foreach (var s in TrackList.Elements("Sample"))
        {
            var sn = (string?) s.Attribute("SoundName");
            
            if (sn is null) continue;
            
            result.Add(new RadioTrack
            {
                SoundName    = sn,
                Origin       = sn.StartsWith(Naming.CustomPrefix) ? TrackOrigin.Custom : TrackOrigin.Original,
                Enabled      = live.Contains(sn),
                DisplayName  = (string?) s.Attribute("DisplayName"),
                Artist       = (string?) s.Attribute("Artist"),
                SampleLength = (long?) s.Attribute("SampleLength") ?? 0,
                SampleRate   = (int?) s.Attribute("SampleRate") ?? 0,
            });
        }
        return result;
    }
    
    public void SetEnabled(string soundName, bool enabled)
    {
        foreach (var pl in MusicPlaylists)
        {
            if (enabled)
            { 
                EnableIn(pl, soundName);
            }
            else
            {
                DisableIn(pl, soundName);
            }
        }
    }

    private static void DisableIn(XElement playlist, string name)
    {
        foreach (var e in playlist.Elements("Entry")
                     .Where(e => (string?)e.Attribute("Name") == name).ToList())
        {
            e.AddBeforeSelf(new XComment(e.ToString(SaveOptions.DisableFormatting)));
            e.Remove();
        }
    }

    private static void EnableIn(XElement playlist, string name)
    {
        foreach (var c in playlist.Nodes().OfType<XComment>().ToList())
        {
            if (!c.Value.Contains($"Name=\"{name}\"")) continue;
            XElement parsed;
            try { parsed = XElement.Parse(c.Value.Trim()); } catch { continue; }
            if (parsed.Name != "Entry" || (string?)parsed.Attribute("Name") != name) continue;
            c.AddBeforeSelf(parsed);
            c.Remove();
            return;
        }
        
        if (playlist.Elements("Entry").All(e => (string?) e.Attribute("Name") != name))
        {
            playlist.Add(new XElement("Entry", new XAttribute("Name", name)));
        }
    }
    
    public void EditInfo(string soundName, string? displayName, string? artist)
    {
        var s = FindSample(soundName) ?? throw new InvalidOperationException($"no Sample def for {soundName}");
        if (displayName is not null) s.SetAttributeValue("DisplayName", displayName);
        if (artist is not null)      s.SetAttributeValue("Artist", artist);
    }
    
    public RadioTrack AddCustom(string soundName, long sampleLength, int sampleRate,
                                string? displayName, string? artist)
    {
        if (!soundName.StartsWith(Naming.CustomPrefix))
        {
            throw new InvalidOperationException($"AddCustom expects a custom SoundName ('{Naming.CustomPrefix}...')");
        }
        
        if (FindSample(soundName) is not null)
        {
            throw new InvalidOperationException($"{soundName} already exists");
        }
        
        var template = TrackList.Elements("Sample").FirstOrDefault() 
                       ?? throw new InvalidOperationException("no template Sample to clone");
        var ns = new XElement(template);
        
        ns.SetAttributeValue("SoundName", soundName);
        ns.SetAttributeValue("SampleLength", sampleLength);
        ns.SetAttributeValue("SampleRate", sampleRate);
        ns.SetAttributeValue("DisplayName", displayName ?? soundName);
        ns.SetAttributeValue("Artist", artist);
        if (template.Attribute("IsXCloudModeSafe") is not null)
        {
            ns.SetAttributeValue("IsXCloudModeSafe", "true");
        }

        ApplyCustomMarkers(ns, sampleLength);
        ns.Elements("BPM").Remove();

        TrackList.Add(ns);
        foreach (var pl in MusicPlaylists) pl.Add(new XElement("Entry", new XAttribute("Name", soundName)));

        return new RadioTrack
        {
            SoundName = soundName, Origin = TrackOrigin.Custom, Enabled = true,
            DisplayName = displayName ?? soundName, Artist = artist,
            SampleLength = sampleLength, SampleRate = sampleRate,
        };
    }

    private static void ApplyCustomMarkers(XElement sample, long sampleLength)
    {
        var e = sampleLength - 1;
        var stinger = Math.Max(0, e - 96000);
        var djStart = Math.Min(e, stinger + 1000);
        var loopEnd = Math.Max(0, stinger - 1);
        var djSeg = sampleLength / 2;

        var pos = new Dictionary<string, long>
        {
            ["TrackStart"] = 0,
            ["DJDrop"] = 0,
            ["TrackDrop"] = 0,
            ["TrackLoopStart"] = 0,
            ["TrackLoopEnd"] = loopEnd,
            ["DJSegment"] = djSeg,
            ["PostDrop"] = loopEnd,
            ["TrackBreakDown"] = loopEnd,
            ["PostRaceLoopStart"] = 0,
            ["PostRaceLoopEnd"] = loopEnd,
            ["StingerStart"] = stinger,
            ["DJStart"] = djStart,
            ["End"] = e,
        };

        var markerElems = sample.Elements("Marker").ToList();
        if (markerElems.Count > 0)
        {
            foreach (var mk in markerElems)
            {
                var n = (string?) mk.Attribute("Name");
                if (n is not null && pos.TryGetValue(n, out var p))
                {
                    mk.SetAttributeValue("Position", p);
                }
                else
                {
                    // непрофильные маркеры (VeryStart/Loop*/Section*/BinkTransition) выключаем:
                    // у клонированного шаблона они несут позиции чужого трека, возможно больше длины кастома.
                    mk.SetAttributeValue("Position", -1);
                }
            }
        }
        else
        {
            foreach (var (name, p) in pos)
            {
                if (sample.Attribute(name) is not null)
                {
                    sample.SetAttributeValue(name, p);
                }
            }
        }
    }

    public int FixCustomMarkers()
    {
        var fixedCount = 0;
        foreach (var s in TrackList.Elements("Sample"))
        {
            var sn = (string?) s.Attribute("SoundName");
            if (sn is null || !sn.StartsWith(Naming.CustomPrefix))
            {
                continue;
            }

            if (long.TryParse((string?) s.Attribute("SampleLength"), out var len) && len > 0)
            {
                ApplyCustomMarkers(s, len);
                fixedCount++;
            }
        }

        return fixedCount;
    }
    
    public void RemoveCustom(string soundName)
    {
        if (!soundName.StartsWith(Naming.CustomPrefix))
        {
            throw new InvalidOperationException("refusing to remove a non-custom (original) track");
        }

        PurgeSoundName(soundName);
    }

    public IEnumerable<string> TrackBankNames()
    {
        return station.Element("Banks")?.Elements("Bank")
                   .Select(b => (string?) b.Attribute("Name"))
                   .Where(n => n is not null && System.Text.RegularExpressions.Regex.IsMatch(n, @"^R\d+_Tracks(_|$)"))
                   .Select(n => n!)
               ?? [];
    }

    public int ReconcileTracks(IReadOnlySet<ulong> bankIds, Func<string, ulong> hash, Action<string>? log = null)
    {
        var removed = 0;

        foreach (var s in TrackList.Elements("Sample").ToList())
        {
            var sn = (string?) s.Attribute("SoundName");
            if (sn is null || bankIds.Contains(hash(sn)))
            {
                continue;
            }

            PurgeSoundName(sn);
            removed++;
            log?.Invoke($"reconcile: removed dangling '{sn}'");
        }

        return removed;
    }

    private void PurgeSoundName(string soundName)
    {
        FindSample(soundName)?.Remove();

        foreach (var pl in MusicPlaylists)
        {
            pl.Elements("Entry").Where(e => (string?)e.Attribute("Name") == soundName).Remove();

            foreach (var c in pl.Nodes().OfType<XComment>().Where(c => c.Value.Contains($"Name=\"{soundName}\"")).ToList())
            {
                c.Remove();
            }
        }
    }

    private XElement? FindSample(string soundName) => TrackList.Elements("Sample").FirstOrDefault(s => (string?) s.Attribute("SoundName") == soundName);
}
