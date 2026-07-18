using System.Text.Json;

namespace FH6RB.Services;

public sealed class MarkerPreset
{
    public string Name { get; set; } = "";
    public Dictionary<string, long> Markers { get; set; } = new();
    public int SampleRate { get; set; }
    public DateTime Modified { get; set; }
}

public static class MarkerPresetService
{
    private const int DefaultSampleRate = 48000;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string Root => Path.Combine(AppContext.BaseDirectory, "markers");

    private static string FilePathFor(string name) => Path.Combine(Root, Sanitize(name) + ".json");

    private static string Resolve(string rel)
    {
        rel ??= "";
        rel = rel.Replace('/', Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(string.IsNullOrEmpty(rel) ? root : Path.Combine(root, rel));
        var rootWithSep = root + Path.DirectorySeparatorChar;
        if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("path outside markers root");
        }
        return full;
    }

    private static (Dictionary<string, long> markers, int rate) Parse(string json)
    {
        try
        {
            var f = JsonSerializer.Deserialize<MarkerPresetFile>(json, JsonOpts);
            if (f?.Markers is { Count: > 0 })
            {
                return (f.Markers, f.SampleRate > 0 ? f.SampleRate : DefaultSampleRate);
            }
        }
        catch
        {
        }

        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, long>>(json, JsonOpts);
            if (dict is not null)
            {
                return (dict, DefaultSampleRate);
            }
        }
        catch (Exception ex)
        {
            Log.Line($"marker preset parse failed: {ex.Message}");
        }

        return (new Dictionary<string, long>(), DefaultSampleRate);
    }

    private static MarkerPreset? ReadPreset(string path)
    {
        try
        {
            var (markers, rate) = Parse(File.ReadAllText(path));
            return new MarkerPreset
            {
                Name = Path.GetFileNameWithoutExtension(path),
                Markers = markers,
                SampleRate = rate,
                Modified = File.GetLastWriteTime(path),
            };
        }
        catch
        {
            return null;
        }
    }

    private static string Serialize(int sampleRate, IReadOnlyDictionary<string, long> markers)
    {
        var file = new MarkerPresetFile
        {
            SampleRate = sampleRate > 0 ? sampleRate : DefaultSampleRate,
            Markers = new Dictionary<string, long>(markers),
        };
        return JsonSerializer.Serialize(file, JsonOpts);
    }

    public static List<MarkerPreset> List()
    {
        EnsureRoot();
        var result = new List<MarkerPreset>();
        foreach (var file in Directory.EnumerateFiles(Root, "*.json"))
        {
            var p = ReadPreset(file);
            if (p is not null) result.Add(p);
        }
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    public static IReadOnlyList<string> ListSubdirs(string relDir)
    {
        var dir = Resolve(relDir);
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        var names = new List<string>();
        try
        {
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                names.Add(Path.GetFileName(d));
            }
        }
        catch
        {
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    public static IReadOnlyList<MarkerPreset> ListIn(string relDir)
    {
        var dir = Resolve(relDir);
        var result = new List<MarkerPreset>();
        if (!Directory.Exists(dir)) return result;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            var p = ReadPreset(file);
            if (p is not null) result.Add(p);
        }
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    public static MarkerPreset? Load(string name)
    {
        var path = FilePathFor(name);
        if (!File.Exists(path)) return null;
        var p = ReadPreset(path);
        if (p is null) return null;
        p.Name = name;
        return p;
    }

    public static MarkerPreset? LoadByPath(string relPath)
    {
        string path;
        try { path = Resolve(relPath); }
        catch { return null; }
        if (!File.Exists(path)) return null;
        return ReadPreset(path);
    }

    public static bool Save(string name, int sampleRate, IReadOnlyDictionary<string, long> markers)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        EnsureRoot();
        try
        {
            File.WriteAllText(FilePathFor(name), Serialize(sampleRate, markers));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string SaveIn(string relDir, string name, int sampleRate, IReadOnlyDictionary<string, long> markers)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var dir = Resolve(relDir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Sanitize(name) + ".json");
        File.WriteAllText(path, Serialize(sampleRate, markers));
        return Path.GetRelativePath(Path.GetFullPath(Root), Path.GetFullPath(path));
    }

    public static bool Delete(string name)
    {
        var path = FilePathFor(name);
        if (!File.Exists(path)) return false;
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool DeleteByPath(string relPath)
    {
        string path;
        try { path = Resolve(relPath); }
        catch { return false; }
        if (!File.Exists(path)) return false;
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool DeleteFolder(string relDir)
    {
        string dir;
        try { dir = Resolve(relDir); }
        catch { return false; }
        if (!Directory.Exists(dir)) return false;
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool Exists(string name) => File.Exists(FilePathFor(name));

    private static void EnsureRoot()
    {
        Directory.CreateDirectory(Root);
    }

    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !invalid.Contains(c) && c != '.').ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "preset" : clean;
    }

    private sealed class MarkerPresetFile
    {
        public int SampleRate { get; set; } = DefaultSampleRate;
        public Dictionary<string, long> Markers { get; set; } = new();
    }
}
