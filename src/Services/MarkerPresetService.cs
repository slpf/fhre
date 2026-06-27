using System.Text.Json;

namespace FH6RB.Services;

public sealed class MarkerPreset
{
    public string Name { get; set; } = "";
    public Dictionary<string, long> Markers { get; set; } = new();
    public DateTime Modified { get; set; }
}

public static class MarkerPresetService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string Root => Path.Combine(AppContext.BaseDirectory, "markers");

    private static string FilePathFor(string name) => Path.Combine(Root, Sanitize(name) + ".json");

    public static List<MarkerPreset> List()
    {
        EnsureRoot();
        var result = new List<MarkerPreset>();
        foreach (var file in Directory.EnumerateFiles(Root, "*.json"))
        {
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(file), JsonOpts);
                if (dict is null) continue;
                result.Add(new MarkerPreset
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Markers = dict,
                    Modified = File.GetLastWriteTime(file),
                });
            }
            catch
            {
            }
        }
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    public static MarkerPreset? Load(string name)
    {
        var path = FilePathFor(name);
        if (!File.Exists(path)) return null;
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path), JsonOpts);
            if (dict is null) return null;
            return new MarkerPreset { Name = name, Markers = dict };
        }
        catch
        {
            return null;
        }
    }

    public static bool Save(string name, int sampleRate, IReadOnlyDictionary<string, long> markers)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        EnsureRoot();
        try
        {
            File.WriteAllText(FilePathFor(name),
                JsonSerializer.Serialize(new Dictionary<string, long>(markers), JsonOpts));
            return true;
        }
        catch
        {
            return false;
        }
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

    public static bool Exists(string name) => File.Exists(FilePathFor(name));

    private static void EnsureRoot()
    {
        Directory.CreateDirectory(Root);
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !invalid.Contains(c) && c != '.').ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "preset" : clean;
    }
}
