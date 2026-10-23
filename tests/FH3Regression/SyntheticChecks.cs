using System.Xml.Linq;
using FH6RB.Assets;
using FH6RB.Core;
using FH6RB.Services;
using FH6RB.ViewModels;
using static TestSupport;

internal static class SyntheticChecks
{
    public static async Task RunAsync(string root)
    {
        CheckCodec();
        foreach (var obfuscated in new[] { false, true }) CheckScanner(Path.Combine(root, obfuscated ? "obfuscated" : "normal"), obfuscated);
        CheckGenericHorizon3Disabled(root);
        AccessChecks.Run(root);
        CheckXml(root);
        CheckMinimums(root);
        CheckLegacyMarkerValidation();
        await CheckEditorAndPackAsync(root);
        Console.WriteLine("Verified FH3 names, manifest identity, logical scanner paths, variants, original backups, native XML and cross-game editor/import markers.");
    }

    private static void CheckLegacyMarkerValidation()
    {
        var track = new TrackItemViewModel(new RadioTrack
        {
            SoundName = "HZ3_R6_Metrik_Hi_IE", Origin = TrackOrigin.Original, UsesTrackBreakDown = true,
            SampleLength = 11779328, SampleRate = 48000, SubIndex = 1,
            Markers = new Dictionary<string, long>
            {
                ["TrackStart"] = 0, ["End"] = 11779292, ["DJDrop"] = 294639, ["DJStart"] = 11732268,
                ["StingerStart"] = 11731268, ["TrackDrop"] = 3569631, ["TrackLoopStart"] = 2411748,
                ["TrackLoopEnd"] = 3471036, ["TrackBreakDown"] = 9660669, ["DJSegment"] = 6747570,
                ["PostRaceLoopStart"] = 3569631, ["PostRaceLoopEnd"] = 11646874,
            },
        });
        var editor = new EditWindowViewModel(track);
        Check(editor.ValidateMarkers().Count == 0, "An unchanged native FH3 marker exception blocked saving the track.");
        editor.AllMarkers.Single(m => m.Name == "DJStart").Position++;
        Check(editor.ValidateMarkers().Count == 0, "Editing an unrelated marker rejected the unchanged native exception.");
        var drop = editor.AllMarkers.Single(m => m.Name == "TrackDrop");
        drop.Position++;
        Check(editor.ValidateMarkers().Contains(Str.ErrTrackDropAfterLoop), "Changing an invalid native marker pair bypassed validation.");
        drop.Position = 3471036;
        Check(editor.ValidateMarkers().Count == 0, "Fixing the native TrackDrop/TrackLoopEnd pair still failed validation.");
        track.ReplacementPath = "replacement.wav";
        var replacement = new EditWindowViewModel(track);
        Check(replacement.ValidateMarkers().Contains(Str.ErrTrackDropAfterLoop), "New replacement audio inherited an invalid native marker exception.");
    }

    private static void CheckCodec()
    {
        var pairs = new Dictionary<string, string>
        {
            ["media"] = "gq6$l", ["audio"] = "l=6$h", ["fmodopus"] = "vgh6hu=~", ["x64"] = "]j(",
            ["radioinfo_en.xml"] = "4l6$h$8vhyq8.]gb", ["r1_tracks.bank"] = "40y14l^s~.`l8s",
            ["r4_tracks.bank"] = "4(y14l^s~.`l8s", ["r8_tracks.bank"] = "4;y14l^s~.`l8s",
            ["r1_tracks_pdlc1.bank"] = "40y14l^s~yu6b^0.`l8s", ["r6_tracks_pdlc2.bank"] = "4jy14l^s~yu6b^w.`l8s",
        };
        foreach (var (logical, physical) in pairs)
        {
            Check(Fh3FileNameCodec.Encode(logical) == physical, "FH3 encoding changed a verified filename: " + logical);
            Check(Fh3FileNameCodec.Decode(physical) == logical, "FH3 decoding changed a verified filename: " + physical);
        }
    }

