using System.Reflection;
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
        CheckScanner(Path.Combine(root, "xboxone"), false, xbox: true);
        CheckXboxIdentity(root);
        CheckUnverifiedHorizon3(root);
        AccessChecks.Run(root);
        CheckXml(root);
        CheckMinimums(root);
        CheckLegacyMarkerValidation();
        await CheckEditorAndPackAsync(root);
        var missingTools = Tools.MissingRequired().ToArray();
        if (missingTools.Length == 0)
        {
            await UwpBuildChecks.RunAsync(root);
            await AzimuthPackChecks.RunAsync(root);
        }
        else
        {
            Console.WriteLine($"Skipped FH3 audio build checks. Missing tools: {string.Join(", ", missingTools)}.");
        }
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

    private static void CheckScanner(string game, bool obfuscated, bool xbox = false)
    {
        Directory.CreateDirectory(game);
        var executable = xbox ? "forza_durango_release_final.exe" : "forza_x64_release_final.exe";
        var manifest = xbox ? XboxManifest : Manifest;
        File.WriteAllBytes(Path.Combine(game, executable), []);
        File.WriteAllText(Path.Combine(game, "AppxManifest.xml"), manifest);
        var audio = Path.Combine(game, obfuscated ? "gq6$l" : "media", obfuscated ? "l=6$h" : "audio");
        var banks = Path.Combine(audio, obfuscated ? "vgh6hu=~" : "fmodopus", xbox ? "xboxone" : obfuscated ? "]j(" : "x64");
        Directory.CreateDirectory(banks);
        string Physical(string name) => obfuscated ? Fh3FileNameCodec.Encode(name.ToLowerInvariant()) : name;
        var xml = Path.Combine(audio, Physical("RadioInfo_EN.xml"));
        File.WriteAllText(xml, Xml());
        File.WriteAllText(Path.Combine(audio, Physical("RadioInfo_FR.xml")), Xml());
        var expected = new[] { "R1_Tracks", "R1_Tracks_PDLC1", "R6_Tracks", "R6_Tracks_PDLC2" };
        foreach (var bank in expected) UwpBuildChecks.WriteTemplate(Path.Combine(banks, Physical(bank + ".bank")));
        File.WriteAllBytes(Path.Combine(banks, Physical("R1_LFE.bank")), []);
        Check(GameScanner.FindExe(game) == Path.Combine(game, executable) && GameScanner.DetectVersion(game) == 3,
            "FH3 manifest/executable identity was not recognized.");
        var publicScan = GameScanner.Scan(game);
        Check(publicScan.IsValid && publicScan.LanguageFileCount == 2 && publicScan.BankCount == 4 && GameScanner.IsValid(game),
            "A readable UWP or Xbox One FH3 installation was not accepted by the public scanner.");
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
        File.WriteAllBytes(baseBank, []);
        UwpBuildChecks.WriteTemplate(baseBank + ".bak");
        var originalBank = File.ReadAllBytes(baseBank + ".bak");
        File.Copy(xml, xml + ".bak");
        Check(GameScanner.BackupFiles(game).Contains(baseBank + ".bak") && BackupService.Has(game), "FH3 physical original backups were not discovered.");
        var restore = BackupService.Restore(game);
        Check(restore.Restored == 1 && restore.Failed == 0 && File.ReadAllBytes(baseBank).SequenceEqual(originalBank), "FH3 original backup restore missed obfuscated paths.");
        File.SetLastWriteTimeUtc(baseBank, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var savedTime = File.GetLastWriteTimeUtc(baseBank);
        Check(BackupService.Restore(game).Restored == 0 && File.GetLastWriteTimeUtc(baseBank) == savedTime,
            "Repeating the original restore rewrote an identical bank.");
        File.WriteAllBytes(baseBank + ".bak", originalBank[..20]);
        try
        {
            BackupService.Restore(game);
            throw new InvalidOperationException("Restore accepted a truncated bank backup.");
        }
        catch (IOException)
        {
            Check(File.ReadAllBytes(baseBank).SequenceEqual(originalBank), "An invalid backup changed the live bank.");
        }
        File.WriteAllBytes(baseBank + ".bak", originalBank);
        File.Delete(xml);
        Check(BackupService.Restore(game).Restored == 1 && File.ReadAllBytes(xml).SequenceEqual(File.ReadAllBytes(xml + ".bak")),
            "The original XML could not be restored after the live file was removed.");
        var vm = CreateVm(game, "RadioInfo_EN.xml");
        try
        {
            Check(vm.Stations.Count == 2 && vm.SelectedStation is not null && vm.Variants.Count == 2, "Main-window catalog loading failed for FH3.");
            Check(vm.SelectedVariant == "Tracks", "FH3 selected a DLC bank before the base station bank.");
            vm.SelectedVariant = "PDLC1";
            Method("RebuildStations").Invoke(vm, [null]);
            Check(vm.SelectedVariant == "PDLC1", "Rebuilding the station list discarded a selected DLC variant.");

            vm.Settings.LastLanguage = "RadioInfo_FR.xml";
            vm.Settings.LastStationBank = "R6_Tracks_";
            var scanForEditor = typeof(GameScanner).GetMethod("ScanForEditor", BindingFlags.NonPublic | BindingFlags.Static)!;
            var editorScan = (GameScanner.GameScanResult)scanForEditor.Invoke(null, [game, vm.Settings.LastLanguage])!;
            var reference = (ValueTuple<string, RadioInfo>)typeof(GameScanner.GameScanResult)
                .GetProperty("ReferenceRadio", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editorScan)!;
            Method("ApplyGameScan").Invoke(vm, [editorScan]);
            Check(reference.Item1 == vm.Settings.LastLanguage && ReferenceEquals(reference.Item2,
                typeof(MainWindowViewModel).GetField("_radio", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)),
                "Main-window catalog loading parsed the preferred XML again after scanning.");
            Check(vm.Languages.Count == 3 && vm.SelectedLanguage?.FileName == "RadioInfo_FR.xml"
                && vm.Stations.Count == 2 && vm.SelectedStation?.Number == 6 && vm.Variants.Count == 2,
                "Reloading from the game scan lost languages, stations, or the saved selection.");
            CheckRejectedLanguageChange(vm, xml);
        }
        finally { ReleaseVm(vm); }
        File.WriteAllText(Path.Combine(game, "AppxManifest.xml"), manifest.Replace(xbox ? "Opus" : "Microsoft.OpusPG", "Microsoft.OtherGame", StringComparison.Ordinal));
        Check(GameScanner.DetectVersion(game) == 0 && !GameScanner.Scan(game).IsValid, "A shared executable name falsely identified another manifest as FH3.");
        File.WriteAllText(Path.Combine(game, "AppxManifest.xml"), xbox ? Manifest : XboxManifest);
        Check(GameScanner.DetectVersion(game) == 0 && !GameScanner.Scan(game).IsValid, "A UWP/Xbox manifest and executable mismatch was accepted.");
        File.WriteAllText(Path.Combine(game, "AppxManifest.xml"), manifest);
    }

    private static void CheckRejectedLanguageChange(MainWindowViewModel vm, string targetXml)
    {
        var language = vm.SelectedLanguage!;
        var station = vm.SelectedStation!;
        var variant = vm.SelectedVariant!;
        Set(vm, "_loadedSelection", (language, station.Number, variant));
        var edit = new TrackItemViewModel(new RadioTrack
        {
            SoundName = "Pending", DisplayName = "Unsaved", Origin = TrackOrigin.Original,
        });
        vm.Tracks.Add(edit);
        vm.MarkDirty();
        var original = File.ReadAllBytes(targetXml);
        File.WriteAllText(targetXml, "<Radio><RadioStation");
        try
        {
            Set(vm, "_suppressReload", false);
            var target = vm.Languages.Single(l => l.FileName == "RadioInfo_EN.xml");
            var prompts = 0;
            foreach (var confirm in new[] { false, true })
            {
                vm.ConfirmDiscardChanges = () =>
                {
                    prompts++;
                    return Task.FromResult(confirm);
                };
                vm.SelectedLanguage = target;
                Check(vm.SelectedLanguage == language && vm.SelectedStation == station && vm.SelectedVariant == variant
                    && vm.HasUnsavedChanges && vm.Tracks.Single() == edit,
                    "A rejected or failed language change destroyed the active edits or selection.");
            }
            Check(prompts == 2, "Language changes skipped the unsaved changes prompt.");
        }
        finally
        {
            File.WriteAllBytes(targetXml, original);
        }
    }

    private static void CheckXboxIdentity(string root)
    {
        var game = Path.Combine(root, "shared-durango-executable");
        Directory.CreateDirectory(game);
        var executable = Path.Combine(game, "forza_durango_release_final.exe");
        var manifest = Path.Combine(game, "AppxManifest.xml");
        File.WriteAllBytes(executable, []);
        File.WriteAllText(Path.Combine(game, "RadioInfo_EN.xml"), Xml());
        UwpBuildChecks.WriteTemplate(Path.Combine(game, "R1_Tracks.bank"));
        foreach (var (identity, version) in new[] { ("Anthem", 2), ("Opus", 3) })
        {
            File.WriteAllText(manifest, XboxManifest.Replace("Opus", identity, StringComparison.Ordinal));
            Check(GameScanner.DetectVersion(game) == version && GameScanner.Scan(game).IsValid,
                "The shared Xbox executable ignored its package identity: " + identity);
        }
        foreach (var invalid in new[]
        {
            XboxManifest.Replace("Opus", "Other", StringComparison.Ordinal),
            XboxManifest.Replace("Forza_Durango_Release_Final.exe", "other.exe", StringComparison.Ordinal),
            XboxManifest.Replace("Name=\"Opus\"", "Name=\"Microsoft.OpusPG\"", StringComparison.Ordinal),
            "<Package><Identity Name=\"Opus\">",
            "<!DOCTYPE Package [<!ENTITY package 'Opus'>]><Package><Identity Name=\"&package;\"/></Package>"
        })
        {
            File.WriteAllText(manifest, invalid);
            Check(GameScanner.DetectVersion(game) == 0 && !GameScanner.Scan(game).IsValid,
                "An unknown, mismatched or invalid Xbox manifest was accepted.");
        }
        File.Delete(manifest);
        Check(GameScanner.DetectVersion(game) == 0 && !GameScanner.Scan(game).IsValid,
            "An Xbox FH3 directory without a manifest was guessed to be FH2.");
        UwpBuildChecks.WriteTemplate(Path.Combine(game, "HZ2_MX_Radio_01_Tracks.bank"));
        Check(GameScanner.DetectVersion(game) == 2 && GameScanner.Scan(game).IsValid,
            "A legacy FH2 directory without a manifest lost its resource-based identity.");
    }

    private static void CheckUnverifiedHorizon3(string root)
    {
        var game = Path.Combine(root, "unverified-generic-fh3");
        Directory.CreateDirectory(game);
        File.WriteAllBytes(Path.Combine(game, "ForzaHorizon3.exe"), []);
        var xml = Path.Combine(game, "RadioInfo_EN.xml");
        var bank = Path.Combine(game, "R1_Tracks.bank");
        File.WriteAllText(xml, Xml());
        File.WriteAllBytes(bank, []);
        Check(GameScanner.FindExe(game) is null && GameScanner.DetectVersion(game) == 0 && !GameScanner.IsValid(game),
            "An unverified renamed executable was accepted as FH3.");
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
        editor.SetMarkers("S&--", new Dictionary<string, long> { ["DJStart"] = -1 });
        Check(editor.GetMarkers("S&--")!["StingerStart"] == -1, "An off FH3 DJStart enabled StingerStart at the beginning of the track.");
        try
        {
            MarkerDefaults.Apply(new Dictionary<string, string> { ["DJStart"] = MarkerDefaults.Off });
            Check(RadioStationEditor.ComputeAutoMarkers(480000, 48000, true)["StingerStart"] == -1,
                "An off FH2 DJStart default enabled StingerStart.");
        }
        finally { MarkerDefaults.Reset(); }
    }

    private static void CheckMinimums(string root)
    {
        const int minimum = 12;
        foreach (var xbox in new[] { false, true })
        foreach (var originalCount in new[] { 12, 18, 20, 21 })
        {
            var game = Path.Combine(root, $"minimum-{(xbox ? "xbox" : "uwp")}-{originalCount}");
            var audio = Path.Combine(game, "media", "audio");
            Directory.CreateDirectory(audio);
            var executable = Path.Combine(game, xbox ? "forza_durango_release_final.exe" : "forza_x64_release_final.exe");
            File.WriteAllBytes(executable, []);
            File.WriteAllText(Path.Combine(game, "AppxManifest.xml"), xbox ? XboxManifest : Manifest);
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
            File.Delete(executable);
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
                    && modern.PlaylistLow, "A bank-only row bypassed the active-track minimum or exposed metadata editing.");
                Check(orphan.CanPlay == Tools.HasVgmstream, "Bank-only playback did not reflect decoder availability.");
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

    public const string XboxManifest = """<Package xmlns="http://schemas.microsoft.com/appx/2010/manifest"><Identity Name="Opus"/><Properties><DisplayName>Forza Horizon 3</DisplayName></Properties><Applications><Application Executable="Forza_Durango_Release_Final.exe"/></Applications></Package>""";

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
