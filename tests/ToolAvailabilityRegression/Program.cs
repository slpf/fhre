using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using FH6RB.Assets;
using FH6RB.Core;
using FH6RB.Services;
using FH6RB.ViewModels;

var paths = new[] { Tools.FfmpegPath, Tools.FfprobePath, Tools.FsbankclPath, Tools.VgmstreamPath };
var names = new[] { "ffmpeg", "ffprobe", "fsbankcl", "vgmstream" };
var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
try
{
    var output = Path.GetFullPath(AppContext.BaseDirectory);
    Check(output.Split(Path.DirectorySeparatorChar).Contains("ToolAvailabilityRegression", StringComparer.OrdinalIgnoreCase),
        "Tool availability tests require their own isolated project output.");
    foreach (var path in paths)
    {
        Check(Path.GetFullPath(path).StartsWith(output, StringComparison.OrdinalIgnoreCase), "A tool fixture path escaped the isolated output.");
        Check(!File.Exists(path), "Refusing to alter an existing tool: " + path);
    }
    var tracks = CreateTracks();
    var vm = CreateMain(tracks.Select(t => t.Track));
    var backupRows = new[]
    {
        new BackupRow { Entry = new BackupEntry("unopened-compatible", new BackupManifest()), CanRestore = true },
        new BackupRow { Entry = new BackupEntry("unopened-incompatible", new BackupManifest()), CanRestore = false },
    };
    for (var mask = 0; mask < 16; mask++)
    {
        SetTools(mask);
        var ffmpeg = (mask & 1) != 0;
        var ffprobe = (mask & 2) != 0;
        var fsbank = (mask & 4) != 0;
        var vgmstream = (mask & 8) != 0;
        Check(Tools.HasFfmpeg == ffmpeg && Tools.HasFfprobe == ffprobe
            && Tools.HasFsbankcl == fsbank && Tools.HasVgmstream == vgmstream, $"Tool presence flags are incorrect for combination {mask}.");
        Check(Tools.CanAddTracks == (ffmpeg && ffprobe), $"Adding tracks has incorrect dependencies for combination {mask}.");
        Check(Tools.CanBuild == (ffmpeg && fsbank && vgmstream), $"Building has incorrect dependencies for combination {mask}.");
        var missing = names.Where((_, i) => (mask & 1 << i) == 0).ToArray();
        CheckSet(Tools.MissingRequired(), missing, "All required tools");
        CheckSet(Tools.MissingForAddTracks(), missing.Where(n => n is "ffmpeg" or "ffprobe"), "Add dependencies");
        CheckSet(Tools.MissingForBuild(), missing.Where(n => n != "ffprobe"), "Build dependencies");
        Check(vm.CanAddTracks == Tools.CanAddTracks && vm.CanBuild == Tools.CanBuild,
            $"Main-window actions ignored tool presence for combination {mask}.");
        CheckTip(vm.AddTracksTip, Str.TipAddTrack, Tools.MissingForAddTracks());
        CheckTip(vm.BuildTip, Str.TipBuildBank, Tools.MissingForBuild());
        Check(backupRows[0].CanRestoreNow == Tools.CanBuild && !backupRows[1].CanRestoreNow,
            $"Backup restore ignored tools or game compatibility for combination {mask}.");
        foreach (var row in backupRows) CheckTip(row.RestoreTip, Str.BtnRestore, Tools.MissingForBuild());
        foreach (var (track, raw) in tracks)
        {
            var hasDecoder = raw ? ffmpeg : vgmstream;
            Check(track.CanPlay == hasDecoder && track.CanEditMarkers == hasDecoder,
                $"Track {track.SoundName} selected the wrong decoder for combination {mask}.");
            Check(track.CanReplace == (ffmpeg && ffprobe), $"Replacement ignored metadata dependencies for {track.SoundName}.");
            CheckTip(track.PlayTip, Str.TipPlay, hasDecoder ? [] : [raw ? "ffmpeg" : "vgmstream"]);
            CheckTip(track.MarkersTip, Str.TipMarkers, hasDecoder ? [] : [raw ? "ffmpeg" : "vgmstream"]);
            CheckTip(track.ReplaceTip, Str.TipReplaceAudio, Tools.MissingForAddTracks());
            track.MarkersLoading = true;
            Check(!track.CanEditMarkers, "An active marker load allowed a second marker edit.");
            track.MarkersLoading = false;
            Check(track.CanEditMarkers == hasDecoder, "Ending a marker load did not restore decoder-based availability.");
        }
        if (!Tools.CanBuild)
        {
            var guarded = CreateBuildMain(20);
            await guarded.BuildAsync();
            Check(!guarded.IsBuilding && Tools.MissingForBuild().All(n => guarded.Status.Contains(n, StringComparison.OrdinalIgnoreCase)),
                "Direct bank build did not report missing tools before reading game settings.");
            var restoreRejected = false;
            try { await StationBackupService.RestoreAsync(null!, null!, null!); }
            catch (InvalidOperationException ex)
            {
                restoreRejected = ex.Message.StartsWith("Backup restore: missing ", StringComparison.Ordinal)
                    && Tools.MissingForBuild().All(n => ex.Message.Contains(n, StringComparison.OrdinalIgnoreCase));
            }
            Check(restoreRejected, "Backup restore touched the entry or settings before reporting missing build tools.");
        }
    }
    SetTools(15);
    TestBusyAndPlaylist(vm);
    SetTools(0);
    TestNotifications(vm, tracks.Select(t => t.Track).ToArray(), () => SetTools(15));
    SetTools(0);
    TestBackupNotifications(backupRows, () => SetTools(15));
    TestSourceTransitions();
    var low = CreateBuildMain(19);
    await low.BuildAsync();
    Check(low.PlaylistLow && !low.IsBuilding && low.Status == low.PlaylistTip, "Direct build bypassed the minimum playlist guard.");
    var busy = CreateBuildMain(20);
    busy.IsExporting = true;
    await busy.BuildAsync();
    Check(busy.IsExporting && !busy.IsBuilding, "A direct build started while another operation was active.");
    SetTools(0);
    var noSettings = CreateMain([]);
    foreach (var (track, _) in tracks)
    {
        var result = await noSettings.LoadPeaksAsync(track);
        Check(result.Peaks is null && result.Wav is null && noSettings.Status == track.MarkersTip,
            "Waveform loading touched decode settings before rejecting a missing decoder.");
        await noSettings.PlayTrackCommand.ExecuteAsync(track);
        Check(track.PlayState == TrackPlayState.Idle && noSettings.NowPlaying is null, "Playback started with no decoder available.");
    }
    Console.WriteLine("Tool availability checks passed: 16 combinations, raw/replacement/FSB/bank/restore decoders, compatible/incompatible backups, missing-tool tips, refresh notifications, busy/playlist and direct-call guards.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
finally
{
    foreach (var path in created)
    {
        if (File.Exists(path))
        {
            Check(new FileInfo(path).Length == 0, "Refusing to remove a nonempty file from the tool fixture output: " + path);
            File.Delete(path);
        }
    }
}

void SetTools(int mask)
{
    for (var i = 0; i < paths.Length; i++)
    {
        var path = paths[i];
        if ((mask & 1 << i) != 0)
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var file = new FileStream(path, FileMode.CreateNew);
                created.Add(path);
            }
            Check(created.Contains(path) && new FileInfo(path).Length == 0, "A tool placeholder was replaced by an unexpected file.");
        }
        else if (created.Contains(path) && File.Exists(path))
        {
            Check(new FileInfo(path).Length == 0, "Refusing to remove a nonempty tool fixture: " + path);
            File.Delete(path);
        }
    }
}

