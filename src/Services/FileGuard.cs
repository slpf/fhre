namespace FH6RB.Services;

public static class FileGuard
{
    public static bool IsLocked(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static IReadOnlyList<string> Locked(IEnumerable<string?> paths) =>
        paths.Where(IsLocked).Select(p => p!).Distinct().ToList();
}
