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

public sealed class BackupManifest
{
    public string Name { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string GameLabel { get; set; } = "";
    public int StationNumber { get; set; }
    public string StationName { get; set; } = "";
    public List<BackupVariant> Variants { get; set; } = [];
    public List<string> Languages { get; set; } = [];
    public int TrackCount { get; set; }
    public int CustomCount { get; set; }
    public int EnabledCount { get; set; }
}

public sealed record BackupEntry(string Folder, BackupManifest Manifest);

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
            catch
            {
                // ignored
            }
        }

        return result.OrderByDescending(e => e.Manifest.CreatedUtc).ToList();
    }

    public static bool Matches(BackupManifest m, string gamePath, StationInfo station) =>
        station.Number == m.StationNumber
        && m.GameLabel == GameLabel(gamePath)
        && m.Variants.Count > 0
        && m.Variants.All(v => GameScanner.BankPath(gamePath, v.BankName) is not null);

    public static BackupEntry Create(string name, string gamePath, StationInfo station, Action<string>? log = null)
    {
        var id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}_{Sanitize(name)}";
        var folder = Path.Combine(Root, id);
        Directory.CreateDirectory(Path.Combine(folder, "banks"));
        Directory.CreateDirectory(Path.Combine(folder, "xml"));

        var variants = new List<BackupVariant>();

        foreach (var variant in station.Variants)
        {
            var bankName = station.BankName(variant);
            var src = GameScanner.BankPath(gamePath, bankName);

            if (src is null)
            {
                log?.Invoke($"backup: bank missing {bankName}");
                continue;
            }

            var file = Path.GetFileName(src);
            File.Copy(src, Path.Combine(folder, "banks", file), overwrite: true);
            variants.Add(new BackupVariant { Variant = variant, BankName = bankName, BankFile = file });
        }

        var languages = new List<string>();
        int trackCount = 0, customCount = 0, enabledCount = 0;
        var counted = false;

        foreach (var langFile in GameScanner.LanguageFiles(gamePath))
        {
            var path = GameScanner.RadioInfoPathByFile(gamePath, langFile);

            if (path is null)
            {
                continue;
            }

            RadioInfo radio;

            try
            {
                radio = RadioInfo.Load(path);
            }
            catch
            {
                continue;
            }

            var node = radio.Document.Descendants("RadioStation")
                .FirstOrDefault(s => (int?) s.Attribute("Number") == station.Number);

            if (node is null)
            {
                continue;
            }

            var lang = LangCode(langFile);
            File.WriteAllText(Path.Combine(folder, "xml", lang + ".xml"), node.ToString());
            languages.Add(lang);

            if (!counted && radio.StationByNumber(station.Number) is { } editor)
            {
                var tracks = editor.ReadTracks();
                trackCount = tracks.Count;
                customCount = tracks.Count(t => t.Origin == TrackOrigin.Custom);
                enabledCount = tracks.Count(t => t.Enabled);
                counted = true;
            }
        }

        var manifest = new BackupManifest
        {
            Name = name,
            CreatedUtc = DateTime.UtcNow.ToString("o"),
            GameLabel = GameLabel(gamePath),
            StationNumber = station.Number,
            StationName = station.Name,
            Variants = variants,
            Languages = languages,
            TrackCount = trackCount,
            CustomCount = customCount,
            EnabledCount = enabledCount,
        };

        File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOpts));
        log?.Invoke($"backup created: {id}");

        return new BackupEntry(folder, manifest);
    }

    public static (int Banks, int Langs) Restore(BackupEntry e, string gamePath, Action<string>? log = null)
    {
        var banks = 0;
        var langs = 0;

        foreach (var v in e.Manifest.Variants)
        {
            var dst = GameScanner.BankPath(gamePath, v.BankName);
            var src = Path.Combine(e.Folder, "banks", v.BankFile);

            if (dst is null || !File.Exists(src))
            {
                log?.Invoke($"restore: bank skip {v.BankName}");
                continue;
            }

            try
            {
                File.Copy(src, dst, overwrite: true);
                banks++;
                log?.Invoke($"restored bank {v.BankName}");
            }
            catch (Exception ex)
            {
                log?.Invoke($"restore bank FAILED {v.BankName}: {ex.Message}");
            }
        }

        foreach (var langFile in GameScanner.LanguageFiles(gamePath))
        {
            var stored = Path.Combine(e.Folder, "xml", LangCode(langFile) + ".xml");

            if (!File.Exists(stored))
            {
                continue;
            }

            var path = GameScanner.RadioInfoPathByFile(gamePath, langFile);

            if (path is null)
            {
                continue;
            }

            try
            {
                var radio = RadioInfo.Load(path);
                var live = radio.Document.Descendants("RadioStation")
                    .FirstOrDefault(s => (int?) s.Attribute("Number") == e.Manifest.StationNumber);

                if (live is null)
                {
                    log?.Invoke($"restore xml: station not found ({LangCode(langFile)})");
                    continue;
                }

                live.ReplaceWith(XElement.Parse(File.ReadAllText(stored)));
                radio.Save(path);
                langs++;
                log?.Invoke($"restored xml {LangCode(langFile)}");
            }
            catch (Exception ex)
            {
                log?.Invoke($"restore xml FAILED {LangCode(langFile)}: {ex.Message}");
            }
        }

        return (banks, langs);
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
            // ignored
        }
    }

    private static string LangCode(string fileName) =>
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
