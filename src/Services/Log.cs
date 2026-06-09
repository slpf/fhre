using System.Diagnostics;

namespace FH6RB.Services;

public static class Log
{
    public static void Line(string message)
    {
#if DEBUG
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Console.WriteLine(line);
        Trace.WriteLine(line);
#endif
    }
}
