using System.Diagnostics;

namespace FH6RB.Services;

public static class Log
{
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FHRE", "fhre.log");

    private static int _started;

    private static void EnsureStarted()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(LogFile);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(LogFile, $"FHRE log - {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    public static void Line(string message)
    {
        EnsureStarted();

        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";

#if DEBUG
        try
        {
            Console.WriteLine(line);
        }
        catch
        {
        }

        try
        {
            Trace.WriteLine(line);
        }
        catch
        {
        }
#endif

        try
        {
            File.AppendAllText(LogFile, line + Environment.NewLine);
        }
        catch
        {
        }
    }
}
