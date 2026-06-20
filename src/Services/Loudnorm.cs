using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace FH6RB.Services;

public static class Loudnorm
{
    public static string Filter(string source, AppSettings settings)
    {
        var i = settings.TargetLufs.ToString(CultureInfo.InvariantCulture);
        var tp = settings.TargetTruePeak.ToString(CultureInfo.InvariantCulture);
        var basef = $"loudnorm=I={i}:TP={tp}:LRA=11";

        var m = Measure(source, basef);

        if (m is null)
        {
            return basef;
        }

        var (mi, mtp, mlra, mthresh, off) = m.Value;

        return basef +
            $":measured_I={mi}:measured_TP={mtp}:measured_LRA={mlra}:measured_thresh={mthresh}:offset={off}:linear=true";
    }

    private static (string Mi, string Mtp, string Mlra, string Mthresh, string Off)? Measure(string source, string basef)
    {
        try
        {
            var psi = new ProcessStartInfo(Tools.FfmpegPath,
                $"-hide_banner -i \"{source}\" -af {basef}:print_format=json -f null -")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var p = Process.Start(psi);

            if (p is null)
            {
                return null;
            }

            var err = p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();

            return p.ExitCode != 0 ? null : Parse(err);
        }
        catch
        {
            return null;
        }
    }

    private static (string, string, string, string, string)? Parse(string text)
    {
        var open = text.LastIndexOf('{');
        var close = text.LastIndexOf('}');

        if (open < 0 || close <= open)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text[open..(close + 1)]);
            var r = doc.RootElement;

            string Get(string k) => r.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";

            var mi = Get("input_i");
            var mtp = Get("input_tp");
            var mlra = Get("input_lra");
            var mthresh = Get("input_thresh");
            var off = Get("target_offset");

            foreach (var v in new[] { mi, mtp, mlra, mthresh, off })
            {
                if (v.Length == 0 || v.Contains("inf") || v.Contains("nan"))
                {
                    return null;
                }
            }

            return (mi, mtp, mlra, mthresh, off);
        }
        catch
        {
            return null;
        }
    }
}
