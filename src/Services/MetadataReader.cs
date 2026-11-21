using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FH6RB.Services;

public static class MetadataReader
{
    public static (string? Title, string? Artist, double Duration) Read(string path)
    {
        if (!Tools.HasFfprobe)
            throw new FileNotFoundException("ffprobe is required to read audio metadata.", Tools.FfprobePath);

        var args = "-v error -show_entries " +
            "stream=codec_type,duration:stream_tags=title,artist:format=duration:format_tags=title,artist " +
            $"-of json \"{Path.GetFullPath(path)}\"";
        var (output, error, exitCode) = Proc.Run(Tools.FfprobePath, args, timeoutMs: 120_000,
            stdoutEncoding: Encoding.UTF8, stderrEncoding: Encoding.UTF8);
        if (exitCode != 0)
            throw new InvalidDataException($"ffprobe could not read {Path.GetFileName(path)}: {error.Trim()}");

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        var streams = root.GetProperty("streams");
        var audio = streams.EnumerateArray().FirstOrDefault(stream => ReadString(stream, "codec_type") == "audio");
        if (audio.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException($"No audio stream found in {Path.GetFileName(path)}.");

        var format = root.TryGetProperty("format", out var container) ? container : default;
        var title = ReadTag(audio, "title") ?? ReadTag(format, "title");
        var artist = ReadTag(audio, "artist") ?? ReadTag(format, "artist");
        var duration = ReadDuration(audio) ?? (streams.GetArrayLength() == 1 ? ReadDuration(format) : null) ?? 0;
        return (title, artist, duration);
    }

    private static double? ReadDuration(JsonElement element) =>
        double.TryParse(ReadString(element, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
        && double.IsFinite(duration) && duration > 0 ? duration : null;

    private static string? ReadTag(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("tags", out var tags)) return null;
        foreach (var tag in tags.EnumerateObject())
        {
            if (tag.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && tag.Value.ValueKind == JsonValueKind.String
                && tag.Value.GetString()?.Trim() is { Length: > 0 } value)
                return value;
        }
        return null;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
