using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FH6RB.Services;

public static partial class GameScanner
{
    public static bool IsValid(string gamePath)
    {
        return !string.IsNullOrWhiteSpace(gamePath)
            && Directory.Exists(gamePath)
            && FindExe(gamePath) is not null
            && LanguageFiles(gamePath).Count > 0;
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
        catch
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
        return SafeEnumerate(gamePath, "RadioInfo_*.xml")
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
        var names = new List<string>();

        const string assets = ".assets.bank";
        names.AddRange(SafeEnumerate(gamePath, "R*_Tracks_*.assets.bank")
            .Select(f => Path.GetFileName(f)[..^assets.Length])
            .Where(n => VariantBankRegex().IsMatch(n)));

        const string bank = ".bank";
        names.AddRange(SafeEnumerate(gamePath, "R*_Tracks.bank")     // FH4: без _assets и без варианта
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

    private static IEnumerable<string> SafeEnumerate(string root, string pattern, int maxDepth = 8)
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
            catch
            {
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
            catch
            {
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

    [GeneratedRegex(@"^R\d+_Tracks_.+$")]
    private static partial Regex VariantBankRegex();

    [GeneratedRegex(@"^R\d+_Tracks$")]
    private static partial Regex PlainBankRegex();
}
