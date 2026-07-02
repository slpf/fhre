namespace FH6RB.Core;

public static class Naming
{
    public const string CustomPrefix = "HZ_CUSTOM_TRACK_";

    public static string MakeSoundName(int seq) => $"{CustomPrefix}{seq}";

    public static List<(int Seq, ulong Hash, string SoundName)> ScanCustomTracks(IReadOnlySet<ulong> stblIds, int ceiling = 100000)
    {
        var found = new List<(int, ulong, string)>();

        for (var seq = 0; seq <= ceiling; seq++)
        {
            var name = MakeSoundName(seq);
            var h = Lookup.SoundNameToId(name);

            if (stblIds.Contains(h))
            {
                found.Add((seq, h, name));
            }
        }

        return found;
    }
    
    public static Dictionary<ulong, string> ResolveCustomNames(IReadOnlySet<ulong> targetIds, int ceiling = 100000)
    {
        var result = new Dictionary<ulong, string>();
        
        if (targetIds.Count == 0)
        {
            return result;
        }

        for (var seq = 0; seq <= ceiling && result.Count < targetIds.Count; seq++)
        {
            var name = MakeSoundName(seq);
            var h = Lookup.SoundNameToId(name);
            
            if (targetIds.Contains(h))
            {
                result.TryAdd(h, name);
            }
        }

        return result;
    }
}
