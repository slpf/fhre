using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FH6RB.Core;
using FH6RB.Services;
using FH6RB.ViewModels;

internal static class NormalizationOverrideChecks
{
    private sealed record Case(string Name, string Input, NormalizationOverride? Override, AppSettings Expected, double Gain = 0);

    public static async Task RunAsync(string temp, Action<string> writeEmptyBank)
    {
        var source = Path.Combine(temp, "override-rich.wav");
        var transients = Path.Combine(temp, "override-transients.wav");
        NormalizationChecks.WriteSignal(source, 48000, 2, 8, limited: false, transients: false, seed: 31);
        NormalizationChecks.WriteSignal(transients, 48000, 2, 8, limited: false, transients: true, seed: 41);
        CheckEditor(source);
        await CheckPacksAsync(temp, source);
        await CheckInheritedPreviewAsync(source);
        var global = Settings(true, -23, -1);
        var unchanged = JsonSerializer.Serialize(global);
        Case[] cases =
        [
            new("inherit", source, null, Settings(true, -23, -1)),
            new("off", source, new(false), Settings(false, -23, -1), -4),
            new("own-target", source, new(true, -27, -3), Settings(true, -27, -3)),
            new("dynamic", source, new(true, -25, -2, true), Settings(true, -25, -2, true)),
            new("peak-cap", transients, new(true, -9, -6), Settings(true, -9, -6), 20),
        ];
        var bank = await CheckMixedBankAsync(temp, "override-global-on", cases, global, writeEmptyBank);
        Check(JsonSerializer.Serialize(global) == unchanged, "Per-track encoding mutated global settings.");
        Check(Math.Abs(NormalizationChecks.Measure(AudioDecoder.DecodeBank(bank, 0)).Integrated + 23) < 0.5,
            "The inherited bank track missed the global loudness target.");
        Check(Math.Abs(NormalizationChecks.Measure(AudioDecoder.DecodeBank(bank, 2)).Integrated + 27) < 0.5,
            "The overridden bank track missed its own loudness target.");
        var off = AudioBuildValidation.Compare(source, AudioDecoder.DecodeBank(bank, 1));
        Check(off.ChannelCorrelation.All(c => c > 0.98) && off.GainDb.All(g => Math.Abs(g + 4) < 0.4),
            "A disabled track override did not preserve the input dynamics and manual trim.");
        Check(NormalizationChecks.Measure(AudioDecoder.DecodeBank(bank, 4)).TruePeak <= -5.5,
            "The per-track true peak ceiling was replaced by the global ceiling.");
        Case[] reverse =
        [
            new("inherit-off", source, null, Settings(false, -23, -1)),
            new("own-on", source, new(true, -26, -2), Settings(true, -26, -2)),
        ];
        var reverseBank = await CheckMixedBankAsync(temp, "override-global-off", reverse, Settings(false, -23, -1), writeEmptyBank);
        Check(AudioBuildValidation.Compare(source, AudioDecoder.DecodeBank(reverseBank, 0)).GainDb.All(g => Math.Abs(g) < 0.4),
            "An inherited normalization-off track changed its level.");
        Check(Math.Abs(NormalizationChecks.Measure(AudioDecoder.DecodeBank(reverseBank, 1)).Integrated + 26) < 0.5,
            "A track could not enable normalization while the global option was disabled.");
        await CheckEncodedPreservationAsync(temp, bank, writeEmptyBank);
        Console.WriteLine("Verified per-track inheritance, independent LUFS/peak/dynamic options, editor cancel/save/reset, pack compatibility, preview PCM and mixed-bank levels.");
    }

    private static AppSettings Settings(bool normalize, double lufs, double peak, bool dynamic = false) => new()
    {
        LoudnessNormalize = normalize, TargetLufs = lufs, TargetTruePeak = peak, DynamicNormalization = dynamic,
        EncodeParallelism = 2, VorbisQuality = 95,
    };

    private static TrackItemViewModel Track(string source, int index = 100) => new(new RadioTrack
    {
        SoundName = Naming.MakeSoundName(index), Origin = TrackOrigin.Custom, SampleRate = 48000, SampleLength = 48000 * 8,
    }) { SourcePath = source };

