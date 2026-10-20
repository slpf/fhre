using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FH6RB.Core;
using FH6RB.Services;
using FH6RB.ViewModels;

var temp = Path.Combine(AppContext.BaseDirectory, "scratch", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
var previousTemp = Environment.GetEnvironmentVariable("TEMP");
var previousTmp = Environment.GetEnvironmentVariable("TMP");
Environment.SetEnvironmentVariable("TEMP", temp);
Environment.SetEnvironmentVariable("TMP", temp);
try
{
    if (args.Contains("--benchmark-normalization", StringComparer.Ordinal))
    {
        await NormalizationBenchmark.RunAsync(temp);
        return 0;
    }
    TestFsbHeaders();
    TestAudioMeasurements(temp);
    TestTrackPacks(temp);
    TestImportedMarkers(temp);
    await NormalizationChecks.RunAsync(temp);
    var inputs = GenerateInputs(temp);
    await TestPreviewMarkersAsync(inputs[0]);
    await TestBuildAsync(temp, inputs, false);
    await TestBuildAsync(temp, inputs, true);
    await TestFadpcmAsync(temp, inputs[0]);
    await TestSilentVorbisAsync(temp);
    await TestInvalidInputsAsync(temp, inputs[0]);
    Console.WriteLine("Audio build regression checks passed.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
finally
{
    Environment.SetEnvironmentVariable("TEMP", previousTemp);
    Environment.SetEnvironmentVariable("TMP", previousTmp);
    if (args.Contains("--keep", StringComparer.Ordinal)) Console.WriteLine($"Fixtures: {temp}");
    else Directory.Delete(temp, recursive: true);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Reject(Action action, string message)
{
    try { action(); }
    catch (InvalidDataException) { return; }
    throw new InvalidOperationException(message);
}

static void TestFsbHeaders()
{
    var header = new byte[25];
    BinaryPrimitives.WriteUInt64LittleEndian(header, 1UL | 9UL << 1 | 1UL << 5 | 48000UL << 34);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 1U << 25 | 1U << 1 | 1U);
    header[12] = 4;
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(13), 2U << 25 | 4U << 1 | 1U);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(17), 64000);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(21), 14U << 25 | 4U << 1);
    Array.Resize(ref header, 29);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(25), 2);
    var bytes = FsbBytes([header], 64);
    var sample = Fsb5.Parse(bytes).Samples.Single();
    Check(sample.SampleRate == 64000 && sample.Channels == 8, "FSB metadata overrides were ignored.");
    Check(Fsb5.ReadSampleInfo(bytes).Single().SampleRate == 64000, "FSB sample info ignored custom frequency.");
    Check(Fsb5.ReadLayout(bytes[..89]).Samples.Single().Channels == 8, "Header-only FSB layout was rejected.");

    foreach (var (bits, channels) in new[] { (0, 1), (1, 2), (2, 6), (3, 8) })
    {
        var plain = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(plain, (ulong) bits << 5 | 4000UL << 34);
        var parsed = Fsb5.Parse(FsbBytes([plain], 32)).Samples.Single();
        Check(parsed.SampleRate == 4000 && parsed.Channels == channels, "FSB base channel or frequency table is incorrect.");
    }

    var unterminated = (byte[]) bytes.Clone();
    BinaryPrimitives.WriteUInt32LittleEndian(unterminated.AsSpan(81), 14U << 25 | 4U << 1 | 1U);
    Reject(() => Fsb5.ReadLayout(unterminated), "An unterminated FSB metadata chain was accepted.");
    var oversized = (byte[]) bytes.Clone();
    BinaryPrimitives.WriteUInt32LittleEndian(oversized.AsSpan(68), 1U << 25 | 100U << 1);
    Reject(() => Fsb5.ReadLayout(oversized), "FSB metadata crossed the sample header region.");
    Reject(() => Fsb5.Parse(bytes[..^1]), "Truncated FSB audio data was accepted.");
    Check(Fsb5.ReadSampleInfo(oversized).Length == 0, "Malformed FSB sample info was partially accepted.");

    var tooMany = (byte[]) bytes.Clone();
    BinaryPrimitives.WriteUInt32LittleEndian(tooMany.AsSpan(8), uint.MaxValue);
    Reject(() => Fsb5.ReadLayout(tooMany), "An overflowing FSB sample count was accepted.");
    var unsupported = (byte[]) bytes.Clone();
    BinaryPrimitives.WriteUInt32LittleEndian(unsupported.AsSpan(4), 0);
    Reject(() => Fsb5.ReadLayout(unsupported), "A 64-byte FSB header was parsed as a 60-byte header.");
    var rebased = Fsb5.RebaseHeader(header, 0x1000);
    Check(rebased.AsSpan(8).SequenceEqual(header.AsSpan(8)), "Rebasing changed codec metadata.");
    var backwards = FsbBytes([Fsb5.RebaseHeader(header, 64), Fsb5.RebaseHeader(header, 32)], 96);
    Reject(() => Fsb5.ReadLayout(backwards), "Decreasing FSB data offsets were accepted.");
    Reject(() => FevBank.BuildStbl([(1UL, -1)]), "A negative STBL index was accepted.");
    Reject(() => FevBank.BuildStbl([(1UL, 0x1000000)]), "An overflowing STBL index was accepted.");
    Reject(() => FevBank.BuildStbl(Enumerable.Range(0, ushort.MaxValue + 1).Select(i => ((ulong) i, i))), "An overflowing STBL entry count was accepted.");
    Console.WriteLine("Verified FSB metadata, bounds and unchanged codec metadata on rebasing.");
}

