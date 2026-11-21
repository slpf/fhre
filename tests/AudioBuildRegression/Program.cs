using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FH6RB.Collections;
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
    if (args.Contains("--ui-loudness", StringComparer.Ordinal))
    {
        await TrackLoudnessChecks.RunAsync(temp, path => WriteEmptyBank(path));
        return 0;
    }
    if (args.Contains("--benchmark-normalization", StringComparer.Ordinal))
    {
        await NormalizationBenchmark.RunAsync(temp);
        return 0;
    }
    TestFsbHeaders();
    MarkerEditingChecks.Run(temp);
    TestAudioMeasurements(temp);
    await TrackLoudnessChecks.RunAsync(temp, path => WriteEmptyBank(path));
    TestTrackPacks(temp);
    TestImportedMarkers(temp);
    TestImportBatch(temp);
    await XmaEncoderChecks.RunAsync(temp);
    await NormalizationChecks.RunAsync(temp);
    await OutputLoudnessChecks.RunAsync(temp, path => WriteEmptyBank(path));
    await LimiterSettingsChecks.RunAsync(temp);
    await PeakLimiterChecks.RunAsync(temp, path => WriteEmptyBank(path));
    await AudioSafetyChecks.RunAsync(temp, WriteEmptyBank);
    await AudioPipelineChecks.RunAsync(temp, path => WriteEmptyBank(path));
    await GainEnvelopeChecks.RunAsync(temp);
    await NormalizationOverrideChecks.RunAsync(temp, path => WriteEmptyBank(path));
    var inputs = GenerateInputs(temp);
    await TestGameWritesAsync(temp, inputs[1]);
    await RadioBuildChecks.RunAsync(temp, inputs[1], Path.Combine(temp, "short-replacement.wav"), WriteEmptyBank);
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
    var vm = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
    var track = new TrackItemViewModel(new RadioTrack { SoundName = "R_TEST", Origin = TrackOrigin.Original, SampleLength = 441000, SampleRate = 44100 });
    var imported = new PackTrack { File = "track.mp3", SampleLength = 441000, SampleRate = 44100, Markers = new Dictionary<string, long> { ["TrackStart"] = 44100, ["DJDrop"] = -1 } };
    var pack = new ImportedPack(new PackManifest(), temp);
    vm.StageImportedReplace(track, pack, imported);
    Check(track.SampleRate == 48000 && track.SampleLength == 480000 && track.Markers!["TrackStart"] == 48000 && track.Markers["DJDrop"] == -1, "Modern imported markers were not staged at the encoded sample rate.");
    imported.File = "track.fsb";
    vm.StageImportedReplace(track, pack, imported);
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

static void TestImportBatch(string temp)
{
    var vm = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
    void Set(string field, object value) => typeof(MainWindowViewModel)
        .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, value);

    var original = new TrackItemViewModel(new RadioTrack
    {
        SoundName = "PACK_TARGET", Origin = TrackOrigin.Original, SampleRate = 44100,
        SampleLength = 441000, Enabled = true,
        Markers = new Dictionary<string, long> { ["TrackStart"] = 44100 },
    });
    var tracks = new RangeObservableCollection<TrackItemViewModel>([original]);
    var locks = new Dictionary<string, FileStream>();
    Set("<Tracks>k__BackingField", tracks);
    Set("<FilteredTracks>k__BackingField", new RangeObservableCollection<TrackItemViewModel>());
    Set("<Settings>k__BackingField", new AppSettings());
    Set("_gameVersion", 6);
    Set("_occupiedTrackIds", new HashSet<ulong>());
    Set("_sourceLocks", locks);

    var file = Path.Combine(temp, "import-batch.wav");
    WriteWav(file, 44100, 2, 44100, 1);
    original.StageReplacement(file, "Pending", null, 1, false);
    Check(original.SampleRate == 44100 && original.SampleLength == 44100 && original.Markers!["TrackStart"] == 44100,
        "A pending replacement lost the sample rate used by its markers.");
    var first = new PackTrack { Id = "one", File = Path.GetFileName(file), DisplayName = "Imported",
        Enabled = false, SampleRate = 44100, SampleLength = 44100 };
    var second = new PackTrack { Id = "two", File = first.File, SampleRate = 44100, SampleLength = long.MaxValue };
    var third = new PackTrack { Id = "three", File = first.File, SampleRate = 44100, SampleLength = 44100 };
    var pack = new ImportedPack(new PackManifest { Tracks = [first, second, third] }, temp);
    var mapping = new Dictionary<string, string?> { [first.Id] = original.SoundName };
    var updates = 0;
    tracks.CollectionChanged += (_, _) => updates++;

    Reject(() => vm.ApplyImport(pack, mapping), "Import accepted timing that overflows after resampling.");
    Check(original.Title == "Pending" && original.Enabled && !vm.HasUnsavedChanges && tracks.Count == 1 && updates == 0,
        "An invalid entry partially changed the station.");

    second.SampleLength = 44100;
    try
    {
        vm.ApplyImport(pack, mapping);
        Check(original.Title == "Imported" && !original.Enabled && vm.HasUnsavedChanges && tracks.Count == 3 && updates == 1,
            "Valid import lost the replacement state or refreshed the track list more than once.");
    }
    finally
    {
        foreach (var stream in locks.Values) stream.Dispose();
    }

    var selection = new ImportWindowViewModel([first], tracks.ToArray());
    selection.Rows[0].Selected = true;
    selection.Rows[1].Selected = true;
    Check(!selection.MarkSaved(), "Import accepted more replacement targets than entries.");
    selection.SelectAll(true);
    Check(selection.MarkSaved() && selection.SelectedCount == 1 && selection.Result[first.Id] == original.SoundName,
        "Select all did not respect the number of imported entries.");
}

