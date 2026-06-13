using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FH6RB.Services;
using FH6RB.ViewModels;

namespace FH6RB.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;
    
    private static readonly FilePickerFileType AudioFiles = new("Audio files")
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

        if (vm.Saved)
        {
            Vm.ReloadFromGame();
        }

        if (firstRun && !GameScanner.IsValid(Vm.Settings.GamePath))
        {
            Close();
        }
    }

    private async void OnSettings(object? sender, RoutedEventArgs e) => await ShowSettingsAsync();

    private async void OnAddTrack(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select audio files to add",
            AllowMultiple = true,
            FileTypeFilter = [AudioFiles],
        });

        if (files.Count == 0)
        {
            return;
        }

        Vm.IsAddingTracks = true;
        var added = 0;
        try
        {
            var i = 0;
            foreach (var file in files)
            {
                i++;
                if (file.TryGetLocalPath() is not { } path)
                {
                    continue;
                }

                Vm.Status = $"Processing {i}/{files.Count}: {System.IO.Path.GetFileName(path)}";
                
                var (fTitle, fArtist) = ParseName(System.IO.Path.GetFileNameWithoutExtension(path));
                var (mTitle, mArtist, duration) = await Task.Run(() => MetadataReader.Read(path));
                Vm.Tracks.Add(Vm.CreateCustomStub(path, mTitle ?? fTitle, mArtist ?? fArtist, duration));
                added++;
            }
        }
        finally
        {
            Vm.IsAddingTracks = false;
        }

        Vm.Recount();
        Vm.Status = added == 1 ? $"Added: {Vm.Tracks[^1].SoundName}" : $"Added {added} tracks";
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

    private async Task EditTrackAsync(TrackItemViewModel track)
    {
        var vm = new EditWindowViewModel(track);
        var dialog = new EditWindow { DataContext = vm };
        await dialog.ShowDialog(this);

        if (vm.Saved)
        {
            vm.Apply(track);
            Vm.RefreshNowPlaying();
            Vm.Status = "Saved: " + track.SoundName;
        }
    }

    private async void OnBuild(object? sender, RoutedEventArgs e)
    {
        await Vm.BuildAsync();

        if (Vm.PendingErrorDialog is { } msg)
        {
            Vm.PendingErrorDialog = null;
            await MessageDialog.ShowAsync(this, "Bank too large", msg);
        }
    }

    private bool _forceClose;
    private bool _confirmingQuit;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_forceClose && Vm.IsBuilding)
        {
            e.Cancel = true;
            if (!_confirmingQuit)
            {
                _ = ConfirmQuitAsync();
            }

            return;
        }

        base.OnClosing(e);
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
        var quit = await MessageDialog.ShowAsync(this, "Build in progress",
            "A build is still running. Quitting now may leave the bank half-written " +
            "(a .bak backup exists to restore from). Quit anyway?",
            okText: "Quit", cancelText: "Keep building");

        if (quit)
        {
            _forceClose = true;
            Close();
        }
    }
    
    private static (string Title, string? Artist) ParseName(string fileName)
    {
        var i = fileName.IndexOf(" - ", StringComparison.Ordinal);
        if (i > 0)
        {
            return (fileName[(i + 3)..].Trim(), fileName[..i].Trim());
        }

        return (fileName.Trim(), null);
    }
}
