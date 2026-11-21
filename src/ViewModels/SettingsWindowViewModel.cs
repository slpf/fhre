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
    [NotifyPropertyChangedFor(nameof(ShowWarning))]
    private bool _isValid;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    [NotifyPropertyChangedFor(nameof(ShowWarning))]
    private bool _scanning;

    [ObservableProperty] private string _scanningText = "";

    public bool ShowError => !IsValid && !Scanning && Error.Length > 0;
    public bool ShowWarning => IsValid && !Scanning && Error.Length > 0;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScanDetails))]
    [NotifyPropertyChangedFor(nameof(HasGameLine))]
    private string _gameLine = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScanDetails))]
    private string _exeLine = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScanDetails))]
    private string _langLine = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScanDetails))]
    private string _bankLine = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    [NotifyPropertyChangedFor(nameof(ShowWarning))]
    private string _error = "";

    public bool HasGameLine => GameLine.Length > 0;
    public bool HasScanDetails => HasGameLine || ExeLine.Length > 0 || LangLine.Length > 0 || BankLine.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetLufsText))]
    private double _targetLufs;

    public string TargetLufsText => $"{TargetLufs:0.#} LUFS";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetTruePeakText))]
    private double _targetTruePeak;

    public string TargetTruePeakText => $"{TargetTruePeak:0.0} dBTP";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditPeakLimiter))]
    private bool _loudnessNormalize;

    [ObservableProperty] private bool _peakLimiter;
    [ObservableProperty] private bool _measureTrackLoudness;

    public bool CanEditPeakLimiter => LoudnessNormalize;

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
    internal GameScanner.GameScanResult? SavedScan { get; private set; }
    private GameScanner.GameScanResult? _validatedScan;

    public SettingsWindowViewModel(AppSettings settings)
    {
        _settings = settings;
        _gamePath = settings.GamePath;
        _targetLufs = AppSettings.ClampTargetLufs(settings.TargetLufs);
        _targetTruePeak = AppSettings.ClampTargetTruePeak(settings.TargetTruePeak);
        _loudnessNormalize = settings.LoudnessNormalize;
        _peakLimiter = settings.PeakLimiter;
        _measureTrackLoudness = settings.MeasureTrackLoudness;
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

    internal void CancelValidation() => _validateCts?.Cancel();

    public async Task ValidateAsync()
    {
        CancelValidation();
        var cts = new CancellationTokenSource();
        _validateCts = cts;
        var token = cts.Token;

        var path = GamePath;
        _validatedScan = null;
        Error = "";
        GameLine = "";
        ExeLine = "";
        LangLine = "";
        BankLine = "";
        Scanning = true;
        IsValid = false;

        try
        {
            var result = await Task.Run(() => GameScanner.Scan(path, token), token);

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
        _validatedScan = result.IsValid ? result : null;
        var found = result.ExePath is not null || result.LanguageFileCount > 0 || result.BankCount > 0;
        GameLine = result.GameName;
        ExeLine = result.ExePath is null ? "" : string.Format(Str.FoundFmt, Path.GetFileName(result.ExePath));
        LangLine = found ? string.Format(Str.LocalizationsFmt, result.LanguageFileCount) : "";
        BankLine = found ? string.Format(Str.RadioBanksFmt, result.BankCount) : "";
        Error = IsValid && result.Issue != GameScanner.GameScanIssue.PartialAccess ? "" : FormatScanError(result);
    }

    internal static string FormatScanError(GameScanner.GameScanResult r)
    {
        return r.Issue switch
        {
            GameScanner.GameScanIssue.EmptyPath => Str.ErrScanEmptyPath,
            GameScanner.GameScanIssue.DirectoryMissing => string.Format(Str.ErrScanDirectoryMissingFmt, r.Detail ?? ""),
            GameScanner.GameScanIssue.ExeMissing => string.Format(Str.ErrScanExeMissingFmt, r.Detail ?? ""),
            GameScanner.GameScanIssue.LanguageFilesMissing => Str.ErrScanLanguageMissing,
            GameScanner.GameScanIssue.RadioBanksMissing => Str.ErrScanRadioBanksMissing,
            GameScanner.GameScanIssue.AccessDenied => string.Format(Str.ErrScanAccessDeniedFmt, r.Detail ?? ""),
            GameScanner.GameScanIssue.ResourceReadDenied => string.Format(Str.ErrScanResourceReadDeniedFmt, r.Detail ?? ""),
            GameScanner.GameScanIssue.EncryptedResources => string.Format(Str.ErrScanEncryptedResourcesFmt, r.Detail ?? ""),
            GameScanner.GameScanIssue.ResourceReadError => string.Format(Str.ErrScanResourceReadErrorFmt, r.Detail ?? ""),
            GameScanner.GameScanIssue.InvalidRadioResource => string.Format(Str.ErrScanInvalidResourceFmt, r.Detail ?? ""),
            GameScanner.GameScanIssue.TransientLock => string.Format(Str.ErrScanTransientLockFmt, r.Detail ?? ""),
            GameScanner.GameScanIssue.PartialAccess => string.Format(Str.ErrScanPartialAccessFmt, r.Detail ?? ""),
            _ => string.Format(Str.ErrScanOtherErrorFmt, r.Detail ?? ""),
        };
    }

    public void ResetSettings()
    {
        var d = new AppSettings();

        SettingsService.Update(_settings, draft =>
        {
            draft.LastLanguage = d.LastLanguage;
            draft.LastStationBank = d.LastStationBank;
            draft.TargetLufs = d.TargetLufs;
            draft.TargetTruePeak = d.TargetTruePeak;
            draft.VorbisQuality = d.VorbisQuality;
            draft.EncodeParallelism = d.EncodeParallelism;
            draft.LoudnessNormalize = d.LoudnessNormalize;
            draft.PeakLimiter = d.PeakLimiter;
            draft.MeasureTrackLoudness = d.MeasureTrackLoudness;
            draft.MarkerDefaults = new();
            draft.WaveformLabelRows = new();
            draft.LoopAutoTune = d.LoopAutoTune;
            draft.LoopMinSeconds = d.LoopMinSeconds;
            draft.LoopNoteDeviation = d.LoopNoteDeviation;
            draft.LoopMinMatch = d.LoopMinMatch;
            draft.LoopPreEmphasis = d.LoopPreEmphasis;
            draft.LoopMultiResolution = d.LoopMultiResolution;
            draft.LoopDisablePruning = d.LoopDisablePruning;
            draft.LoopBorderSimilarity = d.LoopBorderSimilarity;
            draft.LoopTransitionSmoothness = d.LoopTransitionSmoothness;
            draft.LoopLoudnessDifference = d.LoopLoudnessDifference;
            draft.LoopUseHarmonicChroma = d.LoopUseHarmonicChroma;
            draft.LoopRequireOnsetAlignment = d.LoopRequireOnsetAlignment;
            draft.LoopStages = d.LoopStages;
            draft.SettingsVersion = SettingsService.CurrentSettingsVersion;
        });
        MarkerDefaults.Reset();

        TargetLufs = _settings.TargetLufs;
        TargetTruePeak = _settings.TargetTruePeak;
        EncodeParallelism = AppSettings.RecommendedParallelism;
        LoudnessNormalize = _settings.LoudnessNormalize;
        PeakLimiter = _settings.PeakLimiter;
        MeasureTrackLoudness = _settings.MeasureTrackLoudness;
        VorbisQuality = _settings.VorbisQuality;

        Saved = true;
    }

    internal async Task<bool> TryCommitAsync(Func<Task<bool>>? confirmGamePathChange)
    {
        if (!IsValid || Scanning) return false;
        var path = GamePath;
        if (!string.Equals(path, _settings.GamePath, StringComparison.OrdinalIgnoreCase)
            && confirmGamePathChange is not null && !await confirmGamePathChange())
            return false;
        if (!IsValid || Scanning || !string.Equals(path, GamePath, StringComparison.OrdinalIgnoreCase)) return false;

        TargetLufs = AppSettings.ClampTargetLufs(TargetLufs);
        TargetTruePeak = AppSettings.ClampTargetTruePeak(TargetTruePeak);
        SettingsService.Update(_settings, draft =>
        {
            draft.GamePath = path;
            draft.TargetLufs = TargetLufs;
            draft.TargetTruePeak = TargetTruePeak;
            draft.EncodeParallelism = EncodeParallelism;
            draft.LoudnessNormalize = LoudnessNormalize;
            draft.PeakLimiter = PeakLimiter;
            draft.MeasureTrackLoudness = MeasureTrackLoudness;
            draft.VorbisQuality = VorbisQuality;
        });
        SavedScan = _validatedScan;
        Saved = true;
        return true;
    }
}