static async Task TestExportSafeguardsAsync(string temp, string bank, string source, ulong bankId)
{
    var path = Path.Combine(temp, "checked-export.fhre");
    var neighbor = path + ".tmp";
    File.WriteAllText(neighbor, "unrelated");
    var native = new ExportItem(null, 0, "Bank track", null, 144000, 48000, null, true, "default", null, BankId: bankId);
    await TrackPack.ExportAsync(path, "test", bank, [native], null, null, default);
    Check(File.ReadAllText(neighbor) == "unrelated", "Export overwrote an unrelated neighboring file.");
    var committed = File.ReadAllBytes(path);

    var existing = native with { FileSource = source, SubIndex = -1 };
    var missing = native with { FileSource = Path.Combine(temp, "nonexistent-replacement.wav"), Role = "replacement" };
    try
    {
        await TrackPack.ExportAsync(path, "test", bank, [existing, missing], null, null, default);
        throw new InvalidOperationException("Export silently substituted original bank audio for a missing replacement.");
    }
    catch (FileNotFoundException)
    {
    }
    try
    {
        await TrackPack.ExportAsync(path, "test", bank, [native with { BankId = ~bankId }], null, null, default);
        throw new InvalidOperationException("Export accepted a bank sample with a different track ID.");
    }
    catch (IOException)
    {
    }
    Check(File.ReadAllBytes(path).SequenceEqual(committed) && File.ReadAllText(neighbor) == "unrelated",
        "A failed export changed a previously saved package or its neighbor.");
}

