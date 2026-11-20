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

        FileGuard.EnsureWritable(bankTargets.Select(t => t.Dst).Concat(xmlTargets.Select(t => t.Path)));

        var langs = 0;
        foreach (var (dst, bak, _) in bankTargets)
        {
            transaction.StageCopy(bak, dst);
        }

        foreach (var (path, bak) in xmlTargets)
        {
            try
            {
                var original = RadioInfo.Load(bak);
                var orig = original.Document.Descendants("RadioStation")
                    .FirstOrDefault(s => (int?) s.Attribute("Number") == station.Number);

                if (orig is null)
                {
                    log?.Invoke("restore xml: station not in original");
                    continue;
                }

                var radio = RadioInfo.Load(path);
                var live = radio.Document.Descendants("RadioStation")
                    .FirstOrDefault(s => (int?) s.Attribute("Number") == station.Number);

                if (live is null)
                {
                    log?.Invoke("restore xml: station not found");
                    continue;
                }

                live.ReplaceWith(new XElement(orig));
                radio.Save(transaction.Stage(path));
                langs++;
            }
            catch (Exception ex)
            {
                throw new IOException($"Could not prepare station XML {path}: {ex.Message}", ex);
            }
        }

        transaction.Commit();
        log?.Invoke($"restored station #{station.Number}: {bankTargets.Count} bank(s), {langs} language file(s)");
        return (bankTargets.Count, langs);
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
        FileGuard.EnsureWritable(baks.Select(b => b[..^4]));

        foreach (var bak in baks)
        {
            transaction.StageCopy(bak, bak[..^4]);
        }

        transaction.Commit();
        log?.Invoke($"restored {baks.Count} original file(s)");
        return (baks.Count, 0);
    }
}