    private static void CheckScanner(string game, bool obfuscated)
    {
        Directory.CreateDirectory(game);
        File.WriteAllBytes(Path.Combine(game, "forza_x64_release_final.exe"), []);
        File.WriteAllText(Path.Combine(game, "AppxManifest.xml"), Manifest);
        var audio = Path.Combine(game, obfuscated ? "gq6$l" : "media", obfuscated ? "l=6$h" : "audio");
        var banks = Path.Combine(audio, obfuscated ? "vgh6hu=~" : "fmodopus", obfuscated ? "]j(" : "x64");
        Directory.CreateDirectory(banks);
        string Physical(string name) => obfuscated ? Fh3FileNameCodec.Encode(name.ToLowerInvariant()) : name;
        var xml = Path.Combine(audio, Physical("RadioInfo_EN.xml"));
        File.WriteAllText(xml, Xml());
        File.WriteAllText(Path.Combine(audio, Physical("RadioInfo_FR.xml")), Xml());
        var expected = new[] { "R1_Tracks", "R1_Tracks_PDLC1", "R6_Tracks", "R6_Tracks_PDLC2" };
        foreach (var bank in expected) File.WriteAllBytes(Path.Combine(banks, Physical(bank + ".bank")), []);
        File.WriteAllBytes(Path.Combine(banks, Physical("R1_LFE.bank")), []);
        Check(GameScanner.FindExe(game) == Path.Combine(game, "forza_x64_release_final.exe") && GameScanner.DetectVersion(game) == 3,
            "FH3 manifest/executable identity was not recognized.");
        CheckHorizon3Disabled(game);
        var scan = ScanResources(game);
        Check(scan.IsValid && scan.LanguageFileCount == 2 && scan.BankCount == 4, "FH3 scanner lost languages or DLC banks.");
        Check(new HashSet<string>(GameScanner.RadioBankNames(game)).SetEquals(expected), "FH3 logical bank names changed with physical encoding.");
        var languages = GameScanner.LanguageFiles(game);
        Check(new HashSet<string>(languages, StringComparer.OrdinalIgnoreCase).SetEquals(["RadioInfo_EN.xml", "RadioInfo_FR.xml"]), "FH3 language names exposed physical filenames.");
        Check(GameScanner.RadioInfoPathByFile(game, "RadioInfo_EN.xml") == xml, "FH3 language resolution did not return its physical file.");
        var catalog = RadioCatalog.Build(game, RadioInfo.Load(xml));
        Check(catalog.Count == 2 && catalog.Single(s => s.Number == 1).Variants.Count == 2
            && catalog.Single(s => s.Number == 6).Variants.Count == 2, "FH3 catalog omitted base/DLC variants.");
        var baseBank = GameScanner.BankPath(game, "R1_Tracks")!;
        File.WriteAllBytes(baseBank + ".bak", [1, 2, 3]);
        File.Copy(xml, xml + ".bak");
        Check(GameScanner.BackupFiles(game).Contains(baseBank + ".bak") && BackupService.Has(game), "FH3 physical original backups were not discovered.");
        var restore = BackupService.Restore(game);
        Check(restore.Restored == 2 && restore.Failed == 0 && File.ReadAllBytes(baseBank).SequenceEqual(new byte[] { 1, 2, 3 }), "FH3 original backup restore missed obfuscated paths.");
        var vm = CreateVm(game, "RadioInfo_EN.xml");
        try
        {
            Check(vm.Stations.Count == 2 && vm.SelectedStation is not null && vm.Variants.Count == 2, "Main-window catalog loading failed for FH3.");
            Check(vm.SelectedVariant == "Tracks", "FH3 selected a DLC bank before the base station bank.");
            vm.SelectedVariant = "PDLC1";
            Method("RebuildStations").Invoke(vm, null);
            Check(vm.SelectedVariant == "PDLC1", "Rebuilding the station list discarded a selected DLC variant.");
        }
        finally { ReleaseVm(vm); }
        File.WriteAllText(Path.Combine(game, "AppxManifest.xml"), Manifest.Replace("Microsoft.OpusPG", "Microsoft.OtherGame", StringComparison.Ordinal));
        Check(GameScanner.DetectVersion(game) == 0 && !GameScanner.Scan(game).IsValid, "A shared executable name falsely identified another manifest as FH3.");
        File.WriteAllText(Path.Combine(game, "AppxManifest.xml"), Manifest);
    }

    private static void CheckGenericHorizon3Disabled(string root)
    {
        var game = Path.Combine(root, "disabled-generic-fh3");
        Directory.CreateDirectory(game);
        File.WriteAllBytes(Path.Combine(game, "ForzaHorizon3.exe"), []);
        var xml = Path.Combine(game, "RadioInfo_EN.xml");
        var bank = Path.Combine(game, "R1_Tracks.bank");
        File.WriteAllText(xml, Xml());
        File.WriteAllBytes(bank, []);
        using var xmlLock = new FileStream(xml, FileMode.Open, FileAccess.Read, FileShare.None);
        using var bankLock = new FileStream(bank, FileMode.Open, FileAccess.Read, FileShare.None);
        CheckHorizon3Disabled(game);
        Check(!GameScanner.IsValid(game), "The public validity check bypassed disabled FH3 selection.");
    }

