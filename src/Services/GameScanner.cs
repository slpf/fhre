using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace FH6RB.Services;

public static partial class GameScanner
{
    public enum GameScanIssue
    {
        None,
        EmptyPath,
        DirectoryMissing,
        ExeMissing,
        LanguageFilesMissing,
        AccessDenied,
        TransientLock,
        PartialAccess,
        OtherError,
    }

    public sealed record GameScanResult(
        bool IsValid,
        GameScanIssue Issue,
        string? Detail,
        string? ExePath,
        int LanguageFileCount,
        int BankCount);

    private sealed class ScanStats
    {
        public int AccessDeniedDirs;
        public int TransientLockDirs;
        public int OtherErrorDirs;
        public readonly List<string> Samples = new();
    }

    private const int MaxScanDetailSamples = 3;

    public static bool IsValid(string gamePath) => Scan(gamePath).IsValid;

    public static GameScanResult Scan(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return new GameScanResult(false, GameScanIssue.EmptyPath, null, null, 0, 0);
        }

        if (!Directory.Exists(gamePath))
        {
            return new GameScanResult(false, GameScanIssue.DirectoryMissing, gamePath, null, 0, 0);
        }

        var exe = FindExe(gamePath);
        if (exe is null)
        {
            return new GameScanResult(false, GameScanIssue.ExeMissing, gamePath, null, 0, 0);
        }

        var stats = new ScanStats();
        var langs = CollectLanguageFiles(gamePath, stats);

#if DEBUG
        if (langs.Count == 0 && stats.TransientLockDirs > 0)
        {
            Thread.Sleep(150);
            stats = new ScanStats();
            langs = CollectLanguageFiles(gamePath, stats);
            if (langs.Count > 0)
            {
                Log.Line($"GameScanner: language scan recovered after retry ({langs.Count} files)");
            }
        }
#endif

        var banks = CollectBankNames(gamePath, stats);

        if (langs.Count == 0)
        {
            var issue = stats.OtherErrorDirs > 0 ? GameScanIssue.OtherError
                      : stats.AccessDeniedDirs > 0 ? GameScanIssue.AccessDenied
                      : stats.TransientLockDirs > 0 ? GameScanIssue.TransientLock
                      : GameScanIssue.LanguageFilesMissing;
            return new GameScanResult(false, issue, BuildDetail(stats), exe, 0, banks.Count);
        }

        if (stats.AccessDeniedDirs > 0 || stats.TransientLockDirs > 0 || stats.OtherErrorDirs > 0)
        {
            return new GameScanResult(true, GameScanIssue.PartialAccess, BuildDetail(stats),
                exe, langs.Count, banks.Count);
        }

