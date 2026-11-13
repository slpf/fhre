using System.IO.Compression;
using System.Text.Json;
using FH6RB.Core;
using FH6RB.Services;
using FH6RB.ViewModels;

internal static class LimiterSettingsChecks
{
    public static async Task RunAsync(string temp)
    {
        CheckMigration();
        var source = Path.Combine(temp, "limiter-options-source.wav");
        NormalizationChecks.WriteSignal(source, 48000, 2, 8, limited: false, transients: false);
        CheckEditors(source);
        var enabled = new NormalizationOverride(true, -23, -1, true);
        var path = Path.Combine(temp, "limiter-options.fhre");
        await TrackPack.ExportAsync(path, "Regression", null,
            [new ExportItem(source, -1, "Limiter", null, 48000 * 8, 48000, null, true, "custom", null, enabled)],
            null, null, CancellationToken.None);
        using (var zip = ZipFile.OpenRead(path))
        using (var input = zip.GetEntry("manifest.json")!.Open())
        using (var document = JsonDocument.Parse(input))
        {
            var normalization = document.RootElement.GetProperty("tracks")[0].GetProperty("normalization");
            Check(normalization.GetProperty("peakLimiter").GetBoolean() && normalization.EnumerateObject().Count() == 4,
                "New track packs do not contain exactly the current normalization settings.");
        }
        CheckImport(path, enabled);
        var legacy = Path.Combine(temp, "limiter-options-legacy.fhre");
        using (var zip = ZipFile.Open(legacy, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(source, "audio/old.wav");
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            writer.Write("""{"format":"FH6RB-EXPORT","version":1,"tracks":[{"id":"old","file":"audio/old.wav","normalization":{"dynamicNormalization":true,"twoPassDynamicNormalization":false}}]}""");
        }
        CheckImport(legacy, enabled);
        Console.WriteLine("Verified limiter defaults, legacy settings/pack/backup migration, explicit-field precedence, snapshots and dependent editor state.");
    }

    private static void CheckMigration()
    {
        Check(!new AppSettings().PeakLimiter && !new NormalizationOverride().PeakLimiter,
            "The peak limiter is enabled by default.");
        var cases = new (string Settings, string Track, bool Expected)[]
        {
            ("{}", "{}", false),
            ("{\"DynamicNormalization\":true}", "{\"dynamicNormalization\":true}", true),
            ("{\"DynamicNormalization\":true,\"PeakLimiter\":false}", "{\"dynamicNormalization\":true,\"peakLimiter\":false}", false),
            ("{\"PeakLimiter\":false,\"DynamicNormalization\":true}", "{\"peakLimiter\":false,\"dynamicNormalization\":true}", false),
            ("{\"DynamicNormalization\":false,\"PeakLimiter\":true}", "{\"dynamicNormalization\":false,\"peakLimiter\":true}", true),
            ("{\"PeakLimiter\":true,\"DynamicNormalization\":false}", "{\"peakLimiter\":true,\"dynamicNormalization\":false}", true),
            ("{\"TwoPassDynamicNormalization\":true}", "{\"twoPassDynamicNormalization\":true}", false),
            ("{\"DynamicNormalization\":true,\"TwoPassDynamicNormalization\":false}", "{\"dynamicNormalization\":true,\"twoPassDynamicNormalization\":false}", true),
        };
        foreach (var test in cases)
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(test.Settings)!;
            var track = JsonSerializer.Deserialize<NormalizationOverride>(test.Track)!;
            Check(settings.PeakLimiter == test.Expected && track.PeakLimiter == test.Expected,
                "Legacy settings override an explicit limiter choice or changed their migration default.");
            var settingsJson = JsonSerializer.Serialize(settings);
            var trackJson = JsonSerializer.Serialize(track);
            Check(!settingsJson.Contains("DynamicNormalization", StringComparison.Ordinal)
                && !trackJson.Contains("dynamicNormalization", StringComparison.Ordinal)
                && JsonSerializer.Deserialize<AppSettings>(settingsJson)!.PeakLimiter == test.Expected
                && JsonSerializer.Deserialize<NormalizationOverride>(trackJson)!.PeakLimiter == test.Expected,
                "Saving migrated options wrote removed processing flags or changed the limiter choice.");
        }
        var global = new AppSettings { PeakLimiter = false };
        var inherited = NormalizationOverride.Resolve(global);
        global.PeakLimiter = true;
        Check(!inherited.PeakLimiter && NormalizationOverride.Resolve(global).PeakLimiter
            && !new NormalizationOverride(PeakLimiter: false).ApplyTo(global).PeakLimiter && global.PeakLimiter,
            "Resolving track options changed globals or retained a mutable settings reference.");
        var backup = JsonSerializer.Deserialize<BackupTrack>("{\"SoundName\":\"OLD\",\"Normalization\":{\"dynamicNormalization\":true}}")!;
        var saved = JsonSerializer.Serialize(backup);
        Check(backup.Normalization!.PeakLimiter && !saved.Contains("dynamicNormalization", StringComparison.Ordinal)
            && JsonSerializer.Deserialize<BackupTrack>(saved)!.Normalization!.PeakLimiter,
            "Backup migration lost the peak limiter or wrote removed options.");
        foreach (var json in new[] { "{\"peakLimiter\":\"true\"}", "{\"dynamicNormalization\":1}", "{\"targetLufs\":1e999}" })
        {
            var rejected = false;
            try { _ = JsonSerializer.Deserialize<NormalizationOverride>(json); }
            catch (JsonException) { rejected = true; }
            Check(rejected, "Malformed normalization options were accepted: " + json);
        }
    }