static (TrackItemViewModel Track, bool Raw)[] CreateTracks()
{
    var raw = Track("RAW", TrackOrigin.Custom);
    raw.SourcePath = "unopened.wav";
    var replacement = Track("REPLACEMENT");
    replacement.ReplacementPath = "unopened.mp3";
    var fsb = Track("FSB", TrackOrigin.Custom);
    fsb.SourcePath = "unopened.FSB";
    var bank = Track("BANK");
    var restore = Track("RESTORE");
    restore.SubIndex = -1;
    restore.RestoreFsbPath = "unopened-restore.fsb";
    return [(raw, true), (replacement, true), (fsb, false), (bank, false), (restore, false)];
}

static TrackItemViewModel Track(string name, TrackOrigin origin = TrackOrigin.Original) => new(new RadioTrack
{
    SoundName = name, Origin = origin, SubIndex = 0, SampleRate = 48000, SampleLength = 48000, Enabled = true,
});

static MainWindowViewModel CreateMain(IEnumerable<TrackItemViewModel> tracks)
{
    var vm = (MainWindowViewModel) RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
    SetField(vm, "<Tracks>k__BackingField", new ObservableCollection<TrackItemViewModel>(tracks));
    return vm;
}

static MainWindowViewModel CreateBuildMain(int count)
{
    var vm = CreateMain(Enumerable.Range(0, count).Select(i => Track("GUARDED_" + i)));
    SetField(vm, "<Settings>k__BackingField", new AppSettings());
    SetField(vm, "_selectedStation", new StationInfo(1, "Test", ["Main"], new Dictionary<string, string> { ["Main"] = "Unopened" }));
    SetField(vm, "_selectedLanguage", new LangOption("EN", "RadioInfo_EN.xml"));
    SetField(vm, "_selectedVariant", "Main");
    return vm;
}

