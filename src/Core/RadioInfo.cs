using System.Text.RegularExpressions;
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
    public bool Replaced { get; set; }
    public bool UsesMilliseconds { get; init; }
    public bool UsesTrackBreakDown { get; init; }
    public ulong? BankId { get; init; }
    public long BankBytes { get; set; }
    public IReadOnlyDictionary<string, long>? Markers { get; set; }

}

public sealed class RadioInfo
{
    private readonly XDocument _doc;
    public XDocument Document => _doc;

    private RadioInfo(XDocument doc) => _doc = doc;

    public static RadioInfo Load(string path) => new(Parse(File.ReadAllText(path)));

    private static readonly Regex RawAmpersand =
        new(@"<!--[\s\S]*?-->|<!\[CDATA\[[\s\S]*?\]\]>|<\?[\s\S]*?\?>|&(?!amp;|lt;|gt;|quot;|apos;|#\d+;|#x[0-9A-Fa-f]+;)", RegexOptions.Compiled);

    private static XDocument Parse(string xml) => ParseXml(xml, XDocument.Parse);

    internal static XElement ParseElement(string xml) => ParseXml(xml, XElement.Parse);

    private static T ParseXml<T>(string xml, Func<string, T> parse)
    {
        try
        {
            return parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            var repaired = RawAmpersand.Replace(xml, match => match.Value == "&" ? "&amp;" : match.Value);
            return parse(repaired);
        }
    }

    public const string XmlMarker = "FH6RB";

    public void Save(string path)
    {
        foreach (var station in _doc.Descendants("RadioStation"))
        {
            new RadioStationEditor(station).NormalizeDisabledEntries();
        }
        if (!_doc.Nodes().OfType<XComment>().Any(c => c.Value.Contains(XmlMarker)))
        {
            _doc.AddFirst(new XComment($" {XmlMarker} edited "));
        }

        Atomic.Write(path, fs => _doc.Save(fs));
    }

