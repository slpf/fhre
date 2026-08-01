using System.Globalization;
using System.Text.Json;

namespace FH6RB.Services;

public static class Loudnorm
{
    private const double DefaultLra = 11.0;
    private const double MaxLra = 50.0;

    public static string Filter(string source, AppSettings settings)
    {
        var basef = BaseFilter(settings);
        return BuildFilter(settings, Measure(source, basef));
    }

    public static async Task<string> FilterAsync(string source, AppSettings settings)
    {
        var basef = BaseFilter(settings);
        return BuildFilter(settings, await MeasureAsync(source, basef).ConfigureAwait(false));
    }

    public static async Task<string?> MeasureIntegratedAsync(string source)
    {
        var m = await MeasureAsync(source, "loudnorm=I=-23:TP=-1:LRA=11").ConfigureAwait(false);
        return m?.Mi;
    }

    public static SecondPass? ParseSecondPass(string text)
    {
        var json = ExtractJson(text);
        if (json is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string? Get(string k) => r.TryGetProperty(k, out var v) ? v.GetString() : null;
            return new SecondPass(
                Get("normalization_type"),
                Get("input_i"),
                Get("input_lra"),
                Get("output_i"),
                Get("output_lra"));
        }
        catch
        {
            return null;
        }
    }

    private static string BaseFilter(AppSettings settings)
    {
        var i = settings.TargetLufs.ToString(CultureInfo.InvariantCulture);
        var tp = settings.TargetTruePeak.ToString(CultureInfo.InvariantCulture);
        var lra = DefaultLra.ToString(CultureInfo.InvariantCulture);
        return $"loudnorm=I={i}:TP={tp}:LRA={lra}";
    }

    private static string BuildFilter(AppSettings settings, (string Mi, string Mtp, string Mlra, string Mthresh, string Off)? m)
    {
        var core = m is null ? BaseFilter(settings) : LinearCore(settings, m.Value);
        return core + ":print_format=json";
    }

    private static string LinearCore(AppSettings settings, (string Mi, string Mtp, string Mlra, string Mthresh, string Off) m)
    {
        var lra = ResolveLra(m.Mlra);
        var i = ResolveTarget(settings, m.Mi, m.Mtp);

        return $"loudnorm=I={Fmt(i)}:TP={Fmt(settings.TargetTruePeak)}:LRA={Fmt(lra)}" +
               $":measured_I={m.Mi}:measured_TP={m.Mtp}:measured_LRA={m.Mlra}:measured_thresh={m.Mthresh}:offset={m.Off}:linear=true";
    }

    private static double ResolveLra(string mlra)
    {
        if (double.TryParse(mlra, CultureInfo.InvariantCulture, out var v) && v > DefaultLra)
        {
            return Math.Min(v, MaxLra);
        }

        return DefaultLra;
    }

    private static double ResolveTarget(AppSettings settings, string mi, string mtp)
    {
        if (!double.TryParse(mi, CultureInfo.InvariantCulture, out var ii) ||
            !double.TryParse(mtp, CultureInfo.InvariantCulture, out var tp))
        {
            return settings.TargetLufs;
        }

        var safe = ii - tp + settings.TargetTruePeak - 0.1;
        return safe < settings.TargetLufs ? safe : settings.TargetLufs;
    }

    private static string Fmt(double d) => Math.Round(d, 2).ToString(CultureInfo.InvariantCulture);

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

    private static (string Mi, string Mtp, string Mlra, string Mthresh, string Off)? Parse(string text)
    {
        var json = ExtractJson(text);
        if (json is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
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

    private static string? ExtractJson(string text)
    {
        var open = text.LastIndexOf('{');
        var close = text.LastIndexOf('}');
        return (open < 0 || close <= open) ? null : text[open..(close + 1)];
    }

    public sealed record SecondPass(string? NormType, string? InputI, string? InputLra, string? OutputI, string? OutputLra);
}
