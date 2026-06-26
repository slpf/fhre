using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using FH6RB.Assets;
using FH6RB.Core;
using FH6RB.Services;

namespace FH6RB.ViewModels;

public sealed partial class SettingsWindowViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    public string AppVersion => $"v{Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0"}";

    [ObservableProperty] private string _gamePath;
    [ObservableProperty] private bool _isValid;
    [ObservableProperty] private bool _scanning;
    [ObservableProperty] private string _exeLine = "";
    [ObservableProperty] private string _langLine = "";
    [ObservableProperty] private string _bankLine = "";
    [ObservableProperty] private string _error = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetLufsText))]
    private double _targetLufs;

    public string TargetLufsText => $"{TargetLufs:0} LUFS";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EncodeParallelismText))]
    private int _encodeParallelism;

    public int MaxThreads => Environment.ProcessorCount;
    public string EncodeParallelismText => $"{EncodeParallelism} / {MaxThreads}";
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CardVisible))]
    private bool _canRestore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CardVisible))]
    private string _backupLine = "";

    public bool CardVisible => CanRestore || !string.IsNullOrWhiteSpace(BackupLine);
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAdvanced))]
    private bool _isFirstRun;

    public bool ShowAdvanced => !IsFirstRun;

    public bool Saved { get; private set; }
    public bool Restored { get; private set; }

    public SettingsWindowViewModel(AppSettings settings)
    {
        _settings = settings;
        _gamePath = settings.GamePath;
        _targetLufs = settings.TargetLufs;
        _encodeParallelism = settings.EncodeParallelism > 0
            ? Math.Min(settings.EncodeParallelism, MaxThreads)
            : AppSettings.RecommendedParallelism;
        Validate();
    }

    partial void OnGamePathChanged(string value) => _ = ValidateAsync();

    public void Validate() => _ = ValidateAsync();

    private CancellationTokenSource? _validateCts;
    
    private static GameScan? _gameScanCache;
    // _gameScanCache is intentionally static: the settings dialog reuses the last scan while
    // the user keeps the same path so the success lines don't flicker on every keystroke.
    // The static lifetime means a stale transient result can persist across dialog opens
    // until the path changes or the app restarts — that's an acceptable trade-off for now.
    private sealed record GameScan(string Path, GameScanner.GameScanResult Result);

    public async Task ValidateAsync()
    {
        _validateCts?.Cancel();
        var cts = new CancellationTokenSource();
        _validateCts = cts;
        var token = cts.Token;

        var path = GamePath;
        Error = "";

        var cachedGame = _gameScanCache is { } gc && string.Equals(gc.Path, path, StringComparison.OrdinalIgnoreCase) ? gc : null;

        if (cachedGame is not null)
        {
            ApplyGameScan(cachedGame);
            Scanning = false;
        }
        else
        {
            Scanning = true;
            IsValid = false;
        }

        try
        {
            var r = await Task.Run(() =>
            {
                var scan = cachedGame?.Result ?? GameScanner.Scan(path);
                var hasBackups = BackupService.Has(path);
                var modified = hasBackups && BackupService.HasModified(path);
                return (new GameScan(path, scan), hasBackups, modified);
            }, token);

            if (token.IsCancellationRequested)
            {
                return;
            }

            _gameScanCache = r.Item1;

            if (cachedGame is null)
            {
                ApplyGameScan(r.Item1);
            }

            CanRestore = r.modified;

            if (!Restored)
            {
                BackupLine = CanRestore ? Str.HintRestore : "";
            }
        }
        catch (OperationCanceledException)
        {

        }
        finally
        {
            if (_validateCts == cts)
            {
                Scanning = false;
            }
        }
    }

    private void ApplyGameScan(GameScan game)
    {
        var result = game.Result;
        IsValid = result.IsValid;

        if (IsValid)
        {
            ExeLine = result.ExePath is null ? "" : string.Format(Str.FoundFmt, Path.GetFileName(result.ExePath));
            LangLine = string.Format(Str.LocalizationsFmt, result.LanguageFileCount);
            BankLine = string.Format(Str.RadioBanksFmt, result.BankCount);
            Error = "";
        }
        else
        {
            ExeLine = "";
            LangLine = "";
            BankLine = "";
            Error = FormatScanError(result);
        }
    }

    private static string FormatScanError(GameScanner.GameScanResult r)
    {
        return r.Issue switch
        {
            GameScanner.GameScanIssue.EmptyPath => Str.ErrScanEmptyPath,
            GameScanner.GameScanIssue.DirectoryMissing => string.Format(Str.ErrScanDirectoryMissingFmt, r.Detail ?? ""),
            GameScanner.GameScanIssue.ExeMissing => string.Format(Str.ErrScanExeMissingFmt, r.Detail ?? ""),
            GameScanner.GameScanIssue.LanguageFilesMissing => Str.ErrScanLanguageMissing,
            GameScanner.GameScanIssue.AccessDenied => string.Format(Str.ErrScanAccessDeniedFmt, r.Detail ?? ""),
            GameScanner.GameScanIssue.TransientLock => string.Format(Str.ErrScanTransientLockFmt, r.Detail ?? ""),
            GameScanner.GameScanIssue.PartialAccess => string.Format(Str.ErrScanPartialAccessFmt, r.Detail ?? ""),
            _ => string.Format(Str.ErrScanOtherErrorFmt, r.Detail ?? ""),
        };
    }

    public void RestoreBackups()
    {
        var (restored, failed) = BackupService.Restore(GamePath, Log.Line);
        BackupLine = failed == 0
            ? string.Format(Str.EditRestoredFmt, restored)
            : string.Format(Str.EditRestoredFailedFmt, restored, failed);
        CanRestore = false;
        Restored = true;
    }

    public void ResetSettings()
    {
        var keepPath = _settings.GamePath;
        var d = new AppSettings();

        _settings.LastLanguage = d.LastLanguage;
        _settings.LastStationBank = d.LastStationBank;
        _settings.TargetLufs = d.TargetLufs;
        _settings.TargetTruePeak = d.TargetTruePeak;
        _settings.VorbisQuality = d.VorbisQuality;
        _settings.EncodeParallelism = d.EncodeParallelism;
        _settings.MarkerDefaults = new();
        _settings.WaveformLabelRows = new();
        _settings.LoopAutoTune = d.LoopAutoTune;
        _settings.LoopMinSeconds = d.LoopMinSeconds;
        _settings.LoopNoteDeviation = d.LoopNoteDeviation;
        _settings.LoopMinMatch = d.LoopMinMatch;
        _settings.LoopPreEmphasis = d.LoopPreEmphasis;
        _settings.LoopMultiResolution = d.LoopMultiResolution;
        _settings.LoopDisablePruning = d.LoopDisablePruning;
        _settings.SettingsVersion = SettingsService.CurrentSettingsVersion;
        _settings.GamePath = keepPath;

        SettingsService.Save(_settings);
        MarkerDefaults.Reset();

        TargetLufs = _settings.TargetLufs;
        EncodeParallelism = AppSettings.RecommendedParallelism;

        Saved = true;
    }

    public void Commit()
    {
        _settings.GamePath = GamePath;
        _settings.TargetLufs = Math.Round(TargetLufs);
        _settings.EncodeParallelism = EncodeParallelism;
        SettingsService.Save(_settings);
        Saved = true;
    }
}
