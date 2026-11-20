using System.Buffers.Binary;
using System.Xml.Linq;
using FH6RB.Core;

namespace FH6RB.Services;

public static class BackupService
{
    internal static List<string> Find(string gamePath) => GameScanner.BackupFiles(gamePath);

    public static bool Has(string gamePath) => Find(gamePath).Count > 0;

    public static bool HasModified(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
        {
            return false;
        }

        foreach (var file in GameScanner.LanguageFiles(gamePath))
        {
            var path = GameScanner.RadioInfoPathByFile(gamePath, file);

            if (path is null)
            {
                continue;
            }

            try
            {
                var text = File.ReadAllText(path);

                if (text.Contains(RadioInfo.XmlMarker) || text.Contains(Naming.CustomPrefix))
                {
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    public static (int Banks, int Langs) RestoreStation(string gamePath, StationInfo station, Action<string>? log = null)
    {
        using var transaction = new GameFileTransaction(gamePath);
        transaction.EnsureGameClosed();
        var resources = GameScanner.ReadRadioResources(gamePath);
        var bankTargets = new List<(string Dst, string Bak, string Name)>();

        foreach (var variant in station.Variants)
        {
            var bankName = station.BankName(variant);
            var dst = resources.BankPath(bankName);

            if (dst is null)
            {
                log?.Invoke($"restore: bank not found {bankName}");
                continue;
            }

            var bak = dst + ".bak";

            if (!File.Exists(bak))
            {
                log?.Invoke($"restore: no original for {bankName}");
                continue;
            }

            bankTargets.Add((dst, bak, bankName));
        }

        var xmlTargets = new List<(string Path, string Bak)>();

        foreach (var langFile in resources.Languages)
        {
            var path = resources.LanguagePath(langFile);

            if (path is null)
            {
                continue;
            }

            var bak = path + ".bak";

            if (!File.Exists(bak))
            {
                continue;
            }

            xmlTargets.Add((path, bak));
        }

        var restoredIds = new Dictionary<string, HashSet<ulong>>(StringComparer.OrdinalIgnoreCase);
        var changedBanks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var removed = new HashSet<ulong>();
        var langs = 0;
        foreach (var (dst, bak, name) in bankTargets)
        {
            var staged = StageIfDifferent(transaction, bak, dst);
            var ids = ValidateBank(staged ?? bak, bak);
            restoredIds.Add(name, ids);
            if (staged is null) continue;

            changedBanks.Add(name, staged);
            if (FevBank.ReadCompleteStblIdsFromFile(dst) is { } previous)
                removed.UnionWith(previous.Where(id => !ids.Contains(id)));
        }

        var bankIds = restoredIds.ToDictionary(pair => pair.Key, pair => (HashSet<ulong>?)pair.Value, StringComparer.OrdinalIgnoreCase);
        var selectedBanks = new Dictionary<string, HashSet<ulong>>(restoredIds, StringComparer.OrdinalIgnoreCase);
        foreach (var bankName in station.Variants.Select(station.BankName))
        {
            if (!selectedBanks.ContainsKey(bankName) && resources.BankPath(bankName) is { } bankPath
                && FevBank.ReadCompleteStblIdsFromFile(bankPath) is { } ids)
            {
                selectedBanks.Add(bankName, ids);
                bankIds[bankName] = ids;
            }
        }
        foreach (var (path, bak) in xmlTargets)
        {
            try
            {
                var original = ReadRadioBackup(bak, bak);
                var orig = original.Document.Descendants("RadioStation")
                    .FirstOrDefault(s => (int?) s.Attribute("Number") == station.Number);

                if (orig is null)
                {
                    log?.Invoke("restore xml: station not in original");
                    continue;
                }

                var radio = RadioInfo.Load(path);
                var before = new XDocument(radio.Document);
                var live = radio.Document.Descendants("RadioStation")
                    .FirstOrDefault(s => (int?) s.Attribute("Number") == station.Number);

                if (live is null)
                {
                    log?.Invoke("restore xml: station not found");
                    continue;
                }

                var edited = new RadioStationEditor(live);
                var reference = new RadioStationEditor(orig);
                var markerChanges = reference.ReadTracks().Where(track => !edited.SameAudioFields(reference, track.SoundName))
                    .Select(track => track.SoundName).ToHashSet(StringComparer.Ordinal);
                live.ReplaceWith(new XElement(orig));
                RestoreSharedAudio(radio, original, station.Number, selectedBanks, restoredIds, markerChanges,
                    removed, resources, bankIds, log);
                langs++;
                if (XNode.DeepEquals(before, radio.Document)) continue;
                radio.Save(transaction.Stage(path));
            }
            catch (Exception ex)
            {
                throw new IOException($"Could not prepare station XML {path}: {ex.Message}", ex);
            }
        }

        var withoutBackup = resources.Languages.Select(resources.LanguagePath).OfType<string>()
            .Where(path => !File.Exists(path + ".bak")).ToArray();
        if (changedBanks.Count > 0 && withoutBackup.Length > 0)
        {
            var layouts = changedBanks.ToDictionary(pair => pair.Key,
                pair => FevBank.ReadTrackInfoFromFile(pair.Value).ToDictionary(track => track.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var path in withoutBackup)
            {
                var radio = RadioInfo.Load(path);
                if (layouts.Any(pair => radio.StationsForBank(pair.Key).SelectMany(editor => editor.ReadTracks())
                    .Any(track => NeedsOriginalTiming(track, pair.Value, removed))))
                    throw new IOException($"Cannot restore a bank without the original RadioInfo: {path}");
            }
        }

        transaction.Commit();
        log?.Invoke($"restored station #{station.Number}: {bankTargets.Count} bank(s), {langs} language file(s)");
        return (bankTargets.Count, langs);
    }

    private static bool NeedsOriginalTiming(RadioTrack track, IReadOnlyDictionary<ulong, FevBank.BankTrack> originals,
        IReadOnlySet<ulong> removed)
    {
        var id = Lookup.SoundNameToId(track.SoundName);
        if (removed.Contains(id)) return true;
        if (!originals.TryGetValue(id, out var original)) return false;
        if (original.SampleRate <= 0) return true;

        var originalLength = track.UsesMilliseconds
            ? (long)Math.Round(original.Frames * 1000m / original.SampleRate, MidpointRounding.AwayFromZero)
            : original.Frames;
        return track.Replaced || track.SampleLength != originalLength
            || !track.UsesMilliseconds && track.SampleRate > 0 && track.SampleRate != original.SampleRate;
    }

    private static void RestoreSharedAudio(RadioInfo radio, RadioInfo original, int stationNumber,
        IReadOnlyDictionary<string, HashSet<ulong>> selectedBanks, IReadOnlyDictionary<string, HashSet<ulong>> restoredIds,
        IReadOnlySet<string> markerChanges, IReadOnlySet<ulong> removed, GameScanner.RadioResources resources,
        Dictionary<string, HashSet<ulong>?> bankIds, Action<string>? log)
    {
        foreach (var (bank, ids) in selectedBanks)
        {
            foreach (var editor in radio.StationsForBank(bank, stationNumber))
            {
                if (editor.Number is not { } number || original.StationByNumber(number) is not { } reference) continue;
                foreach (var track in editor.ReadTracks())
                {
                    var restoreMarkers = markerChanges.Contains(track.SoundName);
                    if ((restoreMarkers || restoredIds.ContainsKey(bank)) && ids.Contains(Lookup.SoundNameToId(track.SoundName)))
                        editor.RestoreAudioFieldsFrom(reference, track.SoundName, restoreMarkers);
                }
                if (removed.Count == 0) continue;

                var union = new HashSet<ulong>();
                bool CompleteBank(string name)
                {
                    if (!bankIds.TryGetValue(name, out var current))
                    {
                        var path = resources.BankPath(name);
                        current = path is null ? null : FevBank.ReadCompleteStblIdsFromFile(path);
                        bankIds.Add(name, current);
                    }
                    if (current is null) return false;
                    union.UnionWith(current);
                    return true;
                }
                if (editor.TrackBankNames().All(CompleteBank))
                    editor.ReconcileTracks(union, Lookup.SoundNameToId, log, removed);
            }
        }
    }

    public static (int Restored, int Failed) Restore(string gamePath, Action<string>? log = null)
    {
        using var transaction = new GameFileTransaction(gamePath);
        return Restore(transaction, Find(gamePath), log);
    }

    internal static (int Restored, int Failed) Restore(string gamePath, IReadOnlyList<string> baks, Action<string>? log)
    {
        using var transaction = new GameFileTransaction(gamePath);
        return Restore(transaction, baks, log);
    }

    private static (int Restored, int Failed) Restore(GameFileTransaction transaction, IReadOnlyList<string> baks, Action<string>? log)
    {
        transaction.EnsureGameClosed();
        var restored = 0;
        foreach (var bak in baks)
        {
            var target = bak[..^4];
            var staged = StageIfDifferent(transaction, bak, target);
            if (GameScanner.LogicalFileName(target).EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                ReadRadioBackup(staged ?? bak, bak);
            else
                ValidateBank(staged ?? bak, bak);
            if (staged is not null) restored++;
        }

        transaction.Commit();
        log?.Invoke($"restored {restored} original file(s)");
        return (restored, 0);
    }

    private static string? StageIfDifferent(GameFileTransaction transaction, string backup, string target)
    {
        if (File.Exists(target) && SameContents(backup, target)) return null;
        var staged = transaction.Stage(target);
        Atomic.Copy(backup, staged);
        return staged;
    }

    private static bool SameContents(string source, string target)
    {
        using var left = File.OpenRead(source);
        using var right = File.OpenRead(target);
        if (left.Length != right.Length) return false;

        var a = new byte[64 * 1024];
        var b = new byte[a.Length];
        while (left.Position < left.Length)
        {
            var size = (int)Math.Min(a.Length, left.Length - left.Position);
            left.ReadExactly(a.AsSpan(0, size));
            right.ReadExactly(b.AsSpan(0, size));
            if (!a.AsSpan(0, size).SequenceEqual(b.AsSpan(0, size))) return false;
        }
        return true;
    }

    private static RadioInfo ReadRadioBackup(string path, string backup)
    {
        try
        {
            var radio = RadioInfo.Load(path);
            if (!radio.Document.Descendants("RadioStation").Any())
                throw new InvalidDataException("No radio stations were found.");
            return radio;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidDataException)
        {
            throw new IOException($"Invalid original RadioInfo backup: {backup}", ex);
        }
    }

    internal static HashSet<ulong> ValidateBank(string path, string backup)
    {
        try
        {
            var ids = FevBank.ReadCompleteStblIdsFromFile(path) ?? throw new InvalidDataException("Invalid FEV/STBL data.");
            var skeleton = FevBank.ReadSkeleton(path);
            if (skeleton.Empty && ids.Count == 0) return ids;

            var (header, samples) = Fsb5.ReadLayout(skeleton.Fsb5HeaderRegion);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
            if (skeleton.Stbl.Count != ids.Count || samples.Count != ids.Count
                || skeleton.Stbl.Select(track => track.Index).Distinct().Count() != ids.Count
                || skeleton.Stbl.Any(track => (uint)track.Index >= (uint)samples.Count)
                || skeleton.DataStartAbs + length > new FileInfo(path).Length)
                throw new InvalidDataException("Invalid FSB5 sample mapping or data size.");
            return ids;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or OverflowException)
        {
            throw new IOException($"Invalid original bank backup: {backup}", ex);
        }
    }
}
