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
    private sealed record GameScan(string Path, string? Exe, int LangCount, int BankCount);

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
                var game = cachedGame ?? new GameScan(
                    path,
                    GameScanner.FindExe(path),
                    string.IsNullOrWhiteSpace(path) ? 0 : GameScanner.LanguageFiles(path).Count,
                    string.IsNullOrWhiteSpace(path) ? 0 : GameScanner.RadioBankNames(path).Count);
                
                var hasBackups = BackupService.Has(path);
                var modified = hasBackups && BackupService.HasModified(path);
                return (game, hasBackups, modified);
            }, token);

            if (token.IsCancellationRequested)
            {
                return;
            }

            _gameScanCache = r.game;
            
            if (cachedGame is null)
            {
                ApplyGameScan(r.game);
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
        IsValid = game.Exe is not null && game.LangCount > 0;

        if (IsValid)
        {
            ExeLine = string.Format(Str.FoundFmt, Path.GetFileName(game.Exe));
            LangLine = string.Format(Str.LocalizationsFmt, game.LangCount);
            BankLine = string.Format(Str.RadioBanksFmt, game.BankCount);
            Error = "";
        }
        else
        {
            ExeLine = "";
            LangLine = "";
            BankLine = "";
            Error = Str.ErrNoRadioInfo;
        }
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
