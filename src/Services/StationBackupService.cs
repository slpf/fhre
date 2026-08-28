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
    public string? DisplayName { get; set; }
    public string? Artist { get; set; }
    public long SampleLength { get; set; }
    public int SampleRate { get; set; }
    public double? GainDb { get; set; }
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

public sealed record BackupEntry(string Folder, BackupManifest Manifest);

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
    Dictionary<string, long>? Markers);

public static class StationBackupService
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "backups");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static List<BackupEntry> List()
    {
        var result = new List<BackupEntry>();

        if (!Directory.Exists(Root))
        {
            return result;
        }

        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
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
                Log.Line($"backup manifest parse failed ({Path.GetFileName(mf)}): {ex.Message}");
            }
        }

        return result.OrderByDescending(e => e.Manifest.CreatedUtc).ToList();
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
        IReadOnlyList<BackupTrackSource> sources, Action<string>? log = null)
    {
        var id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}_{Sanitize(name)}";
        var folder = Path.Combine(Root, id);
        var audioDir = Path.Combine(folder, "audio");
        Directory.CreateDirectory(audioDir);

        var radio = LoadReferenceRadio(gamePath, log);
        var languages = GameScanner.LanguageFiles(gamePath).Select(LangCode).ToList();
        var tracks = new List<BackupTrack>();
        var variants = new List<BackupVariant>();
        var key = 0;

        foreach (var v in station.Variants)
        {
            var bankName = station.BankName(v);
            var bankPath = GameScanner.BankPath(gamePath, bankName);

            if (bankPath is null)
            {
                log?.Invoke($"backup: bank not found, skipped: {bankName}");
                continue;
            }

            var variantSources = v == variant
                ? sources
                : ReadDiskSources(bankPath, radio, station.Number, log);
            var before = tracks.Count;

            foreach (var s in variantSources)
            {
                var role = s.IsCustom ? "custom" : (s.IsReplacing || s.Replaced) ? "replacement" : "default";
                byte[]? bytes = null;
                string? copyFrom = null;

                if (!s.IsCustom && s.IsReplacing && !string.IsNullOrEmpty(s.ReplacementPath) && File.Exists(s.ReplacementPath))
                {
                    copyFrom = s.ReplacementPath;
                }
                else if (s.IsCustom && s.SubIndex < 0 && !string.IsNullOrEmpty(s.SourcePath) && File.Exists(s.SourcePath))
                {
                    copyFrom = s.SourcePath;
                }
                else if (s.SubIndex >= 0)
                {
                    try
                    {
                        progressExtract(log, s.SoundName);
                        bytes = TrackPack.ExtractSampleFsb(bankPath, s.SubIndex);
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"backup: extract failed {s.SoundName}: {ex.Message}");
                    }
                }
                else if (s.IsCustom && !string.IsNullOrEmpty(s.SourcePath) && File.Exists(s.SourcePath))
                {
                    copyFrom = s.SourcePath;
                }

                if (bytes is null && copyFrom is null)
                {
                    log?.Invoke($"backup: skip {s.SoundName} (no audio source)");
                    continue;
                }

                var ext = bytes is not null ? ".fsb" : Path.GetExtension(copyFrom!);
                if (string.IsNullOrEmpty(ext)) ext = ".fsb";
                var rel = $"audio/{key}{ext}";
                var abs = Path.Combine(folder, rel);

                if (bytes is not null)
                {
                    File.WriteAllBytes(abs, bytes);
                }
                else
                {
                    Atomic.Copy(copyFrom!, abs);
                }

                tracks.Add(new BackupTrack
                {
                    SoundName = s.SoundName,
                    Role = role,
                    Bank = bankName,
                    DisplayName = s.DisplayName,
                    Artist = s.Artist,
                    SampleLength = s.SampleLength,
                    SampleRate = s.SampleRate,
                    GainDb = s.GainDb,
                    Enabled = s.Enabled,
                    Markers = s.Markers is { Count: > 0 } ? new Dictionary<string, long>(s.Markers) : null,
                    Audio = rel,
                });
                key++;
            }

            variants.Add(new BackupVariant
            {
                Variant = v,
                BankName = bankName,
                BankFile = Path.GetFileName(bankPath),
            });
            log?.Invoke($"backup: {bankName}: {tracks.Count - before} track(s)");
        }

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
        log?.Invoke($"backup created: {id} ({variants.Count} bank(s), {tracks.Count} track(s))");

        return new BackupEntry(folder, manifest);
    }

    private static RadioInfo? LoadReferenceRadio(string gamePath, Action<string>? log)
    {
        var files = GameScanner.LanguageFiles(gamePath);
        var refFile = files.FirstOrDefault(f => LangCode(f).StartsWith("en", StringComparison.OrdinalIgnoreCase))
                      ?? files.FirstOrDefault();

        if (refFile is null)
        {
            return null;
        }

        var path = GameScanner.RadioInfoPathByFile(gamePath, refFile);

        if (path is null)
        {
            return null;
        }

        try
        {
            return RadioInfo.Load(path);
        }
        catch (Exception ex)
        {
            log?.Invoke($"backup: RadioInfo load failed ({refFile}): {ex.Message}");
            return null;
        }
    }

    private static List<BackupTrackSource> ReadDiskSources(string bankPath, RadioInfo? radio, int stationNumber,
        Action<string>? log)
    {
        if (!File.Exists(bankPath))
        {
            log?.Invoke($"backup: bank file missing: {bankPath}");
            return [];
        }

        var result = BankReader.ReadTracks(bankPath, radio, stationNumber);

        if (result.Error is { } err)
        {
            log?.Invoke($"backup: read failed {Path.GetFileName(bankPath)}: {err}");
        }

        // "sound_N" is BankReader's fallback for ids unknown to RadioInfo;
        // such tracks can't round-trip through the name->hash pipeline on restore.
        var named = result.Tracks.Where(t => !t.SoundName.StartsWith("sound_", StringComparison.Ordinal)).ToList();

        if (named.Count < result.Tracks.Count)
        {
            log?.Invoke($"backup: skipped {result.Tracks.Count - named.Count} unnamed track(s) in {Path.GetFileName(bankPath)}");
        }

        return named
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
                Markers: t.Markers is { Count: > 0 } ? new Dictionary<string, long>(t.Markers) : null))
            .ToList();
    }

    private static void progressExtract(Action<string>? log, string soundName)
    {
        log?.Invoke($"backup: extracting {soundName}");
    }

    public static HashSet<string> CollectOccupiedCustomNames(string gamePath, IReadOnlyCollection<string>? excludeBankNames)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var excluded = excludeBankNames is null
            ? null
            : new HashSet<string>(excludeBankNames, StringComparer.OrdinalIgnoreCase);

        foreach (var bankName in GameScanner.RadioBankNames(gamePath))
        {
            if (excluded is not null && excluded.Contains(bankName))
            {
                continue;
            }

            var bankPath = GameScanner.BankPath(gamePath, bankName);
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
            var bankPath = GameScanner.BankPath(gamePath, g.Bank)
                ?? throw new InvalidOperationException($"bank not found: {g.Bank}");
            bankPaths.Add((g.Bank, bankPath, g.Tracks));
        }

        var xmlTargets = new List<string>();
        foreach (var langFile in GameScanner.LanguageFiles(gamePath))
        {
            var p = GameScanner.RadioInfoPathByFile(gamePath, langFile);
            if (p is not null)
            {
                xmlTargets.Add(p);
            }
        }

        ResolveCustomConflicts(tracks, gamePath, groups.Select(g => g.Bank).ToList(), log);

        // a track whose audio file is gone from the backup folder would silently vanish
        // from the rebuilt bank while RadioInfo still expects it
        var missing = tracks
            .Where(t => string.IsNullOrEmpty(t.Audio) || !File.Exists(Path.Combine(e.Folder, t.Audio)))
            .Select(t => t.SoundName)
            .ToList();

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "backup audio missing for: " + string.Join(", ", missing.Take(5)) + (missing.Count > 5 ? " …" : ""));
        }

        FileGuard.EnsureWritable(bankPaths.Select(b => b.Path).Concat(xmlTargets));

        // phase 1: build every bank to a temp file — a failure here leaves the game files untouched
        var builds = new List<(string Bank, string Path, string Tmp, List<BackupTrack> Tracks)>();
        var framesByName = new Dictionary<string, long>(StringComparer.Ordinal);
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
                    IsReplacement: t.Role == "replacement" || t.Role == "default"))
                .ToList();

            EnsureOriginal(bankPath);

            var tmp = bankPath + ".tmp";

            try
            {
                var added = await BankBuildService.BuildToFileAsync(bankPath, tmp, buildItems, settings, log, null)
                    .ConfigureAwait(false);

                foreach (var a in added)
                {
                    framesByName[a.SoundName] = a.Frames;
                }

                builds.Add((bank, bankPath, tmp, bankTracks));
            }
            catch
            {
                foreach (var b in builds)
                {
                    TryDelete(b.Tmp);
                }

                TryDelete(tmp);
                throw;
            }

            foreach (var t in bankTracks.Where(t => t.Role == "custom"))
            {
                backupCustoms.Add(t.SoundName);
            }
        }

        // phase 2: commit — swap the banks in, then write RadioInfo for every language.
        // Locks may have appeared while the banks were encoding, so re-check right before writing.
        FileGuard.EnsureWritable(bankPaths.Select(b => b.Path).Concat(xmlTargets));

        try
        {
            foreach (var (_, bankPath, tmp, _) in builds)
            {
                File.Move(tmp, bankPath, overwrite: true);
            }

            var failures = new List<string>();
            var langs = 0;

            foreach (var path in xmlTargets)
            {
                try
                {
                    var radio = RadioInfo.Load(path);
                    var editor = radio.StationByNumber(m.StationNumber);

                    if (editor is null)
                    {
                        log?.Invoke($"restore xml: station not found ({LangCode(Path.GetFileName(path))})");
                        continue;
                    }

                    foreach (var (bank, _, bankTracks) in bankPaths)
                    {
                        editor.RegisterBank(bank);

                        foreach (var t in bankTracks)
                        {
                            var frames = framesByName.TryGetValue(t.SoundName, out var f) ? f : t.SampleLength;

                            if (t.Role == "custom")
                            {
                                editor.AddCustom(t.SoundName, frames, t.SampleRate, t.DisplayName, t.Artist);
                            }
                            else
                            {
                                editor.ApplyReplacement(t.SoundName, frames, t.SampleRate);
                                if (t.Role == "default") editor.ClearReplacement(t.SoundName);
                                editor.SetSampleMeta(t.SoundName, t.DisplayName, t.Artist);
                            }
                        }

                        foreach (var t in bankTracks)
                        {
                            if (t.Markers is not null)
                            {
                                editor.SetMarkers(t.SoundName, t.Markers);
                            }
                            editor.SetEnabled(t.SoundName, t.Enabled);
                        }
                    }

                    foreach (var sn in editor.CustomSoundNames().Where(c => !backupCustoms.Contains(c)).ToList())
                    {
                        editor.RemoveCustom(sn);
                        log?.Invoke($"restore xml: removed dangling custom {sn}");
                    }

                    SaveXmlWithBackup(radio, path);
                    langs++;
                    log?.Invoke($"restored xml {LangCode(Path.GetFileName(path))}");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"restore xml FAILED {path}: {ex.Message}");
                    failures.Add($"{LangCode(Path.GetFileName(path))}: {ex.Message}");
                }
            }

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    "RadioInfo save failed for: " + string.Join("; ", failures) +
                    ". Banks are already restored - close whatever holds these files and run the restore again.");
            }

            return (builds.Count, langs);
        }
        catch
        {
            foreach (var (_, _, tmp, _) in builds)
            {
                TryDelete(tmp);
            }

            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
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

    private static void ResolveCustomConflicts(List<BackupTrack> tracks, string gamePath,
        IReadOnlyCollection<string> restoredBanks, Action<string>? log)
    {
        var occupied = CollectOccupiedCustomNames(gamePath, excludeBankNames: restoredBanks);
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
            stationNode = XElement.Parse(File.ReadAllText(refFile));
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

    private static void EnsureOriginal(string path)
    {
        var bak = path + ".bak";

        if (!File.Exists(bak))
        {
            try
            {
                File.Copy(path, bak);
            }
            catch (Exception ex)
            {
                Log.Line($"backup: could not create original .bak for {Path.GetFileName(path)}: {ex.Message}");
            }
            return;
        }

        try
        {
            if (!FevBank.LooksModified(path))
            {
                File.Copy(path, bak, overwrite: true);
            }
        }
        catch
        {
        }
    }

    private static void SaveXmlWithBackup(RadioInfo radio, string path)
    {
        var bak = path + ".bak";

        try
        {
            if (!File.Exists(bak))
            {
                File.Copy(path, bak);
            }
            else if (!XmlIsMarked(path))
            {
                File.Copy(path, bak, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Log.Line($"backup: could not preserve xml .bak for {Path.GetFileName(path)}: {ex.Message}");
        }

        radio.Save(path);
    }

    private static bool XmlIsMarked(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            return text.Contains(RadioInfo.XmlMarker + " edited");
        }
        catch
        {
            return true;
        }
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

    public static string LangCode(string fileName) =>
        fileName.Replace("RadioInfo_", "").Replace(".xml", "");

    private static string GameLabel(string gamePath)
    {
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

        return path is not null && path.EndsWith(".assets.bank") ? "FH5/6" : "FH4";
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
