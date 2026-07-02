using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FH6RB.Services;

public static class AudioDecoder
{
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "FHRE", "preview");
    private static readonly TimeSpan MaxCacheAge = TimeSpan.FromDays(7);
    private static int _purged;

    public static void ClearAll()
    {
        try
        {
            if (Directory.Exists(Dir))
            {
                Directory.Delete(Dir, recursive: true);
            }
        }
        catch
        {
            // ignored
        }
    }

    private static void PurgeStale()
    {
        if (Interlocked.CompareExchange(ref _purged, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (!Directory.Exists(Dir))
            {
                return;
            }

            var cutoff = DateTime.UtcNow - MaxCacheAge;
            foreach (var f in Directory.EnumerateFiles(Dir))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(f) < cutoff)
                    {
                        File.Delete(f);
                    }
                }
                catch
                {
                    // ignored
                }
            }
        }
        catch
        {
            // ignored
        }
    }

    public static string DecodeAdded(string source, AppSettings s, CancellationToken ct = default)
    {
        var i = s.TargetLufs.ToString(CultureInfo.InvariantCulture);
        var tp = s.TargetTruePeak.ToString(CultureInfo.InvariantCulture);
        var key = Key($"add|{source}|{Stamp(source)}|{i}|{tp}");
        var outWav = Path.Combine(Dir, key + ".wav");

        if (File.Exists(outWav))
        {
            return outWav;
        }

        Directory.CreateDirectory(Dir);
        PurgeStale();

        var part = outWav + ".part";
        try
        {
            Run(Tools.FfmpegPath,
                $"-y -hide_banner -loglevel error -i \"{source}\" -ar 48000 -ac 2 -c:a pcm_s16le " +
                $"-af {Loudnorm.Filter(source, s)} \"{part}\"", ct);
            File.Move(part, outWav, overwrite: true);
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }

        return outWav;
    }

    public static string DecodeBank(string bankPath, int sub0, CancellationToken ct = default)
    {
        var key = Key($"bank|{bankPath}|{Stamp(bankPath)}|{sub0}");
        var outWav = Path.Combine(Dir, key + ".wav");

        if (File.Exists(outWav))
        {
            return outWav;
        }

        Directory.CreateDirectory(Dir);
        PurgeStale();

        var part = outWav + ".part";
        try
        {
            Run(Tools.VgmstreamPath, $"-s {sub0 + 1} -o \"{part}\" \"{bankPath}\"", ct);
            File.Move(part, outWav, overwrite: true);
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }

        return outWav;
    }

    private static long Stamp(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;

    private static string Key(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..16];

    private static void Run(string exe, string args, CancellationToken ct = default)
    {
        var (_, err, code) = Proc.Run(exe, args, ct, timeoutMs: 20 * 60 * 1000);

        if (code != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(exe)} exited {code}: {err.Trim()}");
        }
    }
}
