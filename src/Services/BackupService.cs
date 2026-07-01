using System.Xml.Linq;
using FH6RB.Core;

namespace FH6RB.Services;

public static class BackupService
{
    private static readonly string[] Patterns =
    [
        "R*_Tracks_*.assets.bank.bak",
        "R*_Tracks.bank.bak",
        "RadioInfo_*.xml.bak",
    ];

    private static List<string> Find(string gamePath)
    {
        var result = new List<string>();
        
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
        {
            return result;
        }

        foreach (var pattern in Patterns)
        {
            try
            { 
                result.AddRange(Directory.EnumerateFiles(gamePath, pattern, SearchOption.AllDirectories));
            }
            catch
            {
                // ignored
            }
        }

        return result;
    }

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
                // ignored
            }
        }

        return false;
    }

    public static (int Banks, int Langs) RestoreStation(string gamePath, StationInfo station, Action<string>? log = null)
    {
        var banks = 0;
        var langs = 0;

        foreach (var variant in station.Variants)
        {
            var bankName = station.BankName(variant);
            var dst = GameScanner.BankPath(gamePath, bankName);

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

            try
            {
                Atomic.Copy(bak, dst);
                banks++;
                log?.Invoke($"restored bank {bankName}");
            }
            catch (Exception ex)
            {
                log?.Invoke($"restore bank FAILED {bankName}: {ex.Message}");
            }
        }

        foreach (var langFile in GameScanner.LanguageFiles(gamePath))
        {
            var path = GameScanner.RadioInfoPathByFile(gamePath, langFile);

            if (path is null)
            {
                continue;
            }

            var bak = path + ".bak";

            if (!File.Exists(bak))
            {
                continue;
            }

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
                radio.Save(path);
                langs++;
                log?.Invoke("restored xml");
            }
            catch (Exception ex)
            {
                log?.Invoke($"restore xml FAILED: {ex.Message}");
            }
        }

        return (banks, langs);
    }

    public static (int Restored, int Failed) Restore(string gamePath, Action<string>? log = null)
    {
        var restored = 0;
        var failed = 0;

        foreach (var bak in Find(gamePath))
        {
            var original = bak[..^4];
            
            try
            {
                Atomic.Copy(bak, original);
                restored++;
                log?.Invoke($"restored {Path.GetFileName(original)}");
            }
            catch (Exception ex)
            {
                failed++;
                log?.Invoke($"restore FAILED {Path.GetFileName(original)}: {ex.Message}");
            }
        }

        return (restored, failed);
    }
}