    public int MaxCustomSeq()
    {
        var max = -1;
        var names = _doc.Descendants("Sample").Select(s => (string?) s.Attribute("SoundName"))
            .Concat(_doc.Descendants("RadioStation").Select(st => new RadioStationEditor(st))
                .Where(st => st.UsesMilliseconds).SelectMany(st => st.CustomSoundNames()));
        foreach (var sn in names)
        {
            if (sn is null || !sn.StartsWith(Naming.CustomPrefix, StringComparison.Ordinal))
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
        var registered = (from st in _doc.Descendants("RadioStation") where st.Descendants("Bank")
            .Any(b => (string?) b.Attribute("Name") == bankName) select new RadioStationEditor(st))
            .FirstOrDefault();
        if (registered is not null)
        {
            return registered;
        }

        var match = Regex.Match(bankName, @"^HZ2_MX_Radio_(\d+)_Tracks$", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var number)
            ? StationByNumber(number)
            : null;
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
    private XElement Station => station;

    public bool UsesMilliseconds => station.Elements("Playlist")
        .Any(pl => (string?) pl.Attribute("Type") is "FreeroamTracks" or "EventTracks");

    public bool UsesTrackBreakDown => UsesMilliseconds || station.Elements("SampleList")
        .Where(sl => (string?) sl.Attribute("Type") == "Track")
        .SelectMany(sl => sl.Elements("Sample"))
        .Any(sample => sample.Attribute("TrackBreakDown") is not null && sample.Attribute("PostDrop") is null);

    private XElement TrackList => station.Elements("SampleList")
        .First(sl => (string?) sl.Attribute("Type") == "Track");

    private IEnumerable<XElement> MusicPlaylists => UsesMilliseconds
        ? station.Elements("Playlist").Where(pl => (string?) pl.Attribute("Type") is "FreeroamTracks" or "EventTracks")
        : station.Elements("PlayList").Where(pl => (string?) pl.Attribute("Type") is "FreeRoam" or "Event");

    private XElement? FreeRoamPlaylist => MusicPlaylists.FirstOrDefault(pl =>
        (string?) pl.Attribute("Type") == (UsesMilliseconds ? "FreeroamTracks" : "FreeRoam"));

    private IEnumerable<(XElement Element, XComment? Comment)> TrackNodes()
    {
        if (!UsesMilliseconds)
        {
            foreach (var sample in TrackList.Elements("Sample"))
            {
                yield return (sample, null);
            }
            yield break;
        }

        foreach (var playlist in MusicPlaylists)
        {
            foreach (var node in playlist.Nodes())
            {
                if (node is XElement { Name.LocalName: "Entry" } entry)
                {
                    yield return (entry, null);
                }
                else if (node is XComment comment && ReadDisabledEntry(comment) is { } disabled
                         && disabled.Attribute("SoundName") is not null)
                {
                    yield return (disabled, comment);
                }
            }
        }
    }

    private static XElement? ReadDisabledEntry(XComment comment)
    {
        try
        {
            var entry = RadioInfo.ParseElement(comment.Value.Trim());
            return entry.Name == "Entry" && (entry.Attribute("SoundName") is not null || entry.Attribute("Name") is not null)
                ? entry : null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static string DisabledMarkup(XElement entry) =>
        entry.ToString(SaveOptions.DisableFormatting).Replace("--", "&#45;&#45;", StringComparison.Ordinal);

    internal void NormalizeDisabledEntries()
    {
        if (!UsesMilliseconds) return;
        foreach (var (element, comment) in TrackNodes())
        {
            if (comment is not null) comment.Value = DisabledMarkup(element);
        }
    }

    private IEnumerable<XElement> TrackSamples() => TrackNodes().Select(n => n.Element)
        .DistinctBy(s => (string?) s.Attribute("SoundName"));

    private XElement? FindSample(string soundName) => TrackSamples()
        .FirstOrDefault(s => (string?) s.Attribute("SoundName") == soundName);

    private bool EditSamples(string soundName, Action<XElement> edit)
    {
        var found = false;
        foreach (var (element, comment) in TrackNodes().Where(n => (string?) n.Element.Attribute("SoundName") == soundName).ToList())
        {
            edit(element);
            if (comment is not null)
            {
                comment.Value = DisabledMarkup(element);
            }
            found = true;
        }
        return found;
    }

    public void RegisterBank(string bankName)
    {
        if (UsesMilliseconds)
        {
            return;
        }

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

    public void UnregisterBank(string bankName)
    {
        if (UsesMilliseconds)
        {
            return;
        }

        var banks = station.Element("Banks");
        if (banks is null)
        {
            return;
        }

        foreach (var b in banks.Elements("Bank").Where(b => (string?) b.Attribute("Name") == bankName).ToList())
        {
            b.Remove();
        }

        if (!banks.Elements("Bank").Any())
        {
            banks.Remove();
        }
    }

    public IEnumerable<string> CustomSoundNames()
    {
        return TrackSamples()
            .Select(s => (string?) s.Attribute("SoundName"))
            .Where(sn => sn is not null && sn.StartsWith(Naming.CustomPrefix))
            .Select(sn => sn!);
    }
    
    public List<RadioTrack> ReadTracks()
    {
        var fr = FreeRoamPlaylist;
        var usesTrackBreakDown = UsesTrackBreakDown;
        
        var live = fr?.Elements("Entry")
            .Select(e => (string?)e.Attribute(UsesMilliseconds ? "SoundName" : "Name"))
            .ToHashSet() ?? [];

        var result = new List<RadioTrack>();
        
        foreach (var s in TrackSamples())
        {
            var sn = (string?) s.Attribute("SoundName");
            
            if (sn is null) continue;
            
            result.Add(new RadioTrack
            {
                SoundName = sn,
                Origin = sn.StartsWith(Naming.CustomPrefix) ? TrackOrigin.Custom : TrackOrigin.Original,
                Enabled = live.Contains(sn),
                DisplayName = (string?) s.Attribute("DisplayName"),
                Artist = (string?) s.Attribute("Artist"),
                SampleLength = (long?) s.Attribute("SampleLength") ?? 0,
                SampleRate = UsesMilliseconds ? 1000 : (int?) s.Attribute("SampleRate") ?? 0,
                Replaced = (string?) s.Attribute("Replaced") == "true",
                UsesMilliseconds = UsesMilliseconds,
                UsesTrackBreakDown = usesTrackBreakDown,
                Markers = ReadMarkers(s),
            });
        }
        return result;
    }

    public int FreeRoamActiveCount()
    {
        var fr = FreeRoamPlaylist;
        return fr is null ? 0 : fr.Elements("Entry").Count();
    }

    public void SetEnabled(string soundName, bool enabled)
    {
        if (!UsesMilliseconds)
        {
            if (FindSample(soundName) is null) return;
            if (FreeRoamPlaylist is { } freeRoam &&
                freeRoam.Elements("Entry").Any(e => (string?) e.Attribute("Name") == soundName) == enabled)
            {
                return;
            }
            var playlists = MusicPlaylists.ToList();
            var hasDisabled = playlists.Any(pl => pl.Nodes().OfType<XComment>()
                .Any(c => (string?) ReadDisabledEntry(c)?.Attribute("Name") == soundName));
            foreach (var playlist in playlists)
            {
                if (enabled) EnableIn(playlist, soundName, !hasDisabled);
                else DisableIn(playlist, soundName);
            }
            return;
        }
        var template = FindSample(soundName);
        foreach (var playlist in MusicPlaylists)
        {
            var entries = playlist.Elements("Entry").Where(e => (string?) e.Attribute("SoundName") == soundName).ToList();
            var disabled = playlist.Nodes().OfType<XComment>()
                .Where(c => (string?) ReadDisabledEntry(c)?.Attribute("SoundName") == soundName).ToList();
            if (enabled)
            {
                if (entries.Count == 0)
                {
                    if (disabled.Count > 0)
                    {
                        disabled[0].ReplaceWith(ReadDisabledEntry(disabled[0])!);
                        disabled.RemoveAt(0);
                    }
                    else if (template is not null && (ReferenceEquals(playlist, FreeRoamPlaylist)
                             || soundName.StartsWith(Naming.CustomPrefix, StringComparison.Ordinal)))
                    {
                        playlist.Add(new XElement(template));
                    }
                }
                disabled.Remove();
            }
            else
            {
                foreach (var entry in entries)
                {
                    entry.ReplaceWith(new XComment(DisabledMarkup(entry)));
                }
            }
        }
    }

    private static void DisableIn(XElement playlist, string name, bool preserveEntry = true)
    {
        foreach (var e in playlist.Elements("Entry")
                     .Where(e => (string?)e.Attribute("Name") == name).ToList())
        {
            if (preserveEntry) e.ReplaceWith(new XComment(DisabledMarkup(e)));
            else e.Remove();
        }
        if (preserveEntry) return;
        foreach (var c in playlist.Nodes().OfType<XComment>()
                     .Where(c => (string?) ReadDisabledEntry(c)?.Attribute("Name") == name
                                 || c.Value.Contains($"Name=\"{name}\"", StringComparison.Ordinal)).ToList())
        {
            c.Remove();
        }
    }

    private static void EnableIn(XElement playlist, string name, bool createIfMissing = true)
    {
        var active = playlist.Elements("Entry").Any(e => (string?) e.Attribute("Name") == name);
        foreach (var c in playlist.Nodes().OfType<XComment>()
                     .Where(c => (string?) ReadDisabledEntry(c)?.Attribute("Name") == name).ToList())
        {
            if (active) c.Remove();
            else c.ReplaceWith(ReadDisabledEntry(c)!);
        }
        if (createIfMissing && playlist.Elements("Entry").All(e => (string?) e.Attribute("Name") != name))
        {
            playlist.Add(new XElement("Entry", new XAttribute("Name", name)));
        }
    }

    public RadioTrack AddCustom(string soundName, long sampleLength, int sampleRate, string? displayName, string? artist)
    {
        if (!soundName.StartsWith(Naming.CustomPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"AddCustom expects a custom SoundName ('{Naming.CustomPrefix}...')");
        }
        if (!EditSamples(soundName, s => ConfigureCustom(s, soundName, sampleLength, sampleRate, displayName, artist, true)))
        {
            var template = TrackSamples().FirstOrDefault()
                ?? throw new InvalidOperationException("no template track to clone");
            var sample = new XElement(template);
            ConfigureCustom(sample, soundName, sampleLength, sampleRate, displayName ?? soundName, artist);
            if (UsesMilliseconds)
            {
                foreach (var playlist in MusicPlaylists)
                {
                    playlist.Add(new XElement(sample));
                }
            }
            else
            {
                TrackList.Add(sample);
            }
        }
        SetEnabled(soundName, true);
        return new RadioTrack
        {
            SoundName = soundName, Origin = TrackOrigin.Custom, Enabled = true,
            DisplayName = displayName ?? soundName, Artist = artist,
            SampleLength = sampleLength, SampleRate = sampleRate, UsesMilliseconds = UsesMilliseconds,
            UsesTrackBreakDown = UsesTrackBreakDown,
        };
    }

    private void ConfigureCustom(XElement sample, string soundName, long sampleLength, int sampleRate, string? displayName, string? artist, bool preserveArtist = false)
    {
        sample.SetAttributeValue("SoundName", soundName);
        sample.SetAttributeValue("SampleLength", XmlLength(sampleLength, sampleRate));
        sample.SetAttributeValue("SampleRate", UsesMilliseconds ? null : sampleRate);
        if (displayName is not null)
        {
            sample.SetAttributeValue("DisplayName", displayName);
        }

        if (artist is not null || !preserveArtist)
        {
            sample.SetAttributeValue("Artist", artist);
        }

        if (sample.Attribute("IsXCloudModeSafe") is not null)
        {
            sample.SetAttributeValue("IsXCloudModeSafe", "true");
        }
        sample.SetAttributeValue("Replaced", null);
        ApplyCustomMarkers(sample, sampleLength, UsesMilliseconds, sampleRate, UsesTrackBreakDown);
        sample.Elements("BPM").Remove();
    }

    public static readonly string[] MarkerNames =
    [
        "VeryStart", "TrackStart", "End",
        "DJDrop", "TrackDrop", "DJSegment", "PostDrop", "TrackBreakDown", "DJStart", "StingerStart",
        "TrackLoopStart", "TrackLoopEnd", "PostRaceLoopStart", "PostRaceLoopEnd",
        "Loop1Start", "Loop1End", "Loop2Start", "Loop2End", "Loop3Start", "Loop3End",
        "Loop4Start", "Loop4End", "Loop5Start", "Loop5End",
        "Section1", "Section2", "Section3", "Section4", "Section5",
        "BinkTransition",
    ];

    private static readonly HashSet<string> MillisecondMarkerNames =
        ["TrackStart", "DJDrop", "TrackDrop", "TrackBreakDown", "StingerStart", "DJStart", "End"];

    public static Dictionary<string, long> ComputeAutoMarkers(long sampleLength) =>
        ComputeAutoMarkers(sampleLength, 48000);

    public static Dictionary<string, long> ComputeAutoMarkers(long sampleLength, int sampleRate) =>
        MarkerDefaults.Compute(sampleLength, sampleRate);

    public static Dictionary<string, long> ComputeAutoMarkers(long sampleLength, int sampleRate, bool usesMilliseconds,
        bool usesTrackBreakDown = false)
    {
        var markers = ComputeAutoMarkers(sampleLength, sampleRate);
        if (usesMilliseconds || usesTrackBreakDown)
        {
            UseNativeBreakdown(markers);
        }
        if (!usesMilliseconds)
        {
            return markers;
        }

        if (markers.TryGetValue("DJStart", out var dj))
        {
            markers["StingerStart"] = Math.Max(0, dj - sampleRate);
        }
        var result = markers.Where(m => MillisecondMarkerNames.Contains(m.Key)).ToDictionary();
        foreach (var name in MillisecondMarkerNames)
        {
            result.TryAdd(name, -1);
        }

        return result;
    }

    public static void ApplyXmlMarkerRules(IDictionary<string, long> markers, long sampleLength)
    {
        if (markers.TryGetValue("DJStart", out var dj))
        {
            markers["StingerStart"] = Math.Max(0, dj - 1000);
        }

        if (markers.TryGetValue("PostDrop", out var pdForBreak))
        {
            markers["TrackBreakDown"] = pdForBreak;
        }

        if (sampleLength > 0)
        {
            var last = sampleLength - 1;

            foreach (var key in markers.Keys.ToList())
            {
                if (markers[key] > last)
                {
                    markers[key] = last;
                }
            }
        }
    }

    public static Dictionary<string, long> ReadMarkers(XElement sample)
    {
        var result = new Dictionary<string, long>();

        foreach (var mk in sample.Elements("Marker"))
        {
            var n = (string?) mk.Attribute("Name");
            if (n is not null && long.TryParse((string?) mk.Attribute("Position"), out var p))
            {
                result[n] = p;
            }
        }

        foreach (var name in MarkerNames)
        {
            if (!result.ContainsKey(name) && long.TryParse((string?) sample.Attribute(name), out var p))
            {
                result[name] = p;
            }
        }

        return result;
    }

    public bool SetMarkers(string soundName, IReadOnlyDictionary<string, long> markers, int sampleRate = 0)
    {
        var usesTrackBreakDown = UsesTrackBreakDown;
        return EditSamples(soundName, sample =>
        {
            var targetRate = UsesMilliseconds ? 1000 : (int?) sample.Attribute("SampleRate") ?? sampleRate;
            var positions = markers.ToDictionary(m => m.Key,
                m => sampleRate > 0 && targetRate > 0 ? ScalePosition(m.Value, sampleRate, targetRate) : m.Value);
            if (usesTrackBreakDown)
            {
                UseNativeBreakdown(positions);
            }
            var current = ReadMarkers(sample);
            if (positions.All(m => current.TryGetValue(m.Key, out var value) && value == m.Value))
            {
                return;
            }
            if (UsesMilliseconds)
            {
                positions = positions.Where(m => MillisecondMarkerNames.Contains(m.Key)).ToDictionary();
                ClampMarkers(positions, (long?) sample.Attribute("SampleLength") ?? 0);
            }
            else
            {
                ApplyXmlMarkerRules(positions, (long?) sample.Attribute("SampleLength") ?? 0);
            }
            if (usesTrackBreakDown)
            {
                NormalizeBreakdownSchema(sample, true);
            }
            WriteMarkers(sample, positions);
        });
    }

    private static void UseNativeBreakdown(IDictionary<string, long> markers)
    {
        if (!markers.ContainsKey("TrackBreakDown") && markers.TryGetValue("PostDrop", out var value))
        {
            markers["TrackBreakDown"] = value;
        }
        markers.Remove("PostDrop");
    }

    private static void NormalizeBreakdownSchema(XElement sample, bool usesTrackBreakDown)
    {
        var target = usesTrackBreakDown ? "TrackBreakDown" : "PostDrop";
        var source = usesTrackBreakDown ? "PostDrop" : "TrackBreakDown";
        if (sample.Attribute(target) is null && sample.Attribute(source) is { } attribute)
        {
            sample.SetAttributeValue(target, attribute.Value);
        }
        if (usesTrackBreakDown)
        {
            sample.SetAttributeValue(source, null);
        }
        foreach (var marker in sample.Elements("Marker").Where(m => (string?) m.Attribute("Name") == source).ToList())
        {
            if (!sample.Elements("Marker").Any(m => (string?) m.Attribute("Name") == target))
            {
                var copy = new XElement(marker);
                copy.SetAttributeValue("Name", target);
                sample.Add(copy);
            }
            if (usesTrackBreakDown)
            {
                marker.Remove();
            }
        }
    }

    private bool UsesTrackBreakDownForCopy(RadioStationEditor reference) => UsesTrackBreakDown ||
        !station.Elements("SampleList").Where(sl => (string?) sl.Attribute("Type") == "Track")
            .SelectMany(sl => sl.Elements("Sample")).Any() && reference.UsesTrackBreakDown;

    private static void ClampMarkers(IDictionary<string, long> markers, long length)
    {
        if (length <= 0)
        {
            return;
        }

        foreach (var name in markers.Keys.ToList())
        {
            if (markers[name] > length)
            {
                markers[name] = length;
            }
        }
    }

    private static void WriteMarkers(XElement sample, IReadOnlyDictionary<string, long> markers)
    {
        var markerElements = sample.Elements("Marker").ToList();
        if (markerElements.Count == 0)
        {
            foreach (var (name, position) in markers)
            {
                sample.SetAttributeValue(name, position);
            }

            return;
        }
        foreach (var (name, position) in markers)
        {
            var existing = markerElements.FirstOrDefault(m => (string?) m.Attribute("Name") == name);
            if (existing is not null)
            {
                existing.SetAttributeValue("Position", position);
            }
            else
            {
                sample.Add(new XElement("Marker", new XAttribute("Name", name), new XAttribute("Position", position)));
            }
        }
    }

    public IReadOnlyDictionary<string, long>? GetMarkers(string soundName, int sampleRate = 0)
    {
        var sample = FindSample(soundName);
        if (sample is null) return null;
        var markers = ReadMarkers(sample);
        var sourceRate = UsesMilliseconds ? 1000 : (int?) sample.Attribute("SampleRate") ?? sampleRate;
        return sampleRate > 0 && sourceRate > 0 && sourceRate != sampleRate
            ? markers.ToDictionary(m => m.Key, m => ScalePosition(m.Value, sourceRate, sampleRate))
            : markers;
    }

    public bool ResetMarkersAuto(string soundName, long sampleLength, int sampleRate = 0)
    {
        return EditSamples(soundName, sample => ApplyCustomMarkers(sample, sampleLength, UsesMilliseconds, sampleRate, UsesTrackBreakDown));
    }

    public bool ApplyReplacement(string soundName, long sampleLength, int sampleRate)
    {
        return EditSamples(soundName, sample =>
        {
            var xmlLength = XmlLength(sampleLength, sampleRate);
            sample.SetAttributeValue("SampleLength", xmlLength);
            sample.SetAttributeValue("SampleRate", UsesMilliseconds ? null : sampleRate);
            sample.SetAttributeValue("Replaced", "true");
            ApplyCustomMarkers(sample, sampleLength, UsesMilliseconds, sampleRate, UsesTrackBreakDown);
            sample.Elements("BPM").Remove();
        });
    }

    public bool ClearReplacement(string soundName)
    {
        return EditSamples(soundName, s => s.SetAttributeValue("Replaced", null));
    }

    public bool SetSampleMeta(string soundName, string? displayName, string? artist)
    {
        return EditSamples(soundName, sample =>
        {
            if (displayName is not null)
            {
                sample.SetAttributeValue("DisplayName", displayName);
            }

            if (artist is not null)
            {
                sample.SetAttributeValue("Artist", artist);
            }
        });
    }

    public (string? DisplayName, string? Artist)? GetSampleMeta(string soundName)
    {
        var s = FindSample(soundName);
        if (s is null) return null;
        return ((string?) s.Attribute("DisplayName"), (string?) s.Attribute("Artist"));
    }

    private long XmlLength(long sampleLength, int sampleRate)
    {
        if (sampleLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleLength));
        }

        return UsesMilliseconds && sampleRate > 0 ? ScalePosition(sampleLength, sampleRate, 1000) : sampleLength;
    }

    private static long ScalePosition(long position, int sourceRate, int destinationRate) =>
        position < 0 ? position : checked((long) Math.Round(position * (decimal) destinationRate / sourceRate, MidpointRounding.AwayFromZero));

    private static void ApplyCustomMarkers(XElement sample, long sampleLength, bool usesMilliseconds, int sampleRate = 0,
        bool usesTrackBreakDown = false)
    {
        if (usesMilliseconds)
        {
            var markerRate = sampleRate > 0 ? sampleRate : 1000;
            var positions = ComputeAutoMarkers(sampleLength, markerRate, true)
                .ToDictionary(m => m.Key, m => ScalePosition(m.Value, markerRate, 1000));
            var xmlLength = (long?) sample.Attribute("SampleLength") ?? ScalePosition(sampleLength, markerRate, 1000);
            ClampMarkers(positions, xmlLength);

            foreach (var marker in MarkerNames.Where(n => !MillisecondMarkerNames.Contains(n)))
            {
                sample.SetAttributeValue(marker, null);
            }

            WriteMarkers(sample, positions);
            return;
        }

        var rate = int.TryParse((string?) sample.Attribute("SampleRate"), out var r) && r > 0 ? r : 48000;
        var pos = ComputeAutoMarkers(sampleLength, rate, false, usesTrackBreakDown);
        ApplyXmlMarkerRules(pos, sampleLength);
        if (usesTrackBreakDown)
        {
            NormalizeBreakdownSchema(sample, true);
        }
        var markerElems = sample.Elements("Marker").ToList();

        if (markerElems.Count > 0)
        {
            var existing = new HashSet<string>();

            foreach (var mk in markerElems)
            {
                var n = (string?) mk.Attribute("Name");

                if (n is not null && pos.TryGetValue(n, out var p))
                {
                    mk.SetAttributeValue("Position", p);
                }
                else
                {
                    mk.SetAttributeValue("Position", -1);
                }

                if (n is not null) existing.Add(n);
            }

            foreach (var (name, p) in pos)
            {
                if (!existing.Contains(name))
                {
                    sample.Add(new XElement("Marker", new XAttribute("Name", name), new XAttribute("Position", p)));
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
        if (UsesMilliseconds)
        {
            return (int?) station.Attribute("Number") is { } number
                ? [$"HZ2_MX_Radio_{number:00}_Tracks"]
                : [];
        }

        return station.Element("Banks")?
                   .Elements("Bank")
                   .Select(b => (string?) b.Attribute("Name"))
                   .Where(n => n is not null && System.Text.RegularExpressions.Regex.IsMatch(n, @"^R\d+_Tracks(_|$)"))
                   .Select(n => n!) ?? [];
    }

    public int ReconcileTracks(IReadOnlySet<ulong> bankIds, Func<string, ulong> hash, Action<string>? log = null)
    {
        var removed = 0;

        foreach (var s in TrackSamples().ToList())
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
        foreach (var (element, comment) in TrackNodes().Where(n => (string?) n.Element.Attribute("SoundName") == soundName).ToList())
        {
            if (comment is not null)
            {
                comment.Remove();
            }
            else
            {
                element.Remove();
            }
        }

        if (UsesMilliseconds)
        {
            return;
        }

        foreach (var playlist in MusicPlaylists)
        {
            DisableIn(playlist, soundName, preserveEntry: false);
        }
    }

    private bool IsCustomEnabled(string soundName)
    {
        var fr = FreeRoamPlaylist;
        return fr?.Elements("Entry").Any(e => (string?) e.Attribute(UsesMilliseconds ? "SoundName" : "Name") == soundName) == true;
    }

    public bool RestoreTrackFrom(RadioStationEditor reference, string soundName)
    {
        var sample = reference.FindSample(soundName);
        if (sample is null)
        {
            return false;
        }

        if (ReferenceEquals(station, reference.Station))
        {
            return true;
        }

        if (UsesMilliseconds != reference.UsesMilliseconds
            && (UsesMilliseconds || station.Elements("SampleList").Any(sl => (string?) sl.Attribute("Type") == "Track")))
        {
            throw new InvalidOperationException("Cannot restore a track from a different radio XML format.");
        }

        var sourcePlaylists = reference.MusicPlaylists.Select(p => new XElement(p)).ToList();
        var usesTrackBreakDown = UsesTrackBreakDownForCopy(reference);
        if (UsesMilliseconds || station.Elements("SampleList").Any(sl => (string?) sl.Attribute("Type") == "Track"))
        {
            PurgeSoundName(soundName);
        }

        if (!reference.UsesMilliseconds)
        {
            var trackList = station.Elements("SampleList").FirstOrDefault(sl => (string?) sl.Attribute("Type") == "Track");
            if (trackList is null)
            {
                trackList = new XElement(reference.TrackList.Name, reference.TrackList.Attributes());
                station.Add(trackList);
            }

            var restored = new XElement(sample);
            NormalizeBreakdownSchema(restored, usesTrackBreakDown);
            trackList.Add(restored);
        }

        foreach (var source in sourcePlaylists)
        {
            var nodes = source.Nodes().Where(node => node switch
            {
                XElement entry => (string?) entry.Attribute(reference.UsesMilliseconds ? "SoundName" : "Name") == soundName,
                XComment comment => reference.UsesMilliseconds
                    ? (string?) ReadDisabledEntry(comment)?.Attribute("SoundName") == soundName
                    : (string?) ReadDisabledEntry(comment)?.Attribute("Name") == soundName
                      || comment.Value.Contains($"Name=\"{soundName}\"", StringComparison.Ordinal),
                _ => false,
            }).ToList();
            if (nodes.Count == 0)
            {
                continue;
            }

            var playlist = station.Elements(source.Name)
                .FirstOrDefault(p => (string?) p.Attribute("Type") == (string?) source.Attribute("Type"));
            if (playlist is null)
            {
                playlist = new XElement(source.Name, source.Attributes());
                station.Add(playlist);
            }

            foreach (var node in nodes)
            {
                if (node is XElement entry)
                {
                    playlist.Add(new XElement(entry));
                }
                else if (node is XComment comment)
                {
                    playlist.Add(new XComment(comment.Value));
                }
            }
        }

        return true;
    }

    private static List<XNode> ModernPlaylistNodes(XElement playlist, string soundName) => playlist.Nodes()
        .Where(node => node switch
        {
            XElement entry => (string?) entry.Attribute("Name") == soundName,
            XComment comment => (string?) ReadDisabledEntry(comment)?.Attribute("Name") == soundName,
            _ => false,
        }).ToList();

    private static XNode CopyPlaylistNode(XNode node) => node switch
    {
        XElement entry => new XElement(entry),
        XComment comment => new XComment(comment.Value),
        _ => throw new InvalidOperationException("Unsupported playlist node."),
    };

    private static void SyncModernPlaylist(XElement target, XElement? source, string soundName)
    {
        var current = ModernPlaylistNodes(target, soundName);
        List<XNode> expected = source is not null ? ModernPlaylistNodes(source, soundName) : [];
        if (current.Count == expected.Count && current.Zip(expected).All(pair => XNode.DeepEquals(pair.First, pair.Second)))
        {
            return;
        }
        var copies = expected.Select(CopyPlaylistNode).ToArray();
        if (current.Count > 0)
        {
            current[0].AddBeforeSelf(copies);
            foreach (var node in current) node.Remove();
        }
        else
        {
            target.Add(copies);
        }
    }

    private void SyncModernCustomFrom(RadioStationEditor reference, XElement sample, bool usesTrackBreakDown)
    {
        var soundName = (string) sample.Attribute("SoundName")!;
        var copy = new XElement(sample);
        NormalizeBreakdownSchema(copy, usesTrackBreakDown);
        var existing = TrackList.Elements("Sample")
            .Where(s => (string?) s.Attribute("SoundName") == soundName).ToList();
        if (existing.Count > 0)
        {
            if (!XNode.DeepEquals(existing[0], copy)) existing[0].ReplaceWith(copy);
            foreach (var duplicate in existing.Skip(1)) duplicate.Remove();
        }
        else
        {
            TrackList.Add(copy);
        }

        var targets = MusicPlaylists.ToList();
        var sources = reference.MusicPlaylists.ToList();
        foreach (var source in sources)
        {
            var target = targets.FirstOrDefault(p => (string?) p.Attribute("Type") == (string?) source.Attribute("Type"));
            if (target is null && ModernPlaylistNodes(source, soundName).Count > 0)
            {
                target = new XElement(source.Name, source.Attributes());
                station.Add(target);
                targets.Add(target);
            }
            if (target is not null) SyncModernPlaylist(target, source, soundName);
        }
        foreach (var target in targets.Where(t => sources.All(s => (string?) s.Attribute("Type") != (string?) t.Attribute("Type"))))
        {
            SyncModernPlaylist(target, null, soundName);
        }
    }

    public void SyncCustomsFrom(RadioStationEditor reference)
    {
        if (ReferenceEquals(station, reference.Station))
        {
            return;
        }
        var referenceSamples = reference.TrackSamples()
            .Where(s => ((string?) s.Attribute("SoundName"))?.StartsWith(Naming.CustomPrefix, StringComparison.Ordinal) == true).ToList();
        var referenceNames = referenceSamples.Select(s => (string) s.Attribute("SoundName")!).ToHashSet(StringComparer.Ordinal);
        var usesTrackBreakDown = UsesTrackBreakDownForCopy(reference);
        foreach (var soundName in CustomSoundNames().Where(sn => !referenceNames.Contains(sn)).ToList())
        {
            PurgeSoundName(soundName);
        }
        foreach (var sample in referenceSamples)
        {
            var soundName = (string) sample.Attribute("SoundName")!;
            if (!UsesMilliseconds && !reference.UsesMilliseconds)
            {
                SyncModernCustomFrom(reference, sample, usesTrackBreakDown);
                continue;
            }
            PurgeSoundName(soundName);
            if (UsesMilliseconds && reference.UsesMilliseconds)
            {
                foreach (var playlist in MusicPlaylists)
                {
                    var source = reference.MusicPlaylists.FirstOrDefault(p => (string?) p.Attribute("Type") == (string?) playlist.Attribute("Type"));
                    if (source is null)
                    {
                        continue;
                    }

                    foreach (var node in source.Nodes())
                    {
                        if (node is XElement entry && (string?) entry.Attribute("SoundName") == soundName)
                        {
                            playlist.Add(new XElement(entry));
                        }
                        else if (node is XComment comment && (string?) ReadDisabledEntry(comment)?.Attribute("SoundName") == soundName)
                        {
                            playlist.Add(new XComment(comment.Value));
                        }
                    }
                }
            }
            else
            {
                var copy = new XElement(sample);
                NormalizeBreakdownSchema(copy, usesTrackBreakDown);
                TrackList.Add(copy);
                SetEnabled(soundName, reference.IsCustomEnabled(soundName));
            }
        }
    }
}
