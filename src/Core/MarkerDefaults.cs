using System.Globalization;

namespace FH6RB.Core;

public static class MarkerDefaults
{
    public const string Off = "";

    private static readonly (string Name, string Spec)[] BaseDefaults =
    [
        ("TrackStart", "0%"),
        ("DJDrop", "5%"),
        ("TrackDrop", "15%"),
        ("TrackLoopStart", "15%"),
        ("PostRaceLoopStart", "75%"),
        ("DJSegment", "70%"),
        ("StingerStart", "92%"),
        ("DJStart", "95%"),
        ("TrackLoopEnd", "90%"),
        ("PostDrop", "75%"),
        ("TrackBreakDown", "95%"),
        ("PostRaceLoopEnd", "90%"),
        ("End", "100%"),
    ];

    private static readonly Dictionary<string, string> Current =
        BaseDefaults.ToDictionary(d => d.Name, d => d.Spec);

    public static IReadOnlyList<(string Name, string Spec)> Order => BaseDefaults;

    public static string Default(string name) =>
        BaseDefaults.FirstOrDefault(d => d.Name == name).Spec ?? Off;

    public static string Get(string name) => Current.TryGetValue(name, out var v) ? v : Off;

    public static void Apply(IDictionary<string, string>? specs)
    {
        if (specs is null)
        {
            return;
        }

        foreach (var (name, _) in BaseDefaults)
        {
            if (specs.TryGetValue(name, out var v))
            {
                Current[name] = v ?? Off;
            }
        }
    }

    public static Dictionary<string, string> Snapshot() => new(Current);

    public static bool TryParse(string? spec, out bool percent, out double value)
    {
        percent = false;
        value = 0;

        var t = spec?.Trim();

        if (string.IsNullOrEmpty(t))
        {
            return false;
        }

        if (t.EndsWith('%'))
        {
            percent = true;
            t = t[..^1].Trim();
        }

        return double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    public static long Resolve(string? spec, long length, int sampleRate)
    {
        if (length <= 0 || !TryParse(spec, out var percent, out var value))
        {
            return -1;
        }

        var frame = percent
            ? (long) Math.Round(value / 100.0 * length)
            : (long) Math.Round(value * sampleRate);

        return Math.Clamp(frame, 0, length - 1);
    }

    public static Dictionary<string, long> Compute(long length, int sampleRate)
    {
        var result = new Dictionary<string, long>();

        if (length <= 0)
        {
            return result;
        }

        foreach (var (name, _) in BaseDefaults)
        {
            var frame = Resolve(Current[name], length, sampleRate);

            if (frame >= 0)
            {
                result[name] = frame;
            }
        }

        return result;
    }
}