static async Task TestGameWritesAsync(string temp, string input)
{
    const string bankName = "R1_Tracks";
    const string shared = "SHARED_TRACK";
    const string sibling = "SIBLING_TRACK";
    var game = Path.Combine(temp, "game-writes");
    Directory.CreateDirectory(game);
    var bank = Path.Combine(game, bankName + ".assets.bank");
    var otherBank = Path.Combine(game, bankName + "_CU1.assets.bank");
    var en = Path.Combine(game, "RadioInfo_EN.xml");
    var de = Path.Combine(game, "RadioInfo_DE.xml");
    var template = Path.Combine(temp, "write-template.bank");
    WriteEmptyBank(template);
    var settings = new AppSettings { LoudnessNormalize = false };
    await BankBuildService.BuildToFileAsync(template, bank, [new BuildItem(shared, true, input, null, null, null, true)], settings);
    await BankBuildService.BuildToFileAsync(template, otherBank, [new BuildItem(sibling, true, input, null, null, null, true)], settings);
    var otherBankBytes = File.ReadAllBytes(otherBank);
    File.WriteAllBytes(otherBank, otherBankBytes[..100]);

    static string RadioXml(string title) => $"""
        <Radio><RadioStations>
          <RadioStation Number="1" Name="Test"><Banks><Bank Name="{bankName}"/><Bank Name="{bankName}_CU1"/></Banks>
            <SampleList Type="Track"><Sample SoundName="{shared}" SampleLength="144000" SampleRate="48000" End="143999" DisplayName="{title}"/><Sample SoundName="{sibling}" SampleLength="144000" SampleRate="48000"/></SampleList>
            <PlayList Type="FreeRoam"><Entry Name="{shared}"/><Entry Name="{sibling}"/></PlayList>
          </RadioStation>
          <RadioStation Number="2" Name="Shared"><Banks><Bank Name="{bankName}"/></Banks>
            <SampleList Type="Track"><Sample SoundName="{shared}" SampleLength="144000" SampleRate="48000" End="143999" DisplayName="Shared station"/></SampleList>
            <PlayList Type="FreeRoam"><Entry Name="{shared}"/></PlayList>
          </RadioStation>
        </RadioStations></Radio>
        """;
    File.WriteAllText(en, RadioXml("English"));
    File.WriteAllText(de, RadioXml("German"));
    var replacement = Path.Combine(temp, "short-replacement.wav");
    WriteWav(replacement, 48000, 2, 48000, 1);
    var station = RadioCatalog.Build(game, RadioInfo.Load(en)).Single(st => st.Number == 1);
    var vm = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
    var resources = typeof(GameScanner).GetMethod("ReadRadioResources", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [game]);
    var build = typeof(MainWindowViewModel).GetMethod("BuildGameFilesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    Task<(IReadOnlyList<AddedSample> Added, long Written, int SavedXml, int Dead)> BuildAsync(List<BuildItem> plan, string[] languages) =>
        (Task<(IReadOnlyList<AddedSample>, long, int, int)>)build.Invoke(vm,
            [game, station, bankName, bank, en, languages, resources, plan, settings, (Action<string>)(_ => { }), CancellationToken.None, null])!;

    var markersOnly = await BuildAsync([new BuildItem(shared, false, null, "English", null, null, true,
        new Dictionary<string, long> { ["TrackStart"] = 4800, ["End"] = 143999 }, MarkerSampleRate: 48000)], []);
    Check(markersOnly.Written == 0 && !File.Exists(bank + ".bak")
        && RadioInfo.Load(de).StationByNumber(2)!.ReadTracks().Single().Markers!["TrackStart"] == 4800,
        "The marker-only fixture did not change the linked station without writing the bank.");
    var restoredMarkers = BackupService.RestoreStation(game, station);
    Check(restoredMarkers.Banks == 0 && RadioInfo.Load(de).StationByNumber(2)!.ReadTracks().Single().Markers?.ContainsKey("TrackStart") == false,
        "Original restore missed shared markers in a bank without an audio backup.");

    var edited = await BuildAsync([new BuildItem(shared, false, replacement, "Edited", null, null, true, IsReplacement: true)], []);
    Check(edited.SavedXml == 2 && edited.Dead == 0, "Single-language replacement skipped shared audio or pruned the truncated sibling bank.");
    foreach (var path in new[] { en, de })
    {
        var radio = RadioInfo.Load(path);
        var ownTracks = radio.StationByNumber(1)!.ReadTracks();
        var sharedTrack = radio.StationByNumber(2)!.ReadTracks().Single();
        Check(ownTracks.Any(t => t.SoundName == sibling && t.Enabled), "Build removed a sibling track from XML.");
        Check(ownTracks.Single(t => t.SoundName == shared).SampleLength == 48000 && sharedTrack.SampleLength == 48000
            && sharedTrack.Markers!["End"] < 48000, "Shared station or localization has stale audio timing.");
        Check(ownTracks.Single(t => t.SoundName == shared).DisplayName == (path == en ? "Edited" : "German")
            && sharedTrack.DisplayName == "Shared station", "Audio synchronization changed another station's or language's title.");
    }

    File.WriteAllBytes(otherBank, otherBankBytes);
    var plan = BankReader.ReadTracks(bank, RadioInfo.Load(en), station.Number).Tracks
        .Select(track => new BuildItem(track.SoundName, false, null, track.DisplayName, track.Artist, null,
            track.Enabled, track.Markers, MarkerSampleRate: track.SampleRate, BankId: track.BankId)).ToList();
    var stamps = new[] { bank, en, de }.ToDictionary(path => path, File.GetLastWriteTimeUtc);
    var repeat = await BuildAsync(plan, []);
    Check(repeat.Written == 0 && repeat.SavedXml == 0 && stamps.All(pair => File.GetLastWriteTimeUtc(pair.Key) == pair.Value),
        "A build without changes rewrote the bank or RadioInfo.");

    var backup = bank + ".bak";
    var savedBackup = backup + ".saved";
    var fileState = new[] { bank, en, de }.ToDictionary(path => path, File.ReadAllBytes);
    File.Move(backup, savedBackup);
    try
    {
        var restorePlan = plan.Select(item => item with { RestoreFromBak = true }).ToList();
        try
        {
            await BuildAsync(restorePlan, []);
            throw new InvalidOperationException("Restoring with a missing backup succeeded.");
        }
        catch (FileNotFoundException ex) when (ex.FileName == backup)
        {
            Check(!File.Exists(backup) && fileState.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),
                "A failed restore wrote the bank, backup or XML.");
        }

        File.Copy(otherBank, backup);
        try
        {
            await BuildAsync(restorePlan, []);
            throw new InvalidOperationException("Restoring a track absent from the backup succeeded.");
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("original audio is missing", StringComparison.Ordinal))
        {
            Check(File.ReadAllBytes(backup).SequenceEqual(otherBankBytes)
                && fileState.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),
                "A restore with the wrong backup wrote the bank, backup or XML.");
        }
    }
    finally
    {
        File.Move(savedBackup, backup, overwrite: true);
    }

    foreach (var path in new[] { en, de })
    {
        var radio = RadioInfo.Load(path);
        radio.StationByNumber(2)!.SetSampleMeta(shared, "Independent station edit", null);
        radio.Save(path);
    }
    var restored = BackupService.RestoreStation(game, station);
    Check(restored.Banks == 1 && restored.Langs == 2 && File.ReadAllBytes(bank).SequenceEqual(File.ReadAllBytes(bank + ".bak")),
        "Original station restore did not recover the bank byte for byte.");
    foreach (var path in new[] { en, de })
    {
        var track = RadioInfo.Load(path).StationByNumber(2)!.ReadTracks().Single();
        Check(track.SampleLength == 144000 && track.Markers!["End"] == 143999 && track.DisplayName == "Independent station edit",
            "Original station restore left stale audio timing or removed another station's edit.");
    }
    var beforeChange = BankReader.ReadTracks(bank, RadioInfo.Load(en), station.Number);
    var snapshot = typeof(BankReadResult).GetProperty("Snapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(beforeChange);
    typeof(MainWindowViewModel).GetField("_loadedBankSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, snapshot);
    var updatedBank = Path.Combine(temp, "external-edit.bank");
    var custom = new BuildItem(Naming.MakeSoundName(77), true, input, "External track", null, null, true);
    await BankBuildService.BuildToFileAsync(bank, updatedBank, [..plan, custom], settings);
    File.Move(updatedBank, bank, overwrite: true);
    var updatedBytes = File.ReadAllBytes(bank);
    var updatedXml = File.ReadAllBytes(en);
    try
    {
        await BuildAsync(plan.Select(item => item with { DisplayName = "Stale title" }).ToList(), []);
        throw new InvalidOperationException("A stale track list was published.");
    }
    catch (IOException ex) when (ex.Message.Contains("Reload the station", StringComparison.Ordinal))
    {
        Check(File.ReadAllBytes(bank).SequenceEqual(updatedBytes) && File.ReadAllBytes(en).SequenceEqual(updatedXml),
            "A rejected stale build changed the bank or XML.");
    }

    var loaded = BankReader.ReadTracks(bank, RadioInfo.Load(en), station.Number).Tracks;
    var tracks = new RangeObservableCollection<TrackItemViewModel>();
    tracks.Reset(loaded.Select(track => new TrackItemViewModel(track)));
    var toDelete = tracks.Single(track => track.SoundName == custom.SoundName);
    void SetField(string name, object value) =>
        typeof(MainWindowViewModel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, value);
    SetField("<Tracks>k__BackingField", tracks);
    SetField("_baseBankMode", 15);
    SetField("_baseBankSize", updatedBytes.LongLength);
    SetField("_baseBankAudioBytes", loaded.Sum(track => track.BankBytes));
    vm.DeleteCommand.Execute(toDelete);
    var projected = (long) typeof(MainWindowViewModel).GetField("_projectedTotalBytes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
    Check(projected == updatedBytes.LongLength - toDelete.BankBytes && vm.StatusSizeText.StartsWith('~'),
        "Removing a previously saved custom track did not reduce the projected size.");

    Console.WriteLine("Verified bank writes preserve sibling tracks, skip unchanged files, and reject failed restores and stale edits.");
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
    if (!normalize)
    {
        await TestExportSafeguardsAsync(temp, output, inputs[0], Lookup.SoundNameToId(items[0].SoundName));
        await TestBankStructureAsync(temp, output, fsb, singleFsb, kept, settings);
    }
    Console.WriteLine($"Verified WAV stereo/mono, FLAC and MP3 with normalization={normalize}, gain, STBL, repeat build, passthrough and empty plan.");
}

static async Task TestBankStructureAsync(string temp, string bank, byte[] fsb, string singleFsb,
    BuildItem[] items, AppSettings settings)
{
    var bankBytes = File.ReadAllBytes(bank);
    var parsed = new FevBank(bankBytes);
    var stbl = FevBank.BuildStbl(parsed.ReadStbl());
    var names = new byte[items.Length * 8];
    for (var i = 0; i < items.Length; i++)
    {
        var offset = (items.Length + i) * 4;
        BinaryPrimitives.WriteInt32LittleEndian(names.AsSpan(i * 4), offset);
        Encoding.ASCII.GetBytes($"t{i}").CopyTo(names, offset);
    }
    var nameStart = 60 + BinaryPrimitives.ReadInt32LittleEndian(fsb.AsSpan(12));
    byte[] namedFsb = [..fsb[..nameStart], ..names, ..fsb[nameStart..]];
    BinaryPrimitives.WriteInt32LittleEndian(namedFsb.AsSpan(16), names.Length);
    var namedBank = Path.Combine(temp, "named.bank");
    var namedBytes = parsed.WithStblAndFsb(stbl, namedFsb);
    File.WriteAllBytes(namedBank, namedBytes);
    var replacement = items[0] with { IsNewCustom = true, SourcePath = singleFsb };
    var rebuilt = Path.Combine(temp, "named-rebuilt.bank");
    await BankBuildService.BuildToFileAsync(namedBank, rebuilt, [items[2], replacement, items[1]], settings);

    static string[] ReadNames(byte[] bank)
    {
        var block = new FevBank(bank).ExtractFsb5();
        var table = block.AsSpan(60 + BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(12)),
            BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(16)));
        var count = BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(8));
        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            var offset = BinaryPrimitives.ReadInt32LittleEndian(table[(i * 4)..]);
            result[i] = Encoding.ASCII.GetString(table.Slice(offset, 2));
        }
        return result;
    }

    Check(ReadNames(File.ReadAllBytes(rebuilt)).SequenceEqual(["t2", "t0", "t1"]),
        "Replacing, reordering and removing samples lost their names.");
    var editor = new BankEditor(namedBytes);
    var edited = editor.Build([PlanItem.Keep(3), PlanItem.Add(editor.HashForIndex(0), editor.SourceSamples[1])]);
    Check(ReadNames(edited).SequenceEqual(["t3", "t0"]), "The in-memory editor lost names after a replacement.");

    var malformed = Path.Combine(temp, "bad-stbl-index.bank");
    var indexOffset = FindTag(namedBytes, "STBL"u8) + 8 + 10 + items.Length * 8 + 2;
    namedBytes[indexOffset] = (byte) items.Length;
    File.WriteAllBytes(malformed, namedBytes);
    Check(BankReader.ReadTracks(malformed, null, 1).Error?.Contains("outside the FSB5 sample table", StringComparison.Ordinal) == true,
        "The track reader accepted an out-of-range STBL index.");

    foreach (var emptyTemplate in new[] { false, true })
    {
        var template = emptyTemplate ? Path.Combine(temp, "size-limit-empty.bank") : bank;
        if (emptyTemplate) WriteEmptyBank(template, sndhSize: 0);
        var layout = FevBank.ReadSkeleton(template);
        var output = Path.Combine(temp, $"size-limit-{emptyTemplate}.bank");
        var trackTable = FevBank.BuildStbl([(Lookup.SoundNameToId(items[0].SoundName), 0)]);
        void Assemble(long dataLength) =>
            FevBank.AssembleToFile(output, layout.FmtChunk, layout.ListPayload, layout.StblRelOff, layout.StblOldSize,
                trackTable, fsb[..60], [new byte[32]], [new FevBank.DataRef(bank, 0, dataLength)], emptyTemplate: emptyTemplate);

        Assemble(0);
        var baseLength = new FileInfo(output).Length;
        File.Delete(output);
        var dataLength = (long) int.MaxValue - 3 + FevBank.ModMarker.Length - baseLength;
        try
        {
            Assemble(dataLength);
            throw new InvalidOperationException("The bank writer accepted a file over its size limit.");
        }
        catch (BankTooLargeException)
        {
            Check(!File.Exists(output), "The size limit was checked after opening the output.");
        }
    }
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
