using FH6RB.Services;

namespace FH6RB.Core;

public static class AppPaths
{
    public static string TempBase { get; } = ResolveTempBase();
    private static readonly string SessionId = Guid.NewGuid().ToString("N");

    public static string SessionDirectory(string name) => Path.Combine(TempBase, name, SessionId);

    internal static void CleanStaleSessions(string name)
    {
        var root = new DirectoryInfo(Path.Combine(TempBase, name));
        try
        {
            if (!root.Exists || (root.Attributes & FileAttributes.ReparsePoint) != 0) return;
            foreach (var directory in root.EnumerateDirectories())
            {
                if (!Guid.TryParseExact(directory.Name, "N", out _)
                    || string.Equals(directory.Name, SessionId, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if ((directory.Attributes & FileAttributes.ReparsePoint) == 0)
                        directory.Delete(recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Line($"could not remove temporary session '{directory.FullName}': {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Line($"could not scan temporary sessions in '{root.FullName}': {ex.Message}");
        }
    }

    private static string ResolveTempBase()
    {
        var primary = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FHRE");
        try
        {
            Directory.CreateDirectory(primary);
            var probe = Path.Combine(primary, Path.GetRandomFileName());
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return primary;
        }
        catch
        {
        }

        var fallback = Path.Combine(Path.GetTempPath(), "FHRE");
        try { Directory.CreateDirectory(fallback); } catch { }
        return ShortPath.Of(fallback);
    }
}