    private static void CheckXml(string root)
    {
        var path = Path.Combine(root, "native.xml");
        File.WriteAllText(path, Xml());
        var radio = RadioInfo.Load(path);
        var editor = radio.StationByNumber(1)!;
        var tracks = editor.ReadTracks();
        Check(editor.UsesTrackBreakDown && !editor.UsesMilliseconds && tracks.All(t => t.UsesTrackBreakDown && !t.UsesMilliseconds), "FH3 native marker profile was misclassified.");
        Check(tracks[0].SampleRate == 48000 && tracks[0].Markers!["TrackBreakDown"] == 360000, "FH3 marker values were converted out of sample units.");
        var before = Playlists(radio);
        var sampleEnds = radio.Document.Descendants("Sample").Select(e => (string?) e.Attribute("End")).ToArray();
        Apply(radio, new StationInfo(1, "Test", ["Tracks"], new Dictionary<string, string> { ["Tracks"] = "R1_Tracks" }),
            "R1_Tracks", [], tracks.Select(Item).ToList());
        Check(Playlists(radio) == before, "A no-op XML build changed FreeRoam/Event membership, order or unrelated playlists.");
        Check(sampleEnds.SequenceEqual(radio.Document.Descendants("Sample").Select(e => (string?) e.Attribute("End"))), "A no-op XML build rewrote native End positions.");
        foreach (var name in new[] { "S&--", "S2" })
        {
            editor.SetEnabled(name, false);
            radio.Save(path);
            radio = RadioInfo.Load(path);
            editor = radio.StationByNumber(1)!;
            editor.SetEnabled(name, true);
            Check(Playlists(radio) == before, "Disabling and enabling an original changed its playlist location, Entry attributes or event membership.");
        }
        var custom = Naming.MakeSoundName(50);
        editor.AddCustom(custom, 480000, 48000, "A & B", "Artist");
        var added = editor.ReadTracks().Single(t => t.SoundName == custom);
        Check(added.UsesTrackBreakDown && added.SampleLength == 480000 && added.Markers!.ContainsKey("TrackBreakDown") && !added.Markers.ContainsKey("PostDrop"), "FH3 custom markers used a foreign schema.");
        editor.ApplyReplacement("S&--", 960000, 48000);
        Check(editor.GetMarkers("S&--")!.ContainsKey("TrackBreakDown") && !editor.GetMarkers("S&--")!.ContainsKey("PostDrop"), "FH3 replacement introduced PostDrop.");
        radio.Save(path);
        Check(XDocument.Load(path).Descendants("Sample").Single(e => (string?) e.Attribute("SoundName") == custom).Attribute("DisplayName")!.Value == "A & B", "FH3 XML metadata was incorrectly escaped.");
    }

