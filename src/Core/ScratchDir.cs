namespace FH6RB.Core;

public sealed class ScratchDir : IDisposable
{
    private const string Root = "FH6RB";

    public string Path { get; }

    public ScratchDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch
        {
            // ignored
        }
    }
    
    public static void CleanupStale(TimeSpan olderThan)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Root);
        if (!Directory.Exists(root))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - olderThan;
        foreach (var dir in Directory.GetDirectories(root))
        {
            try
            {
                if (Directory.GetCreationTimeUtc(dir) < cutoff)
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
