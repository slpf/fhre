using System.Globalization;

namespace FH6RB.Core;

public static class MarkerDefaults
{
    public const string Off = "";

    private static readonly (string Name, string Spec)[] BaseDefaults =
    [
        ("TrackStart", "0%"),
        ("End", "100%"),
        ("DJStart", "-5"),
        ("DJDrop", "0%"),
        ("DJSegment", "60%"),
        ("TrackDrop", "0%"),
        ("TrackLoopStart", "0%"),
        ("TrackLoopEnd", "100%"),
        ("PostDrop", "70%"),
        ("PostRaceLoopStart", "70%"),
        ("PostRaceLoopEnd", "90%"),
    ];
    
    public static readonly IReadOnlyDictionary<string, int> DefaultLabelRows = new Dictionary<string, int>
    {
        ["TrackStart"] = 0,
        ["End"] = 0,
        ["DJDrop"] = 4,
        ["DJStart"] = 4,
        ["DJSegment"] = 4,
        ["TrackDrop"] = 8,
        ["PostDrop"] = 8,
        ["PostRaceLoopStart"] = 24,
        ["PostRaceLoopEnd"] = 24,
        ["TrackLoopStart"] = 28,
        ["TrackLoopEnd"] = 28,
    };

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

    public static void Reset()
    {
        Current.Clear();

        foreach (var (name, spec) in BaseDefaults)
        {
            Current[name] = spec;
        }
    }

    public static Dictionary<string, string> Snapshot() => new(Current);

    public static bool TryParse(string? spec, out bool percent, out bool samples, out double value)
    {
        percent = false;
        samples = false;
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
        else if (t.EndsWith('s') || t.EndsWith('S'))
        {
            samples = true;
            t = t[..^1].Trim();
        }

        return double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public static long Resolve(string? spec, long length, int sampleRate)
    {
        if (length <= 0 || !TryParse(spec, out var percent, out var samples, out var value))
        {
            return -1;
        }

        var off = samples
            ? (long) Math.Round(value)
            : percent
                ? (long) Math.Round(value / 100.0 * length)
                : (long) Math.Round(value * sampleRate);

        var frame = value < 0 ? length + off : off;

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
