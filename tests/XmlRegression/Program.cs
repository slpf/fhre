using System.Xml.Linq;
using FH6RB.Core;

var root = Path.Combine(AppContext.BaseDirectory, "scratch", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var failed = new List<string>();
var passed = 0;
try
{
    foreach (var horizon2 in new[] { true, false })
    {
        Run($"metadata roundtrip horizon2={horizon2}", () => TestMetadata(root, horizon2));
        Run($"localization and backup horizon2={horizon2}", () => TestCopies(root, horizon2));
        Run($"raw active ampersand horizon2={horizon2}", () => TestRawActive(root, horizon2));
    }
    Run("raw disabled FH2 entry", () => TestRawDisabled(root));
    Run("valid comment preservation", () => TestLiteralXml(root, false, false));
    Run("valid CDATA preservation", () => TestLiteralXml(root, false, true));
    Run("comment preservation while repairing active metadata", () => TestLiteralXml(root, true, false));
    Run("CDATA preservation while repairing active metadata", () => TestLiteralXml(root, true, true));
    Run("processing instruction preservation", () => TestProcessingInstruction(root));
    Run("direct disabled station fragment", TestDirectDisabledFragment);
    Run("malformed backup station fragment", TestBackupFragment);
    foreach (var failure in failed) Console.Error.WriteLine(failure);
    Console.WriteLine($"XML regression checks: {passed} passed, {failed.Count} failed.");
    return failed.Count == 0 ? 0 : 1;
}
finally
{
    var scratch = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "scratch")) + Path.DirectorySeparatorChar;
    var target = Path.GetFullPath(root);
    if (!target.StartsWith(scratch, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("The fixture directory lies outside its scratch root.");
    Directory.Delete(target, recursive: true);
}

void Run(string name, Action action)
{
    try
    {
        action();
        passed++;
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failed.Add($"FAIL {name}: {ex}");
    }
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static string Fixture(string root, string name, bool horizon2)
{
    var path = Path.Combine(root, name + (horizon2 ? "-fh2.xml" : "-modern.xml"));
    File.WriteAllText(path, horizon2 ? Horizon2Xml() : ModernXml());
    return path;
}

static void TestMetadata(string root, bool horizon2)
{
    var cases = new (string Title, string Artist)[]
    {
        ("A & B", "Singer & Producer"),
        ("Quote \" ' <mix> & Лето -- café 🎵", "X < Y & Z \"N\" 'Q' -- 和音"),
        ("Literal &amp; &lt; &#38; &#x26;", "Artist &quot; literal"),
        ("Trailing dash-", "First--Second"),
    };
    for (var index = 0; index < cases.Length; index++)
    {
        var data = cases[index];
        var path = Fixture(root, $"metadata-{index}", horizon2);
        var custom = Naming.MakeSoundName(7);
        var radio = RadioInfo.Load(path);
        var station = radio.StationByNumber(1)!;
        station.AddCustom(custom, 480000, 48000, data.Title, data.Artist);
        Check(station.ApplyReplacement("R_TEST", 480000, 48000), "Original track was not replaced.");
        station.SetSampleMeta("R_TEST", data.Title, data.Artist);
        radio.Save(path);
        CheckMetadata(path, custom, data.Title, data.Artist, horizon2, true);
        CheckMetadata(path, "R_TEST", data.Title, data.Artist, horizon2, true);
        if (index == 0)
        {
            var text = File.ReadAllText(path);
            Check(text.Contains("DisplayName=\"A &amp; B\"", StringComparison.Ordinal), "An ordinary ampersand was not escaped once on disk.");
            Check(!text.Contains("A &amp;amp; B", StringComparison.Ordinal), "An ordinary ampersand was double escaped.");
        }
        if (index == 2)
            Check(File.ReadAllText(path).Contains("Literal &amp;amp;", StringComparison.Ordinal), "An intentionally literal entity was decoded before saving.");

        for (var cycle = 0; cycle < 3; cycle++)
        {
            radio = RadioInfo.Load(path);
            station = radio.StationByNumber(1)!;
            Check(station.GetSampleMeta(custom)?.DisplayName == data.Title && station.GetSampleMeta(custom)?.Artist == data.Artist, "Metadata changed while loading active XML.");
            station.SetEnabled(custom, false);
            station.SetEnabled("R_TEST", false);
            radio.Save(path);
            CheckMetadata(path, custom, data.Title, data.Artist, horizon2, false);
            CheckMetadata(path, "R_TEST", data.Title, data.Artist, horizon2, false);
            radio = RadioInfo.Load(path);
            station = radio.StationByNumber(1)!;
            Check(station.ReadTracks().Count == 2 && station.ReadTracks().All(t => !t.Enabled), "A disabled track was lost or reenabled on reload.");
            station.SetSampleMeta(custom, data.Title + " & edited", data.Artist + " -- edited");
            radio.Save(path);
            CheckMetadata(path, custom, data.Title + " & edited", data.Artist + " -- edited", horizon2, false);
            station.SetSampleMeta(custom, data.Title, data.Artist);
            station.SetEnabled(custom, true);
            station.SetEnabled("R_TEST", true);
            radio.Save(path);
            CheckMetadata(path, custom, data.Title, data.Artist, horizon2, true);
            CheckMetadata(path, "R_TEST", data.Title, data.Artist, horizon2, true);
        }
    }
}

static void CheckMetadata(string path, string sound, string title, string artist, bool horizon2, bool enabled)
{
    var document = XDocument.Load(path);
    var station = document.Descendants("RadioStation").Single();
    List<XElement> samples;
    if (horizon2)
    {
        samples = station.Elements("Playlist").Where(p => (string?) p.Attribute("Type") is "FreeroamTracks" or "EventTracks")
            .SelectMany(p => p.Nodes()).Select(n => n switch
            {
                XElement entry when enabled => entry,
                XComment comment when !enabled => XElement.Parse(comment.Value.Trim()),
                _ => null,
            }).Where(e => (string?) e?.Attribute("SoundName") == sound).Cast<XElement>().ToList();
        Check(samples.Count == 2, "FH2 metadata is missing from one playlist or has the wrong enabled state.");
    }
    else
    {
        samples = station.Elements("SampleList").SelectMany(p => p.Elements("Sample"))
            .Where(s => (string?) s.Attribute("SoundName") == sound).ToList();
        Check(samples.Count == 1, "Modern sample metadata was lost or duplicated.");
        var entries = station.Elements("PlayList").SelectMany(p => p.Elements("Entry"))
            .Count(e => (string?) e.Attribute("Name") == sound);
        Check(entries == (enabled ? 2 : 0), "Modern playlist enabled state changed.");
    }
    Check(samples.All(s => (string?) s.Attribute("DisplayName") == title && (string?) s.Attribute("Artist") == artist), "Strict XML parsing did not recover the original title and artist.");
}

static void TestCopies(string root, bool horizon2)
{
    const string title = "A&B <live> -- \"version\"";
    const string artist = "Певец & Singer &amp; literal";
    var sourcePath = Fixture(root, "reference", horizon2);
    var localizedPath = Fixture(root, "localized", horizon2);
    var backupPath = Fixture(root, "backup", horizon2);
    var source = RadioInfo.Load(sourcePath);
    var reference = source.StationByNumber(1)!;
    var custom = Naming.MakeSoundName(8);
    reference.AddCustom(custom, 480000, 48000, title, artist);
    reference.SetEnabled(custom, false);
    reference.SetSampleMeta("R_TEST", title, artist);
    reference.SetEnabled("R_TEST", false);
    source.Save(sourcePath);
    var localized = RadioInfo.Load(localizedPath);
    var station = localized.StationByNumber(1)!;
    station.SyncCustomsFrom(reference);
    station.SyncCustomsFrom(reference);
    localized.Save(localizedPath);
    CheckMetadata(localizedPath, custom, title, artist, horizon2, false);
    var restored = RadioInfo.Load(backupPath);
    var restoreStation = restored.StationByNumber(1)!;
    Check(restoreStation.RestoreTrackFrom(reference, "R_TEST"), "A disabled original track could not be restored.");
    Check(restoreStation.RestoreTrackFrom(reference, custom), "A disabled custom track could not be restored.");
    restored.Save(backupPath);
    CheckMetadata(backupPath, "R_TEST", title, artist, horizon2, false);
    CheckMetadata(backupPath, custom, title, artist, horizon2, false);
    restored = RadioInfo.Load(backupPath);
    restoreStation = restored.StationByNumber(1)!;
    restoreStation.SetEnabled("R_TEST", true);
    restoreStation.SetEnabled(custom, true);
    restored.Save(backupPath);
    CheckMetadata(backupPath, "R_TEST", title, artist, horizon2, true);
    CheckMetadata(backupPath, custom, title, artist, horizon2, true);
}

static void TestRawActive(string root, bool horizon2)
{
    var path = Fixture(root, "raw-active", horizon2);
    var text = File.ReadAllText(path).Replace("DisplayName=\"Original\"", "DisplayName=\"A&B\"", StringComparison.Ordinal)
        .Replace("Artist=\"Artist\"", "Artist=\"C&D\"", StringComparison.Ordinal);
    File.WriteAllText(path, text);
    var radio = RadioInfo.Load(path);
    Check(radio.StationByNumber(1)!.GetSampleMeta("R_TEST")?.DisplayName == "A&B", "Malformed active metadata could not be recovered.");
    radio.Save(path);
    CheckMetadata(path, "R_TEST", "A&B", "C&D", horizon2, true);
}

static void TestRawDisabled(string root)
{
    var path = Fixture(root, "raw-disabled", true);
    var document = XDocument.Load(path);
    foreach (var entry in document.Descendants("Entry").Where(e => (string?) e.Attribute("SoundName") == "R_TEST").ToList())
        entry.ReplaceWith(new XComment("<Entry EventName=\"/Master/Radio/Track\" SoundName=\"R_TEST\" SampleLength=\"10000\" DisplayName=\"A&B\" Artist=\"C&D\" />"));
    document.Save(path);
    _ = XDocument.Load(path);
    var radio = RadioInfo.Load(path);
    var station = radio.StationByNumber(1)!;
    var track = station.ReadTracks().Single();
    Check(!track.Enabled && track.DisplayName == "A&B" && track.Artist == "C&D", "A raw ampersand inside a valid disabled-entry comment lost the track.");
    radio.Save(path);
    _ = XDocument.Load(path);
    CheckMetadata(path, "R_TEST", "A&B", "C&D", true, false);
    Check(File.ReadAllText(path).Contains("DisplayName=\"A&amp;B\"", StringComparison.Ordinal), "Saving a disabled raw entry did not escape its ampersand.");
    station.SetEnabled("R_TEST", true);
    radio.Save(path);
    CheckMetadata(path, "R_TEST", "A&B", "C&D", true, true);
}

static void TestLiteralXml(string root, bool rawActive, bool inspectCdata)
{
    var path = Fixture(root, $"literal-{rawActive}-{inspectCdata}", true);
    const string comment = " note A&B and literal &amp; <not-an-entry> ";
    const string cdata = "text A&B and literal &amp; <xml>";
    var document = XDocument.Load(path);
    document.Root!.AddFirst(new XComment(comment), new XElement("Notes", new XCData(cdata)));
    document.Save(path);
    if (rawActive)
        File.WriteAllText(path, File.ReadAllText(path).Replace("DisplayName=\"Original\"", "DisplayName=\"A&B\"", StringComparison.Ordinal));
    var radio = RadioInfo.Load(path);
    if (inspectCdata)
        Check(radio.Document.DescendantNodes().OfType<XCData>().Single().Value == cdata, "Loading XML changed CDATA containing an ampersand.");
    else
        Check(radio.Document.DescendantNodes().OfType<XComment>().Any(c => c.Value == comment), "Loading XML changed a comment containing an ampersand.");
    radio.Save(path);
    var strict = XDocument.Load(path);
    if (inspectCdata)
        Check(strict.DescendantNodes().OfType<XCData>().Single().Value == cdata, "Saving XML changed CDATA contents.");
    else
        Check(strict.DescendantNodes().OfType<XComment>().Any(c => c.Value == comment), "Saving XML changed comment contents.");
    if (rawActive)
        Check(radio.StationByNumber(1)!.GetSampleMeta("R_TEST")?.DisplayName == "A&B", "Preserving literal XML prevented active metadata repair.");
}

static void TestProcessingInstruction(string root)
{
    const string expected = "data A&B and literal &amp;";
    foreach (var rawActive in new[] { false, true })
    {
        var path = Fixture(root, "processing-" + rawActive, true);
        var document = XDocument.Load(path);
        document.Root!.AddFirst(new XProcessingInstruction("test", expected));
        document.Save(path);
        if (rawActive)
            File.WriteAllText(path, File.ReadAllText(path).Replace("DisplayName=\"Original\"", "DisplayName=\"A&B\"", StringComparison.Ordinal));
        var radio = RadioInfo.Load(path);
        Check(radio.Document.DescendantNodes().OfType<XProcessingInstruction>().Single().Data == expected, "Loading XML changed processing instruction contents.");
        radio.Save(path);
        Check(XDocument.Load(path).DescendantNodes().OfType<XProcessingInstruction>().Single().Data == expected, "Saving XML changed processing instruction contents.");
    }
}

static void TestDirectDisabledFragment()
{
    var document = XDocument.Parse(Horizon2Xml());
    foreach (var entry in document.Descendants("Entry").ToList())
        entry.ReplaceWith(new XComment("<Entry EventName=\"/Master/Radio/Track\" SoundName=\"R_TEST\" SampleLength=\"10000\" DisplayName=\"A&B\" Artist=\"C&D\" />"));
    var editor = new RadioStationEditor(document.Descendants("RadioStation").Single());
    var track = editor.ReadTracks().Single();
    Check(!track.Enabled && track.DisplayName == "A&B" && track.Artist == "C&D", "Reading a station fragment bypassed disabled metadata recovery.");
    editor.SetEnabled("R_TEST", true);
    var strict = XDocument.Parse(document.ToString());
    Check(strict.Descendants("Entry").Count() == 2 && strict.Descendants("Entry").All(e => (string?) e.Attribute("DisplayName") == "A&B" && (string?) e.Attribute("Artist") == "C&D"),
        "Restoring a disabled station fragment did not serialize valid metadata.");
}

static void TestBackupFragment()
{
    const string fragment = "<RadioStation Name=\"R&B\" Number=\"1\"><Playlist Type=\"FreeroamTracks\"><Entry EventName=\"/Master/Radio/Track\" SoundName=\"R_TEST\" SampleLength=\"10000\" DisplayName=\"A&B\" Artist=\"C&D\" /></Playlist><Playlist Type=\"EventTracks\"><Entry EventName=\"/Master/Radio/Track\" SoundName=\"R_TEST\" SampleLength=\"10000\" DisplayName=\"A&B\" Artist=\"C&D\" /></Playlist></RadioStation>";
    var parsed = RadioInfo.ParseElement(fragment);
    var strict = XElement.Parse(parsed.ToString());
    Check((string?) strict.Attribute("Name") == "R&B", "Backup station metadata was not recovered.");
    var track = new RadioStationEditor(strict).ReadTracks().Single();
    Check(track.DisplayName == "A&B" && track.Artist == "C&D", "Backup track metadata was changed while repairing the station fragment.");
}

static string Horizon2Xml() => """
<Radio><RadioStations><RadioStation Name="Test" Number="1">
<Playlist Type="FreeroamTracks"><Entry EventName="/Master/Radio/Track" SoundName="R_TEST" SampleLength="10000" DisplayName="Original" Artist="Artist" TrackStart="0" End="10000" /></Playlist>
<Playlist Type="EventTracks"><Entry EventName="/Master/Radio/Track" SoundName="R_TEST" SampleLength="10000" DisplayName="Original" Artist="Artist" TrackStart="0" End="10000" /></Playlist>
</RadioStation></RadioStations></Radio>
""";

static string ModernXml() => """
<Radio><RadioStations><RadioStation Name="Test" Number="1"><Banks><Bank Name="R1_Tracks_Main" /></Banks>
<SampleList Type="Track"><Sample SoundName="R_TEST" SampleLength="480000" SampleRate="48000" DisplayName="Original" Artist="Artist"><Marker Name="TrackStart" Position="0" /><Marker Name="End" Position="479999" /></Sample></SampleList>
<PlayList Type="FreeRoam"><Entry Name="R_TEST" /></PlayList><PlayList Type="Event"><Entry Name="R_TEST" /></PlayList>
</RadioStation></RadioStations></Radio>
""";
