using System.Reflection;
using FH6RB.Core;
using FH6RB.Services;
using FH6RB.ViewModels;
using NAudio.Wave;

internal static class MarkerEditingChecks
{
    public static void Run(string temp)
    {
        CheckMarkers();
        CheckExistingMarkers();
        CheckPresetCollision();
        CheckCancelledSearch();
        CheckLoopPlayback(temp);
        Console.WriteLine("Verified marker bounds and locks, preset round trips and collisions, loop cancellation, and audio loop sample boundaries.");
    }

    private static void CheckExistingMarkers()
    {
        var track = new TrackItemViewModel(new RadioTrack
        {
            SoundName = "MARKER_PRESERVATION",
            Origin = TrackOrigin.Original,
            SampleRate = 48000,
            SampleLength = 480000,
            Markers = new Dictionary<string, long> { ["DJDrop"] = 30000, ["BinkTransition"] = 60000 },
        });
        var original = track.Markers!;
        var editor = new EditWindowViewModel(track) { Title = "Renamed" };
        editor.Apply(track);
        Check(track.Title == "Renamed" && ReferenceEquals(track.Markers, original),
            "Saving track metadata rewrote its markers.");

        editor.AllMarkers.Single(f => f.Name == "DJDrop").Position = 36000;
        editor.Apply(track);
        Check(track.Markers is { Count: 2 } && track.Markers["DJDrop"] == 36000 && track.Markers["BinkTransition"] == 60000,
            "Editing a marker rewrote hidden markers or inserted untouched defaults.");
    }

    private static void CheckMarkers()
    {
        var track = new TrackItemViewModel(new RadioTrack
        {
            SoundName = "MARKER_TEST",
            Origin = TrackOrigin.Original,
            SampleRate = 48000,
            SampleLength = 480000,
        });
        var vm = new EditWindowViewModel(track);
        var field = vm.AllMarkers.Single(f => f.Name == "DJDrop");
        field.SecondsText = "60";
        Check(field.Position == track.SampleLength - 1, "Seconds bypassed the last sample boundary.");
        field.SecondsText = "-1";
        Check(field.Position == 432000, "Negative seconds no longer count back from the end of the track.");
        field.SecondsText = "Infinity";
        Check(field.Position == 432000, "Non-finite marker input changed the position.");
        field.SecondsText = "";
        var saved = vm.CollectMarkerPositions();
        field.Position = 1234;
        vm.ApplyMarkerPositions(saved, track.SampleRate);
        Check(field.IsOff, "Loading a preset did not restore a disabled marker.");

        field.Position = 1234;
        field.Locked = true;
        vm.ApplyMarkerPositions(saved, track.SampleRate);
        vm.ResetMarkersToDefaults();
        field.SecondsText = "2";
        field.Reset();
        field.Revert();
        Check(field.Position == 1234, "A marker change bypassed the lock.");

        var millisecondField = new MarkerField { SampleLength = 480000, SampleRate = 48000, UsesMilliseconds = true };
        millisecondField.SecondsText = "100%";
        Check(millisecondField.Position == 480000, "FH2 could not put its end marker at the full duration.");
    }

    private static void CheckPresetCollision()
    {
        var directory = "regression-" + Guid.NewGuid().ToString("N");
        try
        {
            var first = MarkerPresetService.SaveIn(directory, "A.B", 48000, new Dictionary<string, long> { ["End"] = 111 });
            var second = MarkerPresetService.SaveIn(directory, "AB", 48000, new Dictionary<string, long> { ["End"] = 222 });
            Check(first != second && MarkerPresetService.LoadByPath(first)?.Markers["End"] == 111
                && MarkerPresetService.LoadByPath(second)?.Markers["End"] == 222, "Two presets with the same file name replaced each other.");
        }
        finally
        {
            MarkerPresetService.DeleteFolder(directory);
        }
    }

    private static void CheckCancelledSearch()
    {
        const int rate = 8000;
        var samples = new float[rate * 20];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(0.5 * Math.Sin(i * 0.2));
        }

        var options = new LoopSearchOptions { AutoTune = false, MinLoopSeconds = 2 };
        const string key = "loop-cancellation";
        using var cancel = new CancellationTokenSource();
        try
        {
            LoopFinder.Find(samples, rate, options, key, line =>
            {
                if (line.Contains("nominators", StringComparison.Ordinal)) cancel.Cancel();
            }, ct: cancel.Token);
            throw new InvalidOperationException("Cancellation did not stop the loop search.");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
        }

        var retried = LoopFinder.Find(samples, rate, options, key);
        LoopFinder.ClearCache();
        var fresh = LoopFinder.Find(samples, rate, options);
        Check(fresh.Count > 0 && retried.Select(p => (p.LoopStart, p.LoopEnd)).SequenceEqual(fresh.Select(p => (p.LoopStart, p.LoopEnd))),
            "Retry reused an incomplete loop search from cache.");
    }

    private static void CheckLoopPlayback(string temp)
    {
        var path = Path.Combine(temp, "preview-loop.wav");
        using (var writer = new WaveFileWriter(path, new WaveFormat(8000, 16, 1)))
        {
            foreach (var sample in new[] { 0.125f, 0.25f, 0.375f, 0.5f, 0.625f, 0.75f }) writer.WriteSample(sample);
        }

        using var reader = new AudioFileReader(path);
        var source = new float[6];
        Check(reader.Read(source, 0, source.Length) == source.Length, "The preview test signal was truncated.");
        var align = reader.WaveFormat.BlockAlign;
        reader.Position = 0;
        var providerType = typeof(PlaybackService).GetNestedType("LoopSampleProvider", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The playback sample provider was not found.");
        var provider = (ISampleProvider)Activator.CreateInstance(providerType, reader)!;
        providerType.GetMethod("SetLoop")!.Invoke(provider, [(long)align, 4L * align]);
        var actual = new float[8];
        float[] expected = [source[0], source[1], source[2], source[3], source[1], source[2], source[3], source[1]];
        Check(provider.Read(actual, 0, actual.Length) == actual.Length && actual.SequenceEqual(expected),
            "The first playback buffer crossed the loop end before wrapping.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
