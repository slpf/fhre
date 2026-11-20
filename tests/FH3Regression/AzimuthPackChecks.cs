using System.Xml.Linq;
using FH6RB.Core;
using FH6RB.Services;
using FH6RB.ViewModels;
using static TestSupport;

internal static class AzimuthPackChecks
{
    public static async Task RunAsync(string root)
    {
        var audio = Path.Combine(root, "azimuth-pack.wav");
        WriteWave(audio);
        var empty = Path.Combine(root, "azimuth-pack.empty.bank");
        UwpBuildChecks.WriteTemplate(empty);
        var encoded = Path.Combine(root, "azimuth-pack.seed.bank");
        await BankBuildService.BuildToFileAsync(empty, encoded,
            [new BuildItem("AUDIT_NATIVE", true, audio, "Original & title", "Original artist", null, true),
             new BuildItem("AUDIT_KEEP", true, audio, "Keep original", "Keep artist", null, true)],
            new AppSettings { LoudnessNormalize = false });
        var frames = FevBank.ReadTrackInfoFromFile(encoded).First().Frames;
        var fixtures = new Dictionary<int, (string Game, string Bank, string Xml)>();
        foreach (var version in new[] { 2, 3 })
        {
            var game = Path.Combine(root, "azimuth-pack-FH" + version);
            Directory.CreateDirectory(game);
            File.WriteAllBytes(Path.Combine(game, "forza_durango_release_final.exe"), []);
            File.WriteAllText(Path.Combine(game, "AppxManifest.xml"),
                SyntheticChecks.XboxManifest.Replace("Opus", version == 2 ? "Anthem" : "Opus", StringComparison.Ordinal));
            var bankName = version == 2 ? "HZ2_MX_Radio_01_Tracks" : "R1_Tracks";
            var bank = Path.Combine(game, bankName + ".bank");
            File.Copy(encoded, bank);
            var xml = Path.Combine(game, "RadioInfo_EN.xml");
            WriteRadio(xml, version, bankName, frames);
            fixtures.Add(version, (game, bank, xml));
        }
        var rawInfo = AudioBuildValidation.Inspect(audio);
        var rawMarkers = new Dictionary<string, long>
        {
            ["TrackStart"] = 0, ["End"] = rawInfo.Frames - 1, ["DJDrop"] = -1,
            ["DJStart"] = 88200, ["PostDrop"] = 44100
        };
        foreach (var sourceVersion in new[] { 2, 3 })
        {
            var source = fixtures[sourceVersion];
            var native = BankReader.ReadTracks(source.Bank, RadioInfo.Load(source.Xml), 1).Tracks.First();
            var archive = Path.Combine(root, $"FH{sourceVersion}-roundtrip.fhre");
            await TrackPack.ExportAsync(archive, Path.GetFileNameWithoutExtension(source.Bank), source.Bank,
                [new ExportItem(null, 0, native.DisplayName, native.Artist, native.SampleLength, native.SampleRate, null, true,
                    "default", native.Markers!.ToDictionary()),
                 new ExportItem(audio, -1, "Imported & <title> --", null, rawInfo.Frames, rawInfo.SampleRate, -3, false,
                    "custom", rawMarkers, new(false))], null, null, default);
            var pack = TrackPack.Load(archive);
            try
            {
                Check(pack.Manifest.Tracks.Count == 2 && pack.Manifest.Tracks[1].Normalization == new NormalizationOverride(false)
                    && pack.Manifest.Tracks[1].GainDb == -3 && !pack.Manifest.Tracks[1].Enabled,
                    "Export/import lost raw audio processing settings or inactive state.");
                var fsbPath = Path.Combine(pack.Dir, pack.Manifest.Tracks[0].File);
                Check(File.ReadAllBytes(fsbPath).SequenceEqual(TrackPack.ExtractSampleFsb(source.Bank, 0)),
                    "Export changed the native encoded FSB.");
                foreach (var targetVersion in new[] { 2, 3 })
                {
                    var target = fixtures[targetVersion];
                    var main = CreateVm(target.Game, "RadioInfo_EN.xml");
                    try
                    {
                        Set(main, "_nextSeq", 7000);
                        var radio = RadioInfo.Load(target.Xml);
                        var originalTracks = BankReader.ReadTracks(target.Bank, radio, 1).Tracks;
                        var existing = new TrackItemViewModel(originalTracks.First());
                        main.StageImportedReplace(existing, pack, pack.Manifest.Tracks[1]);
                        var additions = pack.Manifest.Tracks.Select(t => main.StageImportedAdd(pack, t)).ToArray();
                        Check(additions.All(t => t.UsesMilliseconds == (targetVersion == 2) && t.UsesTrackBreakDown)
                            && additions[1].Markers!["TrackBreakDown"] == 48000 && additions[1].Markers!["DJStart"] == 96000
                            && additions[1].Markers!["DJDrop"] == -1 && !additions[1].Enabled,
                            $"FH{sourceVersion} -> FH{targetVersion}: native profile, resampling or inactive state changed.");
                        var items = new[] { existing }.Concat(additions).Select(t => new BuildItem(t.SoundName, t.IsCustom,
                            t.FileSource, t.Title, t.Artist, t.GainDb, t.Enabled, t.Markers, t.IsReplacing,
                            MarkerSampleRate: t.SampleRate, Normalization: t.Normalization, BankId: t.BankId)).ToList();
                        items.Add(Item(originalTracks.Single(t => t.SoundName == "AUDIT_KEEP")));
                        var output = Path.Combine(root, $"FH{sourceVersion}-to-FH{targetVersion}.bank");
                        var added = await BankBuildService.BuildToFileAsync(target.Bank, output, items,
                            new AppSettings { LoudnessNormalize = true, TargetLufs = -30, VorbisQuality = 40 });
                        var state = BankState(output);
                        var original = BankState(source.Bank)[Lookup.SoundNameToId("AUDIT_NATIVE")];
                        Check(state[Lookup.SoundNameToId(additions[0].SoundName)] == original,
                            "Import re-encoded an already compatible FSB or reapplied global audio settings.");
                        Apply(radio, main.SelectedStation!, Path.GetFileNameWithoutExtension(target.Bank), added, items);
                        var saved = Path.Combine(root, $"FH{sourceVersion}-to-FH{targetVersion}.xml");
                        radio.Save(saved);
                        var editor = RadioInfo.Load(saved).StationByNumber(1)!;
                        var replacement = editor.GetSampleMeta(existing.SoundName)!.Value;
                        Check(replacement.DisplayName == "Imported & <title> --" && string.IsNullOrEmpty(replacement.Artist),
                            $"FH{sourceVersion} -> FH{targetVersion}: replacement retained the original artist instead of empty metadata.");
                        var imported = editor.ReadTracks().Single(t => t.SoundName == additions[1].SoundName);
                        Check(!imported.Enabled && imported.DisplayName == "Imported & <title> --" && string.IsNullOrEmpty(imported.Artist),
                            "Roundtrip changed metadata escaping or enabled state.");
                        var markers = editor.GetMarkers(imported.SoundName)!;
                        Check(markers["TrackBreakDown"] == (targetVersion == 2 ? 1000 : 48000)
                            && markers["DJStart"] == (targetVersion == 2 ? 2000 : 96000) && markers["DJDrop"] == -1
                            && !markers.ContainsKey("PostDrop"), "Committed markers used the wrong XML schema or time units.");
                        var rebuilt = BankReader.ReadTracks(output, RadioInfo.Load(saved), 1);
                        Check(rebuilt.Error is null && rebuilt.Tracks.Count == 4
                            && rebuilt.Tracks.All(t => !t.SoundName.StartsWith("sound_", StringComparison.Ordinal)),
                            "Imported STBL identities no longer match XML metadata.");
                        await CheckBackupsAsync(target.Game, target.Bank, target.Xml, output, saved, root, targetVersion);
                    }
                    finally { ReleaseVm(main); }
                }
            }
            finally { DeleteDirectory(pack.Dir, Path.Combine(AppPaths.TempBase, "import")); }
        }
        Console.WriteLine("Verified FH2/FH3 Azimuth pack and backup roundtrips in both directions: raw and FSB audio, replacement/addition, localized metadata, native units, disabled tracks and encoded byte identity.");
    }

