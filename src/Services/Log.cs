using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace FH6RB.Services;

public static class Log
{
    private static readonly SessionLog Writer = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FHRE", "fhre.log"));

    public static void Line(string message) => Writer.Line(message);
}

internal sealed class SessionLog(string logFile)
{
    private const int MaxLogFiles = 5;
    private readonly object _sync = new();
    private bool _started;

    private void EnsureStarted()
    {
        if (_started)
        {
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(logFile);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
            var header = $"FHRE log - {DateTime.Now:yyyy-MM-dd HH:mm:ss}; version={version}; " +
                $"runtime={Environment.Version}; pid={Environment.ProcessId}{Environment.NewLine}";
            try
            {
                Rotate();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                File.AppendAllText(logFile, Environment.NewLine + header +
                    $"[{DateTime.Now:HH:mm:ss.fff}] WARN log.rotation.failed: {ex.Message}{Environment.NewLine}");
                _started = true;
                return;
            }
            File.WriteAllText(logFile, header);
            _started = true;
        }
        catch
        {
        }
    }

    private void Rotate()
    {
        if (!File.Exists(logFile)) return;
        for (var index = MaxLogFiles - 1; index >= 1; index--)
        {
            var source = index == 1 ? logFile : ArchivePath(index - 1);
            if (File.Exists(source)) File.Move(source, ArchivePath(index), overwrite: true);
        }
    }

    private string ArchivePath(int index) => Path.ChangeExtension(logFile, $"{index}{Path.GetExtension(logFile)}");

    public void Line(string message)
    {
        lock (_sync)
        {
            EnsureStarted();
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var lines = new StringBuilder();
            foreach (var text in (message ?? "").Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Split('\n'))
            {
                lines.Append('[').Append(timestamp).Append("] ").AppendLine(text);
            }
            var line = lines.ToString();

#if DEBUG
            try
            {
                Console.Write(line);
            }
            catch
            {
            }

            try
            {
                Trace.Write(line);
            }
            catch
            {
            }
#endif

            try
            {
                File.AppendAllText(logFile, line);
                _started = true;
            }
            catch
            {
            }
        }
    }
}
