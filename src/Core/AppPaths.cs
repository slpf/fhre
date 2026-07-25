namespace FH6RB.Core;

public static class AppPaths
{
    public static string TempBase { get; } = ResolveTempBase();

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