static byte[] FsbBytes(IReadOnlyList<byte[]> headers, int dataSize)
{
    var bytes = new byte[60 + headers.Sum(h => h.Length) + dataSize];
    "FSB5"u8.CopyTo(bytes);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint) headers.Count);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint) headers.Sum(h => h.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), (uint) dataSize);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 15);
    var pos = 60;
    foreach (var header in headers)
    {
        header.CopyTo(bytes, pos);
        pos += header.Length;
    }
    return bytes;
}

static void TestAudioMeasurements(string temp)
{
    var original = Path.Combine(temp, "measure.wav");
    var half = Path.Combine(temp, "half.wav");
    var inverted = Path.Combine(temp, "inverted.wav");
    var silence = Path.Combine(temp, "silence.wav");
    WriteWav(original, 48000, 2, 48000, 1);
    WriteWav(half, 48000, 2, 48000, 0.5);
    WriteWav(inverted, 48000, 2, 48000, -1);
    WriteWav(silence, 48000, 2, 48000, 0);
    var identity = AudioBuildValidation.Compare(original, original);
    Check(identity.ChannelCorrelation.All(c => Math.Abs(c - 1) < 1e-9) && identity.GainDb.All(g => Math.Abs(g) < 1e-9), "Identity measurement changed the signal.");
    var attenuated = AudioBuildValidation.Compare(original, half);
    Check(attenuated.ChannelCorrelation.All(c => c > 0.99999) && attenuated.GainDb.All(g => Math.Abs(g + 6.0206) < 0.01), "Measured signal gain is incorrect.");
    Check(AudioBuildValidation.Compare(original, inverted).ChannelCorrelation.All(c => c < -0.99999), "Signal inversion was not detected.");
    var info = AudioBuildValidation.Inspect(silence);
    Check(info.Frames == 48000 && info.RmsDb.All(double.IsNegativeInfinity), "Silence measurements are invalid.");
    Check(AudioBuildValidation.Compare(silence, silence).ChannelCorrelation.All(c => c == 1), "Identical silence correlation is invalid.");
    Reject(() => AudioBuildValidation.Compare(original, InvalidLengthWav(temp)), "Mismatched frame counts were accepted.");
    Console.WriteLine("Verified PCM identity, gain, inversion, silence and frame mismatch measurements.");
}

static string InvalidLengthWav(string temp)
{
    var path = Path.Combine(temp, "short.wav");
    WriteWav(path, 48000, 2, 24000, 1);
    return path;
}

static void TestTrackPacks(string temp)
{
    var track = new PackTrack { Id = "t0", File = "audio/nested/a.mp3", SampleLength = 441000, SampleRate = 44100 };
    var validPath = Path.Combine(temp, "nested.fhre");
    WritePack(validPath, [track], ["audio/nested/a.mp3"]);
    var imported = TrackPack.Load(validPath);
    try
    {
        Check(imported.Manifest.Tracks.Single().File == "nested_a.mp3" && File.Exists(Path.Combine(imported.Dir, "nested_a.mp3")), "Nested pack audio was mapped to a missing file.");
    }
    finally
    {
        var root = Path.GetFullPath(Path.Combine(AppPaths.TempBase, "import")) + Path.DirectorySeparatorChar;
        var directory = Path.GetFullPath(imported.Dir);
        Check(directory.StartsWith(root, StringComparison.OrdinalIgnoreCase), "Imported fixture directory is outside the import root.");
        Directory.Delete(directory, recursive: true);
    }
    var cases = new (string Name, PackTrack[] Tracks, string[] Entries)[]
    {
        ("collision", [track], ["audio/nested/a.mp3", "audio/nested_a.mp3"]),
        ("duplicate-entry", [track], ["audio/nested/a.mp3", "audio/nested/a.mp3"]),
        ("missing", [track], []),
        ("duplicate-id", [track, new PackTrack { Id = "t0", File = track.File }], [track.File]),
        ("traversal", [new PackTrack { Id = "t0", File = "audio/../a.mp3" }], ["audio/a.mp3"]),
    };
    foreach (var test in cases)
    {
        var path = Path.Combine(temp, "pack-" + test.Name + ".fhre");
        WritePack(path, test.Tracks, test.Entries);
        Reject(() => TrackPack.Load(path), $"An invalid {test.Name} pack was accepted.");
    }
    Console.WriteLine("Verified nested pack paths, destination collisions, missing audio and duplicate archive entries and ids.");
}

