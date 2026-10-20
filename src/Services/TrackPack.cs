using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FH6RB.Core;

namespace FH6RB.Services;

public sealed class PackManifest
{
    [JsonPropertyName("format")] public string Format { get; set; } = "FH6RB-EXPORT";
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("exportedFromBank")] public string? ExportedFromBank { get; set; }
    [JsonPropertyName("tracks")] public List<PackTrack> Tracks { get; set; } = [];
}

public sealed class PackTrack
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("artist")] public string? Artist { get; set; }
    [JsonPropertyName("sampleLength")] public long SampleLength { get; set; }
    [JsonPropertyName("sampleRate")] public int SampleRate { get; set; }
    [JsonPropertyName("gainDb")] public double? GainDb { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("role")] public string Role { get; set; } = "custom";
    [JsonPropertyName("markers")] public Dictionary<string, long>? Markers { get; set; }
}

public sealed record ExportItem(
    string? FileSource, int SubIndex,
    string? DisplayName, string? Artist,
    long SampleLength, int SampleRate, double? GainDb, bool Enabled,
    string Role, Dictionary<string, long>? Markers);

public sealed record ImportedPack(PackManifest Manifest, string Dir);

public static class TrackPack
{
    private const string ManifestEntry = "manifest.json";
    private const string TrackListEntry = "tracklist.txt";
    private const string AudioPrefix = "audio/";
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static async Task ExportAsync(string outPath, string? bankName, string? bankPath,
        IReadOnlyList<ExportItem> tracks, Action<string>? progress, Action<string>? log, CancellationToken ct)
    {
        if (tracks.Count == 0)
        {
            throw new InvalidOperationException("nothing to export (no custom or replacement tracks)");
        }

        var manifest = new PackManifest { ExportedFromBank = bankName, Tracks = [] };
        var audioSources = new List<(string Entry, string? Path, byte[]? Bytes)>();

        for (var i = 0; i < tracks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var t = tracks[i];
            var id = "t" + i;

            string entry;
            string? srcPath = null;
            byte[]? srcBytes = null;

            if (t.FileSource is not null && File.Exists(t.FileSource))
            {
                srcPath = t.FileSource;
                entry = AudioPrefix + id + Path.GetExtension(srcPath);
            }
            else if (bankPath is not null && t.SubIndex >= 0)
            {
                progress?.Invoke($"Extracting {t.DisplayName ?? "track"}…");
                srcBytes = await Task.Run(() => ExtractSampleFsb(bankPath, t.SubIndex), ct);
                entry = AudioPrefix + id + ".fsb";
            }
            else
            {
                log?.Invoke($"  skip {t.DisplayName ?? id}: no audio source");
                continue;
            }

            audioSources.Add((entry, srcPath, srcBytes));

            manifest.Tracks.Add(new PackTrack
            {
                Id = id,
                File = entry,
                DisplayName = t.DisplayName,
                Artist = t.Artist,
                SampleLength = t.SampleLength,
                SampleRate = t.SampleRate,
                GainDb = t.GainDb,
                Enabled = t.Enabled,
                Role = t.Role,
                Markers = t.Markers is { } m && m.Count > 0 ? new Dictionary<string, long>(m) : null,
            });
        }

        if (manifest.Tracks.Count == 0)
        {
            throw new InvalidOperationException("no track audio could be resolved for export");
        }

        progress?.Invoke("Writing archive…");

        Atomic.Write(outPath, fs =>
        {
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true);

            foreach (var (entry, srcPath, srcBytes) in audioSources)
            {
                var ze = zip.CreateEntry(entry, CompressionLevel.Optimal);
                using var zes = ze.Open();

                if (srcBytes is not null)
                {
                    zes.Write(srcBytes, 0, srcBytes.Length);
                }
                else
                {
                    using var src = File.OpenRead(srcPath!);
                    src.CopyTo(zes);
                }
            }

            var me = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
            using (var mes = me.Open())
            {
                JsonSerializer.Serialize(mes, manifest, JsonOpts);
            }

            var tl = new StringBuilder();
            foreach (var t in manifest.Tracks)
            {
                var name = string.IsNullOrWhiteSpace(t.DisplayName) ? t.Id : t.DisplayName!;
                tl.AppendLine(string.IsNullOrWhiteSpace(t.Artist) ? name : $"{name} - {t.Artist}");
            }

            var tle = zip.CreateEntry(TrackListEntry, CompressionLevel.Optimal);
            using (var tles = tle.Open())
            {
                var tlBytes = new UTF8Encoding(false).GetBytes(tl.ToString());
                tles.Write(tlBytes, 0, tlBytes.Length);
            }
        });

