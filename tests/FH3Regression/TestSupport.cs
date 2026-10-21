using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Xml.Linq;
using FH6RB.Collections;
using FH6RB.Core;
using FH6RB.Services;
using FH6RB.ViewModels;

internal static class TestSupport
{
    public static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void DeleteDirectory(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Check(full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase), "Refusing cleanup outside the test root: " + full);
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }

    public static MainWindowViewModel CreateVm(string game, string language)
    {
        var vm = (MainWindowViewModel) RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        Set(vm, "<Settings>k__BackingField", new AppSettings { GamePath = game, LoudnessNormalize = false });
        Set(vm, "<Languages>k__BackingField", new ObservableCollection<LangOption> { new("EN", language) });
        Set(vm, "<Stations>k__BackingField", new ObservableCollection<StationInfo>());
        Set(vm, "<Variants>k__BackingField", new ObservableCollection<string>());
        Set(vm, "<Tracks>k__BackingField", new ObservableCollection<TrackItemViewModel>());
        Set(vm, "<FilteredTracks>k__BackingField", new RangeObservableCollection<TrackItemViewModel>());
        Set(vm, "_sourceLocks", new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase));
        Set(vm, "_suppressReload", true);
        Set(vm, "_selectedLanguage", new LangOption("EN", language));
        Method("RebuildStations").Invoke(vm, null);
        return vm;
    }

    public static void ReleaseVm(MainWindowViewModel vm)
    {
        var locks = (Dictionary<string, FileStream>) typeof(MainWindowViewModel).GetField("_sourceLocks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
        foreach (var stream in locks.Values) stream.Dispose();
        locks.Clear();
    }

    public static void Set(MainWindowViewModel vm, string name, object value)
    {
        var field = typeof(MainWindowViewModel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(nameof(MainWindowViewModel), name);
        field.SetValue(vm, value);
    }

    public static MethodInfo Method(string name) => typeof(MainWindowViewModel).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(MainWindowViewModel), name);

    public static void Apply(RadioInfo radio, StationInfo station, string bank, IReadOnlyList<AddedSample> added, List<BuildItem> items)
        => Method("ApplyRadioInfo").Invoke(null, [radio, station, bank, added, items]);

    public static BuildItem Item(RadioTrack track) => new(track.SoundName, false, null, track.DisplayName, track.Artist, null,
        track.Enabled, track.Markers, MarkerSampleRate: track.SampleRate, BankId: track.BankId);

    public static Dictionary<ulong, (long Frames, int Rate, int Channels, string Hash)> BankState(string path)
    {
        var skeleton = FevBank.ReadSkeleton(path);
        var layout = Fsb5.ReadLayout(skeleton.Fsb5HeaderRegion).Samples;
        return skeleton.Stbl.ToDictionary(e => e.Id, e =>
        {
            var sample = layout[e.Index];
            return (sample.Frames, sample.SampleRate, sample.Channels, Hash(path, skeleton.DataStartAbs + sample.DataOff, sample.DataLen));
        });
    }

    public static string Hash(string path, long offset = 0, long? length = null)
    {
        using var input = File.OpenRead(path);
        input.Position = offset;
        var remaining = length ?? input.Length;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        while (remaining > 0)
        {
            var count = input.Read(buffer, 0, (int) Math.Min(remaining, buffer.Length));
            if (count == 0) throw new EndOfStreamException();
            hash.AppendData(buffer, 0, count);
            remaining -= count;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static string Playlists(RadioInfo radio, int station = 1) => string.Join("\n", radio.Document.Descendants("RadioStation")
        .Single(s => (int?) s.Attribute("Number") == station).Elements().Where(e => e.Name.LocalName is "PlayList" or "Playlist")
        .Select(e => e.ToString(SaveOptions.DisableFormatting)));

    public static void WriteWave(string path, double seconds = 3.013)
    {
        var result = Proc.Run(Tools.FfmpegPath,
            $"-y -hide_banner -loglevel error -f lavfi -i \"sine=frequency=521:sample_rate=44100:duration={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}\" -ac 2 -c:a pcm_s16le \"{path}\"");
        Check(result.ExitCode == 0, "Could not generate audio fixture: " + result.StdErr);
    }
}
