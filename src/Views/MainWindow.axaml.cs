using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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

    private async void OnOpened(object? sender, EventArgs e)
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

    private async void OnSettings(object? sender, RoutedEventArgs e) => await ShowSettingsAsync();

    private async void OnSaveBackup(object? sender, RoutedEventArgs e)
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

    private async void OnBackups(object? sender, RoutedEventArgs e)
    {
        var gamePath = Vm.Settings.GamePath;
        var vm = new BackupsViewModel(gamePath, Vm.SelectedStation);
        var w = new BackupsWindow { DataContext = vm };
        await w.ShowDialog(this);

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

    private void OnPreviewPressed(object? sender, PointerPressedEventArgs e) => _toggleMods = e.KeyModifiers;

    private void OnToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TrackItemViewModel track })
        {
            Vm.ToggleEnabled(track, _toggleMods.HasFlag(KeyModifiers.Shift), _toggleMods.HasFlag(KeyModifiers.Control));
        }
    }

    private async void OnAddTrack(object? sender, RoutedEventArgs e)
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
            if (file.TryGetLocalPath() is { } path)
            {
                paths.Add(path);
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

    private async void OnEdit(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TrackItemViewModel track })
        {
            await EditTrackAsync(track);
        }
    }

    private async void OnTransportEdit(object? sender, RoutedEventArgs e)
    {
        if (Vm.NowPlaying is { } track)
        {
            await EditTrackAsync(track);
        }
    }

    private async void OnReplace(object? sender, RoutedEventArgs e)
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

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
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
        
        track.Markers = null;
        track.SampleLength = 0;
        track.SampleRate = 0;
        track.PendingDurationSeconds = mDuration > 0 ? mDuration : null;

        Vm.MarkDirty();
        Vm.RefreshNowPlaying();
        Vm.Status = string.Format(Str.StatusReplaceStagedFmt, track.SoundName);
    }

    private async void OnMarkers(object? sender, RoutedEventArgs e)
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

        var vm = new EditWindowViewModel(track) { Peaks = media.Peaks, WavPath = media.Wav, Settings = Vm.Settings };

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

    private async void OnBuild(object? sender, RoutedEventArgs e)
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
