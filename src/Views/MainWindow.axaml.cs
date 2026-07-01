using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FH6RB;
using FH6RB.Assets;
using FH6RB.Services;
using FH6RB.ViewModels;

namespace FH6RB.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnPreviewPressed, RoutingStrategies.Tunnel);
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
        => SafeAsync.Run(OpenedAsync, "open", this);

    private async Task OpenedAsync()
    {
        if (!GameScanner.IsValid(Vm.Settings.GamePath))
        {
            return;
        }

        var running = await Task.Run(Vm.IsGameRunning);

        if (running)
        {
            await MessageDialog.ShowAsync(this, Str.DlgGameRunningTitle, Str.DlgFilesInUseBody);
        }
    }

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

    private KeyModifiers _toggleMods;
    
    private static readonly FilePickerFileType AudioFiles = new(Str.FilterAudioFiles)
    {
        Patterns =
        [
            "*.mp3", "*.flac", "*.wav", "*.m4a", "*.aac", "*.ogg", "*.oga",
            "*.opus", "*.wma", "*.aiff", "*.aif", "*.alac", "*.ape", "*.wv",
        ],
        MimeTypes = ["audio/*"],
        AppleUniformTypeIdentifiers = ["public.audio"],
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".oga",
        ".opus", ".wma", ".aiff", ".aif", ".alac", ".ape", ".wv",
    };

    private static string? ResolveAudioPath(string path)
    {
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            path = ResolveShortcutChain(path) ?? string.Empty;
        }

        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        return AudioExtensions.Contains(System.IO.Path.GetExtension(path)) ? path : null;
    }

    private static string? ResolveShortcut(string lnkPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return null;
            }

            dynamic shortcut = shell.CreateShortcut(lnkPath);
            string target = shortcut.TargetPath;
            return string.IsNullOrEmpty(target) ? null : target;
        }
        catch
        {
            return null;
        }
    }

    private const int ShortcutMaxDepth = 8;

    private static string? ResolveShortcutChain(string path)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < ShortcutMaxDepth && path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase); i++)
        {
            if (!seen.Add(path))
            {
                return null;
            }

            var next = ResolveShortcut(path);
            if (string.IsNullOrEmpty(next))
            {
                return null;
            }

            path = next;
        }

        return path;
    }

    public async Task ShowSettingsAsync(bool firstRun = false)
    {
        var vm = new SettingsWindowViewModel(Vm.Settings) { IsFirstRun = firstRun };
        var dialog = new SettingsWindow { DataContext = vm };
        
        await dialog.ShowDialog(this);

        if (vm.Saved || vm.Restored)
        {
            Vm.ReloadFromGame();
        }

        if (firstRun && !GameScanner.IsValid(Vm.Settings.GamePath))
        {
            Close();
        }
    }

    private void OnSettings(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(() => ShowSettingsAsync(), "settings", this);

    private void OnResetAllMarkers(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(ResetAllMarkersAsync, "reset markers", this);

    private async Task ResetAllMarkersAsync()
    {
        var proceed = await MessageDialog.ShowAsync(this, Str.DlgResetMarkersTitle, Str.DlgResetMarkersBody,
            okText: Str.DlgResetMarkersOk, cancelText: Str.DlgResetMarkersCancel);

        if (proceed)
        {
            Vm.ResetAllCustomMarkers();
        }
    }

    private void OnMarkerDefaults(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(MarkerDefaultsAsync, "marker defaults", this);

    private async Task MarkerDefaultsAsync()
    {
        var vm = new MarkerDefaultsViewModel(Vm.Settings);
        var w = new MarkerDefaultsWindow { DataContext = vm };
        await w.ShowDialog(this);
    }

    private void OnSaveBackup(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(SaveBackupAsync, "save backup", this);

    private async Task SaveBackupAsync()
    {
        if (Vm.SelectedStation is not { } station)
        {
            Vm.Status = Str.StatusBackupNoStation;
            return;
        }

        if (Vm.HasUnbuiltTracks)
        {
            var proceed = await MessageDialog.ShowAsync(this, Str.DlgBackupUnbuiltTitle,
                Str.DlgBackupUnbuiltBody,
                okText: Str.DlgBackupUnbuiltOk, cancelText: Str.DlgBackupUnbuiltCancel);

            if (!proceed)
            {
                return;
            }
        }

        var name = await InputDialog.ShowAsync(this, Str.BackupNameTitle, Str.BackupNameWatermark);

        if (name is null)
        {
            return;
        }

        var gamePath = Vm.Settings.GamePath;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Vm.IsBackingUp = true;
            Vm.Status = Str.StatusBackupSaving;
        });

        string status;

        try
        {
            var entry = await Task.Run(() => StationBackupService.Create(name, gamePath, station, Log.Line));
            status = string.Format(Str.StatusBackupSavedFmt, entry.Manifest.Name);
        }
        catch (Exception ex)
        {
            status = string.Format(Str.StatusBackupFailedFmt, ex.Message);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Vm.Status = status;
            Vm.IsBackingUp = false;
        });
    }

    private void OnBackups(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(BackupsAsync, "backups", this);

    private async Task BackupsAsync()
    {
        var gamePath = Vm.Settings.GamePath;
        var vm = new BackupsViewModel(gamePath, Vm.SelectedStation);
        var w = new BackupsWindow { DataContext = vm };
        WindowMemory.Restore(w, Vm.Settings, "Backups");
        await w.ShowDialog(this);
        WindowMemory.Save(w, Vm.Settings, "Backups");

        if (w.RestoreTarget is not { } target)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Vm.IsBackingUp = true;
            Vm.Status = Str.StatusBackupRestoring;
        });

        string status;

        try
        {
            await Task.Run(() => StationBackupService.Restore(target, gamePath, Log.Line));
            await Dispatcher.UIThread.InvokeAsync(async () => await Vm.ReloadAsync());
            status = string.Format(Str.StatusBackupRestoredFmt, target.Manifest.Name);
        }
        catch (Exception ex)
        {
            status = string.Format(Str.StatusBackupFailedFmt, ex.Message);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Vm.Status = status;
            Vm.IsBackingUp = false;
        });
    }

    private void OnRestoreStation(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(RestoreStationAsync, "restore station", this);

    private async Task RestoreStationAsync()
    {
        if (Vm.SelectedStation is not { } station)
        {
            Vm.Status = Str.StatusBackupNoStation;
            return;
        }

        var gamePath = Vm.Settings.GamePath;

        if (!GameScanner.IsValid(gamePath))
        {
            return;
        }

        if (!BackupService.Has(gamePath))
        {
            Vm.Status = Str.StatusRestoreNoOriginal;
            return;
        }

        var running = await Task.Run(Vm.IsGameRunning);

        if (running)
        {
            await MessageDialog.ShowAsync(this, Str.DlgGameRunningTitle, Str.DlgFilesInUseBody);
            return;
        }

        var proceed = await MessageDialog.ShowAsync(this, Str.DlgRestoreStationTitle,
            string.Format(Str.DlgRestoreStationBody, station.Name),
            okText: Str.DlgRestoreStationOk, cancelText: Str.DlgRestoreStationCancel);

        if (!proceed)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Vm.IsBackingUp = true;
            Vm.Status = Str.StatusBackupRestoring;
        });

        string status;

        try
        {
            var (banks, langs) = await Task.Run(() => BackupService.RestoreStation(gamePath, station, Log.Line));
            await Dispatcher.UIThread.InvokeAsync(async () => await Vm.ReloadAsync());
            status = string.Format(Str.StatusRestoreStationFmt, station.Name, banks, langs);
        }
        catch (Exception ex)
        {
            status = string.Format(Str.StatusBackupFailedFmt, ex.Message);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Vm.Status = status;
            Vm.IsBackingUp = false;
        });
    }

    private void OnPreviewPressed(object? sender, PointerPressedEventArgs e) => _toggleMods = e.KeyModifiers;

    private void OnToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TrackItemViewModel track })
        {
            Vm.ToggleEnabled(track, _toggleMods.HasFlag(KeyModifiers.Shift), _toggleMods.HasFlag(KeyModifiers.Control));
        }
    }

    private void OnAddTrack(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(AddTrackAsync, "add track", this);

    private async Task AddTrackAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Str.PickAddAudio,
            AllowMultiple = true,
            FileTypeFilter = [AudioFiles],
        });

        if (files.Count == 0)
        {
            return;
        }

        var paths = new List<string>();
        
        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is { } path && ResolveAudioPath(path) is { } audio)
            {
                paths.Add(audio);
            }
        }

        if (paths.Count == 0)
        {
            return;
        }

        Vm.IsAddingTracks = true;
        
        var added = 0;
        
        try
        {
            Vm.Status = paths.Count == 1 ? string.Format(Str.StatusReadingOneFmt, System.IO.Path.GetFileName(paths[0])) : string.Format(Str.StatusReadingManyFmt, paths.Count);
            
            var metas = await Task.Run(() =>
            {
                var results = new (string Path, string? Title, string? Artist, double Dur)[paths.Count];
                var opts = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
                };

                Parallel.For(0, paths.Count, opts, idx =>
                {
                    var (t, a, d) = MetadataReader.Read(paths[idx]);
                    results[idx] = (paths[idx], t, a, d);
                });

                return results;
            });

            foreach (var m in metas)
            {
                var (fTitle, fArtist) = ParseName(System.IO.Path.GetFileNameWithoutExtension(m.Path));
                Vm.Tracks.Add(Vm.CreateCustomStub(m.Path, m.Title ?? fTitle, m.Artist ?? fArtist, m.Dur));
                added++;
            }
        }
        finally
        {
            Vm.IsAddingTracks = false;
        }

        if (added > 0)
        {
            Vm.MarkDirty();
        }

        Vm.Recount();
        Vm.Status = added == 1 ? string.Format(Str.StatusAddedOneFmt, Vm.Tracks[^1].SoundName) : string.Format(Str.StatusAddedManyFmt, added);
    }

    private void OnEdit(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(() => EditAsync(sender), "edit", this);

    private async Task EditAsync(object? sender)
    {
        if (sender is Button { Tag: TrackItemViewModel track })
        {
            await EditTrackAsync(track);
        }
    }

    private void OnTransportEdit(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(TransportEditAsync, "transport edit", this);

    private async Task TransportEditAsync()
    {
        if (Vm.NowPlaying is { } track)
        {
            await EditTrackAsync(track);
        }
    }

    private void OnReplace(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(() => ReplaceAsync(sender), "replace", this);

    private async Task ReplaceAsync(object? sender)
    {
        if (sender is not Button { Tag: TrackItemViewModel track })
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Str.PickReplaceAudio,
            AllowMultiple = false,
            FileTypeFilter = [AudioFiles],
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } picked
            || ResolveAudioPath(picked) is not { } path)
        {
            return;
        }

        if (ReferenceEquals(Vm.NowPlaying, track))
        {
            Vm.StopPlayback();
        }

        Vm.IsAddingTracks = true;
        Vm.Status = string.Format(Str.StatusReplacingFmt, track.SoundName);

        string? mTitle, mArtist;
        double mDuration;
        
        try
        {
            (mTitle, mArtist, mDuration) = await Task.Run(() => MetadataReader.Read(path));
        }
        finally
        {
            Vm.IsAddingTracks = false;
        }

        var (fTitle, fArtist) = ParseName(System.IO.Path.GetFileNameWithoutExtension(path));

        track.ReplacementPath = path;
        track.Title = mTitle ?? fTitle;
        track.Artist = mArtist ?? fArtist;

        if (!track.IsCustom)
        {
            track.Markers = null;
        }
        track.SampleLength = 0;
        track.SampleRate = 0;
        track.PendingDurationSeconds = mDuration > 0 ? mDuration : null;

        Vm.MarkDirty();
        Vm.RefreshNowPlaying();
        Vm.Status = string.Format(Str.StatusReplaceStagedFmt, track.SoundName);
    }

    private void OnMarkers(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(() => MarkersAsync(sender), "markers", this);

    private async Task MarkersAsync(object? sender)
    {
        if (sender is Button { Tag: TrackItemViewModel track })
        {
            await EditMarkersAsync(track);
        }
    }

    private async Task EditMarkersAsync(TrackItemViewModel track)
    {
        Vm.StopPlayback();

        (float[]? Peaks, string? Wav) media;
        track.MarkersLoading = true;
        
        try
        {
            media = await Vm.LoadPeaksAsync(track);
        }
        finally
        {
            track.MarkersLoading = false;
        }

        if (track.SampleLength <= 0 || track.SampleRate <= 0)
        {
            Vm.Status = Str.StatusDecodeFailed;
            return;
        }

        var vm = new EditWindowViewModel(track)
        {
            Peaks = media.Peaks,
            WavPath = media.Wav,
            Settings = Vm.Settings,
        };

        var w = new WaveformWindow { DataContext = vm };
        await w.ShowDialog(this);

        if (w.Saved)
        {
            vm.Apply(track);
            Vm.MarkDirty();
            Vm.RefreshNowPlaying();
            Vm.Status = string.Format(Str.StatusMarkersUpdatedFmt, track.SoundName);
        }
    }

    private async Task EditTrackAsync(TrackItemViewModel track)
    {
        var vm = new EditWindowViewModel(track);
        
        if (vm.CanEditMarkers)
        {
            vm.PeaksLoader = () => Vm.LoadPeaksAsync(track);
        }

        var dialog = new EditWindow { DataContext = vm };
        
        await dialog.ShowDialog(this);

        if (vm.Saved)
        {
            vm.Apply(track);
            Vm.MarkDirty();
            Vm.RefreshNowPlaying();
            Vm.Status = string.Format(Str.StatusSavedFmt, track.SoundName);
        }
    }

    private void OnBuild(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(BuildAsync, "build", this);

    private async Task BuildAsync()
    {
        await Vm.BuildAsync();

        if (Vm.PendingErrorDialog is not { } msg)
        {
            return;
        }
        
        Vm.PendingErrorDialog = null;
        var title = Vm.PendingDialogTitle ?? Str.DlgBankTooLargeTitle;
        Vm.PendingDialogTitle = null;
        
        await MessageDialog.ShowAsync(this, title, msg);
    }

    private bool _forceClose;
    private bool _confirmingQuit;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_forceClose)
        {
            base.OnClosing(e);
            return;
        }

        if (Vm.IsBuilding)
        {
            e.Cancel = true;
            if (!_confirmingQuit)
            {
                _ = ConfirmQuitAsync();
            }

            return;
        }

        if (Vm.HasUnsavedChanges)
        {
            e.Cancel = true;
            if (!_confirmingQuit)
            {
                _ = ConfirmUnsavedAsync();
            }

            return;
        }

        base.OnClosing(e);
    }

    private async Task ConfirmUnsavedAsync()
    {
        _confirmingQuit = true;
        
        try
        {
            var quit = await MessageDialog.ShowAsync(this, Str.DlgUnsavedTitle,
                Str.DlgUnsavedBody,
                okText: Str.DlgUnsavedOk, cancelText: Str.DlgUnsavedCancel);

            if (quit)
            {
                _forceClose = true;
                Close();
            }
        }
        finally
        {
            _confirmingQuit = false;
        }
    }

    private async Task ConfirmQuitAsync()
    {
        _confirmingQuit = true;
        
        try
        {
            await ConfirmQuitCoreAsync();
        }
        finally
        {
            _confirmingQuit = false;
        }
    }

    private async Task ConfirmQuitCoreAsync()
    {
        var quit = await MessageDialog.ShowAsync(this, Str.DlgBuildProgressTitle,
            Str.DlgBuildProgressBody,
            okText: Str.DlgBuildProgressOk, cancelText: Str.DlgBuildProgressCancel);

        if (quit)
        {
            _forceClose = true;
            Close();
        }
    }
    
    private static (string Title, string? Artist) ParseName(string fileName)
    {
        var i = fileName.IndexOf(" - ", StringComparison.Ordinal);
        
        return i > 0 ? (fileName[(i + 3)..].Trim(), fileName[..i].Trim()) : (fileName.Trim(), null);
    }
}
