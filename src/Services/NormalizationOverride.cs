using System.Text.Json;
using System.Text.Json.Serialization;

namespace FH6RB.Services;

[JsonConverter(typeof(NormalizationOverrideConverter))]
public sealed record NormalizationOverride(
    [property: JsonPropertyName("loudnessNormalize")] bool LoudnessNormalize = true,
    [property: JsonPropertyName("targetLufs")] double TargetLufs = -23,
    [property: JsonPropertyName("targetTruePeak")] double TargetTruePeak = -1,
    [property: JsonPropertyName("peakLimiter")] bool PeakLimiter = false)
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
            PeakLimiter = normalization?.PeakLimiter ?? global.PeakLimiter,
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

public sealed class NormalizationOverrideConverter : JsonConverter<NormalizationOverride>
{
    public override NormalizationOverride Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Normalization settings must be an object.");
        }
        var normalize = true;
        var targetLufs = -23.0;
        var targetTruePeak = -1.0;
        bool? peakLimiter = null;
        bool? legacyDynamic = null;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            switch (property.Name)
            {
                case "loudnessNormalize":
                    normalize = ReadBoolean(property);
                    break;
                case "targetLufs":
                    targetLufs = ReadNumber(property);
                    break;
                case "targetTruePeak":
                    targetTruePeak = ReadNumber(property);
                    break;
                case "peakLimiter":
                    peakLimiter = ReadBoolean(property);
                    break;
                case "dynamicNormalization":
                    legacyDynamic = ReadBoolean(property);
                    break;
            }
        }
        return new NormalizationOverride(normalize, targetLufs, targetTruePeak, peakLimiter ?? legacyDynamic ?? false);
    }

    public override void Write(Utf8JsonWriter writer, NormalizationOverride value, JsonSerializerOptions options)
    {
        if (!double.IsFinite(value.TargetLufs) || !double.IsFinite(value.TargetTruePeak))
        {
            throw new JsonException("Normalization levels must be finite.");
        }
        writer.WriteStartObject();
        writer.WriteBoolean("loudnessNormalize", value.LoudnessNormalize);
        writer.WriteNumber("targetLufs", value.TargetLufs);
        writer.WriteNumber("targetTruePeak", value.TargetTruePeak);
        writer.WriteBoolean("peakLimiter", value.PeakLimiter);
        writer.WriteEndObject();
    }

    private static bool ReadBoolean(JsonProperty property)
    {
        if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new JsonException($"Normalization setting {property.Name} must be a boolean.");
        }
        return property.Value.GetBoolean();
    }

    private static double ReadNumber(JsonProperty property)
    {
        if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDouble(out var value)
            || !double.IsFinite(value))
        {
            throw new JsonException($"Normalization setting {property.Name} must be a finite number.");
        }
        return value;
    }
}
