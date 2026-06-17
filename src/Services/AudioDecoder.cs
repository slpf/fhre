using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FH6RB.Services;

public static class AudioDecoder
{
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "FHRE", "preview");
    
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

    public static string DecodeAdded(string source, AppSettings s)
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
        
        Run(Tools.FfmpegPath,
            $"-y -hide_banner -loglevel error -i \"{source}\" -ar 48000 -ac 2 -c:a pcm_s16le " +
            $"-af loudnorm=I={i}:TP={tp}:LRA=11 \"{outWav}\"");
        
        return outWav;
    }

    public static string DecodeBank(string bankPath, int sub0)
    {
        var key = Key($"bank|{bankPath}|{Stamp(bankPath)}|{sub0}");
        var outWav = Path.Combine(Dir, key + ".wav");
        
        if (File.Exists(outWav))
        {
            return outWav;
        }

        Directory.CreateDirectory(Dir);
        Run(Tools.VgmstreamPath, $"-s {sub0 + 1} -o \"{outWav}\" \"{bankPath}\"");
        
        return outWav;
    }

    private static long Stamp(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;

    private static string Key(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..16];

    private static void Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"cannot start {Path.GetFileName(exe)}");
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        _ = so.GetAwaiter().GetResult();
        var err = se.GetAwaiter().GetResult();

        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(exe)} exited {p.ExitCode}: {err.Trim()}");
        }
    }
}