static void SetField(MainWindowViewModel vm, string name, object value)
{
    var field = typeof(MainWindowViewModel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(nameof(MainWindowViewModel), name);
    field.SetValue(vm, value);
}

static void TestBusyAndPlaylist(MainWindowViewModel vm)
{
    Check(vm.CanAddTracks && vm.CanBuild, "Complete tools did not enable idle actions.");
    foreach (var name in new[] { "IsBuilding", "IsAddingTracks", "IsBackingUp", "IsTesting", "IsExporting", "IsImporting" })
    {
        var changes = new HashSet<string?>();
        System.ComponentModel.PropertyChangedEventHandler handler = (_, e) => changes.Add(e.PropertyName);
        vm.PropertyChanged += handler;
        var property = typeof(MainWindowViewModel).GetProperty(name) ?? throw new MissingMemberException(name);
        property.SetValue(vm, true);
        Check(vm.IsBusy && !vm.CanAddTracks && !vm.CanBuild, "Active operation did not disable conflicting actions: " + name);
        Check(changes.Contains(nameof(vm.CanBuild)) && changes.Contains(nameof(vm.CanAddTracks)), "Busy changes did not notify both actions: " + name);
        property.SetValue(vm, false);
        vm.PropertyChanged -= handler;
        Check(!vm.IsBusy && vm.CanAddTracks && vm.CanBuild, "Completing an operation did not reenable actions: " + name);
    }
    vm.PlaylistLow = true;
    Check(!vm.CanBuild && vm.CanAddTracks, "A short playlist prevented adding the tracks needed to fix it.");
    vm.PlaylistLow = false;
    Check(vm.CanBuild, "Meeting the playlist minimum did not reenable building.");
}

static void TestNotifications(MainWindowViewModel vm, TrackItemViewModel[] tracks, Action installTools)
{
    Check(!vm.CanAddTracks && !vm.CanBuild && tracks.All(t => !t.CanPlay && !t.CanReplace && !t.CanEditMarkers),
        "Removing tools did not disable the current action state.");
    var mainChanges = new HashSet<string?>();
    var trackChanges = tracks.ToDictionary(t => t, _ => new HashSet<string?>());
    vm.PropertyChanged += (_, e) => mainChanges.Add(e.PropertyName);
    foreach (var track in tracks) track.PropertyChanged += (_, e) => trackChanges[track].Add(e.PropertyName);
    installTools();
    vm.RefreshToolAvailability();
    Check(vm.CanAddTracks && vm.CanBuild && tracks.All(t => t.CanPlay && t.CanReplace && t.CanEditMarkers),
        "Installing tools and refreshing did not reenable existing track actions.");
    Check(new[] { nameof(vm.CanAddTracks), nameof(vm.CanBuild), nameof(vm.AddTracksTip), nameof(vm.BuildTip) }.All(mainChanges.Contains),
        "Tool refresh did not notify every main-window action and tooltip.");
    string[] properties = ["CanPlay", "CanReplace", "CanEditMarkers", "PlayTip", "ReplaceTip", "MarkersTip"];
    foreach (var changes in trackChanges.Values)
        Check(properties.All(changes.Contains), "Main-window tool refresh did not notify every track action and tooltip.");
}

static void TestSourceTransitions()
{
    var track = Track("TRANSITION");
    var changes = new HashSet<string?>();
    track.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
    track.ReplacementPath = "replacement.wav";
    Check(new[] { "CanPlay", "CanEditMarkers", "PlayTip", "MarkersTip" }.All(changes.Contains),
        "Replacing bank audio did not notify the changed decoder dependencies.");
    changes.Clear();
    track.MarkersLoading = true;
    Check(changes.Contains(nameof(track.CanEditMarkers)) && !track.CanEditMarkers,
        "Marker-loading changes did not notify editor availability.");
    var empty = Track("EMPTY");
    empty.SampleLength = 0;
    Check(!empty.CanPlay && !empty.CanEditMarkers, "An empty bank sample became playable because the decoder was installed.");
    empty.RestoreFsbPath = "restored.fsb";
    empty.SubIndex = -1;
    Check(empty.CanPlay && empty.CanEditMarkers, "A staged standalone restore incorrectly required an original bank index.");
}

static void TestBackupNotifications(BackupRow[] rows, Action installTools)
{
    var vm = (BackupsViewModel) RuntimeHelpers.GetUninitializedObject(typeof(BackupsViewModel));
    var field = typeof(BackupsViewModel).GetField("<Items>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(nameof(BackupsViewModel), "Items");
    field.SetValue(vm, new ObservableCollection<BackupRow>(rows));
    var changes = rows.ToDictionary(row => row, _ => new HashSet<string?>());
    foreach (var row in rows) row.PropertyChanged += (_, e) => changes[row].Add(e.PropertyName);
    Check(rows.All(row => !row.CanRestoreNow), "Backup restore remained available without tools.");
    installTools();
    vm.RefreshToolAvailability();
    Check(rows[0].CanRestoreNow && !rows[1].CanRestoreNow,
        "Refreshing backup tools changed game compatibility or failed to enable a compatible restore.");
    foreach (var notifications in changes.Values)
        Check(notifications.Contains(nameof(BackupRow.CanRestoreNow)) && notifications.Contains(nameof(BackupRow.RestoreTip)),
            "Backup-list tool refresh did not notify every row action and tooltip.");
}

static void CheckTip(string actual, string normal, IEnumerable<string> missing)
{
    var names = missing.ToArray();
    Check(names.Length == 0 ? actual == normal : actual != normal && names.All(n => actual.Contains(n, StringComparison.OrdinalIgnoreCase)),
        "The tooltip did not explain the action's missing tool dependencies: " + actual);
}

static void CheckSet(IEnumerable<string> actual, IEnumerable<string> expected, string label)
{
    var values = actual.ToArray();
    Check(values.Length == values.Distinct(StringComparer.OrdinalIgnoreCase).Count()
        && new HashSet<string>(values, StringComparer.OrdinalIgnoreCase).SetEquals(expected), label + " listed incorrect or duplicate tool names.");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
