using System.Text.Json.Serialization;

namespace FH6RB.Services;

public sealed record NormalizationOverride(
    [property: JsonPropertyName("loudnessNormalize")] bool LoudnessNormalize = true,
    [property: JsonPropertyName("targetLufs")] double TargetLufs = -23,
    [property: JsonPropertyName("targetTruePeak")] double TargetTruePeak = -1,
    [property: JsonPropertyName("dynamicNormalization")] bool DynamicNormalization = false)
{
    public AppSettings ApplyTo(AppSettings global) => Resolve(global, this);

    public static AppSettings Resolve(AppSettings global, NormalizationOverride? normalization = null)
    {
        ArgumentNullException.ThrowIfNull(global);
        return new AppSettings
        {
            GamePath = global.GamePath,
            LastLanguage = global.LastLanguage,
            LastStationBank = global.LastStationBank,
            LoudnessNormalize = normalization?.LoudnessNormalize ?? global.LoudnessNormalize,
            TargetLufs = AppSettings.ClampTargetLufs(normalization?.TargetLufs ?? global.TargetLufs),
            TargetTruePeak = AppSettings.ClampTargetTruePeak(normalization?.TargetTruePeak ?? global.TargetTruePeak),
            DynamicNormalization = normalization?.DynamicNormalization ?? global.DynamicNormalization,
            VorbisQuality = global.VorbisQuality,
            EncodeParallelism = global.EncodeParallelism,
            LoopAutoTune = global.LoopAutoTune,
            LoopMinSeconds = global.LoopMinSeconds,
            LoopNoteDeviation = global.LoopNoteDeviation,
            LoopMinMatch = global.LoopMinMatch,
            LoopPreEmphasis = global.LoopPreEmphasis,
            LoopMultiResolution = global.LoopMultiResolution,
            LoopDisablePruning = global.LoopDisablePruning,
            LoopBorderSimilarity = global.LoopBorderSimilarity,
            LoopTransitionSmoothness = global.LoopTransitionSmoothness,
            LoopLoudnessDifference = global.LoopLoudnessDifference,
            LoopUseHarmonicChroma = global.LoopUseHarmonicChroma,
            LoopRequireOnsetAlignment = global.LoopRequireOnsetAlignment,
            LoopStages = global.LoopStages,
            SettingsVersion = global.SettingsVersion,
            MarkerDefaults = global.MarkerDefaults is { } markers ? new(markers, markers.Comparer) : new(),
            WaveformLabelRows = global.WaveformLabelRows is { } rows ? new(rows, rows.Comparer) : new(),
            WindowSizes = global.WindowSizes is { } sizes ? new(sizes, sizes.Comparer) : new(),
        };
    }
}
