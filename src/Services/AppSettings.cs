using System.Text.Json;
using System.Text.Json.Serialization;
using FH6RB.Core;

namespace FH6RB.Services;

public sealed class AppSettings
{
    public string GamePath { get; set; } = "";
    public string? LastLanguage { get; set; }
    public string? LastStationBank { get; set; }
    
    public double TargetLufs { get; set; } = -23.0;
    public double TargetTruePeak { get; set; } = -1.0;
    public bool LoudnessNormalize { get; set; } = true;
    public bool MeasureTrackLoudness { get; set; } = true;
    private bool? _peakLimiter;
    private bool? _legacyDynamicNormalization;
    public bool PeakLimiter
    {
        get => _peakLimiter ?? _legacyDynamicNormalization ?? false;
        set => _peakLimiter = value;
    }
    [JsonPropertyName("DynamicNormalization")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyDynamicNormalization
    {
        get => null;
        set => _legacyDynamicNormalization = value;
    }
    public int VorbisQuality { get; set; } = 90;


    public int EncodeParallelism { get; set; } = 0;

    public bool LoopAutoTune { get; set; } = true;
    public double LoopMinSeconds { get; set; } = 30.0;
    public double LoopNoteDeviation { get; set; } = 0.0875;
    public double LoopMinMatch { get; set; } = 0.80;
    public bool LoopPreEmphasis { get; set; } = false;
    public bool LoopMultiResolution { get; set; } = false;
    public bool LoopDisablePruning { get; set; } = false;
    public double LoopBorderSimilarity { get; set; } = 0.3;
    public double LoopTransitionSmoothness { get; set; } = 0.3;
    public double LoopLoudnessDifference { get; set; } = 0.4;
    public bool LoopUseHarmonicChroma { get; set; } = false;
    public bool LoopRequireOnsetAlignment { get; set; } = true;
    public LoopStage LoopStages { get; set; } = LoopSearchDefaults.AutoStages;

    public int SettingsVersion { get; set; }

    public Dictionary<string, string> MarkerDefaults { get; set; } = new();

    public Dictionary<string, int> WaveformLabelRows { get; set; } = new();

    public Dictionary<string, WinSize> WindowSizes { get; set; } = new();
    internal Exception? ReadFailure { get; set; }
    internal AppSettings Copy() => (AppSettings)MemberwiseClone();

    public static int RecommendedParallelism => Math.Max(1, Environment.ProcessorCount * 3 / 4);

    public static double ClampTargetLufs(double value) => double.IsFinite(value) ? Math.Clamp(value, -30, -9) : -23;

    public static double ClampTargetTruePeak(double value) => double.IsFinite(value) ? Math.Clamp(value, -9, 0) : -1;
}

public sealed class WinSize
{
    public double W { get; set; }
    public double H { get; set; }
}

public static class SettingsService
{
    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FHRE");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public const int CurrentSettingsVersion = 7;

    public static AppSettings Load() => Load(FilePath);

    internal static AppSettings Load(string path)
    {
        AppSettings s;

        try
        {
            s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))
                ?? throw new JsonException("The settings file contains no settings.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new AppSettings { SettingsVersion = CurrentSettingsVersion };
        }
        catch (JsonException ex)
        {
            Log.Line($"settings could not be parsed: {ex.Message}");
            s = new AppSettings { SettingsVersion = CurrentSettingsVersion };
            try
            {
                var backup = path + ".corrupt";
                File.Move(path, File.Exists(backup) ? backup + "." + Guid.NewGuid().ToString("N") : backup);
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
                Log.Line($"settings could not be preserved: {moveError.Message}");
                s.ReadFailure = moveError;
            }
            return s;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Line($"settings could not be read: {ex.Message}");
            return new AppSettings { SettingsVersion = CurrentSettingsVersion, ReadFailure = ex };
        }

        s.GamePath ??= "";
        s.MarkerDefaults ??= new();
        s.WaveformLabelRows ??= new();
        s.WindowSizes ??= new();
        s.TargetLufs = AppSettings.ClampTargetLufs(s.TargetLufs);
        s.TargetTruePeak = AppSettings.ClampTargetTruePeak(s.TargetTruePeak);

        if (s.SettingsVersion < 2)
        {
            s.MarkerDefaults = new();
        }
        if (s.SettingsVersion < 3)
        {
            s.LoopMinMatch = 0.80;
            s.LoopBorderSimilarity = 0.3;
            s.LoopTransitionSmoothness = 0.3;
        }
        if (s.SettingsVersion < 5)
        {
            s.LoopStages = LoopSearchDefaults.AutoStages;
        }
        if (s.SettingsVersion < 6)
        {
            if (s.VorbisQuality == 70) s.VorbisQuality = 90;
        }
        if (s.SettingsVersion < CurrentSettingsVersion)
        {
            s.SettingsVersion = CurrentSettingsVersion;
            try { Save(s, path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Log.Line($"settings migration could not be saved: {ex.Message}");
            }
        }

        return s;
    }

    public static void Save(AppSettings settings) => Save(settings, FilePath);

    internal static void Update(AppSettings settings, Action<AppSettings> update)
    {
        var draft = settings.Copy();
        update(draft);
        Save(draft);
        update(settings);
        settings.TargetLufs = draft.TargetLufs;
        settings.TargetTruePeak = draft.TargetTruePeak;
    }

    private static void Save(AppSettings settings, string path)
    {
        if (settings.ReadFailure is { } error)
            throw new IOException("Settings could not be loaded. Restart the application after restoring access to settings.json.", error);

        settings.TargetLufs = AppSettings.ClampTargetLufs(settings.TargetLufs);
        settings.TargetTruePeak = AppSettings.ClampTargetTruePeak(settings.TargetTruePeak);
        Atomic.Write(path, JsonSerializer.Serialize(settings, Options));
    }
}