    private static async Task CheckBackupsAsync(string game, string bank, string xml, string builtBank, string builtXml,
        string root, int version)
    {
        var originalBank = File.ReadAllBytes(bank);
        var originalXml = File.ReadAllBytes(xml);
        var french = Path.Combine(game, "RadioInfo_FR.xml");
        BackupEntry? backup = null;
        try
        {
            File.Copy(builtBank, bank, overwrite: true);
            File.Copy(builtXml, xml, overwrite: true);
            File.Copy(xml, french);
            var localized = RadioInfo.Load(french);
            localized.StationByNumber(1)!.SetSampleMeta("AUDIT_KEEP", "Localized & <title> --", "Localized artist");
            localized.Save(french);
            var radio = RadioInfo.Load(xml);
            var station = RadioCatalog.Build(game, radio).Single();
            var read = BankReader.ReadTracks(bank, radio, 1).Tracks;
            var sources = read.Select(t => new BackupTrackSource(t.SoundName, t.Origin == TrackOrigin.Custom, false,
                t.Replaced, null, null, t.SubIndex, t.DisplayName, t.Artist, t.SampleLength, t.SampleRate, null,
                t.Enabled, t.Markers!.ToDictionary(), BankId: t.BankId)).ToArray();
            var state = BankState(bank);
            backup = StationBackupService.Create("Azimuth-audit-" + Guid.NewGuid().ToString("N"), game, station, "Tracks", sources);
            Check(backup.Manifest.GameLabel == $"FH{version}" && StationBackupService.Matches(backup.Manifest, game, station),
                "Manual backup was assigned to the wrong Azimuth game.");
            foreach (var path in new[] { xml, french })
            {
                var changed = RadioInfo.Load(path);
                changed.StationByNumber(1)!.SetSampleMeta("AUDIT_NATIVE", "Damaged", "Wrong artist");
                changed.StationByNumber(1)!.SetSampleMeta("AUDIT_KEEP", "Damaged", "Wrong artist");
                changed.Save(path);
            }
            var result = await StationBackupService.RestoreAsync(backup, game,
                new AppSettings { LoudnessNormalize = true, TargetLufs = -30, VorbisQuality = 30 });
            Check(result.Banks == 1 && result.Langs == 2, "Manual backup restore lost a bank or localization.");
            var restored = BankState(bank);
            Check(state.Count == restored.Count && state.All(p => restored.TryGetValue(p.Key, out var found) && found == p.Value),
                "Manual backup restore changed encoded audio or STBL identities.");
            var meta = RadioInfo.Load(french).StationByNumber(1)!.GetSampleMeta("AUDIT_KEEP")!.Value;
            Check(meta.DisplayName == "Localized & <title> --" && meta.Artist == "Localized artist",
                $"FH{version} backup replaced localized original metadata with the reference language.");
            foreach (var path in new[] { xml, french })
            {
                var editor = RadioInfo.Load(path).StationByNumber(1)!;
                Check(string.IsNullOrEmpty(editor.GetSampleMeta("AUDIT_NATIVE")!.Value.Artist),
                    "Backup restore reintroduced the old artist for untagged replacement audio.");
                Check(read.All(t => editor.ReadTracks().Single(r => r.SoundName == t.SoundName).Enabled == t.Enabled),
                    "Backup restore changed inactive tracks.");
            }
            var pendingSources = sources.Select(t => t.SoundName == "AUDIT_KEEP"
                ? t with { DisplayName = "Pending & title" } : t).ToArray();
            var pending = StationBackupService.Create("Azimuth-pending-" + Guid.NewGuid().ToString("N"), game, station, "Tracks", pendingSources);
            try
            {
                await StationBackupService.RestoreAsync(pending, game, new AppSettings { LoudnessNormalize = false });
                Check(new[] { xml, french }.All(path => RadioInfo.Load(path).StationByNumber(1)!.GetSampleMeta("AUDIT_KEEP")!.Value.DisplayName == "Pending & title")
                    && RadioInfo.Load(french).StationByNumber(1)!.GetSampleMeta("AUDIT_KEEP")!.Value.Artist == "Localized artist",
                    "Restoring staged title changes overwrote an unchanged localized artist.");
            }
            finally { StationBackupService.Delete(pending); }
            CheckBackupCreationFailures(root, game, bank, french, station, sources);
            File.Copy(bank, bank + ".bak", overwrite: true);
            File.Copy(xml, xml + ".bak", overwrite: true);
            File.Copy(french, french + ".bak", overwrite: true);
            var savedBank = File.ReadAllBytes(bank);
            File.WriteAllText(bank, "damaged bank");
            var originalRestore = BackupService.RestoreStation(game, station);
            Check(originalRestore.Banks == 1 && originalRestore.Langs == 2 && File.ReadAllBytes(bank).SequenceEqual(savedBank),
                "Original station backup restore changed encoded audio.");
            File.WriteAllText(bank, "damaged bank");
            File.WriteAllText(xml, "damaged XML");
            var all = BackupService.Restore(game);
            Check(all.Restored == 3 && all.Failed == 0 && File.ReadAllBytes(bank).SequenceEqual(savedBank)
                && File.ReadAllBytes(xml).SequenceEqual(File.ReadAllBytes(xml + ".bak")),
                "Full original restore could not recover malformed bank/XML files.");
        }
        finally
        {
            if (backup is not null) StationBackupService.Delete(backup);
            File.WriteAllBytes(bank, originalBank);
            File.WriteAllBytes(xml, originalXml);
            foreach (var extra in new[] { french, french + ".bak", bank + ".bak", xml + ".bak" }) File.Delete(extra);
        }
    }