static void WritePack(string path, PackTrack[] tracks, string[] entries)
{
    using var output = File.Create(path);
    using var zip = new ZipArchive(output, ZipArchiveMode.Create);
    foreach (var entry in entries)
    {
        using var audio = zip.CreateEntry(entry).Open();
        audio.Write([1, 2, 3]);
    }
    using var manifest = zip.CreateEntry("manifest.json").Open();
    JsonSerializer.Serialize(manifest, new PackManifest { Tracks = tracks.ToList() });
}

static void TestImportedMarkers(string temp)
{
    var method = typeof(MainWindowViewModel).GetMethod("SetImportedAudio", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(MainWindowViewModel), "SetImportedAudio");
    var track = new TrackItemViewModel(new RadioTrack { SoundName = "R_TEST", Origin = TrackOrigin.Original, SampleLength = 441000, SampleRate = 44100 });
    var imported = new PackTrack { File = "track.mp3", SampleLength = 441000, SampleRate = 44100, Markers = new Dictionary<string, long> { ["TrackStart"] = 44100, ["DJDrop"] = -1 } };
    method.Invoke(null, [track, imported, Path.Combine(temp, "track.mp3")]);
    Check(track.SampleRate == 48000 && track.SampleLength == 480000 && track.Markers!["TrackStart"] == 48000 && track.Markers["DJDrop"] == -1, "Modern imported markers were not staged at the encoded sample rate.");
    method.Invoke(null, [track, imported, Path.Combine(temp, "track.fsb")]);
    Check(track.SampleRate == 44100 && track.SampleLength == 441000 && track.Markers!["TrackStart"] == 44100, "Native FSB import changed its marker timing.");

    var xml = Path.Combine(temp, "markers.xml");
    File.WriteAllText(xml, """
    <Radio><RadioStations><RadioStation Name="Test" Number="1"><Banks><Bank Name="R1_Tracks_Main" /></Banks><SampleList Type="Track"><Sample SoundName="R_TEST" SampleLength="441000" SampleRate="44100"><Marker Name="TrackStart" Position="44100" /></Sample></SampleList><PlayList Type="FreeRoam"><Entry Name="R_TEST" /></PlayList></RadioStation></RadioStations></Radio>
    """);
    var radio = RadioInfo.Load(xml);
    var apply = typeof(MainWindowViewModel).GetMethod("ApplyRadioInfo", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(MainWindowViewModel), "ApplyRadioInfo");
    var station = new StationInfo(1, "Test", ["Main"], new Dictionary<string, string> { ["Main"] = "R1_Tracks_Main" });
    var added = new AddedSample[] { new("R_TEST", 480000, 48000, "Test", null, true, true) };
    var items = new List<BuildItem> { new("R_TEST", false, null, "Test", null, null, true, imported.Markers, true, MarkerSampleRate: 44100) };
    apply.Invoke(null, [radio, station, "R1_Tracks_Main", added, items]);
    Check(radio.StationByNumber(1)!.GetMarkers("R_TEST", 48000)!["TrackStart"] == 48000, "Marker timing was not rescaled after FSB conversion changed the sample rate.");
    Console.WriteLine("Verified imported and committed marker timing across 44.1 kHz and 48 kHz audio.");
}

static string[] GenerateInputs(string temp)
{
    var stereo = Path.Combine(temp, "stereo-44100.wav");
    var mono = Path.Combine(temp, "mono-48000.wav");
    var flac = Path.Combine(temp, "stereo-48000.flac");
    var mp3 = Path.Combine(temp, "stereo-44100.mp3");
    WriteWav(stereo, 44100, 2, 44100 * 3, 1);
    WriteWav(mono, 48000, 1, 48000 * 3, 1);
    RunTool(Tools.FfmpegPath, $"-y -hide_banner -loglevel error -i \"{stereo}\" -ar 48000 -c:a flac \"{flac}\"");
    RunTool(Tools.FfmpegPath, $"-y -hide_banner -loglevel error -i \"{stereo}\" -c:a libmp3lame -b:a 256k \"{mp3}\"");
    return [stereo, mono, flac, mp3];
}

