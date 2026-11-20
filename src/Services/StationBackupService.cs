using System.Text.Json;
using System.Xml.Linq;
using FH6RB.Core;

namespace FH6RB.Services;

public sealed class BackupVariant
{
    public string Variant { get; set; } = "";
    public string BankName { get; set; } = "";
    public string BankFile { get; set; } = "";
}

public sealed class BackupTrack
{
    public string SoundName { get; set; } = "";
    public string Role { get; set; } = "";
    public string Bank { get; set; } = "";
    public ulong? BankId { get; set; }
    public string? DisplayName { get; set; }
    public string? Artist { get; set; }
    public long SampleLength { get; set; }
    public int SampleRate { get; set; }
    public double? GainDb { get; set; }
    public NormalizationOverride? Normalization { get; set; }
    public bool Enabled { get; set; } = true;
    public Dictionary<string, long>? Markers { get; set; }
    public string Audio { get; set; } = "";
}

public sealed class BackupManifest
{
    public string Format { get; set; } = "FH6RB-BACKUP";
    public int Version { get; set; } = 2;
    public string Name { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string GameLabel { get; set; } = "";
    public int StationNumber { get; set; }
    public string StationName { get; set; } = "";
    public string Variant { get; set; } = "";
    public string BankName { get; set; } = "";
    public List<BackupVariant> Variants { get; set; } = [];
    public List<string> Languages { get; set; } = [];
    public List<BackupTrack> Tracks { get; set; } = [];
    public int TrackCount { get; set; }
    public int CustomCount { get; set; }
    public int EnabledCount { get; set; }
}

public sealed record BackupEntry(string Folder, BackupManifest Manifest)
{
    internal IReadOnlyList<string> SkippedLanguages { get; init; } = [];
}

public sealed record BackupTrackSource(
    string SoundName,
    bool IsCustom,
    bool IsReplacing,
    bool Replaced,
    string? SourcePath,
    string? ReplacementPath,
    int SubIndex,
    string? DisplayName,
    string? Artist,
    long SampleLength,
    int SampleRate,
    double? GainDb,
    bool Enabled,
    Dictionary<string, long>? Markers,
    NormalizationOverride? Normalization = null,
    ulong? BankId = null);

public static class StationBackupService
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "backups");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static List<BackupEntry> List()
    {
        var result = new List<BackupEntry>();
        var root = Root;
        if (!Directory.Exists(root)) return result;

        try
        {
            ReadBackups(root, result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Line($"backup: could not list {root}: {ex}");
        }

        return result.OrderByDescending(e => e.Manifest.CreatedUtc).ToList();
    }

    private static void ReadBackups(string root, List<BackupEntry> result)
    {
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (Path.GetFileName(dir).StartsWith(".pending-", StringComparison.OrdinalIgnoreCase)) continue;

            var mf = Path.Combine(dir, "manifest.json");

            if (!File.Exists(mf))
            {
                continue;
            }

            try
            {
                var m = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(mf));

                if (m is not null)
                {
                    result.Add(new BackupEntry(dir, m));
                }
            }
            catch (Exception ex)
            {
                Log.Line($"backup manifest read failed ({mf}): {ex}");
            }
        }
    }

    public static bool Matches(BackupManifest m, string gamePath, StationInfo station) =>
        station.Number == m.StationNumber
        && m.GameLabel == GameLabel(gamePath)
        && BankPresent(m, gamePath);

    private static bool BankPresent(BackupManifest m, string gamePath)
    {
        if (!string.IsNullOrEmpty(m.BankName))
        {
            return GameScanner.BankPath(gamePath, m.BankName) is not null;
        }

        return m.Variants.Count > 0
            && m.Variants.All(v => GameScanner.BankPath(gamePath, v.BankName) is not null);
    }

    public static BackupEntry Create(string name, string gamePath, StationInfo station, string variant,
        IReadOnlyList<BackupTrackSource> sources, Action<string>? log = null) =>
        Create(name, gamePath, station, variant, sources, Root, log ?? Log.Line);

    private static BackupEntry Create(string name, string gamePath, StationInfo station, string variant,
        IReadOnlyList<BackupTrackSource> sources, string root, Action<string> log)
    {
        if (!station.Variants.Contains(variant, StringComparer.Ordinal))
        {
            throw new ArgumentException("Select a valid station bank variant.", nameof(variant));
        }

        var token = Guid.NewGuid().ToString("N");
        var id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}_{Sanitize(name)}_{token}";
        var destination = Path.GetFullPath(Path.Combine(root, id));
        try
        {
            Directory.CreateDirectory(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Could not create a backup folder in {root}: {ex.Message}", ex);
        }

        try
        {
            log($"backup: saving station #{station.Number} variant={variant} game={gamePath} destination={destination}");
            return CreateBackup(name, gamePath, station, variant, sources, destination, log);
        }
        finally
        {
            try
            {
                if (!File.Exists(Path.Combine(destination, "manifest.json"))) Directory.Delete(destination, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"backup: could not remove incomplete backup {destination}: {ex}");
            }
        }
    }

    private static BackupEntry CreateBackup(string name, string gamePath, StationInfo station, string variant,
        IReadOnlyList<BackupTrackSource> sources, string folder, Action<string> log)
    {
        var audioDir = Path.Combine(folder, "audio");
        Directory.CreateDirectory(audioDir);

        RadioInfo? radio = null;
        var languages = new List<string>();
        var skippedLanguages = new List<string>();
        var resources = GameScanner.ReadRadioResources(gamePath);
        var languageFiles = resources.Languages
            .OrderByDescending(file => LangCode(file).StartsWith("en", StringComparison.OrdinalIgnoreCase));
        var xmlDir = Path.Combine(folder, "xml");
        Directory.CreateDirectory(xmlDir);
        foreach (var languageFile in languageFiles)
        {
            var path = resources.LanguagePath(languageFile);
            XElement? stationNode;
            RadioInfo localized;
            try
            {
                localized = RadioInfo.Load(path ?? throw new FileNotFoundException($"RadioInfo not found: {languageFile}"));
                stationNode = localized.Document.Descendants("RadioStation")
                    .FirstOrDefault(s => (int?) s.Attribute("Number") == station.Number);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException
                or FormatException or OverflowException)
            {
                skippedLanguages.Add(languageFile);
                log($"backup: skipped localization {path ?? languageFile}: {ex}");
                continue;
            }

            if (stationNode is null) continue;

            var language = LangCode(languageFile);
            Atomic.Write(Path.Combine(xmlDir, language + ".xml"), stationNode.ToString(SaveOptions.DisableFormatting));
            languages.Add(language);
            radio ??= localized;
        }

        if (radio is null)
        {
            throw new InvalidDataException("No readable RadioInfo contains the selected station. See the log for details.");
        }

        var tracks = new List<BackupTrack>();
        var variants = new List<BackupVariant>();
        var key = 0;

        foreach (var v in station.Variants)
        {
            var bankName = station.BankName(v);
            var bankPath = resources.BankPath(bankName)
                ?? throw new FileNotFoundException($"Station bank not found: {bankName}");

            var variantSources = v == variant
                ? sources
                : ReadDiskSources(bankPath, radio, station.Number);
            if (variantSources.Count == 0) ValidateEmptyBank(bankPath);
            var before = tracks.Count;
            TrackPack.SampleReader? reader = null;

            try
            {
                foreach (var s in variantSources)
                {
                    var role = s.IsCustom ? "custom" : (s.IsReplacing || s.Replaced) ? "replacement" : "default";
                    var copyFrom = s.IsReplacing ? s.ReplacementPath : s.SubIndex < 0 ? s.SourcePath : null;
                    var extract = !s.IsReplacing && s.SubIndex >= 0;
                    var ext = extract ? ".fsb" : Path.GetExtension(copyFrom);
                    if (string.IsNullOrEmpty(ext)) ext = ".fsb";
                    var rel = $"audio/{key}{ext}";
                    var abs = Path.Combine(folder, rel);

                    SaveTrackAudio(s, bankPath, abs, extract, copyFrom, ref reader);

                    tracks.Add(new BackupTrack
                    {
                        SoundName = s.SoundName,
                        Role = role,
                        Bank = bankName,
                        BankId = s.BankId,
                        DisplayName = s.DisplayName,
                        Artist = s.Artist,
                        SampleLength = s.SampleLength,
                        SampleRate = s.SampleRate,
                        GainDb = s.GainDb,
                        Normalization = ext.Equals(".fsb", StringComparison.OrdinalIgnoreCase) ? null : s.Normalization,
                        Enabled = s.Enabled,
                        Markers = s.Markers is { Count: > 0 } ? new Dictionary<string, long>(s.Markers) : null,
                        Audio = rel,
                    });
                    key++;
                }
            }
            finally { reader?.Dispose(); }

            variants.Add(new BackupVariant
            {
                Variant = v,
                BankName = bankName,
                BankFile = GameScanner.LogicalFileName(bankPath),
            });
            log($"backup: {bankName}: {tracks.Count - before} track(s)");
        }

        if (tracks.Count == 0) throw new InvalidDataException("The station backup contains no audio tracks.");

        var manifest = new BackupManifest
        {
            Name = name,
            CreatedUtc = DateTime.UtcNow.ToString("o"),
            GameLabel = GameLabel(gamePath),
            StationNumber = station.Number,
            StationName = station.Name,
            Variant = variant,
            BankName = "",
            Variants = variants,
            Languages = languages,
            Tracks = tracks,
            TrackCount = tracks.Count,
            CustomCount = tracks.Count(t => t.Role == "custom"),
            EnabledCount = tracks.Count(t => t.Enabled),
        };

        Atomic.Write(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOpts));
        log($"backup created: {folder} ({variants.Count} bank(s), {tracks.Count} track(s))");

        return new BackupEntry(folder, manifest) { SkippedLanguages = skippedLanguages };
    }

    private static void SaveTrackAudio(BackupTrackSource track, string bankPath, string output, bool extract, string? copyFrom,
        ref TrackPack.SampleReader? reader)
    {
        try
        {
            if (extract)
            {
                reader ??= new TrackPack.SampleReader(bankPath);
                using var stream = File.Create(output);
                reader.CopySample(track.SubIndex, stream);
            }
            else
            {
                if (string.IsNullOrEmpty(copyFrom)) throw new FileNotFoundException("No audio source is available.");
                Atomic.Copy(copyFrom, output);
                if (new FileInfo(output).Length == 0) throw new InvalidDataException("The audio source is empty.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            var source = extract ? $"{bankPath} (sample {track.SubIndex})" : copyFrom ?? bankPath;
            throw new IOException($"Could not save track '{track.SoundName}' from {source}: {ex.Message}", ex);
        }
    }

    private static List<BackupTrackSource> ReadDiskSources(string bankPath, RadioInfo radio, int stationNumber)
    {
        var result = BankReader.ReadTracks(bankPath, radio, stationNumber);

        if (result.Error is { } err)
        {
            throw new IOException($"Could not read station bank {bankPath}: {err}");
        }

        return result.Tracks
            .Select(t => new BackupTrackSource(
                t.SoundName,
                IsCustom: t.Origin == TrackOrigin.Custom,
                IsReplacing: false,
                Replaced: t.Replaced,
                SourcePath: null,
                ReplacementPath: null,
                SubIndex: t.SubIndex,
                DisplayName: t.DisplayName,
                Artist: t.Artist,
                SampleLength: t.SampleLength,
                SampleRate: t.SampleRate,
                GainDb: null,
                Enabled: t.Enabled,
                Markers: t.Markers is { Count: > 0 } ? new Dictionary<string, long>(t.Markers) : null,
                BankId: t.BankId))
            .ToList();
    }

    private static void ValidateEmptyBank(string bankPath)
    {
        try
        {
            var skeleton = FevBank.ReadSkeleton(bankPath);
            if (skeleton.Stbl.Count > 0)
            {
                throw new InvalidDataException("The bank has tracks, but no audio sources were found.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new IOException($"Could not validate station bank {bankPath}: {ex.Message}", ex);
        }
    }

    public static HashSet<string> CollectOccupiedCustomNames(string gamePath, IReadOnlyCollection<string>? excludeBankNames) =>
        CollectOccupiedCustomNames(GameScanner.ReadRadioResources(gamePath), excludeBankNames);

    private static HashSet<string> CollectOccupiedCustomNames(GameScanner.RadioResources resources, IReadOnlyCollection<string>? excludeBankNames)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var excluded = excludeBankNames is null
            ? null
            : new HashSet<string>(excludeBankNames, StringComparer.OrdinalIgnoreCase);

        foreach (var bankName in resources.Banks)
        {
            if (excluded is not null && excluded.Contains(bankName))
            {
                continue;
            }

            var bankPath = resources.BankPath(bankName);
            if (bankPath is null || !File.Exists(bankPath))
            {
                continue;
            }

            try
            {
                var ids = FevBank.ReadStblIdsFromFile(bankPath);
                foreach (var (_, _, sn) in Naming.ScanCustomTracks(ids))
                {
                    set.Add(sn);
                }
            }
            catch
            {
            }
        }

        return set;
    }

    public static async Task<(int Banks, int Langs)> RestoreAsync(BackupEntry e, string gamePath,
        AppSettings settings, Action<string>? log = null)
    {
        var missingTools = Tools.MissingForBuild().ToList();
        if (missingTools.Count > 0)
        {
            throw new InvalidOperationException($"Backup restore: missing {string.Join(", ", missingTools)}.");
        }

        using var transaction = new GameFileTransaction(gamePath);
        var resources = GameScanner.ReadRadioResources(gamePath);
        var m = e.Manifest;

        var tracks = m.Tracks;
        if (tracks.Count == 0)
        {
            tracks = LoadLegacyTracks(e, m, log);
        }

        var groups = GroupByBank(m, tracks);

        if (groups.Count == 0)
        {
            throw new InvalidOperationException("backup contains no restorable banks");
        }

        if (m.Variants.Count > 1)
        {
            log?.Invoke($"restore: {groups.Count} bank(s): {string.Join(", ", groups.Select(g => g.Bank))}");
        }

        var bankPaths = new List<(string Bank, string Path, List<BackupTrack> Tracks)>();

        foreach (var g in groups)
        {
            var bankPath = resources.BankPath(g.Bank)
                ?? throw new InvalidOperationException($"bank not found: {g.Bank}");
            bankPaths.Add((g.Bank, bankPath, g.Tracks));
        }

        var xmlTargets = resources.Languages.Select(language => resources.LanguagePath(language)!).ToList();

        ResolveCustomConflicts(tracks, resources, groups.Select(g => g.Bank).ToList(), log);
        
        var missing = tracks
            .Where(t => string.IsNullOrEmpty(t.Audio) || !File.Exists(Path.Combine(e.Folder, t.Audio)))
            .Select(t => t.SoundName)
            .ToList();

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "backup audio missing for: " + string.Join(", ", missing.Take(5)) + (missing.Count > 5 ? " …" : ""));
        }

        var referenceStations = xmlTargets.ToDictionary(path => path,
            path => LoadBackupStation(e, path, m.StationNumber), StringComparer.OrdinalIgnoreCase);
        var metadataOverrides = new Dictionary<string, (bool Title, bool Artist)>(StringComparer.Ordinal);
        foreach (var track in tracks.Where(t => t.Role == "default" && !IsBankOnlyTrack(t)))
        {
            var snapshots = referenceStations.Values.Where(station => station is not null)
                .Select(station => station!.GetSampleMeta(track.SoundName)).Where(meta => meta.HasValue)
                .Select(meta => meta!.Value).ToArray();
            if (snapshots.Length == 0) continue;
            metadataOverrides[track.SoundName] = (
                track.DisplayName is not null && !snapshots.Any(meta =>
                    string.Equals(meta.DisplayName, track.DisplayName, StringComparison.Ordinal)
                    || meta.DisplayName is null && track.DisplayName == track.SoundName),
                track.Artist is not null && !snapshots.Any(meta =>
                    string.Equals(meta.Artist ?? "", track.Artist, StringComparison.Ordinal)));
        }
        var radioDocuments = new Dictionary<string, RadioInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in xmlTargets)
        {
            var radio = RadioInfo.Load(path);
            var editor = radio.StationByNumber(m.StationNumber);
            if (editor is not null)
            {
                var reference = referenceStations[path];
                foreach (var track in tracks.Where(t => t.Role != "custom" && !IsBankOnlyTrack(t)))
                {
                    var restored = reference is not null && editor.RestoreTrackFrom(reference, track.SoundName);
                    if (!restored && editor.GetSampleMeta(track.SoundName) is null)
                    {
                        throw new InvalidDataException($"Original XML metadata for {track.SoundName} is missing from the backup and {Path.GetFileName(path)}.");
                    }
                }
                if (tracks.Any(t => t.Role == "custom") && editor.ReadTracks().Count == 0)
                {
                    var template = reference?.ReadTracks().FirstOrDefault();
                    if (template is null || !editor.RestoreTrackFrom(reference!, template.SoundName))
                    {
                        throw new InvalidDataException($"No XML track template is available for {Path.GetFileName(path)}.");
                    }
                }
            }
            radioDocuments.Add(path, radio);
        }

        FileGuard.EnsureWritable(bankPaths.Select(b => b.Path).Concat(xmlTargets));
        
        var stagedBanks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var framesByName = new Dictionary<string, long>(StringComparer.Ordinal);
        var sampleRatesByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var backupCustoms = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (bank, bankPath, bankTracks) in bankPaths)
        {
            var buildItems = bankTracks
                .Select(t => new BuildItem(
                    t.SoundName,
                    IsNewCustom: t.Role == "custom",
                    SourcePath: Path.Combine(e.Folder, t.Audio),
                    DisplayName: t.DisplayName,
                    Artist: t.Artist,
                    GainDb: t.GainDb,
                    Enabled: t.Enabled,
                    Markers: t.Markers,
                    IsReplacement: t.Role == "replacement" || t.Role == "default",
                    MarkerSampleRate: t.SampleRate,
                    Normalization: t.Normalization,
                    BankId: t.BankId))
                .ToList();

            var staged = transaction.Stage(bankPath);
            var added = await BankBuildService.BuildToFileAsync(bankPath, staged, buildItems, settings, log, null,
                    Log.IsVerbose, useBackupSources: true)
                .ConfigureAwait(false);

            foreach (var a in added)
            {
                framesByName[a.SoundName] = a.Frames;
                sampleRatesByName[a.SoundName] = a.SampleRate;
            }

            transaction.StageOriginal(bankPath, !FevBank.LooksModified(bankPath));
            stagedBanks.Add(bank, staged);

            foreach (var t in bankTracks.Where(t => t.Role == "custom"))
            {
                backupCustoms.Add(t.SoundName);
            }
        }
        
        var langs = 0;
        var idsByBank = new Dictionary<string, HashSet<ulong>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in xmlTargets)
        {
            var radio = radioDocuments[path];
            var editor = radio.StationByNumber(m.StationNumber);

            if (editor is null)
            {
                log?.Invoke($"restore xml: station not found ({LangCode(Path.GetFileName(path))})");
                continue;
            }

            foreach (var (bank, _, bankTracks) in bankPaths)
            {
                editor.RegisterBank(bank);

                foreach (var t in bankTracks.Where(t => !IsBankOnlyTrack(t)))
                {
                    var frames = framesByName.TryGetValue(t.SoundName, out var f) ? f : t.SampleLength;
                    var sampleRate = sampleRatesByName.GetValueOrDefault(t.SoundName, t.SampleRate);

                    if (t.Role == "custom")
                    {
                        editor.AddCustom(t.SoundName, frames, sampleRate, t.DisplayName, t.Artist);
                    }
                    else
                    {
                        editor.ApplyReplacement(t.SoundName, frames, sampleRate);
                        if (t.Role == "default") editor.ClearReplacement(t.SoundName);
                        if (t.Role == "default" && metadataOverrides.TryGetValue(t.SoundName, out var changed))
                        {
                            editor.SetSampleMeta(t.SoundName, changed.Title ? t.DisplayName : null,
                                changed.Artist ? t.Artist : null, clearMissingArtist: changed.Artist);
                        }
                        else editor.SetSampleMeta(t.SoundName, t.DisplayName, t.Artist, clearMissingArtist: t.Role == "replacement");
                    }
                }

                foreach (var t in bankTracks.Where(t => !IsBankOnlyTrack(t)))
                {
                    if (t.Markers is not null)
                    {
                        editor.SetMarkers(t.SoundName, t.Markers, t.SampleRate);
                    }
                    editor.SetEnabled(t.SoundName, t.Enabled);
                }
            }

            foreach (var sn in editor.CustomSoundNames().Where(c => !backupCustoms.Contains(c)).ToList())
            {
                editor.RemoveCustom(sn);
                log?.Invoke($"restore xml: removed dangling custom {sn}");
            }

            var bankIds = new HashSet<ulong>();
            foreach (var bank in editor.TrackBankNames())
            {
                if (!idsByBank.TryGetValue(bank, out var ids))
                {
                    var bankPath = stagedBanks.GetValueOrDefault(bank) ?? resources.BankPath(bank)
                        ?? throw new InvalidOperationException($"bank not found while restoring playlists: {bank}");
                    var skeleton = FevBank.ReadSkeleton(bankPath);
                    ids = skeleton.Stbl.Select(track => track.Id).ToHashSet();
                    idsByBank.Add(bank, ids);
                }
                bankIds.UnionWith(ids);
            }
            editor.ReconcileTracks(bankIds, Lookup.SoundNameToId, log);

            transaction.StageRadio(radio, path);
            langs++;
        }

        transaction.Commit();
        log?.Invoke($"restored {stagedBanks.Count} bank(s), {langs} language file(s)");
        return (stagedBanks.Count, langs);
    }

    private static bool IsBankOnlyTrack(BackupTrack track) => track.BankId is { } id
        && track.SoundName.StartsWith("sound_", StringComparison.Ordinal)
        && id != Lookup.SoundNameToId(track.SoundName);

    private static RadioStationEditor? LoadBackupStation(BackupEntry backup, string xmlPath, int stationNumber)
    {
        var snapshot = Path.Combine(backup.Folder, "xml", LangCode(Path.GetFileName(xmlPath)) + ".xml");
        if (File.Exists(snapshot))
        {
            var station = RadioInfo.ParseElement(File.ReadAllText(snapshot));
            if (station.Name != "RadioStation" || (int?) station.Attribute("Number") != stationNumber)
            {
                throw new InvalidDataException($"Invalid station XML snapshot: {Path.GetFileName(snapshot)}");
            }
            return new RadioStationEditor(station);
        }
        var original = xmlPath + ".bak";
        return File.Exists(original) ? RadioInfo.Load(original).StationByNumber(stationNumber) : null;
    }

    private static List<(string Bank, List<BackupTrack> Tracks)> GroupByBank(BackupManifest m, List<BackupTrack> tracks)
    {
        var fallback = !string.IsNullOrEmpty(m.BankName)
            ? m.BankName
            : m.Variants.FirstOrDefault()?.BankName ?? "";

        var byBank = new Dictionary<string, List<BackupTrack>>(StringComparer.Ordinal);

        foreach (var v in m.Variants)
        {
            if (!string.IsNullOrEmpty(v.BankName))
            {
                byBank.TryAdd(v.BankName, []);
            }
        }

        foreach (var t in tracks)
        {
            var bank = !string.IsNullOrEmpty(t.Bank) ? t.Bank : fallback;

            if (string.IsNullOrEmpty(bank))
            {
                continue;
            }

            if (!byBank.TryGetValue(bank, out var list))
            {
                byBank[bank] = list = [];
            }

            list.Add(t);
        }

        return byBank
            .Where(kv => kv.Value.Count > 0)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    private static void ResolveCustomConflicts(List<BackupTrack> tracks, GameScanner.RadioResources resources,
        IReadOnlyCollection<string> restoredBanks, Action<string>? log)
    {
        var occupied = CollectOccupiedCustomNames(resources, excludeBankNames: restoredBanks);
        var assigned = new HashSet<string>(StringComparer.Ordinal);

        var seq = occupied.Count == 0
            ? 0
            : occupied.Max(ExtractSeq) + 1;

        foreach (var t in tracks)
        {
            if (t.Role != "custom")
            {
                continue;
            }

            if (!occupied.Contains(t.SoundName) && assigned.Add(t.SoundName))
            {
                continue;
            }

            var original = t.SoundName;
            string newName;
            do
            {
                newName = Naming.MakeSoundName(seq++);
            }
            while (occupied.Contains(newName) || !assigned.Add(newName));

            t.SoundName = newName;
            t.BankId = null;
            log?.Invoke($"restore: custom {original} renamed to {newName} (conflict)");
        }
    }

    private static int ExtractSeq(string soundName)
    {
        if (!soundName.StartsWith(Naming.CustomPrefix, StringComparison.Ordinal))
        {
            return -1;
        }

        return int.TryParse(soundName[Naming.CustomPrefix.Length..], out var n) ? n : -1;
    }

    private static List<BackupTrack> LoadLegacyTracks(BackupEntry e, BackupManifest m, Action<string>? log)
    {
        var result = new List<BackupTrack>();

        var xmlDir = Path.Combine(e.Folder, "xml");
        string[] xmlFiles;
        try
        {
            xmlFiles = Directory.Exists(xmlDir) ? Directory.GetFiles(xmlDir) : Array.Empty<string>();
        }
        catch
        {
            return result;
        }

        if (xmlFiles.Length == 0)
        {
            return result;
        }

        var refFile = xmlFiles
            .OrderBy(f => string.Equals(Path.GetFileName(f), "en.xml", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .First();

        XElement stationNode;
        try
        {
            stationNode = RadioInfo.ParseElement(File.ReadAllText(refFile));
        }
        catch (Exception ex)
        {
            log?.Invoke($"restore legacy: bad ref xml: {ex.Message}");
            return result;
        }

        var trackList = stationNode.Elements("SampleList")
            .FirstOrDefault(sl => (string?) sl.Attribute("Type") == "Track");

        if (trackList is null)
        {
            log?.Invoke("restore legacy: no Track SampleList in ref xml");
            return result;
        }

        var freeRoam = stationNode.Elements("PlayList")
            .FirstOrDefault(pl => (string?) pl.Attribute("Type") == "FreeRoam");
        var live = freeRoam is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : freeRoam.Elements("Entry").Select(en => (string?) en.Attribute("Name")).Where(n => n is not null).ToHashSet()!;

        var variant = m.Variants.FirstOrDefault();
        var legacyBankPath = variant is null ? null : Path.Combine(e.Folder, "banks", variant.BankFile);

        var audioDir = Path.Combine(e.Folder, "audio");
        Directory.CreateDirectory(audioDir);
        foreach (var stale in Directory.GetFiles(audioDir, "*.fsb"))
        {
            try { File.Delete(stale); } catch { }
        }

        var key = 0;
        foreach (var s in trackList.Elements("Sample"))
        {
            var sn = (string?) s.Attribute("SoundName");
            if (string.IsNullOrEmpty(sn))
            {
                continue;
            }

            var isCustom = sn!.StartsWith(Naming.CustomPrefix, StringComparison.Ordinal);
            var isReplaced = (string?) s.Attribute("Replaced") == "true";
            var role = isCustom ? "custom" : isReplaced ? "replacement" : "default";

            var rel = $"audio/{key}.fsb";
            var abs = Path.Combine(e.Folder, rel);

            if (!File.Exists(abs))
            {
                if (legacyBankPath is null || !File.Exists(legacyBankPath))
                {
                    log?.Invoke($"restore legacy: bank file missing, skip {sn}");
                    continue;
                }

                try
                {
                    var bytes = TrackPack.ExtractSampleFsbBySoundName(legacyBankPath, sn);
                    File.WriteAllBytes(abs, bytes);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"restore legacy: extract failed {sn}: {ex.Message}");
                    continue;
                }
            }

            var markers = RadioStationEditor.ReadMarkers(s);

            result.Add(new BackupTrack
            {
                SoundName = sn,
                Role = role,
                DisplayName = (string?) s.Attribute("DisplayName"),
                Artist = (string?) s.Attribute("Artist"),
                SampleLength = (long?) s.Attribute("SampleLength") ?? 0,
                SampleRate = (int?) s.Attribute("SampleRate") ?? 0,
                Enabled = live.Contains(sn),
                Markers = markers is { Count: > 0 } ? new Dictionary<string, long>(markers) : null,
                Audio = rel,
            });
            key++;
        }

        return result;
    }

    public static void Delete(BackupEntry e)
    {
        try
        {
            if (Directory.Exists(e.Folder))
            {
                Directory.Delete(e.Folder, recursive: true);
            }
        }
        catch
        {
        }
    }

    public static string LangCode(string fileName)
    {
        var logical = GameScanner.LogicalFileName(fileName);
        return logical.StartsWith("RadioInfo_", StringComparison.OrdinalIgnoreCase)
            && logical.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
            ? logical[10..^4] : Path.GetFileNameWithoutExtension(logical);
    }

    private static string GameLabel(string gamePath)
    {
        var version = GameScanner.DetectVersion(gamePath);
        if (version > 0)
        {
            return $"FH{version}";
        }

        var exe = GameScanner.FindExe(gamePath);

        if (exe is not null)
        {
            var digits = new string(Path.GetFileNameWithoutExtension(exe).Where(char.IsDigit).ToArray());

            if (digits.Length > 0)
            {
                return "FH" + digits;
            }
        }

        var bank = GameScanner.RadioBankNames(gamePath).FirstOrDefault();
        var path = bank is null ? null : GameScanner.BankPath(gamePath, bank);

        return path is not null && GameScanner.LogicalFileName(path).EndsWith(".assets.bank", StringComparison.OrdinalIgnoreCase)
            ? "FH5/6" : "FH4";
    }

    private static string Sanitize(string s)
    {
        var clean = new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');

        if (string.IsNullOrEmpty(clean))
        {
            return "backup";
        }

        return clean.Length > 40 ? clean[..40] : clean;
    }
}
