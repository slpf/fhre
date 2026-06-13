using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using FH6RB.Services;

namespace FH6RB.ViewModels;

public sealed partial class SettingsWindowViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    public string AppVersion =>
        $"v{Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0"}";

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
    [NotifyPropertyChangedFor(nameof(BackupVisible))]
    private bool _hasBackups;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackupVisible))]
    private string _restoreLine = "";

    public bool BackupVisible => HasBackups || !string.IsNullOrWhiteSpace(RestoreLine);
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAdvanced))]
    private bool _isFirstRun;

    public bool ShowAdvanced => !IsFirstRun;

    public bool Saved { get; private set; }

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

    public async Task ValidateAsync()
    {
        _validateCts?.Cancel();
        var cts = new CancellationTokenSource();
        _validateCts = cts;
        var token = cts.Token;

        var path = GamePath;
        Scanning = true;
        IsValid = false;
        Error = "";

        try
        {
            var r = await Task.Run(() =>
            {
                var hasBackups = BackupService.Has(path);
                var exe = GameScanner.FindExe(path);
                var langs = string.IsNullOrWhiteSpace(path) ? [] : GameScanner.LanguageFiles(path);
                var banks = string.IsNullOrWhiteSpace(path) ? [] : GameScanner.RadioBankNames(path);
                return (hasBackups, exe, langCount: langs.Count, bankCount: banks.Count);
            }, token);

            if (token.IsCancellationRequested)
            {
                return;
            }

            HasBackups = r.hasBackups;
            IsValid = r.exe is not null && r.langCount > 0;

            if (IsValid)
            {
                ExeLine = $"Found {Path.GetFileName(r.exe)}";
                LangLine = $"Localizations: {r.langCount}";
                BankLine = $"Radio banks: {r.bankCount}";
                Error = "";
            }
            else
            {
                ExeLine = "";
                LangLine = "";
                BankLine = "";
                Error = "No RadioInfo_*.xml found. Check the game folder, or close the program.";
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

    public void RestoreBackups()
    {
        var (restored, failed) = BackupService.Restore(GamePath, Log.Line);
        RestoreLine = failed == 0
            ? $"Restored {restored} file(s)"
            : $"Restored {restored}, {failed} failed";
        HasBackups = BackupService.Has(GamePath);
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
