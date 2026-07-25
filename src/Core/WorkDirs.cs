namespace FH6RB.Core;

public static class WorkDirs
{
    public static string WavDir { get; } = Path.Combine(AppPaths.TempBase, "wav");
    public static string FsbDir { get; } = Path.Combine(AppPaths.TempBase, "fsb");

    public static void Ensure()
    {
        Directory.CreateDirectory(WavDir);
        Directory.CreateDirectory(FsbDir);
    }

    public static void Clean()
    {
        foreach (var dir in new[] { WavDir, FsbDir })
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