static async Task TestPreviewMarkersAsync(string input)
{
    var vm = (MainWindowViewModel) RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
    var settings = typeof(MainWindowViewModel).GetField("<Settings>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(nameof(MainWindowViewModel), "Settings");
    settings.SetValue(vm, new AppSettings { LoudnessNormalize = false });
    var track = new TrackItemViewModel(new RadioTrack
    {
        SoundName = Naming.MakeSoundName(25), Origin = TrackOrigin.Custom, SampleLength = 44100 * 3, SampleRate = 44100,
        Markers = new Dictionary<string, long> { ["TrackStart"] = 44100, ["DJDrop"] = -1 },
    }) { SourcePath = input };
    var result = await vm.LoadPeaksAsync(track);
    Check(result.Wav is not null && track.SampleRate == 48000 && track.SampleLength == 48000 * 3 && track.Markers!["TrackStart"] == 48000 && track.Markers["DJDrop"] == -1,
        "Modern waveform preview changed preserved marker timing.");
    await vm.LoadPeaksAsync(track);
    Check(track.Markers!["TrackStart"] == 48000, "Repeated preview rescaled marker timing twice.");
    Console.WriteLine("Verified preserved modern markers after 44.1 kHz audio preview and repeated preview.");
}

static async Task TestBuildAsync(string temp, string[] inputs, bool normalize)
{
    var prefix = normalize ? "normalized" : "plain";
    var source = Path.Combine(temp, prefix + ".empty.bank");
    var output = Path.Combine(temp, prefix + ".bank");
    WriteEmptyBank(source);
    var settings = new AppSettings { LoudnessNormalize = normalize, TargetLufs = -20, EncodeParallelism = 2, VorbisQuality = 95 };
    var gains = new double?[] { 2.5, -3, null, 1 };
    var items = inputs.Select((p, i) => new BuildItem(Naming.MakeSoundName(i + 1), true, p, Path.GetFileName(p), null, gains[i], true)).ToArray();
    var log = new ConcurrentQueue<string>();
    var added = await BankBuildService.BuildToFileAsync(source, output, items, settings, log.Enqueue);
    Check(added.Count == inputs.Length, "A staged input was omitted from the bank.");
    var skeleton = FevBank.ReadSkeleton(output);
    var fsb = new FevBank(File.ReadAllBytes(output)).ExtractFsb5();
    var samples = Fsb5.Parse(fsb).Samples;
    Check(samples.Count == inputs.Length && skeleton.Mode == 15 && FevBank.HasModMarker(output), "Assembled Vorbis bank dimensions are incorrect.");
    Check(skeleton.Stbl.All(e => Lookup.SoundNameToId(items[e.Index].SoundName) == e.Id), "STBL indices do not match the build plan.");
    for (var i = 0; i < inputs.Length; i++)
    {
        var expected = Path.Combine(temp, $"{prefix}-{i}.expected.wav");
        var decoded = Path.Combine(temp, $"{prefix}-{i}.decoded.wav");
        await Loudnorm.RenderAsync(inputs[i], expected, settings, gains[i] ?? 0);
        var expectedInfo = AudioBuildValidation.Inspect(expected);
        var preparation = log.Single(l => l.Contains($"[track={i} ", StringComparison.Ordinal) && l.Contains("wav.prepared ", StringComparison.Ordinal));
        using (var json = JsonDocument.Parse(preparation[(preparation.IndexOf("wav.prepared ", StringComparison.Ordinal) + "wav.prepared ".Length)..]))
            Check(json.RootElement.GetProperty("PcmSha256").GetString() == expectedInfo.PcmSha256, "Bank encoder input differs from the shared audio renderer.");
        var previewWav = AudioDecoder.DecodeAdded(inputs[i], settings, gainDb: gains[i] ?? 0);
        Check(AudioBuildValidation.Inspect(previewWav).PcmSha256 == expectedInfo.PcmSha256, "Preview PCM differs from the bank encoder input.");
        RunTool(Tools.VgmstreamPath, $"-i -w -s {i + 1} -o \"{decoded}\" \"{output}\"");
        var info = AudioBuildValidation.Inspect(decoded);
        Check(info.SampleRate == 48000 && info.Channels == 2 && info.Frames == samples[i].Frames, "The assembled sample has incorrect playback timing or channels.");
        var comparison = AudioBuildValidation.Compare(expected, decoded);
        Check(comparison.ChannelCorrelation.All(c => c > 0.98), "Vorbis encoding changed channel polarity or signal alignment.");
        Check(comparison.GainDb.All(g => Math.Abs(g) < 1), "Vorbis encoding changed output gain unexpectedly.");
    }
    Check(log.Any(l => l.Contains("fsb.validate.ok", StringComparison.Ordinal)), "Per-track FSB validation was not logged.");
    Check(log.Any(l => l.Contains("bank.validate.ok", StringComparison.Ordinal)), "Assembled bank validation was not logged.");

    var repeat = Path.Combine(temp, prefix + ".repeat.bank");
    var kept = items.Select(i => i with { IsNewCustom = false, SourcePath = null }).ToArray();
    await BankBuildService.BuildToFileAsync(output, repeat, kept, settings, log.Enqueue);
    var repeated = Fsb5.Parse(new FevBank(File.ReadAllBytes(repeat)).ExtractFsb5()).Samples;
    Check(samples.Zip(repeated).All(p => p.First.Data.AsSpan().SequenceEqual(p.Second.Data)), "Rebuilding changed copied compressed audio.");

    var singleFsb = Path.Combine(temp, prefix + ".single.fsb");
    File.WriteAllBytes(singleFsb, Fsb5.BuildSingleSample(fsb[..60], samples[0]));
    var passthrough = Path.Combine(temp, prefix + ".passthrough.bank");
    await BankBuildService.BuildToFileAsync(source, passthrough, [items[0] with { SourcePath = singleFsb }], settings, log.Enqueue);
    Check(Fsb5.Parse(new FevBank(File.ReadAllBytes(passthrough)).ExtractFsb5()).Samples.Single().Data.AsSpan().SequenceEqual(samples[0].Data), "FSB passthrough changed compressed audio.");
    var preview = AudioDecoder.DecodeBank(passthrough, 0);
    Check(AudioBuildValidation.Compare(Path.Combine(temp, $"{prefix}-0.decoded.wav"), preview).ChannelCorrelation.All(c => c > 0.99999),
        "An odd-count bank could not be previewed with the same decoded signal.");
    var multiFsb = Path.Combine(temp, prefix + ".multi.fsb");
    File.WriteAllBytes(multiFsb, fsb);
    var direct = AudioDecoder.DecodeBank(multiFsb, 3);
    Check(AudioBuildValidation.Compare(Path.Combine(temp, $"{prefix}-3.decoded.wav"), direct).ChannelCorrelation.All(c => c > 0.99999),
        "Direct FSB preview selected the wrong subsample.");

    var empty = Path.Combine(temp, prefix + ".removed.bank");
    await BankBuildService.BuildToFileAsync(output, empty, [], settings, log.Enqueue);
    Check(FevBank.ReadStblCountFromFile(empty) == 0 && Fsb5.ReadLayout(FevBank.ReadSkeleton(empty).Fsb5HeaderRegion).Samples.Count == 0, "Removing all tracks failed.");
    TestRiffPadding(output, temp);
    Console.WriteLine($"Verified WAV stereo/mono, FLAC and MP3 with normalization={normalize}, gain, STBL, repeat build, passthrough and empty plan.");
}

static async Task TestFadpcmAsync(string temp, string input)
{
    var wav = Path.Combine(temp, "fadpcm-input.wav");
    var encoded = Path.Combine(temp, "fadpcm.fsb");
    var cache = Path.Combine(temp, "fadpcm-cache");
    Directory.CreateDirectory(cache);
    RunTool(Tools.FfmpegPath, $"-y -hide_banner -loglevel error -i \"{input}\" -ar 48000 -ac 2 -c:a pcm_s16le \"{wav}\"");
    RunTool(Tools.FsbankclPath, $"-format fadpcm -o \"{encoded}\" \"{wav}\" -cache_dir \"{cache}\"");
    var source = Path.Combine(temp, "fadpcm-source.bank");
    var skeleton = Path.Combine(temp, "fadpcm-empty.bank");
    WriteEmptyBank(skeleton, 0);
    var template = Fsb5.Parse(File.ReadAllBytes(encoded));
    var combined = template.Build([template.Samples[0], template.Samples[0]]);
    File.WriteAllBytes(source, new FevBank(File.ReadAllBytes(skeleton)).FillEmpty(
        FevBank.BuildStbl([(Lookup.SoundNameToId(Naming.MakeSoundName(10)), 0), (Lookup.SoundNameToId(Naming.MakeSoundName(12)), 1)]), combined));
    var output = Path.Combine(temp, "fadpcm-output.bank");
    var item = new BuildItem(Naming.MakeSoundName(11), true, input, "FADPCM", null, null, true);
    await BankBuildService.BuildToFileAsync(source, output, [item, item with { SoundName = Naming.MakeSoundName(13) }], new AppSettings { LoudnessNormalize = false });
    var decoded = Path.Combine(temp, "fadpcm-decoded.wav");
    RunTool(Tools.VgmstreamPath, $"-i -w -s 1 -o \"{decoded}\" \"{output}\"");
    var info = AudioBuildValidation.Inspect(decoded);
    var frames = Fsb5.Parse(new FevBank(File.ReadAllBytes(output)).ExtractFsb5()).Samples[0].Frames;
    Check(FevBank.ReadSkeleton(output).Mode == 16 && info.Frames == frames, "FADPCM roundtrip changed codec or timing.");
    var expectedFrames = AudioBuildValidation.Inspect(wav).Frames;
    Check(frames == (expectedFrames + 255) / 256 * 256, "FADPCM frame count did not match its block padding.");
    var comparison = AudioBuildValidation.Compare(wav, decoded, 255);
    Console.WriteLine($"FADPCM comparison: {JsonSerializer.Serialize(comparison)}");
    var nativeDecoded = Path.Combine(temp, "fadpcm-native.wav");
    RunTool(Tools.VgmstreamPath, $"-i -w -s 1 -o \"{nativeDecoded}\" \"{encoded}\"");
    var nativeComparison = AudioBuildValidation.Compare(wav, nativeDecoded, 255);
    Console.WriteLine($"Native FADPCM comparison: {JsonSerializer.Serialize(nativeComparison)}");
    Check(AudioBuildValidation.Inspect(nativeDecoded).PcmSha256 == info.PcmSha256, "Assembling FADPCM changed the decoded signal from native FSBank output.");
    Reject(() => AudioBuildValidation.Compare(wav, decoded), "Codec padding was accepted without an explicit allowance.");
    var oddBank = Path.Combine(temp, "fadpcm-odd.bank");
    await BankBuildService.BuildToFileAsync(source, oddBank, [item], new AppSettings { LoudnessNormalize = false });
    Check(AudioBuildValidation.Inspect(AudioDecoder.DecodeBank(oddBank, 0)).PcmSha256 == info.PcmSha256,
        "An odd-count FADPCM bank could not be previewed with the same decoded signal.");
    Console.WriteLine($"Verified FADPCM encoding and decoding ({frames} frames).");
}

static async Task TestInvalidInputsAsync(string temp, string input)
{
    var source = Path.Combine(temp, "invalid-source.bank");
    WriteEmptyBank(source);
    var original = File.ReadAllBytes(source);
    var validBank = Path.Combine(temp, "plain.bank");
    var valid = new FevBank(File.ReadAllBytes(validBank)).ExtractFsb5();
    var parsed = Fsb5.Parse(valid);
    var single = Fsb5.BuildSingleSample(valid[..60], parsed.Samples[0]);
    var noFrames = (byte[]) single.Clone();
    var raw = BinaryPrimitives.ReadUInt64LittleEndian(noFrames.AsSpan(60));
    BinaryPrimitives.WriteUInt64LittleEndian(noFrames.AsSpan(60), raw & ~(0x3FFFFFFFUL << 34));
    var noData = single[..(60 + (int) BinaryPrimitives.ReadUInt32LittleEndian(single.AsSpan(12)))];
    BinaryPrimitives.WriteUInt32LittleEndian(noData.AsSpan(20), 0);
    var corruptData = (byte[]) single.Clone();
    var dataOffset = 60 + (int) BinaryPrimitives.ReadUInt32LittleEndian(single.AsSpan(12)) + (int) BinaryPrimitives.ReadUInt32LittleEndian(single.AsSpan(16));
    corruptData.AsSpan(dataOffset).Fill(0xff);
    var cases = new (string Name, byte[] Bytes)[]
    {
        ("truncated", single[..^1]), ("no-frames", noFrames), ("no-data", noData), ("multi-sample", valid), ("corrupt-audio", corruptData),
    };
    foreach (var test in cases)
    {
        var path = Path.Combine(temp, test.Name + ".fsb");
        var output = Path.Combine(temp, test.Name + ".bank");
        File.WriteAllBytes(path, test.Bytes);
        var destination = new byte[] { 9, 8, 7, 6 };
        File.WriteAllBytes(output, destination);
        var failed = false;
        try
        {
            await BankBuildService.BuildToFileAsync(source, output, [new BuildItem(Naming.MakeSoundName(20), true, path, test.Name, null, null, true)], new AppSettings { LoudnessNormalize = false });
        }
        catch (InvalidDataException) { failed = true; }
        catch (InvalidOperationException) { failed = true; }
        Check(failed, $"Invalid FSB {test.Name} was accepted.");
        Check(File.ReadAllBytes(source).AsSpan().SequenceEqual(original), "A failed build changed its source bank.");
        Check(File.ReadAllBytes(output).AsSpan().SequenceEqual(destination), "A failed FSB validation replaced an existing destination.");
    }
    var mismatch = false;
    try
    {
        await BankBuildService.BuildToFileAsync(Path.Combine(temp, "fadpcm-source.bank"), Path.Combine(temp, "mismatch.bank"),
            [new BuildItem(Naming.MakeSoundName(22), true, Path.Combine(temp, "multi-sample.fsb"), "Mismatch", null, null, true)],
            new AppSettings { LoudnessNormalize = false });
    }
    catch (InvalidDataException) { mismatch = true; }
    catch (InvalidOperationException) { mismatch = true; }
    Check(mismatch, "A multisample FSB was accepted through codec conversion.");
    var failureOutput = Path.Combine(temp, "failed-destination.bank");
    var failedMissing = false;
    try
    {
        await BankBuildService.BuildToFileAsync(source, failureOutput, [new BuildItem(Naming.MakeSoundName(21), true, input + ".missing", null, null, null, true)], new AppSettings());
    }
    catch (FileNotFoundException) { failedMissing = true; }
    Check(failedMissing && File.ReadAllBytes(source).AsSpan().SequenceEqual(original), "Missing source audio changed the original bank.");
    var protectedOutput = Path.Combine(temp, "protected-destination.bank");
    var protectedBytes = new byte[] { 5, 4, 3, 2, 1 };
    File.WriteAllBytes(protectedOutput, protectedBytes);
    var tampered = false;
    var finalValidationFailed = false;
    void TamperAfterAssembly(string message)
    {
        if (!message.Contains("assembled bank:", StringComparison.Ordinal)) return;
        var staged = Directory.GetFiles(temp, ".fhre-*.bank").Single();
        var skeleton = FevBank.ReadSkeleton(staged);
        using var file = new FileStream(staged, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        file.Position = skeleton.DataStartAbs;
        var first = file.ReadByte();
        file.Position--;
        file.WriteByte((byte) (first ^ 0xff));
        tampered = true;
    }
    try
    {
        await BankBuildService.BuildToFileAsync(source, protectedOutput,
            [new BuildItem(Naming.MakeSoundName(26), true, input, "Validate destination", null, null, true)],
            new AppSettings { LoudnessNormalize = false }, TamperAfterAssembly);
    }
    catch (InvalidDataException) { finalValidationFailed = true; }
    Check(tampered && finalValidationFailed && File.ReadAllBytes(protectedOutput).AsSpan().SequenceEqual(protectedBytes),
        "Failed final bank validation replaced an existing destination.");
    Check(!Directory.EnumerateFiles(temp, ".fhre-*.bank").Any(), "A failed build left its staging bank behind.");
    Console.WriteLine("Verified malformed, empty, truncated, multisample and corrupt audio FSB rejection with preserved source banks.");
}

static async Task TestSilentVorbisAsync(string temp)
{
    var source = Path.Combine(temp, "silent-empty.bank");
    var output = Path.Combine(temp, "silent.bank");
    var wav = Path.Combine(temp, "silent-input.wav");
    WriteEmptyBank(source);
    WriteWav(wav, 48000, 2, 48000, 0);
    var item = new BuildItem(Naming.MakeSoundName(27), true, wav, "Silent track", null, null, true);
    var settings = new AppSettings { LoudnessNormalize = false };
    await BankBuildService.BuildToFileAsync(source, output, [item], settings);
    var fsbPath = Path.Combine(temp, "silent.fsb");
    File.WriteAllBytes(fsbPath, new FevBank(File.ReadAllBytes(output)).ExtractFsb5());
    var passthrough = Path.Combine(temp, "silent-passthrough.bank");
    await BankBuildService.BuildToFileAsync(source, passthrough, [item with { SourcePath = fsbPath }], settings);
    Check(AudioBuildValidation.Inspect(AudioDecoder.DecodeBank(passthrough, 0)).RmsDb.All(double.IsNegativeInfinity),
        "A valid encoded silent track was rejected or changed.");
    Console.WriteLine("Verified encoded Vorbis silence and silent FSB passthrough.");
}

static void TestRiffPadding(string bank, string temp)
{
    var bytes = File.ReadAllBytes(bank);
    var padded = new byte[bytes.Length + 10];
    bytes.AsSpan(0, 12).CopyTo(padded);
    "JUNK"u8.CopyTo(padded.AsSpan(12));
    BinaryPrimitives.WriteUInt32LittleEndian(padded.AsSpan(16), 1);
    padded[20] = 7;
    bytes.AsSpan(12).CopyTo(padded.AsSpan(22));
    BinaryPrimitives.WriteUInt32LittleEndian(padded.AsSpan(4), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) + 10);
    var original = FevBank.ReadSkeleton(bank);
    var list = FindTag(padded, "LIST"u8);
    var sndh = FindTag(padded, "SNDH"u8);
    BinaryPrimitives.WriteUInt32LittleEndian(padded.AsSpan(sndh + 12), BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(sndh + 12)) + 10);
    var path = Path.Combine(temp, "odd-riff.bank");
    File.WriteAllBytes(path, padded);
    var result = FevBank.ReadSkeleton(path);
    Check(list > 0 && result.Mode == original.Mode && result.DataStartAbs == original.DataStartAbs + 10, "Odd RIFF chunk padding was not skipped.");
    Check(FevBank.ReadStblCountFromFile(path) == original.Stbl.Count && FevBank.ReadStblIdsFromFile(path).SetEquals(original.Stbl.Select(e => e.Id)), "STBL readers did not skip odd RIFF padding.");
    Check(FevBank.ReadTrackInfoFromFile(path).Count == original.Stbl.Count, "Track info reader did not skip odd RIFF padding.");
    Check(new FevBank(padded).ExtractFsb5().AsSpan().SequenceEqual(new FevBank(bytes).ExtractFsb5()), "FEV extraction did not skip odd RIFF padding.");
}

