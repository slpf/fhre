namespace FH6RB.Services;

public static class BackupService
{
    private static readonly string[] Patterns =
    [
        "R*_Tracks_*.assets.bank.bak",
        "R*_Tracks.bank.bak",
        "RadioInfo_*.xml.bak",
    ];
    
    public static List<string> Find(string gamePath)
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
    
    public static (int Restored, int Failed) Restore(string gamePath, Action<string>? log = null)
    {
        var restored = 0;
        var failed = 0;

        foreach (var bak in Find(gamePath))
        {
            var original = bak[..^4];
            try
            {
                File.Copy(bak, original, overwrite: true);
                File.Delete(bak);
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
