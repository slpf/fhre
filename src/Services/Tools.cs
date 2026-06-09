namespace FH6RB.Services;

public static class Tools
{
    public static string Root => AppContext.BaseDirectory;

    private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    public static string FfmpegPath    => Path.Combine(Root, "ffmpeg",    Exe("ffmpeg"));
    public static string FfprobePath   => Path.Combine(Root, "ffmpeg",    Exe("ffprobe"));
    public static string FsbankclPath  => Path.Combine(Root, "fsbank",    Exe("fsbankcl"));
    public static string VgmstreamPath => Path.Combine(Root, "vgmstream", Exe("vgmstream-cli"));

    public static bool HasFfmpeg    => File.Exists(FfmpegPath);
    public static bool HasFsbankcl  => File.Exists(FsbankclPath);
    public static bool HasVgmstream => File.Exists(VgmstreamPath);
    
    public static IEnumerable<string> MissingForBuild()
    {
        if (!HasFfmpeg)   yield return "ffmpeg";
        if (!HasFsbankcl) yield return "fsbankcl";
    }
}