static int FindTag(byte[] bytes, ReadOnlySpan<byte> tag)
{
    for (var i = 0; i <= bytes.Length - tag.Length; i++)
        if (bytes.AsSpan(i, tag.Length).SequenceEqual(tag)) return i;
    throw new InvalidDataException("Fixture tag was not found.");
}

static void WriteEmptyBank(string path, int sndhSize = 12)
{
    using var list = new MemoryStream();
    list.Write("PROJ"u8);
    WriteChunk(list, "BNKI", []);
    WriteChunk(list, "STBL", []);
    WriteChunk(list, "SNDH", new byte[sndhSize]);
    using var body = new MemoryStream();
    WriteChunk(body, "FMT ", [0x50, 0, 0, 0]);
    WriteChunk(body, "LIST", list.ToArray());
    using var output = File.Create(path);
    output.Write("RIFF"u8);
    using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
    writer.Write((uint) (body.Length + 4));
    output.Write("FEV "u8);
    body.Position = 0;
    body.CopyTo(output);
}

static void WriteChunk(Stream output, string tag, byte[] payload)
{
    output.Write(Encoding.ASCII.GetBytes(tag));
    using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
    writer.Write((uint) payload.Length);
    output.Write(payload);
    if ((payload.Length & 1) != 0) output.WriteByte(0);
}

