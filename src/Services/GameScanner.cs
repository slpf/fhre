using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FH6RB.Services;

public static partial class GameScanner
{
    // Categorised outcome of a Scan(). The settings dialog uses Issue to display a precise
    // reason to the user instead of a generic "no radio info" message. Transient vs permanent
    // classification lets the caller decide whether to retry.
    public enum GameScanIssue
    {
        None,
        EmptyPath,
        DirectoryMissing,
        ExeMissing,
        LanguageFilesMissing,    // tree walked cleanly, no RadioInfo_*.xml under root
        AccessDenied,             // walked but hit UnauthorizedAccessException on some subdirs
        TransientLock,            // walked but hit IOException on some subdirs (sharing violation, dir vanished)
        PartialAccess,            // found files, but some subdirs returned errors
        OtherError,
    }

    public sealed record GameScanResult(
        bool IsValid,
        GameScanIssue Issue,
        string? Detail,
        string? ExePath,
        int LanguageFileCount,
        int BankCount);

    // Per-call exception counters. Passed by reference through SafeEnumerate so callers can
    // tell whether "no files found" means "directory is empty" vs "could not read it".
    private sealed class ScanStats
    {
        public int AccessDeniedDirs;
        public int TransientLockDirs;
        public int OtherErrorDirs;
        public readonly List<string> Samples = new();   // up to 3 offending paths for the detail string
    }

    private const int MaxScanDetailSamples = 3;

    public static bool IsValid(string gamePath) => Scan(gamePath).IsValid;

    // Thorough scan with categorised failure reason. Slightly heavier than IsValid because it
    // also counts bank files; use IsValid for hot-path bool checks and Scan when the reason
    // matters (e.g. the settings dialog).
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
        // Transient I/O on the first attempt is almost always AV / Steam overlay / Windows
        // Search finishing its initial scan. Retry once after a short pause so devs can see
        // the recover rather than chase a phantom "missing files" bug.
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
                .EnumerateFiles(gamePath, "ForzaHorizon*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
        return SafeEnumerate(gamePath, "RadioInfo_*.xml", stats: stats)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .Where(IsLanguageFile)
            .Distinct()
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
        return SafeEnumerate(gamePath, fileName).FirstOrDefault();
    }

    public static List<string> RadioBankNames(string gamePath)
    {
        return CollectBankNames(gamePath, stats: null);
    }

    private static List<string> CollectBankNames(string gamePath, ScanStats? stats)
    {
        var names = new List<string>();

        const string assets = ".assets.bank";
        names.AddRange(SafeEnumerate(gamePath, "R*_Tracks_*.assets.bank", stats: stats)
            .Select(f => Path.GetFileName(f)[..^assets.Length])
            .Where(n => VariantBankRegex().IsMatch(n)));

        const string bank = ".bank";
        names.AddRange(SafeEnumerate(gamePath, "R*_Tracks.bank", stats: stats)     // FH4: без _assets и без варианта
            .Select(f => Path.GetFileName(f)[..^bank.Length])
            .Where(n => PlainBankRegex().IsMatch(n)));

        var result = names.Distinct().OrderBy(x => x).ToList();
        Log.Line($"GameScanner: radio banks = {result.Count} [{string.Join(", ", result)}]");

        return result;
    }

    public static string? BankPath(string gamePath, string bankName)
    {
        var path = SafeEnumerate(gamePath, $"{bankName}.assets.bank").FirstOrDefault()
                   ?? SafeEnumerate(gamePath, $"{bankName}.bank").FirstOrDefault();
        Log.Line($"GameScanner: BankPath({bankName}) -> {path ?? "NOT FOUND"}");
        return path;
    }

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Volume Information", "$RECYCLE.BIN", "$SysReset", "$WinREAgent",
        "$Windows.~BT", "$Windows.~WS", "Config.Msi", "Recovery", "WinSxS",
    };

    private static IEnumerable<string> SafeEnumerate(string root, string pattern,
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

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly);
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

    [GeneratedRegex(@"^R\d+_Tracks_.+$")]
    private static partial Regex VariantBankRegex();

    [GeneratedRegex(@"^R\d+_Tracks$")]
    private static partial Regex PlainBankRegex();
}
