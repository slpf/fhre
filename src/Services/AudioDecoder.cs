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
    private const int NormScheme = 3;
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
        var context = $"preview source \"{source}\"";

        if (File.Exists(outWav))
        {
            LogWave(context, outWav, cacheHit: true);
            return outWav;
        }

        Log.Line($"{context}: cache miss, normalize={norm}, target_I={i}, target_TP={tp}, source_stamp={Stamp(source)}");

        Directory.CreateDirectory(Dir);
        PurgeStale();

        var filter = norm ? Loudnorm.Filter(source, s, message => Log.Line($"{context}: {message}")) : "";
        var af = filter.Length > 0 ? $"-af {filter} " : "";

        var part = Path.Combine(Dir, key + "." + Guid.NewGuid().ToString("N") + ".part.wav");
        try
        {
            var err = Run(Tools.FfmpegPath,
                $"-y -hide_banner -nostats -loglevel info -i \"{source}\" -map 0:a:0 -ar 48000 -ac 2 -c:a pcm_s16le {af}\"{part}\"",
                context, ct);

            if (norm)
            {
                LogLoudnorm(source, err);
            }

            File.Move(part, outWav, overwrite: true);
            LogWave(context, outWav, cacheHit: false);
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
        if (sub0 < 0) throw new ArgumentOutOfRangeException(nameof(sub0));
        var key = Key($"bank|{bankPath}|{Stamp(bankPath)}|{sub0}|bv3");
        var outWav = Path.Combine(Dir, key + ".wav");
        var context = $"preview bank \"{bankPath}\" sample[{sub0}]";

        if (File.Exists(outWav))
        {
            LogWave(context, outWav, cacheHit: true);
            return outWav;
        }

        Log.Line($"{context}: cache miss, source_stamp={Stamp(bankPath)}");

        Directory.CreateDirectory(Dir);
        PurgeStale();

        var part = Path.Combine(Dir, key + "." + Guid.NewGuid().ToString("N") + ".part.wav");
        string? extractedFsb = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var decodePath = bankPath;
            var decodeIndex = sub0;
            bool rawFsb;
            using (var source = File.OpenRead(bankPath))
            {
                Span<byte> magic = stackalloc byte[4];
                source.ReadExactly(magic);
                rawFsb = magic.SequenceEqual("FSB5"u8);
            }
            if (!rawFsb)
            {
                Log.Line($"{context}: extracting selected sample as standalone FSB5");
                var fsb = TrackPack.ExtractSampleFsb(bankPath, sub0);
                ct.ThrowIfCancellationRequested();
                extractedFsb = Path.ChangeExtension(part, ".fsb");
                File.WriteAllBytes(extractedFsb, fsb);
                Log.Line($"{context}: extracted FSB5=\"{extractedFsb}\", bytes={fsb.Length}, " +
                    $"sha256={Convert.ToHexString(SHA256.HashData(fsb))}");
                decodePath = extractedFsb;
                decodeIndex = 0;
            }
            Run(Tools.VgmstreamPath, $"-i -s {decodeIndex + 1} -o \"{part}\" \"{decodePath}\"", context, ct);
            File.Move(part, outWav, overwrite: true);
            LogWave(context, outWav, cacheHit: false);
        }
        catch (Exception ex)
        {
            Log.Line($"{context}: preview failed: {ex.Message}");
            try { File.Delete(part); } catch { }
            throw;
        }
        finally
        {
            if (extractedFsb is not null)
            {
                try { File.Delete(extractedFsb); }
                catch (Exception ex) { Log.Line($"{context}: could not delete extracted FSB5: {ex.Message}"); }
            }
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

        Log.Line($"loudnorm [{sp.NormType}] in_I={sp.InputI} in_LRA={sp.InputLra} in_TP={sp.InputTp} " +
            $"out_I={sp.OutputI} out_LRA={sp.OutputLra} out_TP={sp.OutputTp} | {Path.GetFileName(source)}");
    }

    private static string Stamp(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? $"{file.LastWriteTimeUtc.Ticks}:{file.Length}" : "missing";
    }

    private static string Key(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..16];

    private static void LogWave(string context, string path, bool cacheHit)
    {
        try
        {
            var (rate, frames) = WaveformService.Probe(path);
            var seconds = rate > 0 ? frames / (double) rate : 0;
            Log.Line($"{context}: cache={(cacheHit ? "hit" : "written")}, wav=\"{path}\", " +
                $"bytes={new FileInfo(path).Length}, frames={frames}, rate={rate}, " +
                $"duration={seconds.ToString("0.000", CultureInfo.InvariantCulture)}s");
        }
        catch (Exception ex)
        {
            Log.Line($"{context}: could not inspect preview WAV: {ex.Message}");
        }
    }

    private static string Run(string exe, string args, string context, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        Log.Line($"{context}: $ {Path.GetFileName(exe)} {args}");
        try
        {
            var (output, err, code) = Proc.Run(exe, args, ct, timeoutMs: 20 * 60 * 1000);
            Log.Line($"{context}: {Path.GetFileName(exe)} exit={code}, elapsed_ms={timer.ElapsedMilliseconds}");
            LogOutput(context, "out", output);
            LogOutput(context, "err", err);
            if (code != 0)
            {
                var detail = string.IsNullOrWhiteSpace(err) ? output.Trim() : err.Trim();
                throw new InvalidOperationException($"{Path.GetFileName(exe)} exited {code}: {detail}");
            }
            return err;
        }
        catch (OperationCanceledException)
        {
            Log.Line($"{context}: canceled after {timer.ElapsedMilliseconds} ms");
            throw;
        }
        catch (Exception ex)
        {
            Log.Line($"{context}: failed after {timer.ElapsedMilliseconds} ms: {ex.Message}");
            throw;
        }
    }

    private static void LogOutput(string context, string stream, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        const int limit = 32000;
        var output = text.Length <= limit ? text : text[..limit] + $"\n(truncated, {text.Length} characters total)";
        Log.Line($"{context} [{stream}] {output}");
    }
}
