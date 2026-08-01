using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FH6RB.Core;

namespace FH6RB.Services;

public static class AudioDecoder
{
    private static readonly string Dir = Path.Combine(AppPaths.TempBase, "preview");
    private static readonly TimeSpan MaxCacheAge = TimeSpan.FromDays(7);
    private const int NormScheme = 2;
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
                }
            }
        }
        catch
        {
        }
    }

    public static string DecodeAdded(string source, AppSettings s, CancellationToken ct = default)
    {
        var norm = s.LoudnessNormalize;
        var i = s.TargetLufs.ToString(CultureInfo.InvariantCulture);
        var tp = s.TargetTruePeak.ToString(CultureInfo.InvariantCulture);
        var key = Key($"add|{source}|{Stamp(source)}|{norm}|{i}|{tp}|nv{NormScheme}");
        var outWav = Path.Combine(Dir, key + ".wav");

        if (File.Exists(outWav))
        {
            return outWav;
        }

        Directory.CreateDirectory(Dir);
        PurgeStale();

        var filter = norm ? Loudnorm.Filter(source, s) : "";
        var af = filter.Length > 0 ? $"-af {filter} " : "";

        var part = Path.Combine(Dir, key + "." + Guid.NewGuid().ToString("N") + ".part.wav");
        try
        {
            var (_, err, code) = Proc.Run(Tools.FfmpegPath,
                $"-y -hide_banner -nostats -loglevel info -i \"{source}\" -ar 48000 -ac 2 -c:a pcm_s16le {af}\"{part}\"",
                ct, timeoutMs: 20 * 60 * 1000);

            if (code != 0)
            {
                throw new InvalidOperationException($"ffmpeg exited {code}: {err.Trim()}");
            }

            if (norm)
            {
                LogLoudnorm(source, err);
            }

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

        var part = Path.Combine(Dir, key + "." + Guid.NewGuid().ToString("N") + ".part.wav");
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

    private static void LogLoudnorm(string source, string stderr)
    {
        var sp = Loudnorm.ParseSecondPass(stderr);
        if (sp is null)
        {
            Log.Line($"loudnorm: no stats | {Path.GetFileName(source)}");
            return;
        }

        Log.Line($"loudnorm [{sp.NormType}] in_I={sp.InputI} in_LRA={sp.InputLra} out_I={sp.OutputI} | {Path.GetFileName(source)}");
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