    private static void CheckEditors(string source)
    {
        var global = new AppSettings { PeakLimiter = true };
        var settings = new SettingsWindowViewModel(global);
        var notifications = new List<string?>();
        settings.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        Check(settings.CanEditPeakLimiter && settings.PeakLimiter, "Global settings ignored the saved limiter option.");
        settings.LoudnessNormalize = false;
        Check(!settings.CanEditPeakLimiter && settings.PeakLimiter, "Disabling normalization discarded its limiter choice.");
        settings.LoudnessNormalize = true;
        settings.PeakLimiter = false;
        Check(settings.CanEditPeakLimiter && global.PeakLimiter
            && notifications.Count(name => name == nameof(settings.CanEditPeakLimiter)) == 2,
            "The limiter draft changed saved globals or failed to notify dependent control state.");
        var track = new TrackItemViewModel(new RadioTrack
        {
            SoundName = Naming.MakeSoundName(221), Origin = TrackOrigin.Custom, SampleRate = 48000, SampleLength = 48000 * 8,
        }) { SourcePath = source };
        var editor = new EditWindowViewModel(track, global);
        Check(editor.UseGlobalNormalization && !editor.CanEditPeakLimiter && editor.PeakLimiter,
            "An inherited track exposed independent limiter editing.");
        editor.UseGlobalNormalization = false;
        editor.PeakLimiter = false;
        editor.LoudnessNormalize = false;
        Check(!editor.CanEditPeakLimiter && !editor.PeakLimiter, "Disabling a track discarded its limiter draft.");
        editor.LoudnessNormalize = true;
        editor.UseGlobalNormalization = true;
        Check(editor.PeakLimiter && !editor.CanEditPeakLimiter, "Returning to globals did not display their limiter choice.");
        editor.UseGlobalNormalization = false;
        Check(!editor.PeakLimiter && editor.CanEditPeakLimiter, "Returning to a custom draft lost its limiter choice.");
        editor.Apply(track);
        Check(track.Normalization is { PeakLimiter: false }, "Saving a track lost its limiter override.");
        var cancelled = new EditWindowViewModel(track, global) { PeakLimiter = true };
        Check(!cancelled.Saved && !track.Normalization!.PeakLimiter, "Canceling the editor changed the saved limiter option.");
        var inherited = new EditWindowViewModel(track, global) { UseGlobalNormalization = true };
        inherited.Apply(track);
        Check(track.Normalization is null && global.PeakLimiter, "Restoring inheritance changed the global limiter choice.");
        var encoded = new TrackItemViewModel(new RadioTrack { SoundName = "ENCODED", Origin = TrackOrigin.Custom }) { SourcePath = "encoded.fsb" };
        Check(!new EditWindowViewModel(encoded, global).CanEditPeakLimiter, "Encoded FSB audio exposed limiter controls.");
    }

    private static void CheckImport(string path, NormalizationOverride expected)
    {
        var imported = TrackPack.Load(path);
        try { Check(imported.Manifest.Tracks.Single().Normalization == expected, "Importing a track pack lost or changed its limiter settings."); }
        finally
        {
            var root = Path.GetFullPath(Path.Combine(AppPaths.TempBase, "import")) + Path.DirectorySeparatorChar;
            var directory = Path.GetFullPath(imported.Dir);
            Check(directory.StartsWith(root, StringComparison.OrdinalIgnoreCase), "Imported fixture escaped its import directory.");
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
