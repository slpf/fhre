using System.Diagnostics;
using System.Text;

namespace FH6RB.Services;

public static class Proc
{
    public static (string StdOut, string StdErr, int ExitCode) Run(
        string exe, string args,
        CancellationToken ct = default,
        int timeoutMs = 600_000,
        Encoding? stdoutEncoding = null,
        Encoding? stderrEncoding = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (stdoutEncoding is not null)
        {
            psi.StandardOutputEncoding = stdoutEncoding;
        }

        if (stderrEncoding is not null)
        {
            psi.StandardErrorEncoding = stderrEncoding;
        }

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"cannot start {Path.GetFileName(exe)}");

        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();

        var deadline = Environment.TickCount + timeoutMs;
        while (!p.HasExited)
        {
            var remain = unchecked(deadline - Environment.TickCount);
            if (ct.IsCancellationRequested || remain <= 0)
            {
                break;
            }

            p.WaitForExit(Math.Min(remain, 500));
        }

        if (!p.HasExited)
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }

            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(exe)} exceeded {timeoutMs} ms");
        }

        return (so.GetAwaiter().GetResult(), se.GetAwaiter().GetResult(), p.ExitCode);
    }
}