    private static void CheckBackupCreationFailures(string root, string game, string bank, string french,
        StationInfo station, BackupTrackSource[] sources)
    {
        var testRoot = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var inaccessible = Path.Combine(testRoot, "blocked");
        var backups = Path.Combine(testRoot, "backups");
        File.WriteAllText(inaccessible, "a file occupies the primary backup path");
        var create = typeof(StationBackupService).GetMethod(nameof(StationBackupService.Create),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static, null,
            [typeof(string), typeof(string), typeof(StationInfo), typeof(string), typeof(IReadOnlyList<BackupTrackSource>),
                typeof(string), typeof(Action<string>)], null)!;
        var log = new List<string>();
        BackupEntry Create(IReadOnlyList<BackupTrackSource> tracks, string destination) => (BackupEntry)create.Invoke(null,
            ["Audit", game, station, "Tracks", tracks, destination, (Action<string>)log.Add])!;

        CheckFailure(sources, inaccessible, inaccessible);
        Check(Directory.GetFileSystemEntries(testRoot).SequenceEqual([inaccessible]),
            "An inaccessible backup folder left unexpected files.");

        var localized = File.ReadAllBytes(french);
        try
        {
            File.WriteAllText(french, "<Radio><RadioStation");
            var entry = Create(sources, backups);
            var second = Create(sources, backups);
            Check(entry.Folder != second.Folder && Path.GetDirectoryName(entry.Folder) == backups,
                "Repeated backups with the same name collided or used the wrong folder.");
            Check(entry.Manifest.Languages.SequenceEqual(["EN"]) && entry.Manifest.TrackCount == sources.Length
                && log.Any(line => line.Contains(french, StringComparison.OrdinalIgnoreCase)),
                "A malformed localization prevented saving or was omitted without a diagnostic.");
            Check(new[] { entry, second }.All(backup => File.Exists(Path.Combine(backup.Folder, "manifest.json"))),
                "The backup manifests were not published.");

            var bad = sources.ToArray();
            bad[^1] = bad[^1] with { SubIndex = int.MaxValue };
            CheckFailure(bad, backups, bank);
            CheckFailure([], backups, bank);
            var missing = Path.Combine(testRoot, "missing.wav");
            bad[^1] = bad[^1] with { IsReplacing = true, ReplacementPath = missing };
            CheckFailure(bad, backups, missing);
            Check(Directory.GetDirectories(backups).Length == 2, "A failed backup left files or altered existing backups.");
        }
        finally { File.WriteAllBytes(french, localized); }

        void CheckFailure(IReadOnlyList<BackupTrackSource> tracks, string destination, string source)
        {
            try
            {
                Create(tracks, destination);
                throw new InvalidOperationException("A failed backup was reported as complete.");
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is IOException error)
            {
                Check(error.Message.Contains(source, StringComparison.OrdinalIgnoreCase), "A backup failure omitted the file involved.");
            }
        }
    }