        log?.Invoke($"exported {manifest.Tracks.Count} track(s) -> {outPath}");
    }

    public static ImportedPack Load(string path)
    {
        var dir = Path.Combine(AppPaths.TempBase, "import", Guid.NewGuid().ToString("N"));
        PackManifest manifest;

        using (var fs = File.OpenRead(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
        {
            var manifestEntries = zip.Entries.Where(entry => entry.FullName == ManifestEntry).ToArray();
            if (manifestEntries.Length != 1)
            {
                throw new InvalidDataException(manifestEntries.Length == 0
                    ? "not a FH6RB export (no manifest)" : "export contains duplicate manifests");
            }
            var manifestEntry = manifestEntries[0];

            using (var mes = manifestEntry.Open())
            {
                manifest = JsonSerializer.Deserialize<PackManifest>(mes)
                    ?? throw new InvalidDataException("empty manifest");
            }

            if (!string.Equals(manifest.Format, "FH6RB-EXPORT", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"unknown export format: {manifest.Format}");
            }

            if (manifest.Version > CurrentVersion)
            {
                throw new InvalidDataException(
                    $"export version {manifest.Version} is newer than supported {CurrentVersion}");
            }

            if (manifest.Tracks is null)
            {
                throw new InvalidDataException("export has no track list");
            }

            var audioEntries = new Dictionary<string, (ZipArchiveEntry Entry, string File)>(StringComparer.Ordinal);
            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                var entryName = entry.FullName.Replace('\\', '/');
                if (!entryName.StartsWith(AudioPrefix, StringComparison.Ordinal)
                    || entryName.EndsWith('/')) continue;
                entryName = AudioEntryName(entryName);
                var fileName = SafeFileName(entryName[AudioPrefix.Length..]);
                if (!audioEntries.TryAdd(entryName, (entry, fileName)))
                {
                    throw new InvalidDataException($"duplicate audio entry: {entry.FullName}");
                }
                if (!destinations.Add(fileName))
                {
                    throw new InvalidDataException($"audio entries resolve to the same file: {fileName}");
                }
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var planned = new List<(PackTrack Track, string EntryName, ZipArchiveEntry Entry, string File)>();
            foreach (var track in manifest.Tracks)
            {
                if (track is null || string.IsNullOrWhiteSpace(track.Id))
                {
                    throw new InvalidDataException("export contains a track without an id");
                }
                if (!ids.Add(track.Id))
                {
                    throw new InvalidDataException($"duplicate track id: {track.Id}");
                }
                if (track.GainDb is { } gain && !double.IsFinite(gain))
                {
                    throw new InvalidDataException($"track {track.Id} has an invalid gain");
                }
                var entryName = AudioEntryName(track.File);
                if (!audioEntries.TryGetValue(entryName, out var audio))
                {
                    throw new InvalidDataException($"audio file not found for track {track.Id}: {track.File}");
                }
                if (audio.Entry.Length <= 0)
                {
                    throw new InvalidDataException($"audio file is empty for track {track.Id}: {track.File}");
                }
                planned.Add((track, entryName, audio.Entry, audio.File));
            }

            Directory.CreateDirectory(dir);
            var extracted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in planned)
            {
                var dest = Path.Combine(dir, item.File);
                if (extracted.Add(item.EntryName))
                {
                    using var es = item.Entry.Open();
                    using var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    es.CopyTo(output);
                }
                item.Track.File = item.File;
                Log.Line($"pack import \"{path}\": track={item.Track.Id}, title=\"{item.Track.DisplayName}\", " +
                    $"entry=\"{item.EntryName}\" -> \"{dest}\", bytes={item.Entry.Length}, " +
                    $"frames={item.Track.SampleLength}, rate={item.Track.SampleRate}, " +
                    $"gain={item.Track.GainDb?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}, " +
                    $"markers={item.Track.Markers?.Count ?? 0}");
            }
        }

        Log.Line($"pack import complete: \"{path}\", tracks={manifest.Tracks.Count}, directory=\"{dir}\"");
        return new ImportedPack(manifest, dir);
    }

    public static void CleanupImport()
    {
        var importRoot = Path.Combine(AppPaths.TempBase, "import");
        try
        {
            if (Directory.Exists(importRoot))
            {
                Directory.Delete(importRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static string SafeFileName(string name)
    {
        name = name.Replace('/', '_').Replace('\\', '_');
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        name = name.TrimEnd(' ', '.');
        if (name.Length == 0) throw new InvalidDataException("audio entry has an empty file name");
        return name;
    }

    private static string AudioEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("track has no audio file");
        name = name.Replace('\\', '/');
        if (!name.Contains('/')) name = AudioPrefix + name;
        if (!name.StartsWith(AudioPrefix, StringComparison.Ordinal)
            || name[AudioPrefix.Length..].Split('/').Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".."))
        {
            throw new InvalidDataException($"invalid audio entry path: {name}");
        }
        return name;
    }

    public static byte[] ExtractSampleFsb(string bankPath, int sub0)
    {
        var sk = FevBank.ReadSkeleton(bankPath);
        var (header60, layout) = Fsb5.ReadLayout(sk.Fsb5HeaderRegion);

        if (sub0 < 0 || sub0 >= layout.Count)
        {
            throw new InvalidOperationException($"sample index {sub0} out of range in {Path.GetFileName(bankPath)}");
        }

        var sl = layout[sub0];
        using var fs = File.OpenRead(bankPath);
        if (sl.DataOff < 0 || sl.DataLen < 0 || sl.DataLen > int.MaxValue
            || sk.DataStartAbs + sl.DataOff + sl.DataLen > fs.Length)
        {
            throw new InvalidDataException($"sample {sub0} data is outside {Path.GetFileName(bankPath)}");
        }
        var data = new byte[checked((int) sl.DataLen)];
        fs.Position = sk.DataStartAbs + sl.DataOff;
        fs.ReadExactly(data);

        return Fsb5.BuildSingleSample(header60, new Fsb5Sample { Header = sl.Header, Data = data });
    }

    public static int IndexOfSoundName(string bankPath, string soundName)
    {
        var sk = FevBank.ReadSkeleton(bankPath);
        var hash = Lookup.SoundNameToId(soundName);
        foreach (var (id, index) in sk.Stbl)
        {
            if (id == hash && index >= 0)
            {
                return index;
            }
        }
        return -1;
    }

    public static byte[] ExtractSampleFsbBySoundName(string bankPath, string soundName)
    {
        var idx = IndexOfSoundName(bankPath, soundName);
        if (idx < 0)
        {
            throw new InvalidOperationException($"sound '{soundName}' not found in {Path.GetFileName(bankPath)}");
        }
        return ExtractSampleFsb(bankPath, idx);
    }
}
