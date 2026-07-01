namespace FH6RB.Core;

public static class Atomic
{
    private static string Tmp(string path) => path + ".tmp";

    private static string EnsureDir(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        return dir ?? "";
    }

    public static void Write(string path, ReadOnlySpan<byte> bytes)
    {
        EnsureDir(path);
        var tmp = Tmp(path);
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    public static void Write(string path, string text)
        => Write(path, System.Text.Encoding.UTF8.GetBytes(text));
    
    public static void Write(string path, Action<Stream> write)
    {
        EnsureDir(path);
        var tmp = Tmp(path);
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                write(fs);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            TryDelete(tmp);
        }
    }
    
    public static void Copy(string src, string dst)
    {
        EnsureDir(dst);
        var tmp = Tmp(dst);
        try
        {
            File.Copy(src, tmp, overwrite: true);
            File.Move(tmp, dst, overwrite: true);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
        }
    }
}
