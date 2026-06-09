namespace FH6RB.Core;

public static class Naming
{
    public const string CustomPrefix = "HZ_CUSTOM_TRACK_";

    public static string MakeSoundName(int seq) => $"{CustomPrefix}{seq}";

    public static List<(int Seq, ulong Hash, string SoundName)> ScanCustomTracks(IReadOnlySet<ulong> stblIds, int maxGap = 128)
    {
        var found = new List<(int, ulong, string)>();
        int miss = 0;
        for (int seq = 0; miss < maxGap; seq++)
        {
            string name = MakeSoundName(seq);
            ulong h = Lookup.SoundNameToId(name);
            if (stblIds.Contains(h)) { found.Add((seq, h, name)); miss = 0; }
            else miss++;
        }
        return found;
    }
}