        return new GameScanResult(true, GameScanIssue.None, null, exe, langs.Count, banks.Count);
    }

    private static string? BuildDetail(ScanStats stats)
    {
        if (stats.AccessDeniedDirs == 0 && stats.TransientLockDirs == 0 && stats.OtherErrorDirs == 0)
        {
            return null;
        }

        var parts = new List<string>(4);
        if (stats.AccessDeniedDirs > 0) parts.Add($"{stats.AccessDeniedDirs} access-denied");
        if (stats.TransientLockDirs > 0) parts.Add($"{stats.TransientLockDirs} transient");
        if (stats.OtherErrorDirs > 0) parts.Add($"{stats.OtherErrorDirs} other");
        var summary = string.Join(", ", parts);
        var samples = stats.Samples.Count > 0 ? " [" + string.Join("; ", stats.Samples) + "]" : "";
        return $"{summary}{samples}";
    }

    public static string? FindExe(string gamePath)
    {
        if (!Directory.Exists(gamePath))
        {
            return null;
        }

        try
        {
            return Directory
                .EnumerateFiles(gamePath, "ForzaHorizon*.exe", FileEnumeration)
                .FirstOrDefault()
                ?? Directory.EnumerateFiles(gamePath, "forza_durango_*.exe", FileEnumeration)
                    .FirstOrDefault()
                ?? FindHorizon3Executable(gamePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static int DetectVersion(string gamePath)
    {
        var exe = FindExe(gamePath);
        if (exe is null)
        {
            return 0;
        }

        var name = Path.GetFileNameWithoutExtension(exe);
        if (name.StartsWith("forza_durango_", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (name.Equals("forza_x64_release_final", StringComparison.OrdinalIgnoreCase)
            && FindHorizon3Executable(gamePath) is not null)
        {
            return 3;
        }

        var m = Regex.Match(name, @"\d+$");
        return m.Success && int.TryParse(m.Value, out var v) ? v : 0;
    }

    private static string? FindHorizon3Executable(string gamePath)
    {
        try
        {
            var manifest = Directory.EnumerateFiles(gamePath, "AppxManifest.xml", FileEnumeration).FirstOrDefault();
            if (manifest is null) return null;
            using var reader = XmlReader.Create(manifest, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            var document = XDocument.Load(reader);
            if (!document.Descendants().Any(e => e.Name.LocalName == "Identity"
                    && string.Equals((string?) e.Attribute("Name"), "Microsoft.OpusPG", StringComparison.Ordinal)))
            {
                return null;
            }
            var declared = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Application"
                && string.Equals((string?) e.Attribute("Executable"), "forza_x64_release_final.exe",
                    StringComparison.OrdinalIgnoreCase));
            return declared is null ? null
                : Directory.EnumerateFiles(gamePath, "forza_x64_release_final.exe", FileEnumeration).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }
    }

    public static bool IsGameProcessRunning(string gamePath)
    {
        var exe = FindExe(gamePath);
        if (exe is null)
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(exe);
        Process[] procs;
        try
        {
            procs = Process.GetProcessesByName(name);
        }
        catch
        {
            return false;
        }

        try
        {
            return procs.Length > 0;
        }
        finally
        {
            foreach (var p in procs)
            {
                p.Dispose();
            }
        }
    }

    public static List<string> LanguageFiles(string gamePath)
    {
        return CollectLanguageFiles(gamePath, stats: null);
    }

    private static List<string> CollectLanguageFiles(string gamePath, ScanStats? stats)
    {
        return SafeEnumerate(gamePath, LanguagePatterns, stats: stats)
            .Select(LogicalFileName)
            .Where(IsLanguageFile)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsLanguageFile(string name)
    {
        if (!name.StartsWith("RadioInfo_", StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var code = name[10..^4];
        return code.Length is > 0 and <= 8 && code.All(c => char.IsAsciiLetter(c) || c == '-');
    }

    public static string? RadioInfoPathByFile(string gamePath, string fileName)
    {
        var logical = LogicalFileName(fileName);
        if (!IsLanguageFile(logical)) return null;
        return SafeEnumerate(gamePath, logical).FirstOrDefault()
               ?? SafeEnumerate(gamePath, Fh3FileNameCodec.Encode(logical)).FirstOrDefault();
    }

    public static List<string> RadioBankNames(string gamePath)
    {
        return CollectBankNames(gamePath, stats: null);
    }

    private static List<string> CollectBankNames(string gamePath, ScanStats? stats)
    {
        var result = SafeEnumerate(gamePath, BankPatterns, stats: stats)
            .Select(path => BankNameFromFile(LogicalFileName(path)))
            .Where(name => name is not null)
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        Log.Line($"GameScanner: radio banks = {result.Count} [{string.Join(", ", result)}]");

        return result;
    }

    public static string? BankPath(string gamePath, string bankName)
    {
        var canonical = BankNameFromFile(bankName + ".bank");
        if (!string.Equals(canonical, bankName, StringComparison.OrdinalIgnoreCase)) canonical = null;
        string? path = null;
        if (canonical is not null)
        {
            foreach (var suffix in new[] { ".assets.bank", ".bank" })
            {
                var fileName = canonical + suffix;
                path = SafeEnumerate(gamePath, fileName).FirstOrDefault()
                       ?? SafeEnumerate(gamePath, Fh3FileNameCodec.Encode(fileName)).FirstOrDefault();
                if (path is not null) break;
            }
        }
        Log.Line($"GameScanner: BankPath({bankName}) -> {path ?? "NOT FOUND"}");
        return path;
    }

    public static List<string> BackupFiles(string gamePath) => SafeEnumerate(gamePath, "*.bak")
        .Where(path => IsRadioResource(LogicalFileName(path[..^4])))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public static string LogicalFileName(string pathOrName)
    {
        ArgumentNullException.ThrowIfNull(pathOrName);
        var name = Path.GetFileName(pathOrName);
        var logical = name;
        if (name.EndsWith(".]gb", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".`l8s", StringComparison.OrdinalIgnoreCase))
        {
            var decoded = Fh3FileNameCodec.Decode(name.ToLowerInvariant());
            if (IsRadioResource(decoded)) logical = decoded;
        }
        if (IsLanguageFile(logical)) return $"RadioInfo_{logical[10..^4].ToUpperInvariant()}.xml";
        var bank = BankNameFromFile(logical);
        if (bank is null) return name;
        var suffix = logical.EndsWith(".assets.bank", StringComparison.OrdinalIgnoreCase) ? ".assets.bank" : ".bank";
        return bank + suffix;
    }

    private static bool IsRadioResource(string name) => IsLanguageFile(name) || BankNameFromFile(name) is not null;

    private static string? BankNameFromFile(string fileName)
    {
        var suffixLength = fileName.EndsWith(".assets.bank", StringComparison.OrdinalIgnoreCase) ? 12
            : fileName.EndsWith(".bank", StringComparison.OrdinalIgnoreCase) ? 5 : 0;
        if (suffixLength == 0) return null;
        var bank = fileName[..^suffixLength];
        var match = TrackBankRegex().Match(bank);
        if (match.Success)
        {
            var suffix = match.Groups[2].Value;
            if (suffix.Length == 0) return $"R{match.Groups[1].Value}_Tracks";
            if (suffix.Equals("Disk", StringComparison.OrdinalIgnoreCase)) suffix = "Disk";
            else if (Regex.IsMatch(suffix, @"^(?:PDLC|CU)\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                suffix = suffix.ToUpperInvariant();
            return $"R{match.Groups[1].Value}_Tracks_{suffix}";
        }
        match = Horizon2BankRegex().Match(bank);
        return match.Success ? $"HZ2_MX_Radio_{match.Groups[1].Value}_Tracks" : null;
    }

    private static readonly EnumerationOptions FileEnumeration = new()
    {
        MatchCasing = MatchCasing.CaseInsensitive,
        IgnoreInaccessible = false,
        AttributesToSkip = 0
    };

    private static readonly string[] LanguagePatterns =
    [
        "RadioInfo_*.xml",
        Fh3FileNameCodec.Encode("RadioInfo_*.xml")
    ];

    private static readonly string[] BankPatterns =
    [
        "R*_Tracks*.bank",
        Fh3FileNameCodec.Encode("R*_Tracks*.bank"),
        "HZ2_MX_Radio_*_Tracks.bank"
    ];

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Volume Information", "$RECYCLE.BIN", "$SysReset", "$WinREAgent",
        "$Windows.~BT", "$Windows.~WS", "Config.Msi", "Recovery", "WinSxS",
    };

    private static IEnumerable<string> SafeEnumerate(string root, string pattern,
        int maxDepth = 8, ScanStats? stats = null) => SafeEnumerate(root, new[] { pattern }, maxDepth, stats);

    private static IEnumerable<string> SafeEnumerate(string root, IReadOnlyList<string> patterns,
        int maxDepth = 8, ScanStats? stats = null)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            yield break;
        }

        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));

        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();

            foreach (var pattern in patterns)
            {
                string[] files;
                try
                {
                    files = Directory.GetFiles(dir, pattern, FileEnumeration);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    RecordIssue(stats, dir, ex);
                    files = [];
                }

                foreach (var f in files)
                {
                    yield return f;
                }
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            string[] subdirs;
            try
            {
                subdirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RecordIssue(stats, dir, ex);
                subdirs = [];
            }

            foreach (var sub in subdirs)
            {
                if (!SkipDirs.Contains(Path.GetFileName(sub)))
                {
                    stack.Push((sub, depth + 1));
                }
            }
        }
    }

    private static void RecordIssue(ScanStats? stats, string dir, Exception ex)
    {
        if (stats is null) return;
        switch (ex)
        {
            case UnauthorizedAccessException:
                stats.AccessDeniedDirs++;
                break;
            case IOException:
                stats.TransientLockDirs++;
                break;
            default:
                stats.OtherErrorDirs++;
                break;
        }
        if (stats.Samples.Count < MaxScanDetailSamples)
        {
            stats.Samples.Add($"{ex.GetType().Name}@{dir}");
        }
    }

    [GeneratedRegex(@"^R(\d+)_Tracks(?:_([A-Za-z0-9_-]+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrackBankRegex();

    [GeneratedRegex(@"^HZ2_MX_Radio_(\d+)_Tracks$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Horizon2BankRegex();
}
