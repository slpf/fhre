using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace FH6RB.Services;

public static class Loudnorm
{
    public static string Filter(string source, AppSettings settings)
    {
        var basef = BaseFilter(settings);
        return BuildFilter(basef, Measure(source, basef));
    }

    public static async Task<string> FilterAsync(string source, AppSettings settings)
    {
        var basef = BaseFilter(settings);
        return BuildFilter(basef, await MeasureAsync(source, basef).ConfigureAwait(false));
    }

    public static async Task<string?> MeasureIntegratedAsync(string source)
    {
        var m = await MeasureAsync(source, "loudnorm=I=-23:TP=-1:LRA=11").ConfigureAwait(false);
        return m?.Mi;
    }

    private static string BaseFilter(AppSettings settings)
    {
        var i = settings.TargetLufs.ToString(CultureInfo.InvariantCulture);
        var tp = settings.TargetTruePeak.ToString(CultureInfo.InvariantCulture);
        return $"loudnorm=I={i}:TP={tp}:LRA=11";
    }

    private static string BuildFilter(string basef, (string Mi, string Mtp, string Mlra, string Mthresh, string Off)? m)
    {
        if (m is null)
        {
            return basef;
        }

        var (mi, mtp, mlra, mthresh, off) = m.Value;

        return basef +
            $":measured_I={mi}:measured_TP={mtp}:measured_LRA={mlra}:measured_thresh={mthresh}:offset={off}:linear=true";
    }

    private static string MeasureArgs(string source, string basef)
        => $"-hide_banner -i \"{source}\" -af {basef}:print_format=json -f null -";

    private static (string Mi, string Mtp, string Mlra, string Mthresh, string Off)? Measure(string source, string basef)
    {
        try
        {
            var (_, err, code) = Proc.Run(Tools.FfmpegPath, MeasureArgs(source, basef), timeoutMs: 5 * 60 * 1000);
            return code != 0 ? null : Parse(err);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(string Mi, string Mtp, string Mlra, string Mthresh, string Off)?> MeasureAsync(string source, string basef)
    {
        try
        {
            var (_, err, code) = await Task.Run(
                () => Proc.Run(Tools.FfmpegPath, MeasureArgs(source, basef), timeoutMs: 5 * 60 * 1000)
            ).ConfigureAwait(false);
            return code != 0 ? null : Parse(err);
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