    private static void CheckMinimums(string root)
    {
        const int minimum = 12;
        foreach (var originalCount in new[] { 12, 18, 20, 21 })
        {
            var game = Path.Combine(root, "minimum-" + originalCount);
            var audio = Path.Combine(game, "media", "audio");
            Directory.CreateDirectory(audio);
            File.WriteAllBytes(Path.Combine(game, "forza_x64_release_final.exe"), []);
            File.WriteAllText(Path.Combine(game, "AppxManifest.xml"), Manifest);
            File.WriteAllBytes(Path.Combine(audio, "R1_Tracks.bank"), []);
            var path = Path.Combine(audio, "RadioInfo_EN.xml");
            var station = new XElement("RadioStation", new XAttribute("Number", 1), new XAttribute("Name", "Minimum"),
                new XElement("Banks", new XElement("Bank", new XAttribute("Name", "R1_Tracks"))),
                new XElement("SampleList", new XAttribute("Type", "Track"), Enumerable.Range(0, originalCount).Select(i =>
                    new XElement("Sample", new XAttribute("SoundName", "MIN_" + i), new XAttribute("SampleLength", 480000),
                        new XAttribute("SampleRate", 48000), new XAttribute("TrackBreakDown", 300000)))),
                new XElement("PlayList", new XAttribute("Type", "FreeRoam"), Enumerable.Range(0, originalCount).Select(i => new XElement("Entry", new XAttribute("Name", "MIN_" + i)))),
                new XElement("PlayList", new XAttribute("Type", "Event")));
            new XDocument(new XElement("Radio", new XElement("RadioStations", station))).Save(path);
            var main = CreateVm(game, "RadioInfo_EN.xml");
            try
            {
                Method("UpdatePlaylistMinimum").Invoke(main, null);
                Check(main.MinimumActiveTracks == minimum, "FH3 did not use the fixed 12-track minimum.");
                foreach (var track in RadioInfo.Load(path).StationByNumber(1)!.ReadTracks().Take(minimum - 4)) main.Tracks.Add(new TrackItemViewModel(track));
                Set(main, "_otherBanksActive", 4);
                main.Recount();
                Check(!main.PlaylistLow, "FH3 minimum ignored active tracks in another bank.");
                main.Tracks[0].Enabled = false;
                main.Recount();
                Check(main.PlaylistLow, "FH3 allowed fewer than 12 active tracks.");
                File.Copy(path, path + ".bak");
                var edited = RadioInfo.Load(path);
                edited.StationByNumber(1)!.AddCustom(Naming.MakeSoundName(1), 480000, 48000, "Custom", null);
                edited.Save(path);
                Method("UpdatePlaylistMinimum").Invoke(main, null);
                Check(main.MinimumActiveTracks == minimum, "A saved custom track or original backup changed the fixed FH3 minimum.");
            }
            finally { ReleaseVm(main); }
            File.Delete(Path.Combine(game, "forza_x64_release_final.exe"));
            File.Delete(Path.Combine(game, "AppxManifest.xml"));
            File.WriteAllBytes(Path.Combine(game, "ForzaHorizon4.exe"), []);
            var modern = CreateVm(game, "RadioInfo_EN.xml");
            try
            {
                Method("UpdatePlaylistMinimum").Invoke(modern, null);
                Check(modern.MinimumActiveTracks == 20, "The FH3 minimum changed another game's minimum.");
                for (var i = 0; i < 19; i++) modern.Tracks.Add(new TrackItemViewModel(new RadioTrack
                {
                    SoundName = "KNOWN_" + i, Origin = TrackOrigin.Original, Enabled = true,
                }));
                var orphan = new TrackItemViewModel(new RadioTrack
                {
                    SoundName = "sound_99", Origin = TrackOrigin.Original, Enabled = true, BankId = 0x83607c3d25a4f0b5UL,
                    SampleLength = 480000, SampleRate = 48000, SubIndex = 0,
                });
                modern.Tracks.Add(orphan);
                modern.Recount();
                Check(orphan.IsBankOnly && !orphan.CanToggle && !orphan.CanEdit && !orphan.CanReplace && !orphan.CanEditMarkers
                    && orphan.CanPlay && modern.PlaylistLow, "A bank-only row bypassed the active-track minimum or exposed metadata editing.");
                modern.ToggleEnabled(orphan);
                Check(orphan.Enabled && modern.PlaylistLow, "A direct toggle modified a protected bank-only row.");
                var orphanRadio = RadioInfo.Load(path);
                var beforeOrphan = Playlists(orphanRadio);
                Apply(orphanRadio, modern.SelectedStation!, "R1_Tracks", [], [new BuildItem(orphan.SoundName, false, null, "Unknown", null, null,
                    true, BankId: orphan.BankId)]);
                Check(Playlists(orphanRadio) == beforeOrphan
                    && orphanRadio.Document.Descendants("Entry").All(e => (string?) e.Attribute("Name") != orphan.SoundName),
                    "A bank-only enabled state wrote an unresolved XML playlist entry.");
            }
            finally { ReleaseVm(modern); }
        }
    }