static void WriteWav(string path, int rate, int channels, int frames, double gain)
{
    using var file = File.Create(path);
    using var writer = new BinaryWriter(file);
    writer.Write("RIFF"u8);
    writer.Write(36 + frames * channels * 2);
    writer.Write("WAVEfmt "u8);
    writer.Write(16);
    writer.Write((short) 1);
    writer.Write((short) channels);
    writer.Write(rate);
    writer.Write(rate * channels * 2);
    writer.Write((short) (channels * 2));
    writer.Write((short) 16);
    writer.Write("data"u8);
    writer.Write(frames * channels * 2);
    for (var frame = 0; frame < frames; frame++)
    {
        var t = frame / (double) rate;
        var envelope = 0.6 + 0.4 * Math.Sin(t * 2 * Math.PI * 1.7);
        for (var channel = 0; channel < channels; channel++)
        {
            var signal = envelope * (0.2 * Math.Sin(t * 2 * Math.PI * (channel == 0 ? 317 : 683)) + 0.1 * Math.Sin(t * 2 * Math.PI * 1379));
            writer.Write((short) Math.Round(Math.Clamp(signal * gain, -1, 1) * 32767));
        }
    }
}

static void RunTool(string exe, string arguments)
{
    var result = Proc.Run(exe, arguments);
    Check(result.ExitCode == 0, $"{Path.GetFileName(exe)} failed ({result.ExitCode}): {result.StdErr}\n{result.StdOut}");
}
