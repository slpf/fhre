using System.Text.Json;

namespace FH6RB.Services;

public sealed class AppSettings
{
    public string GamePath { get; set; } = "";
    public string? LastLanguage { get; set; }
    public string? LastStationBank { get; set; }
    
    public double TargetLufs { get; set; } = -23.0;
    public double TargetTruePeak { get; set; } = -1.0;
    public int VorbisQuality { get; set; } = 70;


    public int EncodeParallelism { get; set; } = 0;
    public static int RecommendedParallelism => Math.Max(1, Environment.ProcessorCount * 3 / 4);
}

public static class SettingsService
{
    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FHRE");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
    }
}
