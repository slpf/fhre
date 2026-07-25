using System.Runtime.InteropServices;
using System.Text;

namespace FH6RB.Core;

public static class ShortPath
{
    public static string Of(string path)
    {
        if (string.IsNullOrEmpty(path) || !OperatingSystem.IsWindows())
        {
            return path;
        }

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var sb = new StringBuilder(path.Length + 260);
            var n = GetShortPathName(path, sb, sb.Capacity);
            if (n <= 0)
            {
                return path;
            }

            if (n >= sb.Capacity)
            {
                sb.Capacity = n + 16;
                n = GetShortPathName(path, sb, sb.Capacity);
                if (n <= 0)
                {
                    return path;
                }
            }

            var shortPath = sb.ToString(0, n);
            return shortPath.Length > 0 ? shortPath : path;
        }
        catch
        {
            return path;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetShortPathName(string lpszLongPath, StringBuilder lpszShortPath, int cchBuffer);
}
