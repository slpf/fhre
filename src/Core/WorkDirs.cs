namespace FH6RB.Core;

public static class WorkDirs
{
    public static string WavDir { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "wav");
    public static string FsbDir { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "fsb");

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
                // ignored
            }
        }
    }
}