    private static MainWindowViewModel Preview(AppSettings settings)
    {
        var vm = (MainWindowViewModel) RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        var field = typeof(MainWindowViewModel).GetField("<Settings>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(nameof(MainWindowViewModel), "Settings");
        field.SetValue(vm, settings);
        return vm;
    }

    private static void CheckEditor(string source)
    {
        var global = Settings(true, -22.5, -2.5);
        var snapshot = JsonSerializer.Serialize(global);
        var track = Track(source);
        Check(new EditWindowViewModel(track).UseGlobalNormalization, "The existing editor constructor no longer defaults to inheritance.");
        Check(new BuildItem(track.SoundName, true, source, null, null, null, true).Normalization is null,
            "An existing build constructor unexpectedly created a track override.");
        Check(new ExportItem(source, -1, null, null, 48000, 48000, null, true, "custom", null).Normalization is null,
            "An existing export constructor unexpectedly created a track override.");
        var canceled = new EditWindowViewModel(track, global) { UseGlobalNormalization = false, TargetLufs = -28 };
        Check(track.Normalization is null && !canceled.Saved, "Editing or canceling a normalization draft modified the track.");
        var editor = new EditWindowViewModel(track, global);
        Check(editor.ShowNormalization && editor.UseGlobalNormalization && !editor.CanEditNormalization
            && editor.TargetLufs == -22.5 && editor.TargetTruePeak == -2.5,
            "A new raw track did not display its inherited settings.");
        editor.UseGlobalNormalization = false;
        editor.LoudnessNormalize = true;
        editor.TargetLufs = -26.5;
        editor.TargetTruePeak = -4.5;
        editor.DynamicNormalization = true;
        editor.UseGlobalNormalization = true;
        Check(editor.TargetLufs == -22.5 && editor.TargetTruePeak == -2.5, "Returning to global settings retained the custom draft in the display.");
        editor.UseGlobalNormalization = false;
        Check(editor.TargetLufs == -26.5 && editor.TargetTruePeak == -4.5 && editor.DynamicNormalization,
            "Toggling inheritance discarded the unsaved custom settings.");
        editor.Apply(track);
        var saved = new NormalizationOverride(true, -26.5, -4.5, true);
        Check(track.Normalization == saved, "Saving the editor did not persist its normalization override.");
        var canceledExisting = new EditWindowViewModel(track, global) { TargetLufs = -19 };
        Check(track.Normalization == saved && !canceledExisting.Saved, "Canceling an existing override altered its saved value.");
        var reset = new EditWindowViewModel(track, global) { UseGlobalNormalization = true };
        reset.Apply(track);
        Check(track.Normalization is null, "Saving global inheritance did not clear the override.");
        var invalid = new EditWindowViewModel(track, global)
        {
            UseGlobalNormalization = false, TargetLufs = double.NaN, TargetTruePeak = double.PositiveInfinity,
        };
        invalid.Apply(track);
        Check(track.Normalization is { TargetLufs: -23, TargetTruePeak: -1 }, "Invalid editor values escaped into a track override.");
        foreach (var input in new[] { Track("untouched.fsb"), new TrackItemViewModel(new RadioTrack { SoundName = "ORIGINAL", Origin = TrackOrigin.Original }) })
        {
            input.Normalization = saved;
            var hidden = new EditWindowViewModel(input, global) { UseGlobalNormalization = false, TargetLufs = -17 };
            Check(!hidden.ShowNormalization && !hidden.CanEditNormalization, "Encoded FSB or original audio exposed normalization controls.");
            hidden.Apply(input);
            Check(input.Normalization == saved, "Editing encoded FSB or original audio changed its normalization metadata.");
        }
        var changedSource = Track(source);
        var stale = new EditWindowViewModel(changedSource, global) { UseGlobalNormalization = false, TargetLufs = -19 };
        changedSource.SourcePath = "changed.fsb";
        stale.Apply(changedSource);
        Check(changedSource.Normalization is null, "A stale raw-audio editor applied normalization after the source became an FSB.");
        Check(JsonSerializer.Serialize(global) == snapshot, "Editing a track modified global settings.");
    }

    private static async Task CheckInheritedPreviewAsync(string source)
    {
        var global = Settings(true, -23, -1);
        var vm = Preview(global);
        var inherited = Track(source, 101);
        var first = await vm.LoadPeaksAsync(inherited);
        global.TargetLufs = -27;
        var second = await vm.LoadPeaksAsync(inherited);
        Check(first.Wav is not null && second.Wav is not null && first.Wav != second.Wav,
            "A track inheriting global options reused its old preview after the global target changed.");
        Check(AudioBuildValidation.Compare(first.Wav!, second.Wav!).GainDb.All(g => Math.Abs(g + 4) < 0.02),
            "An inherited preview did not follow the changed global loudness target.");
        inherited.Normalization = new(true, -25, -2);
        var ownFirst = await vm.LoadPeaksAsync(inherited);
        global.LoudnessNormalize = false;
        global.TargetLufs = -18;
        var ownSecond = await vm.LoadPeaksAsync(inherited);
        Check(ownFirst.Wav is not null && ownSecond.Wav is not null
            && AudioBuildValidation.Inspect(ownFirst.Wav).PcmSha256 == AudioBuildValidation.Inspect(ownSecond.Wav).PcmSha256,
            "Changing globals altered a preview with its own normalization override.");
        inherited.Normalization = null;
        var reset = await vm.LoadPeaksAsync(inherited);
        Check(reset.Wav is not null && AudioBuildValidation.Compare(source, reset.Wav).GainDb.All(g => Math.Abs(g) < 0.01),
            "Resetting a track to inheritance did not follow the current global normalization-off option.");
    }

    private static async Task<string> CheckMixedBankAsync(string temp, string prefix, Case[] cases, AppSettings global, Action<string> writeEmptyBank)
    {
        var empty = Path.Combine(temp, prefix + ".empty.bank");
        var output = Path.Combine(temp, prefix + ".bank");
        writeEmptyBank(empty);
        var items = cases.Select((c, i) => new BuildItem(Naming.MakeSoundName(110 + i), true, c.Input, c.Name, null,
            c.Gain, true, Normalization: c.Override)).ToArray();
        var log = new ConcurrentQueue<string>();
        await BankBuildService.BuildToFileAsync(empty, output, items, global, log.Enqueue);
        var vm = Preview(global);
        for (var i = 0; i < cases.Length; i++)
        {
            var test = cases[i];
            var expected = Path.Combine(temp, prefix + "-" + test.Name + ".wav");
            await Loudnorm.RenderAsync(test.Input, expected, test.Expected, test.Gain);
            var expectedInfo = AudioBuildValidation.Inspect(expected);
            const string marker = "wav.prepared ";
            var preparation = log.Single(l => l.Contains($"[track={i} ", StringComparison.Ordinal) && l.Contains(marker, StringComparison.Ordinal));
            using var document = JsonDocument.Parse(preparation[(preparation.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..]);
            Check(document.RootElement.GetProperty("PcmSha256").GetString() == expectedInfo.PcmSha256,
                $"Bank encoder input ignored the effective {test.Name} track settings.");
            var track = Track(test.Input, 110 + i);
            track.Normalization = test.Override;
            track.GainDb = test.Gain;
            var preview = await vm.LoadPeaksAsync(track);
            Check(preview.Wav is not null && AudioBuildValidation.Inspect(preview.Wav).PcmSha256 == expectedInfo.PcmSha256,
                $"The {test.Name} track preview differs from its actual encoder input.");
            var decoded = AudioDecoder.DecodeBank(output, i);
            var comparison = AudioBuildValidation.Compare(expected, decoded);
            Check(comparison.ChannelCorrelation.All(c => c > 0.98) && comparison.GainDb.All(g => Math.Abs(g) < 0.5),
                $"FSB encoding lost the effective {test.Name} track processing.");
        }
        return output;
    }

    private static async Task CheckEncodedPreservationAsync(string temp, string bank, Action<string> writeEmptyBank)
    {
        var single = Path.Combine(temp, "override-encoded.fsb");
        File.WriteAllBytes(single, TrackPack.ExtractSampleFsb(bank, 0));
        var empty = Path.Combine(temp, "override-encoded.empty.bank");
        var output = Path.Combine(temp, "override-encoded.bank");
        writeEmptyBank(empty);
        var requested = new NormalizationOverride(true, -9, -9, true);
        var item = new BuildItem(Naming.MakeSoundName(130), true, single, "Encoded", null, 10, true, Normalization: requested);
        await BankBuildService.BuildToFileAsync(empty, output, [item], Settings(true, -9, -1));
        Check(Fsb5.Parse(new FevBank(File.ReadAllBytes(output)).ExtractFsb5()).Samples.Single().Data.AsSpan()
            .SequenceEqual(Fsb5.Parse(File.ReadAllBytes(single)).Samples.Single().Data), "An FSB import reapplied a normalization override.");
        var original = Fsb5.Parse(new FevBank(File.ReadAllBytes(bank)).ExtractFsb5()).Samples;
        var kept = original.Select((_, i) => new BuildItem(Naming.MakeSoundName(110 + i), false, null, null, null, 10, true,
            Normalization: requested)).ToArray();
        var repeated = Path.Combine(temp, "override-retained.bank");
        await BankBuildService.BuildToFileAsync(bank, repeated, kept, Settings(true, -9, -1));
        var copied = Fsb5.Parse(new FevBank(File.ReadAllBytes(repeated)).ExtractFsb5()).Samples;
        Check(original.Count == copied.Count && original.Zip(copied).All(p => p.First.Data.AsSpan().SequenceEqual(p.Second.Data)),
            "Retained bank audio reapplied normalization or gain.");
    }

    private static async Task CheckPacksAsync(string temp, string source)
    {
        var path = Path.Combine(temp, "override-export.fhre");
        var settings = new NormalizationOverride(true, -25.5, -3.5, true);
        await TrackPack.ExportAsync(path, "Regression", null,
            [new ExportItem(source, -1, "Override", null, 48000 * 8, 48000, -2, true, "custom", null, settings),
             new ExportItem(source, -1, "Inherited", null, 48000 * 8, 48000, null, true, "custom", null)], null, null, CancellationToken.None);
        var imported = TrackPack.Load(path);
        try
        {
            Check(imported.Manifest.Tracks[0].Normalization == settings && imported.Manifest.Tracks[1].Normalization is null,
                "Raw export/import lost the override or converted inheritance into a snapshot.");
            var method = typeof(MainWindowViewModel).GetMethod("SetImportedAudio", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException(nameof(MainWindowViewModel), "SetImportedAudio");
            var track = Track(source);
            method.Invoke(null, [track, imported.Manifest.Tracks[0], Path.Combine(imported.Dir, imported.Manifest.Tracks[0].File)]);
            Check(track.Normalization == settings, "Applying an imported raw track discarded its normalization override.");
            Check(File.ReadAllBytes(Path.Combine(imported.Dir, imported.Manifest.Tracks[0].File)).AsSpan().SequenceEqual(File.ReadAllBytes(source)),
                "A raw track pack changed the original audio bytes.");
            method.Invoke(null, [track, imported.Manifest.Tracks[1], Path.Combine(imported.Dir, imported.Manifest.Tracks[1].File)]);
            Check(track.Normalization is null, "Importing an inherited track retained the previous track override.");
            method.Invoke(null, [track, imported.Manifest.Tracks[0], "encoded.fsb"]);
            Check(track.Normalization is null, "Importing encoded FSB audio activated a raw-audio normalization override.");
        }
        finally { DeleteImport(imported.Dir); }
        const string legacy = """{"format":"FH6RB-EXPORT","version":1,"tracks":[{"id":"t0","file":"audio/t0.wav"}]}""";
        var legacyPath = Path.Combine(temp, "override-legacy.fhre");
        WritePack(legacyPath, legacy, source);
        var old = TrackPack.Load(legacyPath);
        try { Check(old.Manifest.Tracks.Single().Normalization is null, "A legacy manifest stopped inheriting global normalization."); }
        finally { DeleteImport(old.Dir); }
        var backup = JsonSerializer.Deserialize<BackupManifest>(JsonSerializer.Serialize(new BackupManifest
        {
            Tracks = [new BackupTrack { SoundName = "OWN", Normalization = settings }, new BackupTrack { SoundName = "GLOBAL" }],
        }))!;
        Check(backup.Tracks[0].Normalization == settings && backup.Tracks[1].Normalization is null,
            "Backup metadata lost per-track options or changed inheritance.");
        Check(JsonSerializer.Deserialize<BackupTrack>("{\"SoundName\":\"LEGACY\"}")!.Normalization is null,
            "A legacy backup track unexpectedly acquired a normalization override.");
        Check(new BackupTrackSource("LEGACY", true, false, false, source, null, -1, null, null, 48000, 48000, null, true, null).Normalization is null,
            "An existing backup-source constructor stopped defaulting to inheritance.");
        string[] invalid =
        [
            "\"targetLufs\":-31", "\"targetLufs\":-8", "\"targetTruePeak\":-10", "\"targetTruePeak\":0.5",
            "\"targetLufs\":\"NaN\"", "\"targetTruePeak\":\"Infinity\"", "\"targetLufs\":1e999",
        ];
        for (var i = 0; i < invalid.Length; i++)
        {
            var invalidPath = Path.Combine(temp, $"override-invalid-{i}.fhre");
            WritePack(invalidPath, legacy.Replace("\"file\":\"audio/t0.wav\"", "\"file\":\"audio/t0.wav\",\"normalization\":{" + invalid[i] + "}"), source);
            var rejected = false;
            try { var unexpected = TrackPack.Load(invalidPath); DeleteImport(unexpected.Dir); }
            catch (InvalidDataException) { rejected = true; }
            catch (JsonException) { rejected = true; }
            Check(rejected, "A pack with invalid normalization values was accepted: " + invalid[i]);
        }
    }

    private static void WritePack(string path, string manifest, string source)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(source, "audio/t0.wav");
        using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
        writer.Write(manifest);
    }

    private static void DeleteImport(string path)
    {
        var root = Path.GetFullPath(Path.Combine(AppPaths.TempBase, "import")) + Path.DirectorySeparatorChar;
        var directory = Path.GetFullPath(path);
        Check(directory.StartsWith(root, StringComparison.OrdinalIgnoreCase), "Imported fixture directory is outside the import root.");
        Directory.Delete(directory, recursive: true);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
