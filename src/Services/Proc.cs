using System.Diagnostics;
using System.Text;

namespace FH6RB.Services;

public static class Proc
{
    internal static async Task WaitForCleanupAsync(Process process)
    {
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { Log.Line($"Process cleanup timed out for PID {process.Id}."); }
    }

    public static (string StdOut, string StdErr, int ExitCode) Run(
        string exe, string args,
        CancellationToken ct = default,
        int timeoutMs = 600_000,
        Encoding? stdoutEncoding = null,
        Encoding? stderrEncoding = null)
    {
        ct.ThrowIfCancellationRequested();
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

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        try
        {
            Task.WhenAll(p.WaitForExitAsync(timeout.Token), so, se).WaitAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            WaitForCleanupAsync(p).GetAwaiter().GetResult();
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(exe)} exceeded {timeoutMs} ms");
        }

        return (so.GetAwaiter().GetResult(), se.GetAwaiter().GetResult(), p.ExitCode);
    }
}