    private static async Task CheckEditorAndPackAsync(string root)
    {
        var path = Path.Combine(root, "editor.xml");
        File.WriteAllText(path, Xml());
        var original = RadioInfo.Load(path).StationByNumber(1)!.ReadTracks()[0];
        var vm = new TrackItemViewModel(original) { ReplacementPath = "raw.wav" };
        var editor = new EditWindowViewModel(vm);
        Check(vm.UsesTrackBreakDown && editor.UsesTrackBreakDown && !editor.UsesMilliseconds
            && editor.AllMarkers.Any(m => m.Name == "TrackBreakDown") && editor.AllMarkers.All(m => m.Name != "PostDrop"), "FH3 marker editor exposed the wrong fields.");
        editor.ApplyMarkerPositions(new Dictionary<string, long> { ["PostDrop"] = 220500, ["DJStart"] = 396900 }, 44100);
        editor.Apply(vm);
        Check(vm.Markers!["TrackBreakDown"] == 240000 && vm.Markers["DJStart"] == 432000, "Cross-game marker import changed FH3 sample units.");
        var fh2 = MarkerPresetService.AdaptMarkers(new Dictionary<string, long> { ["PostDrop"] = 240000, ["DJStart"] = 432000 }, true, 48000);
        var modern = MarkerPresetService.AdaptMarkers(new Dictionary<string, long> { ["TrackBreakDown"] = 240000 }, false, 48000);
        var native = MarkerPresetService.AdaptMarkers(new Dictionary<string, long> { ["PostDrop"] = 240000 }, false, 48000, true);
        Check(fh2["TrackBreakDown"] == 240000 && fh2["StingerStart"] == 384000 && modern["PostDrop"] == 240000
            && native["TrackBreakDown"] == 240000 && !native.ContainsKey("PostDrop"), "Cross-game marker presets did not adapt to each native profile.");
        var source = Path.Combine(root, "pack-audio.wav");
        File.WriteAllBytes(source, [1, 2, 3]);
        var exported = Path.Combine(root, "markers.fhre");
        await TrackPack.ExportAsync(exported, "R1_Tracks", null,
            [new ExportItem(source, -1, "Metadata", "Artist", 480000, 48000, -2, false, "custom", vm.Markers, new(false))], null, null, default);
        var imported = TrackPack.Load(exported);
        try
        {
            Check(imported.Manifest.Tracks.Single().Markers!["TrackBreakDown"] == 240000, "Pack export changed FH3 marker names or units.");
            var game = Path.Combine(root, "normal");
            var main = CreateVm(game, "RadioInfo_EN.xml");
            try
            {
                var staged = main.StageImportedAdd(imported, imported.Manifest.Tracks.Single());
                Check(staged.UsesTrackBreakDown && !staged.UsesMilliseconds && !staged.Enabled && staged.Markers!["TrackBreakDown"] == 240000,
                    "Imported FH3 custom track lost native marker profile or inactive state.");
            }
            finally { ReleaseVm(main); }
        }
        finally { DeleteDirectory(imported.Dir, Path.Combine(FH6RB.Core.AppPaths.TempBase, "import")); }
    }

    public const string Manifest = """<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Identity Name="Microsoft.OpusPG"/><Properties><DisplayName>Forza Horizon 3</DisplayName></Properties><Applications><Application Executable="forza_x64_release_final.exe"/></Applications></Package>""";

    public static string Xml() => """
    <Radio><RadioStations>
      <RadioStation Name="Test" Number="1"><Banks><Bank Name="R1_Tracks"/><Bank Name="R1_Tracks_PDLC1"/></Banks>
        <SampleList Type="Track"><Sample SoundName="S&amp;--" SampleLength="480000" SampleRate="48000" DisplayName="Original" Artist="Artist" TrackStart="0" TrackBreakDown="360000" DJStart="470000" StingerStart="469000" End="479999"/><Sample SoundName="S1" SampleLength="480000" SampleRate="48000" TrackBreakDown="320000" End="479999"/><Sample SoundName="S2" SampleLength="480000" SampleRate="48000" TrackBreakDown="300000" End="479999"/></SampleList>
        <PlayList Type="FreeRoam"><Entry Name="S2" Weight="3"/><Entry Name="S&amp;--" Weight="2"/></PlayList>
        <PlayList Type="Event"><Entry Name="S1" Weight="5"/><Entry Name="S2" Weight="9"/></PlayList>
        <PlayList Type="DJ"><Entry Name="VO_01"/></PlayList><SampleList Type="Stinger"><Sample SoundName="STINGER" SampleLength="1000"/></SampleList>
      </RadioStation><RadioStation Name="Hospital" Number="6"><Banks><Bank Name="R6_Tracks"/><Bank Name="R6_Tracks_PDLC2"/></Banks><SampleList Type="Track"><Sample SoundName="R6_S0" SampleLength="480000" SampleRate="48000" TrackBreakDown="300000" End="479999"/></SampleList><PlayList Type="FreeRoam"><Entry Name="R6_S0"/></PlayList><PlayList Type="Event"/></RadioStation>
    </RadioStations></Radio>
    """;
}
