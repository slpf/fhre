using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml.Linq;
using FH6RB.Core;
using FH6RB.Services;
using static TestSupport;

internal static class InstallationChecks
{
    public static async Task RunAsync(string sourceGame, string scratch)
    {
        Check(GameScanner.DetectVersion(sourceGame) == 3, "The integration source is not an identified FH3 installation.");
        var names = GameScanner.RadioBankNames(sourceGame);
        Check(names.Count == 10, "Expected eight base music banks and two DLC banks.");
        var languageFiles = GameScanner.LanguageFiles(sourceGame);
        Check(languageFiles.Count == 10, "Expected all ten real FH3 language files.");
        var language = languageFiles.First(f => f.Equals("RadioInfo_EN.xml", StringComparison.OrdinalIgnoreCase));
        var sourceFiles = names.Select(n => GameScanner.BankPath(sourceGame, n)!)
            .Concat(languageFiles.Select(f => GameScanner.RadioInfoPathByFile(sourceGame, f)!)).ToArray();
        var original = sourceFiles.ToDictionary(p => p, p => (new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)));
        var cache = Path.Combine(AppContext.BaseDirectory, "fixture-cache", "pristine-fh3");
        var game = Path.Combine(scratch, "FH3-workspace-copy");
        Check(!Path.GetFullPath(game).StartsWith(Path.GetFullPath(sourceGame), StringComparison.OrdinalIgnoreCase), "The fixture overlaps the real installation.");
        Directory.CreateDirectory(game);
        foreach (var source in sourceFiles)
        {
            var relative = Path.GetRelativePath(sourceGame, source);
            Check(!relative.StartsWith("..", StringComparison.Ordinal), "A source fixture escaped the installation root.");
            var cached = Path.Combine(cache, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            if (!File.Exists(cached) || new FileInfo(cached).Length != new FileInfo(source).Length
                || File.GetLastWriteTimeUtc(cached) != File.GetLastWriteTimeUtc(source))
            {
                File.Copy(source, cached, overwrite: true);
                File.SetLastWriteTimeUtc(cached, File.GetLastWriteTimeUtc(source));
            }
            var destination = Path.Combine(game, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(cached, destination);
        }
        File.WriteAllBytes(Path.Combine(game, "forza_x64_release_final.exe"), []);
        File.WriteAllText(Path.Combine(game, "AppxManifest.xml"), SyntheticChecks.Manifest);
        var xml = GameScanner.RadioInfoPathByFile(game, language)!;
        var radio = RadioInfo.Load(xml);
        var localized = languageFiles.ToDictionary(f => f, f => RadioInfo.Load(GameScanner.RadioInfoPathByFile(game, f)!));
        var catalog = RadioCatalog.Build(game, radio);
        Check(catalog.Count == 8 && GameScanner.RadioBankNames(game).Count == 10, "Workspace fixture lost FH3 stations or DLC variants.");
        var main = CreateVm(game, language);
        try { Check(main.Stations.Count == 8 && main.Languages.Count == 1, "MainVM did not load the copied FH3 catalog."); }
        finally { ReleaseVm(main); }
        var settings = new AppSettings { GamePath = game, LoudnessNormalize = false, EncodeParallelism = 2, VorbisQuality = 95 };
        var records = 0;
        var orphans = 0;
        var validatedMarkers = 0;
        var playlists = catalog.ToDictionary(s => s.Number, s => Playlists(radio, s.Number));
        var ends = radio.Document.Descendants("Sample").Select(s => ((string?) s.Attribute("SoundName"), (string?) s.Attribute("End"))).ToArray();
        var localizedPlaylists = localized.ToDictionary(p => p.Key, p => catalog.ToDictionary(s => s.Number, s => Playlists(p.Value, s.Number)));
        var localizedSamples = localized.ToDictionary(p => p.Key, p => p.Value.Document.Descendants("Sample").Select(e =>
            ((string?) e.Attribute("SoundName"), (string?) e.Attribute("DisplayName"), (string?) e.Attribute("Artist"), (string?) e.Attribute("End"))).ToArray());
        foreach (var station in catalog)
        {
            foreach (var variant in station.Variants)
            {
                var bankName = station.BankName(variant);
                var bank = GameScanner.BankPath(game, bankName)!;
                var read = BankReader.ReadTracks(bank, radio, station.Number);
                Check(read.Error is null && read.Tracks.Count > 0, bankName + ": " + read.Error);
                Check(read.Tracks.All(t => t.UsesTrackBreakDown && !t.UsesMilliseconds && t.SampleRate == 48000 && t.BankId is not null), "FH3 bank tracks lost native profile or source identity.");
                foreach (var track in read.Tracks.Where(t => !t.SoundName.StartsWith("sound_", StringComparison.Ordinal)))
                {
                    var errors = new FH6RB.ViewModels.EditWindowViewModel(new FH6RB.ViewModels.TrackItemViewModel(track)).ValidateMarkers();
                    Check(errors.Count == 0, bankName + ": unchanged native markers were rejected for " + track.SoundName + ": " + string.Join(", ", errors));
                    validatedMarkers++;
                }
                var items = read.Tracks.Select(t => Item(t) with { Enabled = t.SoundName.StartsWith("sound_", StringComparison.Ordinal) || t.Enabled }).ToList();
                foreach (var orphan in read.Tracks.Where(t => t.SoundName.StartsWith("sound_", StringComparison.Ordinal)))
                {
                    var row = new FH6RB.ViewModels.TrackItemViewModel(orphan);
                    Check(row.IsBankOnly && row.CanPlay && !row.CanToggle && !row.CanEdit && !row.CanReplace && !row.CanEditMarkers,
                        "A real FH3 orphan row exposed operations requiring missing XML metadata.");
                }
                var output = Path.Combine(scratch, bankName + ".roundtrip.bank");
                var added = await BankBuildService.BuildToFileAsync(bank, output, items, settings);
                CheckState(BankState(bank), BankState(output), bankName + " no-op");
                Check(FevBank.ReadSkeleton(bank).Mode == FevBank.ReadSkeleton(output).Mode, "A no-op build changed native codec mode.");
                Apply(radio, station, bankName, added, items);
                foreach (var local in localized.Values)
                {
                    var localRead = BankReader.ReadTracks(bank, local, station.Number);
                    Check(localRead.Error is null, "A real localized FH3 XML could not overlay bank metadata.");
                    Apply(local, station, bankName, added, localRead.Tracks.Select(Item).ToList());
                }
                records += read.Tracks.Count;
                orphans += read.Tracks.Count(t => t.SoundName.StartsWith("sound_", StringComparison.Ordinal));
                File.Delete(output);
            }
        }
        Check(records == 156 && orphans == 3, "No-op integration lost known orphan STBL records.");
        Check(validatedMarkers == 153, "Not all 153 matched native tracks passed marker-editor validation.");
        Check(catalog.All(s => Playlists(radio, s.Number) == playlists[s.Number]), "A real-bank no-op changed playlist membership or order.");
        Check(ends.SequenceEqual(radio.Document.Descendants("Sample").Select(s => ((string?) s.Attribute("SoundName"), (string?) s.Attribute("End")))), "A real-bank no-op changed native End markers.");
        Check(radio.Document.Descendants("Sample").All(s => !((string?) s.Attribute("SoundName") ?? "").StartsWith("sound_", StringComparison.Ordinal)), "Orphan bank identities leaked into XML samples.");
        foreach (var (file, local) in localized)
        {
            Check(catalog.All(s => Playlists(local, s.Number) == localizedPlaylists[file][s.Number]), file + " no-op changed localized playlist membership or order.");
            var after = local.Document.Descendants("Sample").Select(e =>
                ((string?) e.Attribute("SoundName"), (string?) e.Attribute("DisplayName"), (string?) e.Attribute("Artist"), (string?) e.Attribute("End"))).ToArray();
            Check(localizedSamples[file].SequenceEqual(after), file + " no-op changed localized names, artists or End markers.");
            CheckNativeXml(local, file);
        }
        Console.WriteLine("Verified all ten real-bank no-op builds and all ten real language XMLs, all 156 STBL records including three orphans, compressed SHA256, native metadata, playlist composition and End markers.");
        foreach (var file in GameScanner.LanguageFiles(game)) File.Copy(GameScanner.RadioInfoPathByFile(game, file)!, GameScanner.RadioInfoPathByFile(game, file)! + ".bak");
        foreach (var name in names)
        {
            var bank = GameScanner.BankPath(game, name)!;
            File.Copy(bank, bank + ".bak");
        }
        var input = Path.Combine(scratch, "new-audio-44100.wav");
        WriteWave(input);
        await CheckEncodedWorkflowAsync(game, input, catalog.Single(s => s.Number == 1), "R1_Tracks", settings, scratch);
        await CheckEncodedWorkflowAsync(game, input, catalog.Single(s => s.Number == 6), "R6_Tracks_PDLC2", settings, scratch);
        await CheckBackupsAsync(game, catalog.Single(s => s.Number == 1), settings);
        Check(original.All(pair => new FileInfo(pair.Key).Length == pair.Value.Length && File.GetLastWriteTimeUtc(pair.Key) == pair.Value.Item2), "A real source fixture changed during workspace-only integration.");
        Console.WriteLine("Verified workspace-only FH3 encoding, replacement/custom markers and gains, FADPCM padding, inactive flags and station/original backup restores.");
    }

    private static async Task CheckEncodedWorkflowAsync(string game, string input, StationInfo station, string bankName, AppSettings settings, string scratch)
    {
        var xml = GameScanner.RadioInfoPathByFile(game, "RadioInfo_EN.xml")!;
        var radio = RadioInfo.Load(xml);
        var bank = GameScanner.BankPath(game, bankName)!;
        var read = BankReader.ReadTracks(bank, radio, station.Number);
        var items = read.Tracks.Select(Item).ToList();
        var replacement = items.FindIndex(i => !i.SoundName.StartsWith("sound_", StringComparison.Ordinal));
        var originalId = items[replacement].BankId!.Value;
        var expected = Path.Combine(scratch, bankName + ".expected.wav");
        await Loudnorm.RenderAsync(input, expected, settings, -6);
        var expectedInfo = AudioBuildValidation.Inspect(expected);
        var markers = RadioStationEditor.ComputeAutoMarkers(expectedInfo.Frames, 48000, false, true);
        items[replacement] = items[replacement] with { IsReplacement = true, SourcePath = input, GainDb = -6,
            DisplayName = "Replacement & FH3", Artist = "Regression", Markers = markers, MarkerSampleRate = 48000 };
        var custom = Naming.MakeSoundName(station.Number * 1000 + 99);
        items.Add(new BuildItem(custom, true, input, "Custom & FH3", "Regression", -2, false, markers,
            MarkerSampleRate: 48000, Normalization: new(true, -25, -3)));
        var log = new ConcurrentQueue<string>();
        var temporary = Path.Combine(scratch, bankName + ".encoded.bank");
        var before = BankState(bank);
        var mode = FevBank.ReadSkeleton(bank).Mode;
        var added = await BankBuildService.BuildToFileAsync(bank, temporary, items, settings, log.Enqueue);
        var state = BankState(temporary);
        Check(state.Count == before.Count + 1 && state.ContainsKey(originalId), "Adding/replacing audio lost original STBL identity.");
        foreach (var (id, data) in before.Where(p => p.Key != originalId)) Check(state[id] == data, "Encoding a track changed an unrelated compressed sample.");
        var frames = mode == 16 ? (expectedInfo.Frames + 255) / 256 * 256 : expectedInfo.Frames;
        Check(state[originalId].Frames == frames && state[originalId].Rate == 48000 && state[originalId].Channels == 2
            && FevBank.ReadSkeleton(temporary).Mode == mode, "FH3 native codec or FADPCM padding changed.");
        const string stage = "wav.prepared ";
        var prepared = log.Single(l => l.Contains($"[track={replacement} ", StringComparison.Ordinal) && l.Contains(stage, StringComparison.Ordinal));
        using (var json = JsonDocument.Parse(prepared[(prepared.IndexOf(stage, StringComparison.Ordinal) + stage.Length)..]))
            Check(json.RootElement.GetProperty("PcmSha256").GetString() == expectedInfo.PcmSha256, "FH3 encoder input ignored manual gain.");
        var customExpected = Path.Combine(scratch, bankName + ".normalized-custom.wav");
        await Loudnorm.RenderAsync(input, customExpected,
            new AppSettings { LoudnessNormalize = true, TargetLufs = -25, TargetTruePeak = -3 }, -2);
        var customPrepared = log.Single(l => l.Contains($"[track={items.Count - 1} ", StringComparison.Ordinal) && l.Contains(stage, StringComparison.Ordinal));
        using (var json = JsonDocument.Parse(customPrepared[(customPrepared.IndexOf(stage, StringComparison.Ordinal) + stage.Length)..]))
            Check(json.RootElement.GetProperty("PcmSha256").GetString() == AudioBuildValidation.Inspect(customExpected).PcmSha256,
                "FH3 custom encoding ignored its own normalization settings.");
        var preview = AudioDecoder.DecodeAdded(input, settings, gainDb: -6);
        Check(AudioBuildValidation.Inspect(preview).PcmSha256 == expectedInfo.PcmSha256, "FH3 preview and encoder input differ.");
        var decoded = AudioDecoder.DecodeBank(temporary, replacement);
        Check(AudioBuildValidation.Inspect(decoded).Frames == frames, "Encoded FH3 audio could not be decoded at the native frame count.");
        Apply(radio, station, bankName, added, items);
        foreach (var name in new[] { items[replacement].SoundName, custom })
        {
            var track = radio.StationByNumber(station.Number)!.ReadTracks().Single(t => t.SoundName == name);
            Check(track.UsesTrackBreakDown && track.Markers!.ContainsKey("TrackBreakDown") && !track.Markers.ContainsKey("PostDrop"), "Encoded FH3 XML used non-native marker names.");
        }
        Check(!radio.StationByNumber(station.Number)!.ReadTracks().Single(t => t.SoundName == custom).Enabled, "An inactive FH3 custom track was enabled during commit.");
        radio.Save(xml);
        File.Move(temporary, bank, overwrite: true);
    }

    private static async Task CheckBackupsAsync(string game, StationInfo station, AppSettings settings)
    {
        var xml = GameScanner.RadioInfoPathByFile(game, "RadioInfo_EN.xml")!;
        var radio = RadioInfo.Load(xml);
        var variant = station.Variants.Single(v => station.BankName(v) == "R1_Tracks");
        var bank = GameScanner.BankPath(game, station.BankName(variant))!;
        var read = BankReader.ReadTracks(bank, radio, station.Number);
        var sources = read.Tracks.Select(t => new BackupTrackSource(t.SoundName, t.Origin == TrackOrigin.Custom, false, t.Replaced,
            null, null, t.SubIndex, t.DisplayName, t.Artist, t.SampleLength, t.SampleRate, null, t.Enabled, t.Markers?.ToDictionary(), BankId: t.BankId)).ToArray();
        BackupEntry? backup = null;
        try
        {
            backup = StationBackupService.Create("FH3Regression-" + Guid.NewGuid().ToString("N"), game, station, variant, sources);
            Check(StationBackupService.Matches(backup.Manifest, game, station) && backup.Manifest.Variants.Count == 2,
                "FH3 station backup lost base/DLC variants or game compatibility.");
            Check(backup.Manifest.Tracks.Count(t => t.SoundName.StartsWith("sound_", StringComparison.Ordinal) && t.BankId is not null) == 2,
                "A station backup omitted orphan STBL identities.");
            var before = station.Variants.ToDictionary(v => v, v => BankState(GameScanner.BankPath(game, station.BankName(v))!));
            var custom = read.Tracks.Single(t => t.Origin == TrackOrigin.Custom);
            radio.StationByNumber(1)!.SetEnabled(custom.SoundName, true);
            radio.StationByNumber(1)!.SetSampleMeta(custom.SoundName, "Changed", "Changed");
            radio.Save(xml);
            var restored = await StationBackupService.RestoreAsync(backup, game, settings);
            Check(restored.Banks == 2 && restored.Langs == GameScanner.LanguageFiles(game).Count, "FH3 station restore missed a variant or language.");
            foreach (var v in station.Variants) CheckState(before[v], BankState(GameScanner.BankPath(game, station.BankName(v))!), "Station backup restore");
            var track = RadioInfo.Load(xml).StationByNumber(1)!.ReadTracks().Single(t => t.SoundName == custom.SoundName);
            Check(!track.Enabled && track.DisplayName == "Custom & FH3" && track.Markers!.ContainsKey("TrackBreakDown") && !track.Markers.ContainsKey("PostDrop"), "FH3 station restore changed native marker metadata or inactive state.");
            Check(RadioInfo.Load(xml).Document.Descendants("Sample").All(s => !((string?) s.Attribute("SoundName") ?? "").StartsWith("sound_", StringComparison.Ordinal)), "Orphan station restore created invalid XML metadata.");
            foreach (var file in GameScanner.LanguageFiles(game))
            {
                var local = RadioInfo.Load(GameScanner.RadioInfoPathByFile(game, file)!);
                CheckNativeXml(local, file);
                var restoredCustom = local.StationByNumber(1)!.ReadTracks().Single(t => t.SoundName == custom.SoundName);
                Check(!restoredCustom.Enabled && restoredCustom.DisplayName == "Custom & FH3", file + " lost the restored custom metadata or inactive state.");
            }
        }
        finally { if (backup is not null) StationBackupService.Delete(backup); }
        var originalRestore = BackupService.RestoreStation(game, station);
        Check(originalRestore.Banks == 2 && originalRestore.Langs == GameScanner.LanguageFiles(game).Count, "FH3 original restore missed physical bank/XML backups.");
        foreach (var v in station.Variants)
        {
            var path = GameScanner.BankPath(game, station.BankName(v))!;
            Check(Hash(path) == Hash(path + ".bak"), "FH3 original restore did not restore compressed original bytes.");
        }
        Check(RadioInfo.Load(xml).StationByNumber(1)!.CustomSoundNames().Count() == 0, "Original restore left custom FH3 XML entries.");
        foreach (var file in GameScanner.LanguageFiles(game))
        {
            var path = GameScanner.RadioInfoPathByFile(game, file)!;
            var local = RadioInfo.Load(path);
            CheckNativeXml(local, file);
            var originalStation = RadioInfo.Load(path + ".bak").Document.Descendants("RadioStation").Single(s => (int?) s.Attribute("Number") == 1);
            var restoredStation = local.Document.Descendants("RadioStation").Single(s => (int?) s.Attribute("Number") == 1);
            Check(XNode.DeepEquals(originalStation, restoredStation), file + " original restore lost localized station metadata.");
        }
    }

    private static void CheckState(Dictionary<ulong, (long Frames, int Rate, int Channels, string Hash)> expected,
        Dictionary<ulong, (long Frames, int Rate, int Channels, string Hash)> actual, string label)
    {
        Check(expected.Count == actual.Count && expected.All(p => actual.TryGetValue(p.Key, out var found) && p.Value == found),
            label + " changed STBL identities, compressed bytes, frames, rate or channels.");
    }

    private static void CheckNativeXml(RadioInfo radio, string file)
    {
        var tracks = radio.Document.Descendants("SampleList").Where(e => (string?) e.Attribute("Type") == "Track").Elements("Sample").ToArray();
        Check(tracks.Length > 0 && tracks.All(t => t.Attribute("TrackBreakDown") is not null && t.Attribute("PostDrop") is null),
            file + " lost native TrackBreakDown or introduced PostDrop.");
    }
}
