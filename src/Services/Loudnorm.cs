using System.Globalization;
using System.Diagnostics;
using System.Text.Json;

namespace FH6RB.Services;

public static class Loudnorm
{
    private const double DefaultLra = 11.0;
    private const double MaxLra = 50.0;

    public static string Filter(string source, AppSettings settings, Action<string>? log = null)
    {
        var basef = BaseFilter(settings);
        return BuildFilter(settings, Measure(source, basef, log));
    }

    public static async Task<string> FilterAsync(string source, AppSettings settings, Action<string>? log = null)
    {
        var basef = BaseFilter(settings);
        return BuildFilter(settings, await MeasureAsync(source, basef, log).ConfigureAwait(false));
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
                Get("output_lra"),
                Get("input_tp"),
                Get("output_tp"));
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
        => $"-hide_banner -nostats -i \"{source}\" -map 0:a:0 -af {basef}:print_format=json -f null -";

    private static (string Mi, string Mtp, string Mlra, string Mthresh, string Off)? Measure(string source, string basef, Action<string>? log = null)
    {
        var timer = Stopwatch.StartNew();
        var args = MeasureArgs(source, basef);
        log?.Invoke($"loudnorm.measure.start $ ffmpeg {args}");
        try
        {
            var (_, err, code) = Proc.Run(Tools.FfmpegPath, args, timeoutMs: 5 * 60 * 1000);
            if (log is not null)
            {
                foreach (var line in err.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    log($"loudnorm.measure.stderr {line.TrimEnd('\r')}");
                }
                log($"loudnorm.measure.exit code={code} elapsedMs={timer.ElapsedMilliseconds}");
            }
            var measured = code != 0 ? null : Parse(err);
            if (measured is null) log?.Invoke("WARN loudnorm.measure.fallback: no finite first-pass statistics; using dynamic normalization.");
            return measured;
        }
        catch (Exception ex)
        {
            log?.Invoke($"WARN loudnorm.measure.failed elapsedMs={timer.ElapsedMilliseconds}: {ex}; using dynamic normalization.");
            return null;
        }
    }

    private static async Task<(string Mi, string Mtp, string Mlra, string Mthresh, string Off)?> MeasureAsync(string source, string basef, Action<string>? log = null)
    {
        return await Task.Run(() => Measure(source, basef, log)).ConfigureAwait(false);
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

    public sealed record SecondPass(string? NormType, string? InputI, string? InputLra, string? OutputI, string? OutputLra,
        string? InputTp = null, string? OutputTp = null);
}