    private static void WriteRadio(string path, int version, string bankName, long frames)
    {
        var rate = version == 2 ? 1000 : 48000;
        var length = version == 2 ? (long) Math.Round(frames / 48m, MidpointRounding.AwayFromZero) : frames;
        var sample = new XElement(version == 2 ? "Entry" : "Sample",
            new XAttribute("SoundName", "AUDIT_NATIVE"), new XAttribute("SampleLength", length),
            new XAttribute("DisplayName", "Original & title"), new XAttribute("Artist", "Original artist"),
            new XAttribute("TrackStart", 0), new XAttribute("End", length - 1), new XAttribute("DJDrop", -1),
            new XAttribute("DJStart", 2 * rate), new XAttribute("TrackBreakDown", rate),
            new XAttribute("StingerStart", version == 2 ? rate : 2 * rate - 1000));
        var station = new XElement("RadioStation", new XAttribute("Number", 1), new XAttribute("Name", "Azimuth audit"),
            new XElement("Banks", new XElement("Bank", new XAttribute("Name", bankName))));
        if (version == 2)
        {
            sample.SetAttributeValue("EventName", "/Master/Radio/Track");
            var keep = new XElement(sample);
            keep.SetAttributeValue("SoundName", "AUDIT_KEEP");
            keep.SetAttributeValue("DisplayName", "Keep original");
            keep.SetAttributeValue("Artist", "Keep artist");
            station.Add(new XElement("Playlist", new XAttribute("Type", "FreeroamTracks"), new XElement(sample), new XElement(keep)),
                new XElement("Playlist", new XAttribute("Type", "EventTracks"), new XElement(sample), new XElement(keep)));
        }
        else
        {
            sample.SetAttributeValue("SampleRate", rate);
            var keep = new XElement(sample);
            keep.SetAttributeValue("SoundName", "AUDIT_KEEP");
            keep.SetAttributeValue("DisplayName", "Keep original");
            keep.SetAttributeValue("Artist", "Keep artist");
            station.Add(new XElement("SampleList", new XAttribute("Type", "Track"), sample, keep),
                new XElement("PlayList", new XAttribute("Type", "FreeRoam"), new XElement("Entry", new XAttribute("Name", "AUDIT_NATIVE")), new XElement("Entry", new XAttribute("Name", "AUDIT_KEEP"))),
                new XElement("PlayList", new XAttribute("Type", "Event"), new XElement("Entry", new XAttribute("Name", "AUDIT_NATIVE")), new XElement("Entry", new XAttribute("Name", "AUDIT_KEEP"))));
        }
        new XDocument(new XElement("Radio", new XElement("RadioStations", station))).Save(path);
    }
}
