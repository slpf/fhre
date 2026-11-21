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
    [JsonPropertyName("normalization")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NormalizationOverride? Normalization { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("role")] public string Role { get; set; } = "custom";
    [JsonPropertyName("markers")] public Dictionary<string, long>? Markers { get; set; }
}

public sealed record ExportItem(
    string? FileSource, int SubIndex,
    string? DisplayName, string? Artist,
    long SampleLength, int SampleRate, double? GainDb, bool Enabled,
    string Role, Dictionary<string, long>? Markers, NormalizationOverride? Normalization = null,
    ulong? BankId = null);

public sealed record ImportedPack(PackManifest Manifest, string Dir);

public static class TrackPack
{
    private const string ManifestEntry = "manifest.json";
    private const string TrackListEntry = "tracklist.txt";
    private const string AudioPrefix = "audio/";
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static Task ExportAsync(string outPath, string? bankName, string? bankPath,
        IReadOnlyList<ExportItem> tracks, Action<string>? progress, Action<string>? log, CancellationToken ct) =>
        ExportAsync(outPath, bankName, bankPath, tracks, progress, log, ct, expectedBankStamp: null);

    internal static Task ExportAsync(string outPath, string? bankName, string? bankPath,
        IReadOnlyList<ExportItem> tracks, Action<string>? progress, Action<string>? log, CancellationToken ct,
        BankFileStamp? expectedBankStamp) =>
        Task.Run(() => Export(outPath, bankName, bankPath, tracks, progress, log, ct, expectedBankStamp), ct);

    private static void Export(string outPath, string? bankName, string? bankPath,
        IReadOnlyList<ExportItem> tracks, Action<string>? progress, Action<string>? log, CancellationToken ct,
        BankFileStamp? expectedBankStamp)
    {
        if (tracks.Count == 0)
        {
            throw new InvalidOperationException("nothing to export");
        }

        var manifest = new PackManifest { ExportedFromBank = bankName, Tracks = [] };
        var audioSources = new List<(string Entry, string? Path, int SubIndex)>();
        using var bank = tracks.Any(track => track.FileSource is null && track.SubIndex >= 0) && bankPath is not null
            ? new SampleReader(bankPath, expectedBankStamp) : null;

        for (var i = 0; i < tracks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var t = tracks[i];
            var id = "t" + i;

            string entry;
            string? srcPath = null;

            if (t.FileSource is not null)
            {
                srcPath = t.FileSource;
                var file = new FileInfo(srcPath);
                if (!file.Exists) throw new FileNotFoundException($"Audio source for '{t.DisplayName ?? id}' is unavailable: {srcPath}", srcPath);
                if (file.Length == 0) throw new InvalidDataException($"Audio source for '{t.DisplayName ?? id}' is empty: {srcPath}");
                entry = AudioPrefix + id + Path.GetExtension(srcPath);
            }
            else if (bank is not null && t.SubIndex >= 0)
            {
                bank.ValidateSample(t.SubIndex, t.BankId, t.DisplayName ?? id);
                entry = AudioPrefix + id + ".fsb";
            }
            else
            {
                throw new InvalidDataException($"No audio source is available for '{t.DisplayName ?? id}'.");
            }

            audioSources.Add((entry, srcPath, t.SubIndex));

            manifest.Tracks.Add(new PackTrack
            {
                Id = id,
                File = entry,
                DisplayName = t.DisplayName,
                Artist = t.Artist,
                SampleLength = t.SampleLength,
                SampleRate = t.SampleRate,
                GainDb = t.GainDb,
                Normalization = srcPath is not null && !srcPath.EndsWith(".fsb", StringComparison.OrdinalIgnoreCase)
                    ? t.Normalization : null,
                Enabled = t.Enabled,
                Role = t.Role,
                Markers = t.Markers is { } m && m.Count > 0 ? new Dictionary<string, long>(m) : null,
            });
        }

        progress?.Invoke("Writing archive…");

        Atomic.Write(outPath, fs =>
        {
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true);

            foreach (var (entry, srcPath, subIndex) in audioSources)
            {
                ct.ThrowIfCancellationRequested();
                var ze = zip.CreateEntry(entry, CompressionLevel.Optimal);
                using var zes = ze.Open();

                if (srcPath is null)
                {
                    bank!.CopySample(subIndex, zes, ct);
                }
                else
                {
                    using var src = File.OpenRead(srcPath);
                    if (src.Length == 0) throw new InvalidDataException($"Audio source became empty: {srcPath}");
                    src.CopyToAsync(zes, ct).GetAwaiter().GetResult();
                }
            }

            ct.ThrowIfCancellationRequested();

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
        }, uniqueTemporary: true);

