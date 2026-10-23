using System.Reflection;
using Avalonia.Threading;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    private bool _isValid;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    private bool _scanning;

    [ObservableProperty] private string _scanningText = "";

    public bool ShowError => !IsValid && !Scanning;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScanDetails))]
    private string _exeLine = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScanDetails))]
    private string _langLine = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScanDetails))]
    private string _bankLine = "";
    [ObservableProperty] private string _error = "";

    public bool HasScanDetails => ExeLine.Length > 0 || LangLine.Length > 0 || BankLine.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetLufsText))]
    private double _targetLufs;

    public string TargetLufsText => $"{TargetLufs:0.#} LUFS";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetTruePeakText))]
    private double _targetTruePeak;

    public string TargetTruePeakText => $"{TargetTruePeak:0.0} dBTP";

    [ObservableProperty] private bool _loudnessNormalize;

    [ObservableProperty] private bool _dynamicNormalization;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VorbisQualityText))]
    private int _vorbisQuality;

    public string VorbisQualityText => $"{VorbisQuality}%";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EncodeParallelismText))]
    private int _encodeParallelism;

    public int MaxThreads => Environment.ProcessorCount;
    public string EncodeParallelismText => $"{EncodeParallelism} / {MaxThreads}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAdvanced))]
    private bool _isFirstRun;

    public bool ShowAdvanced => !IsFirstRun;

    public bool Saved { get; private set; }

    public SettingsWindowViewModel(AppSettings settings)
    {
        _settings = settings;
        _gamePath = settings.GamePath;
        _targetLufs = AppSettings.ClampTargetLufs(settings.TargetLufs);
        _targetTruePeak = AppSettings.ClampTargetTruePeak(settings.TargetTruePeak);
        _loudnessNormalize = settings.LoudnessNormalize;
        _dynamicNormalization = settings.DynamicNormalization;
        _vorbisQuality = settings.VorbisQuality;
        _encodeParallelism = settings.EncodeParallelism > 0
            ? Math.Min(settings.EncodeParallelism, MaxThreads)
            : AppSettings.RecommendedParallelism;
        Validate();
    }

    partial void OnGamePathChanged(string value) => _ = ValidateAsync();

    private DispatcherTimer? _dotsTimer;
    private int _dots;
    private bool _dotsWired;

    partial void OnScanningChanged(bool value)
    {
        if (value)
        {
            _dots = 0;
            ScanningText = "Scanning";
            _dotsTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };

            if (!_dotsWired)
            {
                _dotsTimer.Tick += OnDotsTick;
                _dotsWired = true;
            }

            _dotsTimer.Start();
        }
        else
        {
            _dotsTimer?.Stop();
        }
    }

    private void OnDotsTick(object? sender, EventArgs e)
    {
        _dots = (_dots + 1) % 4;
        ScanningText = "Scanning" + new string('.', _dots);
    }

    public void Validate() => _ = ValidateAsync();

    private CancellationTokenSource? _validateCts;

    public async Task ValidateAsync()
    {
        _validateCts?.Cancel();
        var cts = new CancellationTokenSource();
        _validateCts = cts;
        var token = cts.Token;

        var path = GamePath;
        Error = "";
        ExeLine = "";
        LangLine = "";
        BankLine = "";
        Scanning = true;
        IsValid = false;

        try
        {
            var result = await Task.Run(() => GameScanner.Scan(path), token);

            if (token.IsCancellationRequested)
            {
                return;
            }

            ApplyGameScan(result);
        }
        catch (OperationCanceledException)
        {

        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            Log.Line($"GameScanner failed for \"{path}\": {ex}");
            ApplyGameScan(new GameScanner.GameScanResult(false, GameScanner.GameScanIssue.OtherError,
                ex.Message, null, 0, 0));
        }
        finally
        {
            if (_validateCts == cts)
            {
                _validateCts = null;
                Scanning = false;
            }
            cts.Dispose();
        }
    }

    private void ApplyGameScan(GameScanner.GameScanResult result)
    {
        IsValid = result.IsValid;
        var found = result.ExePath is not null || result.LanguageFileCount > 0 || result.BankCount > 0;
        var showCounts = found && result.Issue != GameScanner.GameScanIssue.Horizon3Disabled;
        ExeLine = result.ExePath is null ? "" : string.Format(Str.FoundFmt, Path.GetFileName(result.ExePath));
        LangLine = showCounts ? string.Format(Str.LocalizationsFmt, result.LanguageFileCount) : "";
        BankLine = showCounts ? string.Format(Str.RadioBanksFmt, result.BankCount) : "";
        Error = IsValid ? "" : FormatScanError(result);
    }

    internal static string FormatScanError(GameScanner.GameScanResult r)
    {
        return r.Issue switch
        {
            GameScanner.GameScanIssue.EmptyPath => Str.ErrScanEmptyPath,
            GameScanner.GameScanIssue.DirectoryMissing => string.Format(Str.ErrScanDirectoryMissingFmt, r.Detail ?? ""),
            GameScanner.GameScanIssue.ExeMissing => string.Format(Str.ErrScanExeMissingFmt, r.Detail ?? ""),
            GameScanner.GameScanIssue.LanguageFilesMissing => Str.ErrScanLanguageMissing,
            GameScanner.GameScanIssue.Horizon3Disabled => Str.ErrScanHorizon3Disabled,
            GameScanner.GameScanIssue.AccessDenied => string.Format(Str.ErrScanAccessDeniedFmt, r.Detail ?? ""),
            GameScanner.GameScanIssue.ResourceReadDenied => string.Format(Str.ErrScanResourceReadDeniedFmt, r.Detail ?? ""),
            GameScanner.GameScanIssue.EncryptedResources => string.Format(Str.ErrScanEncryptedResourcesFmt, r.Detail ?? ""),
            GameScanner.GameScanIssue.ResourceReadError => string.Format(Str.ErrScanResourceReadErrorFmt, r.Detail ?? ""),
            GameScanner.GameScanIssue.TransientLock => string.Format(Str.ErrScanTransientLockFmt, r.Detail ?? ""),
            GameScanner.GameScanIssue.PartialAccess => string.Format(Str.ErrScanPartialAccessFmt, r.Detail ?? ""),
            _ => string.Format(Str.ErrScanOtherErrorFmt, r.Detail ?? ""),
        };
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
        _settings.LoudnessNormalize = d.LoudnessNormalize;
        _settings.DynamicNormalization = d.DynamicNormalization;
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
        TargetTruePeak = _settings.TargetTruePeak;
        EncodeParallelism = AppSettings.RecommendedParallelism;
        LoudnessNormalize = _settings.LoudnessNormalize;
        DynamicNormalization = _settings.DynamicNormalization;
        VorbisQuality = _settings.VorbisQuality;

        Saved = true;
    }

    public void Commit()
    {
        _settings.GamePath = GamePath;
        TargetLufs = AppSettings.ClampTargetLufs(TargetLufs);
        TargetTruePeak = AppSettings.ClampTargetTruePeak(TargetTruePeak);
        _settings.TargetLufs = TargetLufs;
        _settings.TargetTruePeak = TargetTruePeak;
        _settings.EncodeParallelism = EncodeParallelism;
        _settings.LoudnessNormalize = LoudnessNormalize;
        _settings.DynamicNormalization = DynamicNormalization;
        _settings.VorbisQuality = VorbisQuality;
        SettingsService.Save(_settings);
        Saved = true;
    }
}
