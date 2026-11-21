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
        Activated += (_, _) => (DataContext as MainWindowViewModel)?.RefreshToolAvailability();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Vm.ConfirmDiscardChanges = ConfirmDiscardChangesAsync;
        SafeAsync.Run(OpenedAsync, "open", this);
    }

    private Task<bool> ConfirmDiscardChangesAsync() => ConfirmDiscardChangesAsync(this);

    private static Task<bool> ConfirmDiscardChangesAsync(Window owner) =>
        MessageDialog.ShowAsync(owner, Str.DlgSwitchUnsavedTitle, Str.DlgSwitchUnsavedBody,
            Str.DlgSwitchUnsavedOk, Str.DlgSwitchUnsavedCancel);

    private async Task OpenedAsync()
    {
        if (!Vm.HasValidGamePath)
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

    private static readonly FilePickerFileType PackFiles = new(Str.FilterPackFiles)
    {
        Patterns = ["*.fhre"],
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
        if (Vm.IsBusy) return;
        var prevPath = Vm.Settings.GamePath;
        var vm = new SettingsWindowViewModel(Vm.Settings) { IsFirstRun = firstRun };
        var dialog = new SettingsWindow { DataContext = vm };
        dialog.ConfirmGamePathChange = () => Vm.HasUnsavedChanges ? ConfirmDiscardChangesAsync(dialog) : Task.FromResult(true);

        await dialog.ShowDialog(this);

        if (vm.Saved && ((Vm.HasResourceError && !Vm.HasUnsavedChanges)
            || !string.Equals(prevPath, Vm.Settings.GamePath, StringComparison.OrdinalIgnoreCase)))
        {
            Vm.ReloadFromGame(vm.SavedScan);
        }
        else if (vm.Saved)
        {
            Vm.RefreshToolAvailability();
            Vm.RecalcProjectedSize();
            Vm.RefreshNowPlaying();
        }

        if (firstRun && !Vm.HasValidGamePath)
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

    private void OnSaveAllMarkers(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(SaveAllMarkersAsync, "save all markers", this);

    private async Task SaveAllMarkersAsync()
    {
        var proceed = await MessageDialog.ShowAsync(this, Str.DlgSaveAllMarkersTitle, Str.DlgSaveAllMarkersBody,
            okText: Str.DlgSaveAllMarkersOk, cancelText: Str.BtnCancel);

        if (proceed)
        {
            Vm.SaveAllMarkers();
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
        if (Vm.IsBusy) return;

        if (Vm.SelectedStation is not { } station)
        {
            Vm.Status = Str.StatusBackupNoStation;
            return;
        }

        if (Vm.IsLoading || Vm.SelectedVariant is not { } variant || Vm.LoadedBankPath is null || Vm.HasResourceError)
        {
            await MessageDialog.ShowAsync(this, Str.BackupNameTitle, Str.StatusBackupBankNotReady);
            return;
        }

        var name = await InputDialog.ShowAsync(this, Str.BackupNameTitle, Str.BackupNameWatermark);

        if (name is null)
        {
            return;
        }

        var gamePath = Vm.Settings.GamePath;
        var language = Vm.SelectedLanguage?.FileName;
        var sources = Vm.Tracks
            .Select(t => new BackupTrackSource(
                t.SoundName, t.IsCustom, t.IsReplacing, t.Replaced,
                t.RestoreFsbPath ?? t.SourcePath, t.ReplacementPath,
                t.RestoreFsbPath is not null ? -1 : t.SubIndex,
                t.Title, t.Artist, t.SampleLength, t.SampleRate,
                t.GainDb, t.Enabled, t.Markers, t.Normalization, t.BankId))
            .ToList();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Vm.IsBackingUp = true;
            Vm.Status = Str.StatusBackupSaving;
        });

        string? message = null;

        try
        {
            var entry = await Task.Run(() => StationBackupService.CreateFromEditor(name, gamePath, station, variant, sources, language, Log.Line));
            Vm.Status = string.Format(Str.StatusBackupSavedFmt, entry.Manifest.Name);
            if (entry.SkippedLanguages.Count > 0)
            {
                message = string.Format(Str.BackupSkippedLanguagesFmt, string.Join(", ", entry.SkippedLanguages));
            }
        }
        catch (Exception ex)
        {
            Log.Line($"backup failed: station=#{station.Number} variant={variant} game={gamePath}{Environment.NewLine}{ex}");
            Vm.Status = string.Format(Str.StatusBackupFailedFmt, ex.Message);
            message = $"{Vm.Status}{Environment.NewLine}{Environment.NewLine}" +
                string.Format(Str.BackupFolderFmt, StationBackupService.Root);
        }
        finally
        {
            Vm.IsBackingUp = false;
        }

        if (message is not null) await MessageDialog.ShowAsync(this, Str.BackupNameTitle, message);
    }

    private void OnExport(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(ExportAsync, "export", this);

    private async Task ExportAsync()
    {
        if (Vm.SelectedStation is null)
        {
            Vm.Status = Str.StatusBackupNoStation;
            return;
        }

        if (!Vm.CanTransferTracks)
        {
            Vm.Status = Str.StatusPackBankChanged;
            return;
        }
        var bankGeneration = Vm.BankLoadGeneration;

        if (Vm.Tracks.Count == 0)
        {
            Vm.Status = Str.StatusExportEmpty;
            await MessageDialog.ShowAsync(this, Str.TitleNotice, Str.StatusExportEmpty);
            return;
        }

        var stationName = Vm.SelectedStation?.Name ?? "bank";
        var variant = Vm.SelectedVariant ?? "";
        var defaultName = string.IsNullOrEmpty(variant) ? stationName : $"{stationName}.{variant}";

        var selVm = new ExportSelectionWindowViewModel(Vm.Tracks.ToList(), defaultName);
        var selWin = new ExportSelectionWindow { DataContext = selVm };
        await selWin.ShowDialog(this);

        if (!selVm.Saved)
        {
            return;
        }

        var selected = selVm.Selected;

        if (selected.Count == 0)
        {
            Vm.Status = Str.StatusExportEmpty;
            return;
        }

        var suggested = selVm.FileName;
        if (string.IsNullOrWhiteSpace(suggested)) suggested = defaultName;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Str.PickExportFile,
            SuggestedFileName = suggested,
            DefaultExtension = "fhre",
            FileTypeChoices = [PackFiles],
        });

        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        if (!path.EndsWith(".fhre", StringComparison.OrdinalIgnoreCase))
        {
            path += ".fhre";
        }

        if (!CheckPackBank(bankGeneration)) return;
        await Vm.ExportAsync(path, selected);
    }

    private void OnImport(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(ImportAsync, "import", this);

    private async Task ImportAsync()
    {
        if (Vm.SelectedStation is null)
        {
            Vm.Status = Str.StatusBackupNoStation;
            return;
        }

        if (!Vm.CanTransferTracks)
        {
            Vm.Status = Str.StatusPackBankChanged;
            return;
        }
        var bankGeneration = Vm.BankLoadGeneration;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Str.PickImportFile,
            AllowMultiple = false,
            FileTypeFilter = [PackFiles],
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        if (!CheckPackBank(bankGeneration)) return;

        ImportedPack? pack = null;
        var applied = false;
        try
        {
            try
            {
                Vm.IsImporting = true;
                pack = await Task.Run(() => TrackPack.Load(path));
            }
            finally
            {
                Vm.IsImporting = false;
            }

            if (!CheckPackBank(bankGeneration)) return;
            if (pack.Manifest.Tracks.Count == 0)
            {
                Vm.Status = Str.StatusImportEmpty;
                return;
            }

            var defaults = Vm.Tracks.Where(t => !t.IsCustom && !t.IsBankOnly).ToList();
            var vm = new ImportWindowViewModel(pack.Manifest.Tracks, defaults);
            var w = new ImportWindow { DataContext = vm };
            await w.ShowDialog(this);
            if (!vm.Saved || !CheckPackBank(bankGeneration)) return;

            var replaced = vm.Result.Count(kv => kv.Value is not null);
            Vm.ApplyImport(pack, vm.Result);
            applied = true;
            Vm.Status = string.Format(Str.StatusImportedFmt, vm.Result.Count, replaced, vm.Result.Count - replaced);
        }
        catch (Exception ex)
        {
            Log.Line("IMPORT ERROR: " + ex);
            var message = string.Format(Str.StatusImportErrorFmt, ex.Message);
            Vm.Status = message;
            if (IsVisible)
                await MessageDialog.ShowAsync(this, Str.DlgImportFailedTitle, message);
        }
        finally
        {
            if (!applied && pack is not null && !Vm.Tracks.Any(track =>
                string.Equals(Path.GetDirectoryName(track.FileSource), pack.Dir, StringComparison.OrdinalIgnoreCase)))
            {
                TrackPack.DiscardImport(pack);
            }
        }
    }

    private bool CheckPackBank(int generation)
    {
        if (!IsVisible) return false;
        if (Vm.CanTransferTracks && Vm.BankLoadGeneration == generation) return true;
        Vm.Status = Str.StatusPackBankChanged;
        return false;
    }

    private void OnBackups(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(BackupsAsync, "backups", this);

    private async Task BackupsAsync()
    {
        var gamePath = Vm.Settings.GamePath;
        var vm = new BackupsViewModel(gamePath, Vm.SelectedStation);
        var w = new BackupsWindow { DataContext = vm };
        await w.ShowDialog(this);

        if (w.RestoreTarget is not { } target)
        {
            return;
        }

        Vm.RefreshToolAvailability();
        if (!Tools.CanBuild)
        {
            Vm.Status = Vm.BuildTip;
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Vm.IsBackingUp = true;
            Vm.Status = Str.StatusBackupRestoring;
        });

        var settings = Vm.Settings;
        string status;

        try
        {
            await Vm.StopLoudnessAnalysisAsync();
            var (banks, langs) = await Task.Run(
                async () => await StationBackupService.RestoreAsync(target, gamePath, settings, Log.Line));
            await Dispatcher.UIThread.InvokeAsync(async () => await Vm.ReloadAsync());
            status = string.Format(Str.StatusBackupRestoredFmt, target.Manifest.Name, banks, langs);
        }
        catch (Exception ex)
        {
            Log.Line("RESTORE FAILED: " + ex);
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
            await Vm.StopLoudnessAnalysisAsync();
            var (banks, langs) = await Task.Run(() => BackupService.RestoreStation(gamePath, station, Log.Line));
            await Dispatcher.UIThread.InvokeAsync(async () => await Vm.ReloadAsync());
            status = string.Format(Str.StatusRestoreStationFmt, station.Name, banks, langs);
        }
        catch (Exception ex)
        {
            Log.Line("RESTORE STATION FAILED: " + ex);
            status = string.Format(Str.StatusBackupFailedFmt, ex.Message);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Vm.Status = status;
            Vm.IsBackingUp = false;
        });
    }

    private void OnRestoreAllStations(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(RestoreAllStationsAsync, "restore all", this);

    private async Task RestoreAllStationsAsync()
    {
        var gamePath = Vm.Settings.GamePath;
        if (Vm.IsBusy) return;

        var backups = await Task.Run(() => BackupService.Find(gamePath));
        if (Vm.IsBusy || !string.Equals(gamePath, Vm.Settings.GamePath, StringComparison.OrdinalIgnoreCase)) return;
        if (backups.Count == 0)
        {
            Vm.SetCanRestoreAll(false);
            Vm.Status = Str.StatusRestoreNoOriginal;
            return;
        }

        var running = await Task.Run(Vm.IsGameRunning);

        if (running)
        {
            await MessageDialog.ShowAsync(this, Str.DlgGameRunningTitle, Str.DlgFilesInUseBody);
            return;
        }

        var proceed = await MessageDialog.ShowAsync(this, Str.DlgRestoreAllTitle, Str.DlgRestoreAllBody,
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
            await Vm.StopLoudnessAnalysisAsync();
            var (restored, failed) = await Task.Run(() => BackupService.Restore(gamePath, backups, Log.Line));
            if (Vm.HasValidGamePath) await Vm.ReloadAsync();
            else Vm.ReloadFromGame();
            status = failed == 0
                ? string.Format(Str.EditRestoredFmt, restored)
                : string.Format(Str.EditRestoredFailedFmt, restored, failed);
        }
        catch (Exception ex)
        {
            Log.Line("RESTORE ALL FAILED: " + ex);
            status = string.Format(Str.StatusBackupFailedFmt, ex.Message);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Vm.Status = status;
            Vm.IsBackingUp = false;
        });
    }

    private void OnPreviewPressed(object? sender, PointerPressedEventArgs e) => _toggleMods = e.KeyModifiers;

    private void OnRestoreDefault(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TrackItemViewModel track })
        {
            SafeAsync.Run(() => RestoreDefaultAsync(track), "restore track", this);
        }
    }

    private async Task RestoreDefaultAsync(TrackItemViewModel track)
    {
        if (ReferenceEquals(Vm.NowPlaying, track))
        {
            Vm.StopPlayback();
        }

        var confirm = await MessageDialog.ShowAsync(this, Str.DlgRestoreTrackTitle, Str.DlgRestoreTrackBody,
            okText: Str.BtnYes, cancelText: Str.BtnNo);

        if (!confirm)
        {
            return;
        }

        var r = Vm.RestoreDefault(track);

        if (r == RestoreResult.NoBackup)
        {
            await MessageDialog.ShowAsync(this, Str.DlgRestoreNoBakTitle, Str.DlgRestoreNoBakBody);
        }
        else if (r == RestoreResult.NotInBackup)
        {
            await MessageDialog.ShowAsync(this, Str.DlgRestoreNotInBakTitle, Str.DlgRestoreNotInBakBody);
        }
    }

    private void OnToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TrackItemViewModel track })
        {
            Vm.ToggleEnabled(track, _toggleMods.HasFlag(KeyModifiers.Shift), _toggleMods.HasFlag(KeyModifiers.Control));
        }
    }

    private void OnTracksSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox lb)
        {
            lb.SelectedIndex = -1;
        }
    }

    private void OnAddTrack(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(AddTrackAsync, "add track", this);

    private async Task AddTrackAsync()
    {
        if (!CheckAddTrackAvailability()) return;

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

        if (!CheckAddTrackAvailability()) return;

        Vm.IsAddingTracks = true;
        
        var added = 0;
        var failures = new List<string>();
        
        try
        {
            Vm.Status = paths.Count == 1 ? string.Format(Str.StatusReadingOneFmt, System.IO.Path.GetFileName(paths[0])) : string.Format(Str.StatusReadingManyFmt, paths.Count);
            
            var metas = await Task.Run(() =>
            {
                var results = new (string Path, string? Title, string? Artist, double Dur, string? Error)[paths.Count];
                var opts = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
                };

                Parallel.For(0, paths.Count, opts, idx =>
                {
                    var path = paths[idx];
                    try
                    {
                        var (title, artist, duration) = MetadataReader.Read(path);
                        results[idx] = (path, title, artist, duration, null);
                    }
                    catch (Exception ex)
                    {
                        Log.Line($"Failed to read audio metadata for {path}: {ex}");
                        results[idx] = (path, null, null, 0, ex.Message);
                    }
                });

                return results;
            });

            foreach (var m in metas)
            {
                if (m.Error is { } error)
                {
                    failures.Add(error);
                    continue;
                }
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
        Vm.RecalcProjectedSize();
        Vm.Status = failures.Count > 0 ? string.Format(Str.StatusAddedWithErrorsFmt, added, failures.Count)
            : added == 1 ? string.Format(Str.StatusAddedOneFmt, Vm.Tracks[^1].SoundName) : string.Format(Str.StatusAddedManyFmt, added);
        if (failures.Count > 0)
            await MessageDialog.ShowAsync(this, Vm.Status, string.Join(Environment.NewLine + Environment.NewLine, failures.Take(5)));
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

        if (!CheckAddTrackAvailability()) return;

        if (!track.CanReplace)
        {
            Vm.Status = track.ReplaceTip;
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

        if (!CheckAddTrackAvailability()) return;

        Vm.IsAddingTracks = true;
        var previousStatus = Vm.Status;
        Vm.Status = string.Format(Str.StatusReplacingFmt, track.SoundName);

        string? mTitle, mArtist;
        double mDuration;
        
        try
        {
            (mTitle, mArtist, mDuration) = await Task.Run(() => MetadataReader.Read(path));
        }
        catch
        {
            Vm.Status = previousStatus;
            throw;
        }
        finally
        {
            Vm.IsAddingTracks = false;
        }

        var (fTitle, fArtist) = ParseName(System.IO.Path.GetFileNameWithoutExtension(path));

        var resetMarkers = await MessageDialog.ShowAsync(this, Str.DlgReplaceResetMarkersTitle, Str.DlgReplaceResetMarkersBody,
            Str.BtnYes, Str.BtnNo);

        if (ReferenceEquals(Vm.NowPlaying, track)) Vm.StopPlayback();
        track.StageReplacement(path, mTitle ?? fTitle, mArtist ?? fArtist, mDuration, resetMarkers);

        Vm.MarkDirty();
        Vm.RefreshNowPlaying();
        Vm.RecalcProjectedSize();
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
        var media = await Vm.PrepareMarkerEditorAsync(track);
        if (media.Wav is null || media.Peaks is null || !IsVisible || !Vm.Tracks.Contains(track)) return;

        var vm = new EditWindowViewModel(track, Vm.Settings)
        {
            Peaks = media.Peaks,
            WavPath = media.Wav,
            Settings = Vm.Settings,
        };

        var w = new WaveformWindow { DataContext = vm };
        await w.ShowDialog(this);

        if (w.Saved && Vm.Tracks.Contains(track))
        {
            vm.Apply(track);
            Vm.MarkDirty();
            Vm.RefreshNowPlaying();
            Vm.Status = string.Format(Str.StatusMarkersUpdatedFmt, track.SoundName);
        }
    }

    private async Task EditTrackAsync(TrackItemViewModel track)
    {
        if (!track.CanEdit)
        {
            Vm.Status = track.EditTip;
            return;
        }
        var vm = new EditWindowViewModel(track, Vm.Settings);
        
        if (vm.CanEditMarkers && track.CanEditMarkers)
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

    private void OnCancelBuild(object? sender, RoutedEventArgs e) => Vm.CancelBuild();

    private async Task BuildAsync()
    {
        Vm.RefreshToolAvailability();
        if (!Vm.CanBuild)
        {
            if (!Tools.CanBuild) Vm.Status = Vm.BuildTip;
            return;
        }

        if (Vm.ProjectedTotalBytes > int.MaxValue)
        {
            var proceed = await MessageDialog.ShowAsync(this, Str.DlgBankTooLargeWarnTitle,
                Str.DlgBankTooLargeWarnBody, okText: Str.DlgBankTooLargeWarnOk, cancelText: Str.BtnCancel);

            if (!proceed)
            {
                return;
            }
        }

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

        if (Vm.IsBuilding || Vm.IsBackingUp || Vm.IsImporting || Vm.IsExporting)
        {
            e.Cancel = true;
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

    private static (string Title, string? Artist) ParseName(string fileName)
    {
        var i = fileName.IndexOf(" - ", StringComparison.Ordinal);
        
        return i > 0 ? (fileName[(i + 3)..].Trim(), fileName[..i].Trim()) : (fileName.Trim(), null);
    }

    private bool CheckAddTrackAvailability()
    {
        Vm.RefreshToolAvailability();
        if (Vm.IsBusy) return false;
        if (Vm.CanAddTracks) return true;
        Vm.Status = Vm.AddTracksTip;
        return false;
    }
}
