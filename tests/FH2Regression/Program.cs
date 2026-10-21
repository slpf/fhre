using System.Xml.Linq;
using System.Reflection;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using FH6RB.Core;
using FH6RB.Services;
using FH6RB.ViewModels;

var temp = Path.Combine(AppContext.BaseDirectory, "scratch", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    TestScanner(temp);
    TestHorizon2Xml(temp);
    TestMarkerDefaults(temp);
    TestModernXml(temp);
    TestMarkerEditor(temp);
    TestStagedAudio(temp);
    TestPlaylistMinimums(temp);
    if (args.Length > 0)
    {
        await TestInstallationAsync(Path.GetFullPath(args[0]), temp, args.Length > 1 ? Path.GetFullPath(args[1]) : null);
    }
    Console.WriteLine("FH2 regression checks passed.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
finally
{
    Directory.Delete(temp, recursive: true);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void TestScanner(string temp)
{
    var game = Path.Combine(temp, "FH2");
    var audio = Path.Combine(game, "media", "audio");
    var banks = Path.Combine(audio, "fmodanthem", "durango");
    Directory.CreateDirectory(banks);
    File.WriteAllBytes(Path.Combine(game, "forza_durango_release_final.exe"), []);
    File.WriteAllText(Path.Combine(audio, "RadioInfo_EN.xml"), Horizon2Xml());
    File.WriteAllBytes(Path.Combine(banks, "HZ2_MX_Radio_01_Tracks.bank"), []);
    File.WriteAllBytes(Path.Combine(banks, "HZ2_MX_Radio_01_TracksLFE.bank"), []);
    File.WriteAllBytes(Path.Combine(banks, "HZ2_MX_Radio_01_DJ_EN.bank"), []);

    var scan = GameScanner.Scan(game);
    Check(scan.IsValid && scan.LanguageFileCount == 1 && scan.BankCount == 1, "FH2 scan failed.");
    Check(GameScanner.DetectVersion(game) == 2, "FH2 executable detection failed.");
    var radio = RadioInfo.Load(Path.Combine(audio, "RadioInfo_EN.xml"));
    var station = RadioCatalog.Build(game, radio).Single();
    Check(station.Number == 1 && station.Name == "Horizon Pulse", "FH2 station catalog failed.");
    Check(station.BankName(station.Variants.Single()) == "HZ2_MX_Radio_01_Tracks", "FH2 bank mapping failed.");
    Check(GameScanner.BankPath(game, station.BankName("Tracks")) == Path.Combine(banks, "HZ2_MX_Radio_01_Tracks.bank"), "FH2 bank lookup failed.");
    var bankPath = Path.Combine(banks, "HZ2_MX_Radio_01_Tracks.bank");
    File.WriteAllBytes(bankPath + ".bak", [1, 2, 3]);
    Check(BackupService.Has(game), "FH2 original bank backup was not found.");
    var restored = BackupService.Restore(game);
    Check(restored.Restored == 1 && restored.Failed == 0 && File.ReadAllBytes(bankPath).SequenceEqual(new byte[] { 1, 2, 3 }), "FH2 original bank restore failed.");

    var modern = Path.Combine(temp, "FH5");
    Directory.CreateDirectory(modern);
    File.WriteAllBytes(Path.Combine(modern, "ForzaHorizon5.exe"), []);
    File.WriteAllText(Path.Combine(modern, "RadioInfo_EN.xml"), ModernXml());
    File.WriteAllBytes(Path.Combine(modern, "R1_Tracks_Main.assets.bank"), []);
    Check(GameScanner.Scan(modern).IsValid && GameScanner.DetectVersion(modern) == 5, "Modern game scan regressed.");
    Check(RadioCatalog.Build(modern, RadioInfo.Load(Path.Combine(modern, "RadioInfo_EN.xml"))).Single().BankName("Main") == "R1_Tracks_Main", "Modern bank catalog regressed.");
}

static void TestHorizon2Xml(string temp)
{
    var path = Path.Combine(temp, "RadioInfo.xml");
    File.WriteAllText(path, Horizon2Xml());
    var radio = RadioInfo.Load(path);
    var editor = radio.StationForBank("HZ2_MX_Radio_01_Tracks") ?? throw new InvalidOperationException("FH2 station lookup failed.");
    var original = editor.ReadTracks().Single();
    Check(original.SoundName == "HZ2_R1_Test" && original.Enabled, "FH2 track read failed.");
    Check(original.SampleLength / (double) original.SampleRate == 120, "FH2 duration units are incorrect.");
    Check(editor.FreeRoamActiveCount() == 1, "FH2 active count failed.");

    editor.SetEnabled(original.SoundName, false);
    radio.Save(path);
    radio = RadioInfo.Load(path);
    editor = radio.StationByNumber(1)!;
    Check(editor.FreeRoamActiveCount() == 0, "FH2 disabling left an active entry.");
    Check(editor.ReadTracks().Single().Enabled == false, "FH2 disabled track was lost after saving.");
    Check(editor.GetSampleMeta(original.SoundName)?.DisplayName == "Bridge & Tunnel", "FH2 disabled metadata was lost.");
    editor.SetEnabled(original.SoundName, true);
    Check(editor.FreeRoamActiveCount() == 1, "FH2 re-enabling failed.");

    var custom = Naming.MakeSoundName(17);
    editor.AddCustom(custom, 4_800_000, 48000, "Custom track", "Artist");
    Check(radio.MaxCustomSeq() == 17, "FH2 custom numbering failed.");
    var station = radio.Document.Descendants("RadioStation").Single();
    var entries = station.Elements("Playlist").Where(p => (string?) p.Attribute("Type") is "FreeroamTracks" or "EventTracks").SelectMany(p => p.Elements("Entry")).Where(e => (string?) e.Attribute("SoundName") == custom).ToList();
    Check(entries.Count == 2 && entries.All(e => (long?) e.Attribute("SampleLength") == 100000), "FH2 custom track duration must be milliseconds in both playlists.");
    Check(entries.All(e => e.Attribute("SampleRate") is null && (string?) e.Attribute("EventName") == "/Master/Radio/Track"), "FH2 custom entry schema changed.");
    var positions = new Dictionary<string, long> { ["TrackStart"] = 48000, ["DJStart"] = 4_320_000, ["StingerStart"] = 4_272_000, ["TrackBreakDown"] = 3_360_000, ["DJDrop"] = -1 };
    editor.SetMarkers(custom, positions, 48000);
    Check(entries.All(e => (long?) e.Attribute("TrackStart") == 1000 && (long?) e.Attribute("StingerStart") == 89000 && (long?) e.Attribute("DJDrop") == -1), "FH2 marker frame-to-millisecond conversion failed.");
    Check(editor.GetMarkers(custom, 48000)!["StingerStart"] == 4_272_000, "FH2 marker millisecond-to-frame conversion failed.");
    Check(editor.ApplyReplacement(original.SoundName, 2_880_000, 48000), "FH2 replacement failed.");
    Check(station.Elements("Playlist").Where(p => (string?) p.Attribute("Type") is "FreeroamTracks" or "EventTracks").SelectMany(p => p.Elements("Entry")).Where(e => (string?) e.Attribute("SoundName") == original.SoundName).All(e => (long?) e.Attribute("SampleLength") == 60000), "FH2 replacement did not update both playlists in milliseconds.");
    editor.SetSampleMeta(custom, "Updated", "Updated artist");
    Check(entries.All(e => (string?) e.Attribute("DisplayName") == "Updated"), "FH2 metadata did not update every playlist.");
    editor.RegisterBank("HZ2_MX_Radio_01_Tracks");
    Check(station.Element("Banks") is null && editor.TrackBankNames().Single() == "HZ2_MX_Radio_01_Tracks", "FH2 bank registration changed the game schema.");
    editor.SetEnabled(custom, false);
    editor.SetSampleMeta(custom, "Custom -- & updated", "Artist -- name");
    radio.Save(path);
    radio = RadioInfo.Load(path);
    editor = radio.StationByNumber(1)!;
    Check(radio.MaxCustomSeq() == 17 && editor.ReadTracks().Single(t => t.SoundName == custom).Enabled == false, "FH2 disabled custom track was lost.");
    Check(editor.GetSampleMeta(custom)?.DisplayName == "Custom -- & updated" && editor.GetMarkers(custom, 48000)!["DJDrop"] == -1, "FH2 disabled metadata or marker escaping failed.");
    var localizedPath = Path.Combine(temp, "RadioInfoLocalized.xml");
    File.WriteAllText(localizedPath, Horizon2Xml());
    var localized = RadioInfo.Load(localizedPath).StationByNumber(1)!;
    localized.SyncCustomsFrom(editor);
    localized.SyncCustomsFrom(editor);
    editor.SyncCustomsFrom(editor);
    editor.SyncCustomsFrom(radio.StationByNumber(1)!);
    Check(localized.ReadTracks().Count == 2 && !localized.ReadTracks().Single(t => t.SoundName == custom).Enabled, "FH2 custom localization lost disabled state or duplicated tracks.");
    Check(localized.GetSampleMeta(custom)?.DisplayName == "Custom -- & updated", "FH2 custom localization lost metadata.");
    editor.RemoveCustom(custom);
    Check(editor.ReadTracks().Count == 1 && radio.MaxCustomSeq() == -1, "FH2 custom removal failed.");
    Check(editor.ReconcileTracks(new HashSet<ulong>(), Lookup.SoundNameToId) == 1 && editor.ReadTracks().Count == 0, "FH2 dangling entry reconciliation failed.");
}

static void TestMarkerDefaults(string temp)
{
    var snapshot = MarkerDefaults.Snapshot();
    try
    {
        var path = Path.Combine(temp, "RadioInfoDefaults.xml");
        File.WriteAllText(path, Horizon2Xml());
        var radio = RadioInfo.Load(path);
        var editor = radio.StationByNumber(1)!;
        MarkerDefaults.Apply(new Dictionary<string, string> { ["DJDrop"] = "48000s", ["PostDrop"] = "-48000s", ["DJStart"] = "-240000s" });
        var custom = Naming.MakeSoundName(20);
        editor.AddCustom(custom, 4_800_000, 48000, "Defaults", "Artist");
        var markers = editor.GetMarkers(custom)!;
        Check(markers["DJDrop"] == 1000 && markers["TrackBreakDown"] == 99000 && markers["DJStart"] == 95000 && markers["StingerStart"] == 94000, "FH2 sample-based defaults were interpreted as milliseconds.");
        editor.ApplyReplacement("HZ2_R1_Test", 4_800_000, 48000);
        Check(editor.GetMarkers("HZ2_R1_Test")!["TrackBreakDown"] == 99000, "FH2 replacement sample-based defaults changed units.");
        MarkerDefaults.Apply(new Dictionary<string, string> { ["DJDrop"] = "", ["PostDrop"] = "", ["DJStart"] = "" });
        var defaults = RadioStationEditor.ComputeAutoMarkers(4_800_000, 48000, true);
        Check(defaults.Count == 7 && new[] { "DJDrop", "TrackBreakDown", "DJStart", "StingerStart" }.All(name => defaults[name] == -1), "FH2 bulk reset omitted disabled markers.");
        editor.ResetMarkersAuto(custom, 4_800_000, 48000);
        markers = editor.GetMarkers(custom)!;
        Check(new[] { "DJDrop", "TrackBreakDown", "DJStart", "StingerStart" }.All(name => markers[name] == -1), "FH2 off defaults retained cloned marker positions.");
        editor.SetMarkers(custom, new Dictionary<string, long> { ["End"] = 4_800_000, ["DJStart"] = 4_800_000, ["StingerStart"] = 4_752_000 }, 48000);
        Check(editor.GetMarkers(custom)!["End"] == 100000 && editor.GetMarkers(custom)!["DJStart"] == 100000, "FH2 end markers were clamped below sample length.");
        editor.ResetMarkersAuto(custom, 4_800_480, 48000);
        Check(editor.GetMarkers(custom)!.Values.All(p => p < 0 || p <= 100000), "FH2 automatic markers exceeded the authored XML duration because of codec padding.");
    }
    finally
    {
        MarkerDefaults.Apply(snapshot);
    }
}

static void TestMarkerEditor(string temp)
{
    var track = new TrackItemViewModel(new RadioTrack { SoundName = "HZ2_R1_Test", Origin = TrackOrigin.Original, Enabled = true, SampleLength = 5_760_000, SampleRate = 48000, UsesMilliseconds = true });
    var editor = new EditWindowViewModel(track);
    Check(editor.AllMarkers.Select(m => m.Name).ToHashSet().SetEquals(["TrackStart", "End", "DJDrop", "DJStart", "StingerStart", "TrackDrop", "TrackBreakDown"]), "FH2 marker editor uses the wrong schema.");
    Check(editor.AllMarkers.All(m => !m.CanLoop), "FH2 marker editor has a loop button without loop markers.");
    Check(editor.ApplyMarkerPositions(new Dictionary<string, long> { ["TrackStart"] = 2500, ["TrackBreakDown"] = 70000 }, 1000) == 2, "FH2 marker preset import failed.");
    editor.Apply(track);
    Check(track.Markers!["TrackStart"] == 120000 && track.Markers["TrackBreakDown"] == 3_360_000, "FH2 marker preset sample rate conversion failed.");
    Check(editor.ValidateMarkers().Count == 0, "FH2 marker editor validation failed.");
    Check(editor.ApplyMarkerPositions(new Dictionary<string, long> { ["PostDrop"] = 70000, ["DJStart"] = 115000, ["DJDrop"] = -1 }, 1000) == 4, "FH2 shared marker preset adaptation failed.");
    var collected = editor.CollectMarkerPositions();
    Check(collected["TrackBreakDown"] == 3_360_000 && collected["StingerStart"] == 5_472_000 && collected["DJDrop"] == -1, "FH2 shared presets changed breakdown, stinger or off values.");
    editor.ApplyMarkerPositions(new Dictionary<string, long> { ["PostDrop"] = 60000, ["TrackBreakDown"] = 65000 }, 1000);
    Check(editor.CollectMarkerPositions()["TrackBreakDown"] == 3_120_000, "FH2 explicit TrackBreakDown was overwritten by a legacy preset alias.");
    var defaultFields = new MarkerDefaultsViewModel(new AppSettings { GamePath = Path.Combine(temp, "FH2") });
    Check(defaultFields.Fields.Any(f => f.Name == "TrackBreakDown") && defaultFields.Fields.All(f => f.Name is not "PostDrop" and not "DJSegment" and not "TrackLoopStart"), "FH2 defaults expose the wrong marker names.");
    var modern = new EditWindowViewModel(new TrackItemViewModel(new RadioTrack { SoundName = "HZ_R1_Test", Origin = TrackOrigin.Original, Enabled = true, SampleLength = 5_760_000, SampleRate = 48000 }));
    Check(modern.AllMarkers.Count() == 11 && modern.AllMarkers.All(m => m.Name is not "TrackBreakDown" and not "StingerStart"), "Modern marker editor schema regressed.");
}

static void TestStagedAudio(string temp)
{
    var track = new TrackItemViewModel(new RadioTrack
    {
        SoundName = "HZ2_R1_Test", Origin = TrackOrigin.Original, UsesMilliseconds = true,
        SampleLength = 441000, SampleRate = 44100,
        Markers = new Dictionary<string, long> { ["TrackBreakDown"] = 220500, ["DJDrop"] = -1 },
    }) { RestoreFromBak = true, RestoreFsbPath = "restored.fsb", RestoreBakIndex = 2 };
    track.StageReplacement(Path.Combine(temp, "replacement.wav"), "Replacement", "Artist", 10, false);
    Check(track.SampleRate == 48000 && track.SampleLength == 480000 && track.Markers!["TrackBreakDown"] == 240000 && track.Markers["DJDrop"] == -1, "FH2 staged replacement changed marker timing.");
    Check(!track.RestoreFromBak && track.RestoreFsbPath is null && track.RestoreBakIndex == -1, "FH2 replacement retained a staged backup restore.");
    var vm = CreateXmlWriter(Path.Combine(temp, "FH2"), ["RadioInfo_EN.xml"]);
    var entry = new PackTrack
    {
        File = "missing.mp3", SampleLength = 441000, SampleRate = 44100, Enabled = false,
        Markers = new Dictionary<string, long> { ["PostDrop"] = 220500, ["DJStart"] = 396900, ["DJDrop"] = -1 },
    };
    var pack = new ImportedPack(new PackManifest(), temp);
    track.RestoreFromBak = true;
    track.RestoreFsbPath = "restored-again.fsb";
    vm.StageImportedReplace(track, pack, entry);
    Check(track.SampleRate == 48000 && track.SampleLength == 480000 && track.Markers!["TrackBreakDown"] == 240000 && track.Markers["StingerStart"] == 384000, "FH2 imported MP3 markers were not adapted to 48 kHz.");
    Check(!track.RestoreFromBak && track.RestoreFsbPath is null, "FH2 imported replacement retained staged restore audio.");
    var custom = vm.StageImportedAdd(pack, entry);
    Check(custom.UsesMilliseconds && custom.SampleRate == 48000 && custom.SampleLength == 480000 && !custom.Enabled && custom.Markers!["TrackBreakDown"] == 240000, "FH2 imported custom track has incorrect marker profile or units.");
    entry.File = "missing.fsb";
    vm.StageImportedReplace(track, pack, entry);
    Check(track.SampleRate == 44100 && track.SampleLength == 441000 && track.Markers!["TrackBreakDown"] == 220500, "FH2 native FSB import changed its sample rate.");
    var original = new TrackItemViewModel(new RadioTrack
    {
        SoundName = "HZ2_R1_Jetta_Operators", Origin = TrackOrigin.Original, UsesMilliseconds = true,
        SampleLength = 9_112_896, SampleRate = 48000,
        Markers = new Dictionary<string, long> { ["DJStart"] = 9_112_896, ["StingerStart"] = 9_077_616 },
    });
    var eligible = typeof(MainWindowViewModel).GetMethod("IsResetEligible", BindingFlags.NonPublic | BindingFlags.Static)!;
    Check(!(bool) eligible.Invoke(null, [original])!, "FH2 untouched stock markers were selected for a custom-marker reset.");
}

static void TestModernXml(string temp)
{
    var path = Path.Combine(temp, "RadioInfoModern.xml");
    File.WriteAllText(path, ModernXml());
    var radio = RadioInfo.Load(path);
    var editor = radio.StationForBank("R1_Tracks_Main")!;
    var track = editor.ReadTracks().Single();
    Check(track.Enabled && track.SampleRate == 48000 && track.SampleLength == 5_760_000, "Modern XML reading regressed.");
    editor.SetEnabled(track.SoundName, false);
    Check(!editor.ReadTracks().Single().Enabled, "Modern disabling regressed.");
    editor.SetEnabled(track.SoundName, true);
    editor.AddCustom(Naming.MakeSoundName(4), 4_800_000, 48000, "Custom", "Artist");
    Check(radio.MaxCustomSeq() == 4 && editor.FreeRoamActiveCount() == 2, "Modern custom addition regressed.");
    Check(editor.ApplyReplacement(track.SoundName, 2_880_000, 48000), "Modern replacement regressed.");
    Check((long?) radio.Document.Descendants("Sample").First().Attribute("SampleLength") == 2_880_000, "Modern replacement duration units changed.");
}

static async Task TestInstallationAsync(string game, string temp, string? previewDirectory)
{
    Check(GameScanner.DetectVersion(game) == 2, "The supplied installation is not FH2.");
    var languageFiles = GameScanner.LanguageFiles(game);
    Check(languageFiles.Count > 0, "FH2 installation has no language files.");
    var bankNames = GameScanner.RadioBankNames(game);
    var total = 0;
    foreach (var language in languageFiles)
    {
        var radio = RadioInfo.Load(GameScanner.RadioInfoPathByFile(game, language)!);
        var stations = RadioCatalog.Build(game, radio);
        Check(stations.Count == bankNames.Count, $"{language}: catalog lost a music bank.");
        foreach (var station in stations)
        {
            var stationNode = radio.Document.Descendants("RadioStation").Single(s => (int?) s.Attribute("Number") == station.Number);
            var eventPlaylist = stationNode.Elements("Playlist").Single(p => (string?) p.Attribute("Type") == "EventTracks");
            var eventNames = eventPlaylist.Elements("Entry").Select(e => (string) e.Attribute("SoundName")!).ToHashSet();
            var stationEditor = radio.StationByNumber(station.Number)!;
            foreach (var track in stationEditor.ReadTracks()) stationEditor.SetEnabled(track.SoundName, true);
            Check(eventNames.SetEquals(eventPlaylist.Elements("Entry").Select(e => (string) e.Attribute("SoundName")!)), $"{language}, station {station.Number}: enabling originals changed event membership.");
            var freeRoamOnly = stationEditor.ReadTracks().FirstOrDefault(t => !eventNames.Contains(t.SoundName));
            if (freeRoamOnly is not null)
            {
                stationEditor.SetEnabled(freeRoamOnly.SoundName, false);
                stationEditor.SetEnabled(freeRoamOnly.SoundName, true);
                Check(eventNames.SetEquals(eventPlaylist.Elements("Entry").Select(e => (string) e.Attribute("SoundName")!)), $"{language}, station {station.Number}: toggling a track changed event membership.");
            }
            var bank = GameScanner.BankPath(game, station.BankName(station.Variants.Single()))!;
            var read = BankReader.ReadTracks(bank, radio, station.Number);
            Check(read.Error is null && read.Tracks.Count > 0, $"{language}, station {station.Number}: {read.Error ?? "no tracks"}");
            Check(read.Tracks.All(t => !t.SoundName.StartsWith("sound_", StringComparison.Ordinal) && t.SampleRate == 48000 && t.Enabled), $"{language}, station {station.Number}: metadata, rate or playlist mismatch.");
            Check(read.Tracks.All(t => t.Markers is { Count: > 0 } && t.Markers.Values.All(v => v < 0 || v <= t.SampleLength)), $"{language}, station {station.Number}: marker conversion failed.");
            total += read.Tracks.Count;
        }
    }

    var source = GameScanner.BankPath(game, bankNames[0])!;
    var skel = FevBank.ReadSkeleton(source);
    Check(skel.Mode == 10, "FH2 music is expected to use the verified XMA codec.");
    var (header, layout) = Fsb5.ReadLayout(skel.Fsb5HeaderRegion);
    var sample = layout[0];
    var data = new byte[checked((int) sample.DataLen)];
    using (var stream = File.OpenRead(source))
    {
        stream.Position = skel.DataStartAbs + sample.DataOff;
        stream.ReadExactly(data);
    }
    var fsb = Path.Combine(temp, "passthrough.fsb");
    File.WriteAllBytes(fsb, Fsb5.BuildSingleSample(header, new Fsb5Sample { Header = sample.Header, Data = data, Frames = sample.Frames, SampleRate = sample.SampleRate, Channels = 2 }));
    var radioForBuild = RadioInfo.Load(GameScanner.RadioInfoPathByFile(game, languageFiles[0])!);
    var catalog = RadioCatalog.Build(game, radioForBuild);
    var firstStation = catalog.Single(s => s.BankName(s.Variants.Single()) == bankNames[0]);
    var tracks = BankReader.ReadTracks(source, radioForBuild, firstStation.Number).Tracks;
    var items = tracks.Select(t => new BuildItem(t.SoundName, false, null, t.DisplayName, t.Artist, null, t.Enabled, t.Markers, MarkerSampleRate: t.SampleRate)).ToList();
    var output = Path.Combine(temp, "converted.bank");
    var settings = new AppSettings { LoudnessNormalize = false, EncodeParallelism = 4 };
    var added = await BankBuildService.BuildToFileAsync(source, output, items, settings, progress: Console.WriteLine);
    Check(added.Count == 0, "FH2 codec conversion reported retained originals as added or replaced.");
    var rebuilt = FevBank.ReadSkeleton(output);
    Check(rebuilt.Mode == 15 && rebuilt.Stbl.Count == skel.Stbl.Count, "FH2 whole-bank conversion did not produce Vorbis or lost tracks.");
    Check(FevBank.ReadStblIdsFromFile(output).SetEquals(skel.Stbl.Select(t => t.Id)), "FH2 whole-bank conversion changed sound identifiers.");
    var convertedTracks = FevBank.ReadTrackInfoFromFile(output);
    Check(convertedTracks.All(t => t.Frames == tracks[t.Index].SampleLength && t.SampleRate == tracks[t.Index].SampleRate), "FH2 whole-bank conversion changed original sample dimensions.");
    var xmlBefore = new XDocument(radioForBuild.Document);
    ApplyRadioInfo(radioForBuild, firstStation, bankNames[0], added, items);
    Check(XNode.DeepEquals(xmlBefore, radioForBuild.Document), "FH2 whole-bank conversion changed original XML metadata or playlists.");
    DecodeAndCheck(output, 0, Path.Combine(temp, "converted-first.wav"), tracks[0].SampleLength);
    DecodeAndCheck(output, tracks.Count - 1, Path.Combine(temp, "converted-last.wav"), tracks[^1].SampleLength);
    if (previewDirectory is not null)
    {
        Directory.CreateDirectory(previewDirectory);
        File.Copy(output, Path.Combine(previewDirectory, bankNames[0] + ".bank"), overwrite: true);
    }

    var miniSource = Path.Combine(temp, "original-two.bank");
    FevBank.AssembleToFile(miniSource, skel.FmtChunk, skel.ListPayload, skel.StblRelOff, skel.StblOldSize,
        FevBank.BuildStbl(skel.Stbl.Where(t => t.Index < 2)), header,
        layout.Take(2).Select(t => t.Header).ToList(),
        layout.Take(2).Select(t => new FevBank.DataRef(source, skel.DataStartAbs + t.DataOff, t.DataLen)).ToList());
    var wav = Path.Combine(temp, "new.wav");
    RunTool(Tools.FfmpegPath, $"-y -hide_banner -loglevel error -f lavfi -i sine=frequency=660:sample_rate=44100:duration=2 -ac 2 -c:a pcm_s16le \"{wav}\"");
    var mp3 = Path.Combine(temp, "new.mp3");
    RunTool(Tools.FfmpegPath, $"-y -hide_banner -loglevel error -i \"{wav}\" -c:a libmp3lame \"{mp3}\"");
    var wavName = Naming.MakeSoundName(100);
    var mp3Name = Naming.MakeSoundName(101);
    var xmaName = Naming.MakeSoundName(102);
    var mixedItems = items.Take(2).ToList();
    mixedItems.Add(new BuildItem(wavName, true, wav, "WAV test", "Artist", null, true));
    mixedItems.Add(new BuildItem(mp3Name, true, mp3, "MP3 test", "Artist", null, false));
    mixedItems.Add(new BuildItem(xmaName, true, fsb, "XMA import", "Artist", null, true));
    var mixedOutput = Path.Combine(temp, "mixed-inputs.bank");
    var mixedAdded = await BankBuildService.BuildToFileAsync(miniSource, mixedOutput, mixedItems, settings, progress: Console.WriteLine);
    Check(mixedAdded.Count == 3 && mixedAdded.All(t => !t.IsReplacement), "FH2 input conversion returned incorrect added tracks.");
    Check(FevBank.ReadSkeleton(mixedOutput).Mode == 15 && FevBank.ReadStblCountFromFile(mixedOutput) == 5, "FH2 WAV, MP3 or XMA import failed.");
    ApplyRadioInfo(radioForBuild, firstStation, bankNames[0], mixedAdded, mixedItems);
    var mixedTracks = BankReader.ReadTracks(mixedOutput, radioForBuild, firstStation.Number);
    Check(mixedTracks.Error is null && mixedTracks.Tracks.Count == 5, "FH2 converted bank metadata failed.");
    Check(!mixedTracks.Tracks.Single(t => t.SoundName == mp3Name).Enabled, "FH2 disabled imported MP3 became enabled.");
    foreach (var track in mixedTracks.Tracks)
    {
        DecodeAndCheck(mixedOutput, track.SubIndex, Path.Combine(temp, $"mixed-{track.SubIndex}.wav"), track.SampleLength);
    }

    var repeatItems = mixedTracks.Tracks.Select(t => new BuildItem(t.SoundName, false, null, t.DisplayName, t.Artist, null, t.Enabled, t.Markers, MarkerSampleRate: t.SampleRate)).ToList();
    var repeatOutput = Path.Combine(temp, "repeat.bank");
    Check((await BankBuildService.BuildToFileAsync(mixedOutput, repeatOutput, repeatItems, settings)).Count == 0, "FH2 repeated build unexpectedly encoded tracks.");
    foreach (var track in mixedTracks.Tracks) CheckSampleDataEqual(mixedOutput, repeatOutput, track.SubIndex);

    var replacementItems = repeatItems.ToList();
    replacementItems[0] = replacementItems[0] with { SourcePath = wav, IsReplacement = true, Markers = null };
    var replacementOutput = Path.Combine(temp, "replacement.bank");
    var replacementAdded = await BankBuildService.BuildToFileAsync(mixedOutput, replacementOutput, replacementItems, settings);
    Check(replacementAdded.Count == 1 && replacementAdded[0].IsReplacement && replacementAdded[0].Frames < tracks[0].SampleLength, "FH2 shortened replacement failed.");
    ApplyRadioInfo(radioForBuild, firstStation, bankNames[0], replacementAdded, replacementItems);
    Check(radioForBuild.StationByNumber(firstStation.Number)!.ReadTracks().Single(t => t.SoundName == tracks[0].SoundName).SampleLength == 2000, "FH2 replacement XML duration failed.");
    File.Copy(miniSource, replacementOutput + ".bak");
    var restoreItems = repeatItems.ToList();
    restoreItems[0] = restoreItems[0] with { RestoreFromBak = true };
    var restoreOutput = Path.Combine(temp, "restored.bank");
    var restoreAdded = await BankBuildService.BuildToFileAsync(replacementOutput, restoreOutput, restoreItems, settings);
    Check(restoreAdded.Count == 1 && restoreAdded[0].Frames == tracks[0].SampleLength, "FH2 XMA backup restoration did not return original dimensions.");
    ApplyRadioInfo(radioForBuild, firstStation, bankNames[0], restoreAdded, restoreItems);
    var restoredXml = radioForBuild.StationByNumber(firstStation.Number)!.ReadTracks().Single(t => t.SoundName == tracks[0].SoundName);
    var originalXml = RadioInfo.Load(GameScanner.RadioInfoPathByFile(game, languageFiles[0])!).StationByNumber(firstStation.Number)!.ReadTracks().Single(t => t.SoundName == tracks[0].SoundName);
    var restoredLength = (long) Math.Round(tracks[0].SampleLength * 1000.0 / tracks[0].SampleRate, MidpointRounding.AwayFromZero);
    Check(restoredXml.SampleLength == restoredLength && !restoredXml.Replaced, "FH2 XMA backup restoration left replacement duration or flags in XML.");
    Check(originalXml.Markers!.All(m => restoredXml.Markers!.TryGetValue(m.Key, out var position) && position == m.Value), "FH2 XMA backup restoration changed original markers.");
    Check(FevBank.ReadSkeleton(restoreOutput).Mode == 15, "FH2 backup restoration reverted the bank codec.");
    DecodeAndCheck(restoreOutput, 0, Path.Combine(temp, "restored-first.wav"), tracks[0].SampleLength);

    var emptySource = GameScanner.BankPath(game, bankNames[0] + "LFE")!;
    var emptyOutput = Path.Combine(temp, "filled-empty.bank");
    var singleItem = new BuildItem(wavName, true, wav, "Empty bank test", "Artist", null, true);
    var emptyAdded = await BankBuildService.BuildToFileAsync(emptySource, emptyOutput, [singleItem], settings);
    Check(emptyAdded.Count == 1 && FevBank.ReadSkeleton(emptyOutput).Mode == 15 && FevBank.ReadStblCountFromFile(emptyOutput) == 1, "Empty bank Vorbis filling regressed.");
    DecodeAndCheck(emptyOutput, 0, Path.Combine(temp, "filled-empty.wav"), emptyAdded[0].Frames);

    var fadpcmFsb = Path.Combine(temp, "fadpcm.fsb");
    var cache = Path.Combine(temp, "fsbank-cache");
    Directory.CreateDirectory(cache);
    RunTool(Tools.FsbankclPath, $"-format fadpcm -o \"{fadpcmFsb}\" \"{wav}\" -cache_dir \"{cache}\"");
    var fadpcmData = File.ReadAllBytes(fadpcmFsb);
    var (fadpcmHeader, fadpcmLayout) = Fsb5.ReadLayout(fadpcmData);
    var fadpcm = fadpcmLayout[0];
    var fadpcmSource = Path.Combine(temp, "fadpcm-source.bank");
    FevBank.AssembleToFile(fadpcmSource, skel.FmtChunk, skel.ListPayload, skel.StblRelOff, skel.StblOldSize,
        FevBank.BuildStbl([(Lookup.SoundNameToId(tracks[0].SoundName), 0)]), fadpcmHeader,
        [fadpcm.Header], [new FevBank.DataRef(fadpcmFsb, 60 + BitConverter.ToUInt32(fadpcmData, 12) + BitConverter.ToUInt32(fadpcmData, 16) + fadpcm.DataOff, fadpcm.DataLen)]);
    var fadpcmOutput = Path.Combine(temp, "fadpcm-rebuilt.bank");
    var fadpcmAdded = await BankBuildService.BuildToFileAsync(fadpcmSource, fadpcmOutput, [items[0], singleItem], settings);
    Check(fadpcmAdded.Count == 1 && FevBank.ReadSkeleton(fadpcmOutput).Mode == 16, "Existing FADPCM bank codec changed during rebuilding.");
    CheckSampleDataEqual(fadpcmSource, fadpcmOutput, 0);
    var fadpcmDecoded = Path.Combine(temp, "fadpcm-rebuilt.wav");
    RunTool(Tools.VgmstreamPath, $"-i -w -s 2 -o \"{fadpcmDecoded}\" \"{fadpcmOutput}\"");
    Check(WaveformService.Probe(fadpcmDecoded).Frames == fadpcmAdded[0].Frames, "Rebuilt FADPCM audio cannot be decoded.");
    await TestCopiedWorkflowAsync(game, temp, miniSource, firstStation, wav, mp3, settings);
    Console.WriteLine($"Verified {languageFiles.Count} languages, {bankNames.Count} stations, {total} localized tracks, whole-bank Vorbis conversion, WAV/MP3/XMA imports, repeat builds, replacement and XMA backup restore.");
}

static MainWindowViewModel CreateXmlWriter(string game, IReadOnlyList<string> languageFiles)
{
    var vm = (MainWindowViewModel) RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
    void Set(string name, object value)
    {
        var field = typeof(MainWindowViewModel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(nameof(MainWindowViewModel), name);
        field.SetValue(vm, value);
    }
    Set("<Settings>k__BackingField", new AppSettings { GamePath = game });
    Set("<Languages>k__BackingField", new ObservableCollection<LangOption>(languageFiles.Select(f => new LangOption(StationBackupService.LangCode(f), f))));
    Set("_selectedLanguage", new LangOption("ALL", MainWindowViewModel.AllLanguagesFile));
    return vm;
}

static void TestPlaylistMinimums(string temp)
{
    var updateMinimum = typeof(MainWindowViewModel).GetMethod("UpdatePlaylistMinimum", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMethodException(nameof(MainWindowViewModel), "UpdatePlaylistMinimum");
    foreach (var (version, minimum) in new[] { (2, 20), (3, 12), (4, 20), (5, 20), (6, 20) })
    {
        var game = Path.Combine(temp, "playlist-minimum-FH" + version);
        Directory.CreateDirectory(game);
        var executable = version switch
        {
            2 => "forza_durango_release_final.exe",
            3 => "forza_x64_release_final.exe",
            _ => $"ForzaHorizon{version}.exe",
        };
        File.WriteAllBytes(Path.Combine(game, executable), []);
        if (version == 3)
        {
            File.WriteAllText(Path.Combine(game, "AppxManifest.xml"), """<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Identity Name="Microsoft.OpusPG"/><Applications><Application Executable="forza_x64_release_final.exe"/></Applications></Package>""");
        }
        Check(GameScanner.DetectVersion(game) == version, "Playlist fixture has an incorrect game version.");
        var vm = CreateXmlWriter(game, ["RadioInfo_EN.xml"]);
        var tracks = new ObservableCollection<TrackItemViewModel>(Enumerable.Range(0, minimum - 1).Select(i =>
            new TrackItemViewModel(new RadioTrack { SoundName = $"FH{version}_MIN_{i}", Origin = TrackOrigin.Original, Enabled = true })));
        var tracksField = typeof(MainWindowViewModel).GetField("<Tracks>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(nameof(MainWindowViewModel), "Tracks");
        tracksField.SetValue(vm, tracks);
        updateMinimum.Invoke(vm, null);
        Check(vm.MinimumActiveTracks == minimum, $"FH{version} uses an incorrect playlist minimum.");
        vm.Recount();
        Check(vm.PlaylistLow, $"FH{version} allowed {minimum - 1} active tracks below its minimum.");
        var boundary = new TrackItemViewModel(new RadioTrack { SoundName = Naming.MakeSoundName(version), Origin = TrackOrigin.Custom, Enabled = true });
        tracks.Add(boundary);
        vm.Recount();
        Check(!vm.PlaylistLow, $"FH{version} rejected exactly {minimum} active tracks including a custom track.");
        boundary.Enabled = false;
        vm.Recount();
        Check(vm.PlaylistLow, $"FH{version} counted a disabled track toward its minimum.");
        var otherBanksField = typeof(MainWindowViewModel).GetField("_otherBanksActive", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(nameof(MainWindowViewModel), "_otherBanksActive");
        otherBanksField.SetValue(vm, 1);
        vm.Recount();
        Check(!vm.PlaylistLow, $"FH{version} ignored active tracks in other station banks.");
    }
}

static async Task TestCopiedWorkflowAsync(string originalGame, string temp, string originalBank,
    StationInfo station, string wav, string mp3, AppSettings settings)
{
    var game = Path.Combine(temp, "workflow");
    var audio = Path.Combine(game, "media", "audio");
    var bankDirectory = Path.Combine(audio, "fmodanthem", "durango");
    Directory.CreateDirectory(bankDirectory);
    File.WriteAllBytes(Path.Combine(game, "forza_durango_release_final.exe"), []);
    var bankName = station.BankName("Tracks");
    var bank = Path.Combine(bankDirectory, bankName + ".bank");
    File.Copy(originalBank, bank);
    var originalIds = FevBank.ReadStblIdsFromFile(originalBank);
    var languages = GameScanner.LanguageFiles(originalGame);
    foreach (var file in languages)
    {
        var radio = RadioInfo.Load(GameScanner.RadioInfoPathByFile(originalGame, file)!);
        radio.StationByNumber(station.Number)!.ReconcileTracks(originalIds, Lookup.SoundNameToId);
        File.WriteAllText(Path.Combine(audio, file), radio.Document.ToString());
    }
    var referenceFile = languages.Single(f => StationBackupService.LangCode(f) == "EN");
    var reference = RadioInfo.Load(Path.Combine(audio, referenceFile));
    var tracks = BankReader.ReadTracks(bank, reference, station.Number).Tracks;
    var items = tracks.Select(t => new BuildItem(t.SoundName, false, null, t.DisplayName, t.Artist, null, t.Enabled, t.Markers, MarkerSampleRate: t.SampleRate)).ToList();
    items[0] = items[0] with { SourcePath = wav, IsReplacement = true, Markers = null };
    var customName = Naming.MakeSoundName(600);
    items.Add(new BuildItem(customName, true, wav, "Workflow custom", "Artist", null, true));
    var writeBank = typeof(MainWindowViewModel).GetMethod("BuildAndWriteBankAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
    var buildTask = (Task<(IReadOnlyList<AddedSample> Added, long Written)>) writeBank.Invoke(null, [bank, items, settings, (Action<string>) Console.WriteLine])!;
    var build = await buildTask;
    Check(FevBank.ReadSkeleton(bank).Mode == 15 && FevBank.ReadSkeleton(bank + ".bak").Mode == 10, "FH2 workflow did not preserve the native XMA bank backup.");
    var vm = CreateXmlWriter(game, languages);
    var writeXml = typeof(MainWindowViewModel).GetMethod("WriteRadioInfosAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    var xmlTask = (Task<(int SavedXml, int Dead)>) writeXml.Invoke(vm, [station, bankName, build.Added, items])!;
    Check((await xmlTask).SavedXml == languages.Count, "FH2 workflow did not write all language files.");
    foreach (var file in languages)
    {
        var path = Path.Combine(audio, file);
        Check(File.Exists(path + ".bak"), "FH2 workflow omitted an original XML backup.");
        var current = RadioInfo.Load(path);
        var original = RadioInfo.Load(path + ".bak");
        Check(current.StationByNumber(station.Number)!.ReadTracks().Count == 3, "FH2 localized workflow lost tracks.");
        foreach (var other in current.Document.Descendants("RadioStation").Where(s => (int?) s.Attribute("Number") != station.Number))
        {
            var number = (int) other.Attribute("Number")!;
            Check(XNode.DeepEquals(other, original.Document.Descendants("RadioStation").Single(s => (int?) s.Attribute("Number") == number)), "FH2 localized workflow changed another station.");
        }
    }

    var loaded = BankReader.ReadTracks(bank, RadioInfo.Load(Path.Combine(audio, referenceFile)), station.Number).Tracks;
    var changes = loaded.Select(t => new BuildItem(t.SoundName, false, null, t.DisplayName, t.Artist, null, t.Enabled, t.Markers, MarkerSampleRate: t.SampleRate)).ToList();
    var customIndex = changes.FindIndex(t => t.SoundName == customName);
    changes[customIndex] = changes[customIndex] with
    {
        Markers = new Dictionary<string, long>(changes[customIndex].Markers!) { ["DJDrop"] = -1, ["TrackBreakDown"] = 48000 },
    };
    xmlTask = (Task<(int SavedXml, int Dead)>) writeXml.Invoke(vm, [station, bankName, Array.Empty<AddedSample>(), changes])!;
    await xmlTask;
    foreach (var file in languages)
    {
        var markers = RadioInfo.Load(Path.Combine(audio, file)).StationByNumber(station.Number)!.GetMarkers(customName)!;
        Check(markers["DJDrop"] == -1 && markers["TrackBreakDown"] == 1000, "FH2 off marker or breakdown was not propagated to every language.");
    }

    var restoreFsb = Path.Combine(temp, "workflow-original.fsb");
    File.WriteAllBytes(restoreFsb, TrackPack.ExtractSampleFsb(originalBank, 0));
    var originalTrack = tracks[0];
    var customTrack = loaded.Single(t => t.SoundName == customName);
    var otherTrack = loaded.Single(t => t.SoundName == tracks[1].SoundName);
    var backupSources = new List<BackupTrackSource>
    {
        new(originalTrack.SoundName, false, false, false, restoreFsb, null, -1,
            originalTrack.DisplayName, originalTrack.Artist, originalTrack.SampleLength, originalTrack.SampleRate,
            null, true, originalTrack.Markers?.ToDictionary()),
        new(customName, true, true, false, null, mp3, customTrack.SubIndex,
            "Staged custom replacement", "Artist", 96000, 48000, null, false,
            new Dictionary<string, long> { ["TrackStart"] = 0, ["End"] = 96000, ["TrackBreakDown"] = 48000, ["DJDrop"] = -1 }),
    };
    BackupEntry? backup = null;
    try
    {
        backup = StationBackupService.Create("FH2Regression_" + Guid.NewGuid().ToString("N"), game, station, "Tracks", backupSources);
        Check(backup.Manifest.GameLabel == "FH2" && StationBackupService.Matches(backup.Manifest, game, station), "FH2 manual backup identity is incorrect.");
        Check(Directory.GetFiles(Path.Combine(backup.Folder, "xml")).Length == languages.Count, "FH2 manual backup omitted station XML snapshots.");
        var originalAudio = backup.Manifest.Tracks.Single(t => t.SoundName == originalTrack.SoundName);
        var customAudio = backup.Manifest.Tracks.Single(t => t.SoundName == customName);
        Check(File.ReadAllBytes(Path.Combine(backup.Folder, originalAudio.Audio)).SequenceEqual(File.ReadAllBytes(restoreFsb)), "FH2 manual backup stored current audio instead of the staged original restore.");
        Check(File.ReadAllBytes(Path.Combine(backup.Folder, customAudio.Audio)).SequenceEqual(File.ReadAllBytes(mp3)), "FH2 manual backup stored old custom audio instead of its staged replacement.");
        foreach (var file in languages)
        {
            var path = Path.Combine(audio, file);
            var radio = RadioInfo.Load(path);
            foreach (var playlist in radio.Document.Descendants("RadioStation").Single(s => (int?) s.Attribute("Number") == station.Number).Elements("Playlist").Where(p => (string?) p.Attribute("Type") is "FreeroamTracks" or "EventTracks")) playlist.RemoveNodes();
            radio.Save(path);
        }
        var restored = await StationBackupService.RestoreAsync(backup, game, settings);
        Check(restored.Banks == 1 && restored.Langs == languages.Count, "FH2 manual backup restore did not restore all language files.");
        var ids = FevBank.ReadStblIdsFromFile(bank);
        Check(ids.SetEquals([Lookup.SoundNameToId(originalTrack.SoundName), Lookup.SoundNameToId(customName)]), "FH2 manual backup restore kept an omitted original track.");
        foreach (var file in languages)
        {
            var radio = RadioInfo.Load(Path.Combine(audio, file));
            var editor = radio.StationByNumber(station.Number)!;
            var restoredTracks = editor.ReadTracks();
            Check(restoredTracks.Count == 2 && restoredTracks.All(t => ids.Contains(Lookup.SoundNameToId(t.SoundName))), "FH2 manual backup left missing or dangling XML track definitions.");
            Check(!restoredTracks.Single(t => t.SoundName == originalTrack.SoundName).Replaced && !restoredTracks.Single(t => t.SoundName == customName).Enabled, "FH2 manual backup restore changed replacement or enabled state.");
            var eventNames = radio.Document.Descendants("RadioStation").Single(s => (int?) s.Attribute("Number") == station.Number).Elements("Playlist").Single(p => (string?) p.Attribute("Type") == "EventTracks").Elements("Entry").Select(e => (string?) e.Attribute("SoundName")).ToHashSet();
            var wasEventTrack = XElement.Load(Path.Combine(backup.Folder, "xml", StationBackupService.LangCode(file) + ".xml")).Elements("Playlist").Single(p => (string?) p.Attribute("Type") == "EventTracks").Elements("Entry").Any(e => (string?) e.Attribute("SoundName") == originalTrack.SoundName);
            Check(eventNames.Contains(originalTrack.SoundName) == wasEventTrack, "FH2 manual backup restore changed original event playlist membership.");
            Check(!editor.ReadTracks().Any(t => t.SoundName == otherTrack.SoundName), "FH2 manual backup restore left an omitted original in the playlist.");
        }
        var restoredAudio = FevBank.ReadTrackInfoFromFile(bank).Single(t => t.Id == Lookup.SoundNameToId(originalTrack.SoundName));
        Check(restoredAudio.Frames == originalTrack.SampleLength && FevBank.ReadSkeleton(bank).Mode == 15, "FH2 manual backup restore changed original audio timing or reverted the codec.");
    }
    finally
    {
        if (backup is not null) StationBackupService.Delete(backup);
    }

    var exported = Path.Combine(temp, "workflow-export.zip");
    await TrackPack.ExportAsync(exported, bankName, bank,
        [new ExportItem(restoreFsb, -1, originalTrack.DisplayName, originalTrack.Artist, originalTrack.SampleLength, originalTrack.SampleRate, null, true, "default", originalTrack.Markers?.ToDictionary())], null, null, default);
    var imported = TrackPack.Load(exported);
    var exportedTrack = imported.Manifest.Tracks.Single();
    Check(File.ReadAllBytes(Path.Combine(imported.Dir, exportedTrack.File)).SequenceEqual(File.ReadAllBytes(restoreFsb)), "FH2 pack roundtrip changed staged restored audio.");
    Check(exportedTrack.Markers!["TrackBreakDown"] == originalTrack.Markers!["TrackBreakDown"] && exportedTrack.SampleRate == 48000, "FH2 pack roundtrip changed marker units.");

    var automatic = BackupService.RestoreStation(game, station);
    Check(automatic.Banks == 1 && automatic.Langs == languages.Count && File.ReadAllBytes(bank).SequenceEqual(File.ReadAllBytes(bank + ".bak")), "FH2 automatic restore did not restore the original bank and all XML files.");
    var restoredIds = FevBank.ReadStblIdsFromFile(bank);
    foreach (var file in languages)
    {
        var restoredRadio = RadioInfo.Load(Path.Combine(audio, file));
        Check(restoredRadio.StationByNumber(station.Number)!.ReadTracks().All(t => restoredIds.Contains(Lookup.SoundNameToId(t.SoundName))), "FH2 automatic restore left custom or dangling track definitions.");
    }
    var truncated = Path.Combine(temp, "truncated.bank");
    File.Copy(originalBank, truncated);
    using (var stream = new FileStream(truncated, FileMode.Open, FileAccess.Write)) stream.SetLength(FevBank.ReadSkeleton(originalBank).DataStartAbs + 1);
    try
    {
        TrackPack.ExtractSampleFsb(truncated, 0);
        throw new InvalidOperationException("Truncated FH2 audio was exported with silent padding.");
    }
    catch (InvalidDataException) { }
    catch (EndOfStreamException) { }
    Console.WriteLine("Verified copied FH2 workflow: automatic backups, all-language XML writes, off markers, staged backup audio, restore into empty playlists, pack roundtrip and original station restore.");
}

static void ApplyRadioInfo(RadioInfo radio, StationInfo station, string bankName, IReadOnlyList<AddedSample> added, List<BuildItem> items)
{
    var method = typeof(MainWindowViewModel).GetMethod("ApplyRadioInfo", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(MainWindowViewModel), "ApplyRadioInfo");
    method.Invoke(null, [radio, station, bankName, added, items]);
}

static void RunTool(string path, string arguments)
{
    var result = Proc.Run(path, arguments);
    Check(result.ExitCode == 0, $"{Path.GetFileName(path)} failed: {result.StdErr}\n{result.StdOut}");
}

static void DecodeAndCheck(string bankPath, int index, string output, long frames)
{
    RunTool(Tools.VgmstreamPath, $"-i -w -s {index + 1} -o \"{output}\" \"{bankPath}\"");
    var decoded = WaveformService.Probe(output);
    Check(decoded.SampleRate == 48000 && decoded.Frames == frames, $"Decoded Vorbis sample {index} has incorrect dimensions.");
}

static void CheckSampleDataEqual(string firstPath, string secondPath, int index)
{
    static byte[] ReadSample(string path, int index)
    {
        var skeleton = FevBank.ReadSkeleton(path);
        var sample = Fsb5.ReadLayout(skeleton.Fsb5HeaderRegion).Samples[index];
        using var stream = File.OpenRead(path);
        stream.Position = skeleton.DataStartAbs + sample.DataOff;
        var data = new byte[checked((int) sample.DataLen)];
        stream.ReadExactly(data);
        return data;
    }
    Check(ReadSample(firstPath, index).SequenceEqual(ReadSample(secondPath, index)), "FH2 repeat build changed existing Vorbis audio bytes.");
}

static string Horizon2Xml() => """
<Radio><RadioStations><RadioStation Name="Horizon Pulse" Number="1" RecentlyPlayedListSize="-1">
  <Playlist Type="FreeroamTracks"><Entry EventName="/Master/Radio/Track" SoundName="HZ2_R1_Test" SampleLength="120000" DisplayName="Bridge & Tunnel" Artist="Test" TrackStart="100" DJDrop="15000" TrackDrop="15000" TrackBreakDown="80000" StingerStart="114000" DJStart="115000" End="120000" /></Playlist>
  <Playlist Type="EventTracks"><Entry EventName="/Master/Radio/Track" SoundName="HZ2_R1_Test" SampleLength="120000" DisplayName="Bridge & Tunnel" Artist="Test" TrackStart="100" DJDrop="15000" TrackDrop="15000" TrackBreakDown="80000" StingerStart="114000" DJStart="115000" End="120000" /></Playlist>
  <Playlist Type="DJ"><Entry EventName="/Master/Radio/DJ" SoundName="HZ2_VO_Test" SampleLength="1000" /></Playlist>
</RadioStation></RadioStations></Radio>
""";

static string ModernXml() => """
<Radio><RadioStations><RadioStation Name="Horizon Pulse" Number="1">
  <Banks><Bank Name="R1_Tracks_Main" /></Banks>
  <SampleList Type="Track"><Sample SoundName="HZ_R1_Test" SampleLength="5760000" SampleRate="48000" DisplayName="Test" Artist="Artist"><Marker Name="TrackStart" Position="4800" /><Marker Name="End" Position="5759999" /></Sample></SampleList>
  <PlayList Type="FreeRoam"><Entry Name="HZ_R1_Test" /></PlayList>
  <PlayList Type="Event"><Entry Name="HZ_R1_Test" /></PlayList>
</RadioStation></RadioStations></Radio>
""";