        log?.Invoke($"exported {manifest.Tracks.Count} track(s) -> {outPath}");
    }

    public static ImportedPack Load(string path)
    {
        var dir = Path.Combine(AppPaths.SessionDirectory("import"), Guid.NewGuid().ToString("N"));
        try { return Load(path, dir); }
        catch
        {
            CleanupImportDirectory(dir);
            throw;
        }
    }

    private static ImportedPack Load(string path, string dir)
    {
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
                if (track.Normalization is { } normalization &&
                    (!double.IsFinite(normalization.TargetLufs) || normalization.TargetLufs is < -30 or > -9 ||
                     !double.IsFinite(normalization.TargetTruePeak) || normalization.TargetTruePeak is < -9 or > 0))
                {
                    throw new InvalidDataException($"track {track.Id} has invalid normalization settings");
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

            var availableBytes = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace;
            var uniqueAudio = planned.DistinctBy(item => item.EntryName);
            foreach (var item in uniqueAudio)
            {
                if (item.Entry.Length > availableBytes)
                    throw new IOException("Not enough free space to extract the track package.");
                availableBytes -= item.Entry.Length;
            }

            Directory.CreateDirectory(dir);
            var extracted = new HashSet<string>(StringComparer.Ordinal);
            var buffer = new byte[128 * 1024];
            foreach (var item in planned)
            {
                var dest = Path.Combine(dir, item.File);
                if (extracted.Add(item.EntryName))
                {
                    using var es = item.Entry.Open();
                    using var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    var remaining = item.Entry.Length;
                    while (remaining > 0)
                    {
                        var block = buffer.AsSpan(0, (int)Math.Min(remaining, buffer.Length));
                        es.ReadExactly(block);
                        output.Write(block);
                        remaining -= block.Length;
                    }
                    if (es.ReadByte() != -1) throw new InvalidDataException($"Audio entry exceeds its declared size: {item.EntryName}");
                }
                item.Track.File = item.File;
                Log.Line($"pack import \"{path}\": track={item.Track.Id}, title=\"{item.Track.DisplayName}\", " +
                    $"entry=\"{item.EntryName}\" -> \"{dest}\", bytes={item.Entry.Length}, " +
                    $"frames={item.Track.SampleLength}, rate={item.Track.SampleRate}, " +
                    $"gain={item.Track.GainDb?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}, " +
                    $"normalization={JsonSerializer.Serialize(item.Track.Normalization)}, " +
                    $"markers={item.Track.Markers?.Count ?? 0}");
            }
        }

        Log.Line($"pack import complete: \"{path}\", tracks={manifest.Tracks.Count}, directory=\"{dir}\"");
        return new ImportedPack(manifest, dir);
    }

    public static void CleanupImport() => CleanupImportDirectory(AppPaths.SessionDirectory("import"));

    internal static void DiscardImport(ImportedPack pack)
    {
        var root = Path.GetFullPath(AppPaths.SessionDirectory("import"));
        var directory = Path.GetFullPath(pack.Dir);
        if (string.Equals(Path.GetDirectoryName(directory), root, StringComparison.OrdinalIgnoreCase))
            CleanupImportDirectory(directory);
    }

    private static void CleanupImportDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Line($"pack import: could not remove {directory}: {ex}");
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
        using var reader = new SampleReader(bankPath);
        return reader.ReadSample(sub0);
    }

    internal static long WriteSampleFsb(string bankPath, int sub0, string outputPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var reader = new SampleReader(bankPath);
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        reader.CopySample(sub0, output, ct);
        ct.ThrowIfCancellationRequested();
        return output.Length;
    }

    internal sealed class SampleReader : IDisposable
    {
        private readonly FileStream _input;
        private readonly byte[] _header;
        private readonly List<Fsb5.SampleLayout> _samples;
        private readonly Dictionary<int, ulong> _trackIds;
        private readonly long _dataStart;
        private readonly byte[] _buffer = new byte[128 * 1024];

        public SampleReader(string bankPath, BankFileStamp? expectedStamp = null)
        {
            _input = File.OpenRead(bankPath);
            try
            {
                if (expectedStamp is { } stamp && stamp != BankFileStamp.Read(_input))
                    throw new IOException("The bank has changed since it was opened. Reload the station before exporting.");
                var skeleton = FevBank.ReadSkeleton(_input);
                (_header, _samples) = Fsb5.ReadLayout(skeleton.Fsb5HeaderRegion);
                _trackIds = new Dictionary<int, ulong>();
                foreach (var (id, index) in skeleton.Stbl)
                {
                    if (index >= 0) _trackIds.TryAdd(index, id);
                }
                _dataStart = skeleton.DataStartAbs;
            }
            catch
            {
                _input.Dispose();
                throw;
            }
        }

        public void ValidateSample(int index, ulong? bankId, string title)
        {
            if (bankId is { } id && (!_trackIds.TryGetValue(index, out var actual) || actual != id))
                throw new IOException($"The bank sample for '{title}' has changed. Reload the station and try again.");
            var sample = GetSample(index);
            if (sample.DataLen == 0) throw new InvalidDataException($"The bank sample for '{title}' is empty.");
        }

        public void CopySample(int index, Stream output, CancellationToken ct = default)
        {
            var sample = GetSample(index);
            var padding = (int)((32 - sample.DataLen % 32) % 32);
            var header = new byte[_header.Length + sample.Header.Length];
            Fsb5.WriteSingleSampleHeader(_header, sample.Header, (uint)(sample.DataLen + padding), header);
            output.Write(header);
            _input.Position = _dataStart + sample.DataOff;

            for (var remaining = sample.DataLen; remaining > 0;)
            {
                ct.ThrowIfCancellationRequested();
                var count = (int)Math.Min(remaining, _buffer.Length);
                _input.ReadExactly(_buffer.AsSpan(0, count));
                output.Write(_buffer.AsSpan(0, count));
                remaining -= count;
            }

            _buffer.AsSpan(0, padding).Clear();
            output.Write(_buffer.AsSpan(0, padding));
        }

        public byte[] ReadSample(int index)
        {
            var sample = GetSample(index);
            var padding = (32 - sample.DataLen % 32) % 32;
            var bytes = new byte[checked((int)(_header.Length + sample.Header.Length + sample.DataLen + padding))];
            using var output = new MemoryStream(bytes, writable: true);
            CopySample(index, output);
            return bytes;
        }

        private Fsb5.SampleLayout GetSample(int index)
        {
            if (index < 0 || index >= _samples.Count)
                throw new InvalidOperationException($"sample index {index} out of range in {_input.Name}");

            var sample = _samples[index];
            if (sample.DataOff < 0 || sample.DataLen < 0 || sample.DataLen > int.MaxValue
                || _dataStart + sample.DataOff + sample.DataLen > _input.Length)
                throw new InvalidDataException($"sample {index} data is outside {_input.Name}");
            return sample;
        }

        public void Dispose() => _input.Dispose();
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
