using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using FH6RB.Assets;
using FH6RB.Collections;
using FH6RB.Core;
using FH6RB.Services;

namespace FH6RB.ViewModels;

public sealed record LangOption(string Code, string FileName)
{
    public string Display => FileName == MainWindowViewModel.AllLanguagesFile ? Str.LangAll : FileName;
}

public enum RestoreResult { Ok, NoBackup, NotInBackup }

public sealed partial class MainWindowViewModel : ObservableObject
{
    public AppSettings Settings { get; }

    public const string AllLanguagesFile = "*";

    public ObservableCollection<LangOption> Languages { get; } = [];
    public ObservableCollection<StationInfo> Stations { get; } = [];
    public ObservableCollection<string> Variants { get; } = [];
    public RangeObservableCollection<TrackItemViewModel> Tracks { get; } = [];
    public RangeObservableCollection<TrackItemViewModel> FilteredTracks { get; } = [];

    [ObservableProperty] private LangOption? _selectedLanguage;
    [ObservableProperty] private StationInfo? _selectedStation;
    [ObservableProperty] private string? _selectedVariant;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isBuilding;
    [ObservableProperty] private bool _isAddingTracks;
    [ObservableProperty] private bool _isBackingUp;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private bool _isImporting;

    public bool IsBusy => IsBuilding || IsAddingTracks || IsBackingUp || IsTesting || IsExporting || IsImporting;
    private string? _resourceError;
    public bool HasResourceError => _resourceError is not null;
    public bool CanBuild => _resourceError is null && CanChangeSelection && !PlaylistLow && Tools.CanBuild;
    public bool CanChangeSelection => !IsBusy && !IsLoading && !_confirmingSwitch;
    public bool CanUseLoadedBank => CanChangeSelection && IsLoadedBankCurrent(_loadGen);
    public bool CanAddTracks => CanUseLoadedBank && Tools.CanAddTracks;
    public string AddTracksTip => _resourceError ?? (Tools.CanAddTracks ? Str.TipAddTrack
        : string.Format(Str.TipMissingToolsFmt, string.Join(", ", Tools.MissingForAddTracks())));
    public string BuildTip => _resourceError ?? (Tools.CanBuild ? Str.TipBuildBank
        : string.Format(Str.TipMissingToolsFmt, string.Join(", ", Tools.MissingForBuild())));

    private void SetResourceError(string? error)
    {
        _resourceError = error;
        OnPropertyChanged(nameof(HasResourceError));
        OnPropertyChanged(nameof(CanUseLoadedBank));
        RefreshToolAvailability();
    }

    public void RefreshToolAvailability()
    {
        OnPropertyChanged(nameof(CanBuild));
        OnPropertyChanged(nameof(CanAddTracks));
        OnPropertyChanged(nameof(AddTracksTip));
        OnPropertyChanged(nameof(BuildTip));
        foreach (var track in Tracks) track.RefreshToolAvailability();
        ScheduleLoudnessMeasurements();
    }

    public bool HasUnbuiltTracks => Tracks.Any(t => t.IsUnbuilt || t.IsReplacing);

    public bool IsGameRunning()
    {
        return GameScanner.IsGameProcessRunning(Settings.GamePath);
    }

    [ObservableProperty] private bool _hasUnsavedChanges;
    public void MarkDirty() => HasUnsavedChanges = true;
    partial void OnIsBuildingChanged(bool value)
        => NotifyBusyChanged();

    private void NotifyBusyChanged()
    {
        ScheduleLoudnessMeasurements();
        OnPropertyChanged(nameof(IsBusy));
        NotifySelectionAvailability();
    }

    private void NotifySelectionAvailability()
    {
        OnPropertyChanged(nameof(CanChangeSelection));
        OnPropertyChanged(nameof(CanUseLoadedBank));
        OnPropertyChanged(nameof(CanAddTracks));
        OnPropertyChanged(nameof(CanBuild));
    }

    partial void OnPlaylistLowChanged(bool value) => OnPropertyChanged(nameof(CanBuild));
    partial void OnIsAddingTracksChanged(bool value) => NotifyBusyChanged();
    partial void OnIsBackingUpChanged(bool value) => NotifyBusyChanged();
    partial void OnIsTestingChanged(bool value) => NotifyBusyChanged();
    partial void OnIsExportingChanged(bool value) => NotifyBusyChanged();
    partial void OnIsImportingChanged(bool value)
    {
        NotifyBusyChanged();
        OnPropertyChanged(nameof(BusyText));
    }
    public string? PendingErrorDialog { get; set; }
    public string? PendingDialogTitle { get; set; }

    [ObservableProperty] private string _status = "Ready.";

    public string BusyText => IsImporting ? Str.Loading : Status;
    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(BusyText));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusDetail))]
    private string _statusDetail = "";

    public bool HasStatusDetail => !string.IsNullOrEmpty(StatusDetail);
    private const int MinActiveTracksPerStation = 20;
    private const int MinActiveTracksHorizon2 = 20;
    private const int MinActiveTracksHorizon3 = 12;
    private int _minimumActiveTracks = MinActiveTracksPerStation;
    public int MinimumActiveTracks => _minimumActiveTracks > 0 ? _minimumActiveTracks : MinActiveTracksPerStation;
    private const long WarnBankBytes = 1900L * 1024 * 1024;
    private int _otherBanksActive;

    [ObservableProperty] private string _statusHeadText = "";
    [ObservableProperty] private string _statusCustomText = "";
    [ObservableProperty] private string _statusPlaylistText = "";
    [ObservableProperty] private string _statusSizeText = "";
    [ObservableProperty] private bool _playlistLow;
    [ObservableProperty] private bool _bankSizeHigh;
    [ObservableProperty] private string _playlistTip = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearch))]
    [NotifyPropertyChangedFor(nameof(ShowClear))]
    private string _searchText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowClear))]
    private bool _isSearchOpen;

    public bool HasSearch => !string.IsNullOrWhiteSpace(SearchText);
    public bool ShowClear => IsSearchOpen && HasSearch;

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private RadioInfo? _radio;
    private GameScanner.RadioResources? _radioResources;
    private int? _gameVersion;
    private string? _radioForFile;
    private int _nextSeq;
    private HashSet<ulong>? _occupiedTrackIds;
    private RadioInfo? _loadedRadio;
    private readonly Dictionary<string, FileStream> _sourceLocks = new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressReload;
    private bool _confirmingSwitch;
    private int _loadGen;
    private (LangOption Lang, int StationNumber, string Variant)? _loadedSelection;
    
    public Func<Task<bool>>? ConfirmDiscardChanges { get; set; }
    
    private readonly PlaybackService _player = new();
    private TrackItemViewModel? _nowPlaying;
    private sealed record FilePreview(string Path, double GainDb, bool Normalize, bool PeakLimiter,
        double Lufs, double TruePeak);
    private FilePreview? _requestedFilePreview;
    private double _previewResumeSeconds;
    private bool _previewResumePaused;
    private string? _loadedBankPath;
    private BankFileSnapshot? _loadedBankSnapshot;
    public string? LoadedBankPath => _loadedBankPath;
    internal int BankLoadGeneration => _loadGen;
    internal bool IsLoadedBankCurrent(int generation) =>
        generation == _loadGen && !IsLoading && !HasResourceError && _loadedBankPath is not null
        && _loadedSelection is { } loaded && loaded.Lang == SelectedLanguage
        && loaded.StationNumber == SelectedStation?.Number && loaded.Variant == SelectedVariant;
    private int _restoreAvailabilityGeneration;
    public bool CanRestoreAll { get; private set; }
    internal bool HasValidGamePath { get; private set; }
    internal string? GameRecoveryError { get; private set; }
    internal Task RestoreAvailability { get; private set; } = Task.CompletedTask;
    public long ProjectedTotalBytes => _projectedTotalBytes;

    private async Task RefreshCanRestoreAllAsync()
    {
        var generation = ++_restoreAvailabilityGeneration;
        var gamePath = Settings.GamePath;
        var has = await Task.Run(() => BackupService.Has(gamePath));
        if (generation != _restoreAvailabilityGeneration
            || !string.Equals(gamePath, Settings.GamePath, StringComparison.OrdinalIgnoreCase)) return;

        SetCanRestoreAll(has);
    }

    internal void SetCanRestoreAll(bool available)
    {
        if (CanRestoreAll == available) return;
        CanRestoreAll = available;
        OnPropertyChanged(nameof(CanRestoreAll));
    }

    private long _baseBankSize;
    private long _baseBankAudioBytes;
    private long _baseBankOverhead;
    private int _baseBankMode = 15;
    private long _projectedTotalBytes;
    private bool _sizeIsApprox;
    private DispatcherTimer? _tick;
    private readonly DispatcherTimer _searchDebounce;
    private readonly DispatcherTimer _seekDebounce;
    private double _pendingSeekSec;
    private int _playGen;
    private CancellationTokenSource? _playbackCts;
    private CancellationTokenSource? _markerPreparationCts;
    private bool _suppressSeek;
    private double _anchorSec;
    private long _anchorTicks;
    private bool _clockRunning;

    private void SetClockAnchor(double sec, bool running)
    {
        _anchorSec = sec;
        _anchorTicks = Stopwatch.GetTimestamp();
        _clockRunning = running;
    }

    private double ElapsedSinceAnchor()
    {
        if (!_clockRunning) return _anchorSec;
        return _anchorSec + (Stopwatch.GetTimestamp() - _anchorTicks) / (double) Stopwatch.Frequency;
    }

    [ObservableProperty] private bool _transportVisible;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TogglePlayCommand))]
    private bool _isPreparingPlayback;
    [ObservableProperty] private bool _transportPlaying;
    [ObservableProperty] private string _nowTitle = "";
    [ObservableProperty] private string? _nowArtist;
    [ObservableProperty] private double _positionSeconds;
    [ObservableProperty] private double _durationSeconds = 1;
    [ObservableProperty] private string _nowPositionText = "0:00";
    [ObservableProperty] private string _nowDurationText = "0:00";

    public TrackItemViewModel? NowPlaying => _nowPlaying;

    public MainWindowViewModel(AppSettings settings)
    {
        Settings = settings;

        _player.Ended += error =>
        {
            if (error is not null)
            {
                Log.Line("playback error: " + error);
                Status = string.Format(Str.StatusPlaybackErrorFmt, error.Message);
            }
            StopPlayback();
        };
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _tick.Tick += (_, _) =>
        {
            if (_seekDebounce!.IsEnabled) return;
            _suppressSeek = true;
            var pos = ElapsedSinceAnchor();
            if (pos >= 0 && pos <= DurationSeconds)
            {
                PositionSeconds = pos;
                NowPositionText = Fmt(TimeSpan.FromSeconds(pos));
            }
            _suppressSeek = false;
        };

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); RefreshFilter(); };

        Tracks.CollectionChanged += (_, _) =>
        {
            ReleaseUnusedSourceLocks();
            RefreshFilter();
            RefreshLoudnessTracking();
        };

        _seekDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _seekDebounce.Tick += (_, _) =>
        {
            _seekDebounce.Stop();
            if (_player.HasMedia)
            {
                _player.Position = TimeSpan.FromSeconds(_pendingSeekSec);
                SetClockAnchor(_pendingSeekSec, _clockRunning);
            }
        };

        try
        {
            if (Directory.Exists(RestoreDir))
            {
                foreach (var f in Directory.EnumerateFiles(RestoreDir, "*.fsb"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }

        ReloadFromGame();
    }

    public void ReloadFromGame() => Dispatcher.UIThread.Invoke(ReloadFromGameCore);

    internal void ReloadFromGame(GameScanner.GameScanResult? validatedScan) =>
        Dispatcher.UIThread.Invoke(() => ReloadScannedGame(validatedScan));

    private void ReloadFromGameCore() => ReloadScannedGame(null);

    private void ReloadScannedGame(GameScanner.GameScanResult? validatedScan)
    {
        _loadGen++;
        var wasSuppressed = _suppressReload;
        _suppressReload = true;

        try
        {
            Languages.Clear();
            Stations.Clear();
            Variants.Clear();
            _radio = null;
            _radioForFile = null;
            _loadedSelection = null;
            HasUnsavedChanges = false;
            SelectedStation = null;
            SelectedVariant = null;

            GameRecoveryError = null;
            try
            {
                if (GameFileTransaction.RecoverPending(Settings.GamePath)) validatedScan = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException)
            {
                Log.Line("game file recovery failed: " + ex);
                GameRecoveryError = string.Format(Str.StatusGameRecoveryFailedFmt, ex.Message);
            }

            var scan = GameRecoveryError is null ? validatedScan ?? GameScanner.Scan(Settings.GamePath)
                : new GameScanner.GameScanResult(false, GameScanner.GameScanIssue.OtherError, GameRecoveryError, null, 0, 0);
            _radioResources = scan.Resources;
            _gameVersion = scan.Version;
            UpdatePlaylistMinimum();
            HasValidGamePath = scan.IsValid;
            RestoreAvailability = RefreshCanRestoreAllAsync();

            if (!scan.IsValid)
            {
                SelectedLanguage = null;
                SelectedStation = null;
                SelectedVariant = null;
                ClearLoadedTracks();
                var error = GameRecoveryError ?? SettingsWindowViewModel.FormatScanError(scan);
                SetResourceError(error);
                Status = error;
                return;
            }

            SetResourceError(null);

            foreach (var file in scan.RadioLanguages)
            {
                var code = file.Replace("RadioInfo_", "").Replace(".xml", "");
                Languages.Add(new LangOption(code, file));
            }

            if (Languages.Count > 1)
            {
                Languages.Insert(0, new LangOption("ALL", AllLanguagesFile));
            }

            SelectedLanguage = Languages.FirstOrDefault(l => l.FileName == Settings.LastLanguage)
                ?? Languages.FirstOrDefault(l => l.FileName == AllLanguagesFile)
                ?? Languages.FirstOrDefault();

            if (!RebuildStations(scan.RadioBanks))
            {
                ClearLoadedTracks();
                return;
            }

            SelectedStation = Stations.FirstOrDefault(s => s.Prefix == Settings.LastStationBank)
                ?? Stations.FirstOrDefault();
        }
        finally
        {
            _suppressReload = wasSuppressed;
        }
        if (!_suppressReload)
        {
            _ = LoadAsync();
        }
    }

    private RadioInfo? ReadSelectedRadio(out string? error)
    {
        error = null;
        var file = ViewLanguageFile();
        if (file is null)
        {
            error = Str.ErrScanLanguageMissing;
            return null;
        }

        if (_radioForFile == file && _radio is not null)
        {
            return _radio;
        }

        var path = _radioResources is null
            ? GameScanner.RadioInfoPathByFile(Settings.GamePath, file)
            : _radioResources.LanguagePath(file);
        try
        {
            if (path is not null) return RadioInfo.Load(path);
            error = string.Format(Str.ErrScanOtherErrorFmt, file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            Log.Line($"RadioInfo load failed ({file}): {ex.Message}");
            error = string.Format(Str.ErrScanOtherErrorFmt, $"{file}: {ex.Message}");
        }

        return null;
    }

    private string? ViewLanguageFile()
    {
        if (SelectedLanguage is null) return null;
        if (SelectedLanguage.FileName != AllLanguagesFile) return SelectedLanguage.FileName;

        var real = Languages.Where(l => l.FileName != AllLanguagesFile).ToList();
        var english = real.FirstOrDefault(l => l.Code.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        return (english ?? real.FirstOrDefault())?.FileName;
    }

    private bool RebuildStations(IReadOnlyList<string>? radioBanks = null)
    {
        var radio = ReadSelectedRadio(out var error);
        var stations = radio is null ? [] : RadioCatalog.Build(
            radioBanks ?? _radioResources?.Banks ?? GameScanner.RadioBankNames(Settings.GamePath), radio);
        if (stations.Count == 0)
        {
            Status = error ?? Str.ErrScanNoMatchingStations;
            if (_loadedSelection is null) SetResourceError(Status);
            return false;
        }

        _radio = radio;
        _radioForFile = ViewLanguageFile();
        var current = SelectedStation?.Number;
        var variant = SelectedVariant;
        Stations.Clear();

        foreach (var st in stations)
        {
            Stations.Add(st);
        }

        SelectedStation = Stations.FirstOrDefault(s => s.Number == current) ?? Stations.FirstOrDefault();
        if (SelectedStation?.Number == current && variant is not null && Variants.Contains(variant))
            SelectedVariant = variant;
        return true;
    }

    private void ClearLoadedTracks()
    {
        CancelLoudnessMeasurements();
        StopPlayback();
        Tracks.Clear();
        FilteredTracks.Clear();
        _loadedSelection = null;
        _loadedBankPath = null;
        _loadedBankSnapshot = null;
        _loadedRadio = null;
        _occupiedTrackIds = null;
        _otherBanksActive = 0;
        _projectedTotalBytes = 0;
        Recount();
        IsLoading = false;
    }

    private void UpdatePlaylistMinimum()
    {
        var minimum = (_gameVersion ?? GameScanner.DetectVersion(Settings.GamePath)) switch
        {
            2 => MinActiveTracksHorizon2,
            3 => MinActiveTracksHorizon3,
            _ => MinActiveTracksPerStation
        };
        if (_minimumActiveTracks == minimum) return;
        _minimumActiveTracks = minimum;
        OnPropertyChanged(nameof(MinimumActiveTracks));
    }

    partial void OnSelectedLanguageChanged(LangOption? value)
    {
        if (!_suppressReload) RequestLoadAsync();
    }

    partial void OnSelectedStationChanged(StationInfo? oldValue, StationInfo? newValue)
    {
        var wasSuppressed = _suppressReload;
        _suppressReload = true;

        var previous = oldValue?.Number == newValue?.Number ? SelectedVariant : null;
        Variants.Clear();

        if (newValue is not null)
        {
            foreach (var v in newValue.Variants)
            {
                Variants.Add(v);
            }
        }

        SelectedVariant = previous is not null && Variants.Contains(previous) ? previous : PreferredVariant(newValue);

        _suppressReload = wasSuppressed;
        if (!_suppressReload)
        {
            RequestLoadAsync();
        }
    }

    private string? PreferredVariant(StationInfo? station)
    {
        if (station is null) return null;
        var choices = station.Variants.OrderBy(variant => variant != "Tracks").ToArray();
        return choices.FirstOrDefault(variant =>
            _radioResources?.BankPath(station.BankName(variant)) is { } path && FevBank.ReadStblCountFromFile(path) > 0)
            ?? choices.FirstOrDefault();
    }

    partial void OnSelectedVariantChanged(string? value)
    {
        if (!_suppressReload)
        {
            RequestLoadAsync();
        }
    }

    private void RequestLoadAsync()
    {
        _markerPreparationCts?.Cancel();
        if (!_confirmingSwitch) _ = SwitchSelectionAsync();
    }

    private async Task SwitchSelectionAsync()
    {
        if (HasUnsavedChanges)
        {
            _confirmingSwitch = true;
            NotifySelectionAvailability();
            try
            {
                if (ConfirmDiscardChanges is not { } confirm || !await confirm())
                {
                    RestoreLoadedSelection();
                    return;
                }
            }
            finally
            {
                _confirmingSwitch = false;
                NotifySelectionAvailability();
            }
        }

        if (SelectedLanguage != _loadedSelection?.Lang)
        {
            _suppressReload = true;
            try
            {
                if (!RebuildStations())
                {
                    RestoreLoadedSelection();
                    return;
                }
            }
            finally
            {
                _suppressReload = false;
            }
        }

        await LoadAsync();
    }

    private void RestoreLoadedSelection()
    {
        var sel = _loadedSelection;
        if (sel is null) return;

        var wasSuppressed = _suppressReload;
        _suppressReload = true;

        try
        {
            if (SelectedLanguage != sel.Value.Lang)
            {
                SelectedLanguage = sel.Value.Lang;
            }

            var station = Stations.FirstOrDefault(s => s.Number == sel.Value.StationNumber);

            if (station is not null && !Equals(SelectedStation, station))
            {
                SelectedStation = station;
            }

            if (Variants.Contains(sel.Value.Variant))
            {
                SelectedVariant = sel.Value.Variant;
            }
        }
        finally
        {
            _suppressReload = wasSuppressed;
            NotifySelectionAvailability();
        }
    }

    public Task ReloadAsync()
    {
        _radioForFile = null;
        _radio = null;
        return LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (SelectedStation is null || SelectedLanguage is null || SelectedVariant is null)
        {
            return;
        }

        var gen = ++_loadGen;
        var station = SelectedStation;
        var bank = station.BankName(SelectedVariant);
        var selection = (Lang: SelectedLanguage, StationNumber: station.Number, Variant: SelectedVariant);

        Log.Line($"LoadAsync #{gen}: lang={selection.Lang.FileName} station=#{station.Number} variant={selection.Variant} bank={bank}");

        Dispatcher.UIThread.Invoke(() =>
        {
            StopPlayback();
            IsLoading = true;
            Status = string.Format(Str.StatusLoadingFmt, bank);
        });

        var radio = ReadSelectedRadio(out var error);
        if (radio is null)
        {
            ClearLoadedTracks();
            Status = error ?? Str.ErrScanLanguageMissing;
            SetResourceError(Status);
            return;
        }
        _radio = radio;
        _radioForFile = ViewLanguageFile();
        var path = _radioResources is null ? GameScanner.BankPath(Settings.GamePath, bank) : _radioResources.BankPath(bank);

        var result = path is null
            ? new BankReadResult([], "bank file not found")
            : await Task.Run(() => BankReader.ReadTracks(path, radio, station.Number));
        var baseSize = result.Snapshot?.Stamp.Length ?? 0;
        var baseMode = result.Mode == 0 ? 15 : result.Mode;
        if (baseMode == 10) await XmaEncoderService.CheckAsync(Log.Line);

        if (gen != _loadGen)
        {
            Log.Line($"LoadAsync #{gen}: superseded by #{_loadGen}, discarding");
            return;
        }

        Dispatcher.UIThread.Invoke(() =>
        {
            _loadedSelection = selection;
            _loadedBankPath = path;
            _loadedBankSnapshot = result.Snapshot;
            _loadedRadio = radio;
            _occupiedTrackIds = null;
            Tracks.Reset(result.Tracks.Select(track => new TrackItemViewModel(track)));

            _nextSeq = Math.Max(NextCustomSeq(Tracks), (_radio?.MaxCustomSeq() ?? -1) + 1);
            _baseBankSize = baseSize;
            _baseBankAudioBytes = result.Tracks.Sum(track => track.BankBytes);
            _baseBankOverhead = result.Overhead;
            _baseBankMode = baseMode;
            _projectedTotalBytes = baseSize;
            _sizeIsApprox = false;

            var stEditor = _radio?.StationByNumber(station.Number);
            _otherBanksActive = Math.Max(0, (stEditor?.FreeRoamActiveCount() ?? 0) - Tracks.Count(t => t.Enabled));

            if (_baseBankMode == 10) RecalcProjectedSize();
            else Recount();
            HasUnsavedChanges = false;

            Status = result.Error is { } err
                ? string.Format(Str.StatusBankErrorFmt, bank, err)
                : string.Format(Str.StatusLoadedFmt, bank);
            SetResourceError(result.Error is null ? null : Status);
            IsLoading = false;
        });

        Settings.LastLanguage = selection.Lang.FileName;
        Settings.LastStationBank = station.Prefix;
        SettingsService.Save(Settings);
    }
    
    private static int ReconcileStation(RadioStationEditor editor, GameScanner.RadioResources resources,
        Dictionary<string, HashSet<ulong>?> idsByBank, IReadOnlySet<ulong>? candidates = null)
    {
        var union = new HashSet<ulong>();
        foreach (var bank in editor.TrackBankNames())
        {
            if (!idsByBank.TryGetValue(bank, out var ids))
            {
                var path = resources.BankPath(bank);
                ids = path is null ? null : FevBank.ReadCompleteStblIdsFromFile(path);
                idsByBank.Add(bank, ids);
            }

            if (ids is null)
            {
                Log.Line($"reconcile: skipped because the track IDs for {bank} could not be verified");
                return 0;
            }
            union.UnionWith(ids);
        }

        if (union.Count == 0 && candidates is null) return 0;
        return editor.ReconcileTracks(union, Lookup.SoundNameToId, Log.Line, candidates);
    }

    private static int NextCustomSeq(IEnumerable<TrackItemViewModel> list)
    {
        return list
            .Where(t => t.IsCustom)
            .Select(t => int.TryParse(t.SoundName[Naming.CustomPrefix.Length..], out var n) ? n : -1)
            .DefaultIfEmpty(-1)
            .Max() + 1;
    }

    public void Recount()
    {
        var total = Tracks.Count;
        var added = Tracks.Count(t => t.IsCustom);
        var replaced = Tracks.Count(t => !t.IsCustom && (t.Replaced || t.IsReplacing));
        var stationActive = _otherBanksActive + Tracks.Count(t => t.Enabled && !t.IsBankOnly);
        var size = (_sizeIsApprox ? "~" : "") + BankSize.Mb(_projectedTotalBytes);

        var parts = new List<string>();
        if (added > 0) parts.Add($"{added} added");
        if (replaced > 0) parts.Add($"{replaced} replaced");

        StatusHeadText = $"{total} tracks";
        StatusCustomText = parts.Count > 0 ? " · " + string.Join(" · ", parts) : "";
        StatusPlaylistText = $"{stationActive} in playlist";
        StatusSizeText = size;

        PlaylistLow = stationActive < MinimumActiveTracks;
        BankSizeHigh = _projectedTotalBytes > WarnBankBytes;
        PlaylistTip = string.Format(Str.TipStatusPlaylist, stationActive, MinimumActiveTracks);
    }

    public void RecalcProjectedSize()
    {
        if (_baseBankMode == 10)
        {
            var needsEncoder = Tracks.Any(track => (track.UsesFileSource || track.RestoreFromBak)
                && !TryProjectedXmaBytes(track.UsesFileSource ? track.FileSource : track.RestoreFsbPath, out _));
            if (!needsEncoder || XmaEncoderService.IsAvailable)
            {
                long sampleBytes = 0;
                var approximate = 0;
                foreach (var track in Tracks)
                {
                    if (!track.UsesFileSource && !track.RestoreFromBak && track.RestoreFsbPath is null && track.BankBytes > 0)
                    {
                        sampleBytes += track.BankBytes;
                        continue;
                    }
                    var source = track.UsesFileSource ? track.FileSource : track.RestoreFsbPath;
                    if (TryProjectedXmaBytes(source, out var bytes))
                    {
                        sampleBytes += bytes;
                        continue;
                    }
                    sampleBytes += BankSize.EncodedBytes(TrackDurationSeconds(track), 10, Settings.VorbisQuality);
                    approximate++;
                }
                _projectedTotalBytes = _baseBankOverhead + sampleBytes;
                _sizeIsApprox = approximate > 0;
                Log.Line($"projected XMA bank size: {BankSize.Mb(_projectedTotalBytes)} ({approximate} conservative PCM16 estimate(s))");
                Recount();
                return;
            }
            _projectedTotalBytes = _baseBankOverhead + Tracks.Sum(t =>
                BankSize.EncodedBytes(TrackDurationSeconds(t), 15, ProjectedEncodingQuality(t)));
            _sizeIsApprox = Tracks.Count > 0;
            Log.Line($"projected Vorbis bank size: {BankSize.Mb(_projectedTotalBytes)} ({Tracks.Count} track(s))");
            Recount();
            return;
        }

        long added = 0;
        var deletedBytes = Math.Max(0, _baseBankAudioBytes - Tracks.Sum(track => track.BankBytes));
        var removed = deletedBytes;
        var n = 0;

        foreach (var t in Tracks)
        {
            if (IsProjectedNew(t))
            {
                var dur = TrackDurationSeconds(t);
                if (dur <= 0) continue;
                added += BankSize.EncodedBytes(dur, _baseBankMode, ProjectedEncodingQuality(t));
                n++;
            }
            else if (t.IsReplacing || t.RestoreFromBak)
            {
                var dur = TrackDurationSeconds(t);
                if (dur <= 0) continue;
                added += BankSize.EncodedBytes(dur, _baseBankMode, ProjectedEncodingQuality(t));

                if (t.BankBytes > 0)
                {
                    removed += t.BankBytes;
                }
                else if (t.BankSampleRate > 0 && t.BankSampleLength > 0)
                {
                    removed += BankSize.EncodedBytes(
                        t.BankSampleLength / (double) t.BankSampleRate, _baseBankMode, Settings.VorbisQuality);
                }

                n++;
            }
        }

        _projectedTotalBytes = _baseBankSize + added - removed;
        _sizeIsApprox = n > 0 || deletedBytes > 0;
        Log.Line($"projected bank size: {BankSize.Mb(_projectedTotalBytes)} total = base {BankSize.Mb(_baseBankSize)} + {BankSize.Mb(added)} - {BankSize.Mb(removed)} over {n} staged track(s)");
        Recount();
    }

    private static bool TryProjectedXmaBytes(string? path, out long bytes)
    {
        bytes = 0;
        if (path is null || !path.EndsWith(".fsb", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[68];
            stream.ReadExactly(header);
            if (!header[..4].SequenceEqual("FSB5"u8)
                || BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != 1
                || BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) != 1
                || BinaryPrimitives.ReadUInt32LittleEndian(header[24..]) != 10) return false;
            var headerBytes = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
            var nameBytes = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
            var dataBytes = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
            var dataOffset = (long) ((BinaryPrimitives.ReadUInt64LittleEndian(header[60..]) >> 7) & 0x07FFFFFF) * 0x20;
            if (headerBytes < 8 || dataBytes <= dataOffset
                || 60L + headerBytes + nameBytes + dataBytes > stream.Length) return false;
            var length = dataBytes - dataOffset;
            bytes = headerBytes + length + ((0x20 - length % 0x20) % 0x20);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private int ProjectedEncodingQuality(TrackItemViewModel track)
    {
        if (_baseBankMode is not (10 or 15)) return Settings.VorbisQuality;
        var source = track.UsesFileSource ? track.FileSource : track.RestoreFsbPath;
        if (source is null)
        {
            return _baseBankMode == 10 ? BankBuildService.OriginalAudioQuality : Settings.VorbisQuality;
        }
        if (source.EndsWith(".fsb", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (BankBuildService.FsbMode(source) != _baseBankMode || _baseBankMode == 10 && !XmaEncoderService.IsAvailable)
                    return BankBuildService.OriginalAudioQuality;
            }
            catch
            {
            }
        }
        return Settings.VorbisQuality;
    }

    private static bool IsProjectedNew(TrackItemViewModel t)
    {
        return t.IsCustom && t.SourcePath is not null;
    }

    private static double TrackDurationSeconds(TrackItemViewModel t)
    {
        if (t.PendingDurationSeconds is { } pd && pd > 0) return pd;
        return t.SampleRate > 0 && t.SampleLength > 0 ? t.SampleLength / (double)t.SampleRate : 0;
    }

    public void RefreshFilter()
    {
        var q = (SearchText ?? "").Trim();
        FilteredTracks.Reset(q.Length == 0 ? Tracks : Tracks.Where(t => MatchesSearch(t, q)));
    }

    private static bool MatchesSearch(TrackItemViewModel t, string q)
    {
        if ((t.Title ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        if ((t.Artist ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public void ResetAllCustomMarkers()
    {
        if (!CanUseLoadedBank) return;

        var count = 0;

        foreach (var t in Tracks)
        {
            if (!IsResetEligible(t))
            {
                continue;
            }

            t.Markers = t.SampleLength > 0
                ? RadioStationEditor.ComputeAutoMarkers(t.SampleLength, t.SampleRate, t.UsesMilliseconds, t.UsesTrackBreakDown)
                : null;
            count++;
        }

        if (count > 0)
        {
            HasUnsavedChanges = true;
        }

        Status = string.Format(Str.StatusMarkersResetFmt, count);
    }

    private static bool IsResetEligible(TrackItemViewModel t)
    {
        if (t.IsCustom || t.IsReplacing || t.Replaced)
        {
            return true;
        }

        if (t.UsesMilliseconds) return false;
        return t.Markers is { } m
            && m.TryGetValue("DJStart", out var dj) && dj >= 0
            && m.TryGetValue("StingerStart", out var ss) && ss >= 0
            && Math.Abs(dj - ss - 1000) > 5000;
    }

    public string? SaveAllMarkers()
    {
        if (!CanUseLoadedBank || SelectedStation is null || string.IsNullOrEmpty(SelectedVariant))
        {
            Status = Str.StatusMarkersNoBank;
            return null;
        }

        var stamp = DateTime.Now.ToString("yyyy-MM-dd HHmmss");
        var dir = $"{SelectedStation.Name} - {SelectedVariant} - {stamp}";
        var saved = 0;

        foreach (var t in Tracks)
        {
            if (t.Markers is not { Count: > 0 }) continue;

            var filtered = new Dictionary<string, long>();
            var markerNames = EditWindowViewModel.MarkerNamesFor(t.UsesMilliseconds, t.UsesTrackBreakDown);
            foreach (var kv in MarkerPresetService.AdaptMarkers(t.Markers, t.UsesMilliseconds, t.SampleRate, t.UsesTrackBreakDown))
            {
                if (markerNames.Contains(kv.Key))
                {
                    filtered[kv.Key] = kv.Value;
                }
            }

            if (filtered.Count == 0) continue;

            var fname = !string.IsNullOrWhiteSpace(t.Title) || !string.IsNullOrWhiteSpace(t.Artist)
                ? $"{t.Title} - {t.Artist}"
                : t.SoundName;

            try
            {
                MarkerPresetService.SaveIn(dir, fname, t.SampleRate, filtered);
                saved++;
            }
            catch (Exception ex)
            {
                Log.Line($"SaveAllMarkers: failed to save preset '{fname}': {ex.Message}");
            }
        }

        if (saved == 0)
        {
            Status = Str.StatusMarkersNothingToSave;
            return null;
        }

        Status = string.Format(Str.StatusMarkersSavedFmt, saved, dir);
        return dir;
    }

    public async Task<(float[]? Peaks, string? Wav)> PrepareMarkerEditorAsync(TrackItemViewModel track)
    {
        if (IsBusy || IsLoading || _markerPreparationCts is not null || !Tracks.Contains(track))
        {
            return (null, null);
        }

        track.RefreshToolAvailability();
        if (!track.CanEditMarkers)
        {
            Status = track.MarkersTip;
            return (null, null);
        }

        StopPlayback();
        Status = "";
        using var preparation = new CancellationTokenSource();
        _markerPreparationCts = preparation;
        track.MarkersLoading = true;
        var sourcePath = track.FileSource;
        var restorePath = track.RestoreFsbPath;
        try
        {
            var media = await LoadPeaksAsync(track, preparation.Token);
            if (preparation.IsCancellationRequested || IsBusy || IsLoading || !Tracks.Contains(track)
                || track.FileSource != sourcePath || track.RestoreFsbPath != restorePath)
            {
                return (null, null);
            }

            if (media.Wav is null || media.Peaks is not { Length: > 0 } || track.SampleLength <= 0 || track.SampleRate <= 0)
            {
                Status = Str.StatusDecodeFailed;
                return (null, null);
            }

            return media;
        }
        catch (OperationCanceledException) when (preparation.IsCancellationRequested)
        {
            return (null, null);
        }
        finally
        {
            _markerPreparationCts = null;
            track.MarkersLoading = false;
        }
    }

    public async Task<(float[]? Peaks, string? Wav)> LoadPeaksAsync(TrackItemViewModel track, CancellationToken ct = default)
    {
        track.RefreshToolAvailability();
        if (!track.CanPlay)
        {
            Status = track.MarkersTip;
            return (null, null);
        }

        var bankPath = _loadedBankPath;
        var fileSource = track.UsesFileSource ? track.FileSource : null;
        var isFileSourceFsb = track.IsFileSourceFsb;
        var restorePath = track.RestoreFsbPath;
        var subIndex = track.SubIndex;
        var directPath = fileSource ?? restorePath;
        var decodeIndex = directPath is null ? subIndex : 0;
        var probe = fileSource is not null || track.SampleLength <= 0 || track.SampleRate <= 0;
        var settings = NormalizationOverride.Resolve(Settings, track.Normalization);
        var gainDb = track.GainDb ?? 0;
        try
        {
            var (peaks, wav, format) = await Task.Run(() =>
            {
                var wav = fileSource is not null && !isFileSourceFsb
                    ? AudioDecoder.DecodeAdded(fileSource, settings, ct, gainDb)
                    : AudioDecoder.DecodeBank(directPath ?? bankPath!, decodeIndex, ct);
                var peaks = WaveformService.Samples(wav, ct);
                return (peaks, wav, probe ? WaveformService.Probe(wav) : default);
            }, ct);

            ct.ThrowIfCancellationRequested();
            if ((track.UsesFileSource ? track.FileSource : null) != fileSource || track.RestoreFsbPath != restorePath
                || track.SubIndex != subIndex || (directPath is null && _loadedBankPath != bankPath))
            {
                return (null, null);
            }

            var (rate, frames) = format;
            if (rate > 0 && frames > 0)
            {
                var markerRate = track.SampleRate > 0 ? track.SampleRate : track.BankSampleRate;
                if (markerRate > 0 && rate != markerRate && track.Markers is { } markers)
                {
                    var scale = rate / (double)markerRate;
                    track.Markers = markers.ToDictionary(p => p.Key,
                        p => p.Value < 0 ? p.Value : checked((long)Math.Round(p.Value * scale)));
                }
                track.SampleRate = rate;
                track.SampleLength = frames;
            }

            return (peaks, wav);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Line($"waveform.failed sound={track.SoundName}: {ex}");
            return (null, null);
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PlayTrack(TrackItemViewModel? track)
    {
        if (track is null || !track.CanPlay)
        {
            return;
        }

        if (ReferenceEquals(track, _nowPlaying))
        {
            if (IsPreparingPlayback)
            {
                StopPlayback();
                Status = "";
            }
            else
            {
                TogglePlay();
            }
            return;
        }

        await StartAsync(track);
    }

    private bool CanTogglePlay => !IsPreparingPlayback;

    [RelayCommand(CanExecute = nameof(CanTogglePlay))]
    private void TogglePlay()
    {
        if (_nowPlaying is null)
        {
            return;
        }

        _player.TogglePause();
        TransportPlaying = _player.IsPlaying;
        _nowPlaying.PlayState = _player.IsPlaying ? TrackPlayState.Playing : TrackPlayState.Paused;

        if (_player.IsPlaying)
        {
            SetClockAnchor(ElapsedSinceAnchor(), true);
            _tick?.Start();
        }
        else
        {
            SetClockAnchor(ElapsedSinceAnchor(), false);
            _tick?.Stop();
        }
    }

    private FilePreview? GetFilePreview(TrackItemViewModel track)
    {
        if (!track.UsesFileSource || track.IsFileSourceFsb) return null;
        var settings = NormalizationOverride.Resolve(Settings, track.Normalization);
        return new FilePreview(track.FileSource!, track.GainDb ?? 0, settings.LoudnessNormalize,
            settings.PeakLimiter, settings.TargetLufs, settings.TargetTruePeak);
    }

    private async Task StartAsync(TrackItemViewModel track, double fromSec = 0, bool paused = false)
    {
        track.RefreshToolAvailability();
        if (!track.CanPlay)
        {
            StopPlayback();
            Status = track.PlayTip;
            return;
        }

        var gen = ++_playGen;
        _markerPreparationCts?.Cancel();
        _playbackCts?.Cancel();
        using var preparation = new CancellationTokenSource();
        _playbackCts = preparation;
        var ct = preparation.Token;
        var filePreview = GetFilePreview(track);
        _requestedFilePreview = filePreview;
        _previewResumeSeconds = fromSec;
        _previewResumePaused = paused;
        
        _player.Stop();
        _tick?.Stop();
        _seekDebounce.Stop();
        if (_nowPlaying is not null)
        {
            _nowPlaying.PlayState = TrackPlayState.Idle;
        }

        _nowPlaying = track;
        track.PlayState = TrackPlayState.Loading;
        IsPreparingPlayback = true;
        TransportVisible = true;
        TransportPlaying = false;
        NowTitle = track.Title;
        NowArtist = track.Artist;
        PositionSeconds = 0;
        NowPositionText = "0:00";
        Status = string.Format(Str.StatusDecodingFmt, track.Title);

        try
        {
            var bankPath = _loadedBankPath;
            var directPath = (track.UsesFileSource ? track.FileSource : null) ?? track.RestoreFsbPath;
            var decodeIndex = directPath is null ? track.SubIndex : 0;
            var wav = await Task.Run(() =>
            {
                if (filePreview is { } preview)
                {
                    var settings = new AppSettings
                    {
                        LoudnessNormalize = preview.Normalize,
                        PeakLimiter = preview.PeakLimiter,
                        TargetLufs = preview.Lufs,
                        TargetTruePeak = preview.TruePeak
                    };
                    return AudioDecoder.DecodeAdded(preview.Path, settings, ct, preview.GainDb);
                }
                return AudioDecoder.DecodeBank(directPath ?? bankPath!, decodeIndex, ct);
            }, ct);

            if (gen != _playGen)
            {
                return;
            }

            var resumeSeconds = Math.Max(0, fromSec);
            if (resumeSeconds > 0)
            {
                var (rate, frames) = WaveformService.Probe(wav);
                if (rate > 0 && resumeSeconds >= frames / (double) rate) resumeSeconds = 0;
            }
            _player.Play(wav, 0, resumeSeconds);
            if (paused) _player.TogglePause();

            track.PlayState = paused ? TrackPlayState.Paused : TrackPlayState.Playing;
            TransportPlaying = !paused;
            DurationSeconds = Math.Max(0.1, _player.Duration.TotalSeconds);
            NowDurationText = Fmt(_player.Duration);
            _suppressSeek = true;
            PositionSeconds = Math.Clamp(resumeSeconds, 0, DurationSeconds);
            _suppressSeek = false;
            NowPositionText = Fmt(TimeSpan.FromSeconds(PositionSeconds));
            Status = string.Format(Str.StatusPlayingFmt, track.Title);
            SetClockAnchor(PositionSeconds, !paused);
            if (!paused) _tick?.Start();
        }
        catch (Exception ex)
        {
            if (gen == _playGen)
            {
                Log.Line("playback error: " + ex);
                Status = string.Format(Str.StatusPlaybackErrorFmt, ex.Message);
                StopPlayback();
            }
        }
        finally
        {
            if (ReferenceEquals(_playbackCts, preparation)) _playbackCts = null;
            if (gen == _playGen)
            {
                IsPreparingPlayback = false;
            }
        }
    }

    public void StopPlayback()
    {
        _playGen++;
        _playbackCts?.Cancel();
        _markerPreparationCts?.Cancel();
        _tick?.Stop();
        _seekDebounce.Stop();
        SetClockAnchor(0, false);
        _player.Stop();
        if (_nowPlaying is not null)
        {
            _nowPlaying.PlayState = TrackPlayState.Idle;
        }

        _nowPlaying = null;
        _requestedFilePreview = null;
        _previewResumeSeconds = 0;
        _previewResumePaused = false;
        TransportVisible = false;
        TransportPlaying = false;
        IsPreparingPlayback = false;
    }
    
    public void Shutdown()
    {
        _loudnessShutdown = true;
        CancelLoudnessMeasurements();
        if (_loudnessTracks is not null)
            foreach (var track in _loudnessTracks) track.PropertyChanged -= OnLoudnessSourceChanged;
        StopPlayback();
        _player.Dispose();
        foreach (var stream in _sourceLocks.Values) stream.Dispose();
        _sourceLocks.Clear();
        try
        {
            if (Directory.Exists(RestoreDir)) Directory.Delete(RestoreDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Line($"restore: could not remove temporary audio from {RestoreDir}: {ex}");
        }
    }

    private void ReleaseUnusedSourceLocks()
    {
        var active = Tracks.Where(t => t.SourcePath is not null).Select(t => t.SourcePath!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _sourceLocks.Keys.Where(path => !active.Contains(path)).ToList())
        {
            _sourceLocks[path].Dispose();
            _sourceLocks.Remove(path);
        }
    }

    public void RefreshNowPlaying()
    {
        if (_nowPlaying is null)
        {
            return;
        }

        NowTitle = _nowPlaying.Title;
        NowArtist = _nowPlaying.Artist;

        if (GetFilePreview(_nowPlaying) != _requestedFilePreview)
        {
            var position = IsPreparingPlayback ? _previewResumeSeconds : _player.Position.TotalSeconds;
            var paused = IsPreparingPlayback ? _previewResumePaused : _player.IsPaused;
            _ = StartAsync(_nowPlaying, position, paused);
        }
    }

    partial void OnPositionSecondsChanged(double value)
    {
        NowPositionText = Fmt(TimeSpan.FromSeconds(value));
        if (!_suppressSeek && _player.HasMedia)
        {
            _pendingSeekSec = value;
            _seekDebounce.Stop();
            _seekDebounce.Start();
        }
    }

    private static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    public void ToggleEnabled(TrackItemViewModel track, bool above = false, bool below = false)
    {
        if (!track.CanToggle) return;
        var state = !track.Enabled;
        track.Enabled = state;

        if (above || below)
        {
            var idx = Tracks.IndexOf(track);

            for (var i = 0; i < Tracks.Count; i++)
            {
                if (Tracks[i].CanToggle && ((above && i < idx) || (below && i > idx)))
                {
                    Tracks[i].Enabled = state;
                }
            }
        }

        Status = string.Format(state ? Str.StatusEnabledFmt : Str.StatusDisabledFmt, track.SoundName);
        HasUnsavedChanges = true;
        Recount();
    }

    [RelayCommand]
    private void Delete(TrackItemViewModel track)
    {
        if (!track.CanDelete)
        {
            return;
        }

        if (ReferenceEquals(track, _nowPlaying))
        {
            StopPlayback();
        }

        Tracks.Remove(track);
        Status = string.Format(Str.StatusDeletedFmt, track.SoundName);
        HasUnsavedChanges = true;
        RecalcProjectedSize();
    }

    private RadioStationEditor? LoadBakEditor(StationInfo station)
    {
        var refFile = ViewLanguageFile();
        var refXml = refFile is not null ? GameScanner.RadioInfoPathByFile(Settings.GamePath, refFile) : null;

        if (refXml is not null && File.Exists(refXml + ".bak"))
        {
            try { return RadioInfo.Load(refXml + ".bak").StationByNumber(station.Number); }
            catch (Exception ex) { Log.Line($"LoadBakEditor: .bak load failed ({Path.GetFileName(refXml)}): {ex.Message}"); }
        }

        return _radio?.StationByNumber(station.Number);
    }

    private static readonly string RestoreDir = AppPaths.SessionDirectory("restore");

    private static string WriteRestoreFsb(byte[] header60, byte[] sampleHeader, byte[] data)
    {
        Directory.CreateDirectory(RestoreDir);
        var fsb = Fsb5.BuildSingleSample(header60, new Fsb5Sample { Header = sampleHeader, Data = data });
        var path = Path.Combine(RestoreDir, Guid.NewGuid().ToString("N") + ".fsb");
        File.WriteAllBytes(path, fsb);
        return path;
    }

    public RestoreResult RestoreDefault(TrackItemViewModel track)
    {
        var station = SelectedStation;

        if (station is null || !track.CanRestoreDefault)
        {
            return RestoreResult.Ok;
        }

        if (track.Replaced)
        {
            var bankPath = LoadedBankPath;

            if (string.IsNullOrEmpty(bankPath) || !File.Exists(bankPath + ".bak"))
            {
                return RestoreResult.NoBackup;
            }

            try
            {
                var bakPath = bankPath + ".bak";
                using var backup = File.OpenRead(bakPath);
                var sk = FevBank.ReadSkeleton(backup);
                var hash = track.BankId ?? Lookup.SoundNameToId(track.SoundName);
                var bidx = -1;

                foreach (var (id, index) in sk.Stbl)
                {
                    if (id == hash) { bidx = index; break; }
                }

                if (sk.Empty || bidx < 0)
                {
                    return RestoreResult.NotInBackup;
                }

                var (bakHeader60, bakLayout) = Fsb5.ReadLayout(sk.Fsb5HeaderRegion);
                var bsl = bakLayout[bidx];

                var data = new byte[bsl.DataLen];
                backup.Position = sk.DataStartAbs + bsl.DataOff;
                backup.ReadExactly(data);

                track.RestoreBakIndex = bidx;
                track.RestoreFsbPath = WriteRestoreFsb(bakHeader60, bsl.Header, data);
                track.SampleLength = bsl.Frames;
                track.SampleRate = bsl.SampleRate;
            }
            catch
            {
                return RestoreResult.NotInBackup;
            }
        }

        var editor = LoadBakEditor(station);

        if (editor is not null)
        {
            if (editor.GetSampleMeta(track.SoundName) is { } mm)
            {
                track.Title = mm.DisplayName ?? track.SoundName;
                track.Artist = mm.Artist;
            }

            var markerRate = track.Replaced ? track.SampleRate : track.BankSampleRate;
            var mk = editor.GetMarkers(track.SoundName, markerRate);
            track.Markers = mk is not null && mk.Count > 0 ? new Dictionary<string, long>(mk) : null;
        }

        if (track.Replaced)
        {
            track.RestoreFromBak = true;
        }
        else
        {
            track.SampleLength = track.BankSampleLength;
            track.SampleRate = track.BankSampleRate;
        }

        track.PendingDurationSeconds = null;
        track.ReplacementPath = null;
        track.Replaced = false;
        track.GainDb = null;
        track.Normalization = null;

        Status = string.Format(Str.StatusRestoreTrackFmt, track.SoundName);
        HasUnsavedChanges = true;
        RefreshNowPlaying();
        RecalcProjectedSize();
        RefreshFilter();
        return RestoreResult.Ok;
    }

    public TrackItemViewModel CreateCustomStub(string sourcePath, string? title = null, string? artist = null,
                                               double durationSeconds = 0)
    {
        var occupied = GetOccupiedTrackIds();
        var soundName = AllocateCustomSoundName(occupied, ref _nextSeq);
        var version = _gameVersion ?? GameScanner.DetectVersion(Settings.GamePath);
        const int sampleRate = 48000;
        var track = CreateCustomTrack(soundName, sourcePath, title, artist, version, sampleRate,
            (long)(durationSeconds * sampleRate));
        LockCustomSource(sourcePath);
        return track;
    }

    internal void AddCustomTracks(IReadOnlyList<(string Path, string Title, string? Artist, double Duration)> audio)
    {
        if (audio.Count == 0) return;

        var occupied = new HashSet<ulong>(GetOccupiedTrackIds());
        var nextSeq = _nextSeq;
        var version = _gameVersion ?? GameScanner.DetectVersion(Settings.GamePath);
        const int sampleRate = 48000;
        var additions = new List<TrackItemViewModel>(audio.Count);
        foreach (var file in audio)
        {
            var soundName = AllocateCustomSoundName(occupied, ref nextSeq);
            additions.Add(CreateCustomTrack(soundName, file.Path, file.Title, file.Artist, version, sampleRate,
                (long)(file.Duration * sampleRate)));
        }

        var updated = Tracks.Concat(additions).ToArray();
        foreach (var track in additions) LockCustomSource(track.SourcePath!);
        _occupiedTrackIds = occupied;
        _nextSeq = nextSeq;
        MarkDirty();
        Tracks.Reset(updated);
        RecalcProjectedSize();
    }

    private HashSet<ulong> GetOccupiedTrackIds() => _occupiedTrackIds ??= CollectOccupiedTrackIds(
        _radioResources ?? GameScanner.ReadRadioResources(Settings.GamePath), RadioInfo.Load);

    private static string AllocateCustomSoundName(HashSet<ulong> occupied, ref int nextSeq)
    {
        string soundName;
        do
        {
            soundName = Naming.MakeSoundName(nextSeq);
            nextSeq = checked(nextSeq + 1);
        }
        while (!occupied.Add(Lookup.SoundNameToId(soundName)));
        return soundName;
    }

    private static TrackItemViewModel CreateCustomTrack(string soundName, string sourcePath, string? title,
        string? artist, int version, int sampleRate, long sampleLength)
    {
        var track = new RadioTrack
        {
            SoundName = soundName,
            Origin = TrackOrigin.Custom,
            UsesMilliseconds = version == 2,
            UsesTrackBreakDown = version is 2 or 3,
            Enabled = true,
            DisplayName = title ?? Path.GetFileNameWithoutExtension(sourcePath),
            Artist = artist,
            SampleRate = sampleRate,
            SampleLength = sampleLength,
        };
        return new TrackItemViewModel(track) { SourcePath = sourcePath };
    }

    private void LockCustomSource(string sourcePath)
    {
        if (File.Exists(sourcePath) && !_sourceLocks.ContainsKey(sourcePath))
        {
            try
            {
                _sourceLocks[sourcePath] = new FileStream(
                    sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (Exception ex)
            {
                Log.Line($"CreateCustomStub: could not lock source '{Path.GetFileName(sourcePath)}': {ex.Message}");
            }
        }
    }

    public async Task ExportAsync(string outPath, IReadOnlyList<TrackItemViewModel> selected)
    {
        if (!CanUseLoadedBank || !selected.All(Tracks.ToHashSet().Contains))
        {
            Status = Str.StatusPackBankChanged;
            return;
        }
        if (selected.Count == 0)
        {
            Status = Str.StatusExportEmpty;
            return;
        }

        var bankName = SelectedStation?.BankName(SelectedVariant!);
        var bankPath = LoadedBankPath;
        var bankStamp = _loadedBankSnapshot?.Stamp;

        IsExporting = true;
        StopPlayback();
        Status = Str.StatusExporting;

        void Progress(string m) => Dispatcher.UIThread.Post(() => Status = m);

        try
        {
            var items = selected.Select(CreateExportItem).ToArray();
            await TrackPack.ExportAsync(outPath, bankName, bankPath, items, Progress, Log.Line, default, bankStamp);
            Status = string.Format(Str.StatusExportedFmt, items.Length, Path.GetFileName(outPath));
        }
        catch (Exception ex)
        {
            Log.Line("EXPORT ERROR: " + ex);
            Status = string.Format(Str.StatusExportErrorFmt, ex.Message);
        }
        finally
        {
            IsExporting = false;
        }
    }

    private static ExportItem CreateExportItem(TrackItemViewModel track)
    {
        var rate = track.SampleRate;
        var frames = track.SampleLength;
        if (track.UsesFileSource)
        {
            if (rate <= 0) rate = track.BankSampleRate > 0 ? track.BankSampleRate : 48000;
            if (frames <= 0 && track.PendingDurationSeconds is { } seconds)
                frames = checked((long)Math.Round(seconds * rate));
        }

        var role = track.IsCustom ? "custom" : track.Replaced || track.IsReplacing ? "replacement" : "default";
        return new ExportItem(track.UsesFileSource ? track.FileSource : track.RestoreFsbPath, track.SubIndex,
            track.Title, track.Artist, frames, rate, track.GainDb, track.Enabled, role, track.Markers,
            track.UsesFileSource && !track.IsFileSourceFsb ? track.Normalization : null, track.BankId);
    }

    public TrackItemViewModel StageImportedAdd(ImportedPack pack, PackTrack e)
    {
        var audioPath = Path.Combine(pack.Dir, e.File);
        var version = _gameVersion ?? GameScanner.DetectVersion(Settings.GamePath);
        var audio = PrepareImportedAudio(e, audioPath, version == 2, version is 2 or 3);
        var soundName = AllocateCustomSoundName(GetOccupiedTrackIds(), ref _nextSeq);
        var track = CreateImportedTrack(soundName, version, e, audio);
        LockCustomSource(audioPath);
        return track;
    }

    public void StageImportedReplace(TrackItemViewModel defaultTrack, ImportedPack pack, PackTrack e)
    {
        if (defaultTrack.IsBankOnly) throw new InvalidOperationException(Str.TipMissingTrackInfo);
        var audio = PrepareImportedAudio(e, Path.Combine(pack.Dir, e.File),
            defaultTrack.UsesMilliseconds, defaultTrack.UsesTrackBreakDown);
        ReplaceImportedTrack(defaultTrack, e, audio);
    }

    private sealed record ImportedAudio(string Path, int SampleRate, long SampleLength, double? GainDb,
        NormalizationOverride? Normalization, Dictionary<string, long>? Markers);

    private static ImportedAudio PrepareImportedAudio(PackTrack imported, string audioPath,
        bool usesMilliseconds, bool usesTrackBreakDown)
    {
        if (imported.SampleRate < 0 || imported.SampleLength < 0)
            throw new InvalidDataException($"Track '{imported.DisplayName ?? imported.Id}' has invalid sample timing.");

        var sourceRate = imported.SampleRate;
        var isFsb = audioPath.EndsWith(".fsb", StringComparison.OrdinalIgnoreCase);
        var targetRate = isFsb ? sourceRate : 48000;
        long Scale(long frames) => sourceRate > 0 && sourceRate != targetRate
            ? (long)decimal.Round(frames * (decimal)targetRate / sourceRate)
            : frames;

        try
        {
            var sampleLength = Scale(imported.SampleLength);
            var markers = imported.Markers is { } positions
                ? MarkerPresetService.AdaptMarkers(positions, usesMilliseconds, sourceRate, usesTrackBreakDown)
                    .ToDictionary(marker => marker.Key, marker => marker.Value < 0 ? -1 : Scale(marker.Value))
                : null;
            return new ImportedAudio(audioPath, targetRate, sampleLength, imported.GainDb,
                isFsb ? null : imported.Normalization, markers);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException($"Track '{imported.DisplayName ?? imported.Id}' has sample timing outside the supported range.", ex);
        }
    }

    private static TrackItemViewModel CreateImportedTrack(string soundName, int version, PackTrack entry, ImportedAudio audio)
    {
        var track = CreateCustomTrack(soundName, audio.Path, entry.DisplayName, entry.Artist, version,
            audio.SampleRate, audio.SampleLength);
        SetImportedAudio(track, entry, audio);
        return track;
    }

    private static void ReplaceImportedTrack(TrackItemViewModel track, PackTrack entry, ImportedAudio audio)
    {
        track.ReplacementPath = audio.Path;
        track.Title = entry.DisplayName ?? track.Title;
        track.Artist = entry.Artist;
        SetImportedAudio(track, entry, audio);
    }

    private static void SetImportedAudio(TrackItemViewModel track, PackTrack entry, ImportedAudio audio)
    {
        track.SampleLength = audio.SampleLength;
        track.SampleRate = audio.SampleRate;
        track.GainDb = audio.GainDb;
        track.Normalization = audio.Normalization;
        track.PendingDurationSeconds = audio.SampleRate > 0 && audio.SampleLength > 0
            ? audio.SampleLength / (double)audio.SampleRate : null;
        track.Markers = audio.Markers;
        track.Enabled = entry.Enabled;
    }

    public void ApplyImport(ImportedPack pack, IReadOnlyDictionary<string, string?> mapping)
    {
        if (pack.Manifest.Tracks.Count == 0) return;

        var version = _gameVersion ?? GameScanner.DetectVersion(Settings.GamePath);
        var defaultByName = Tracks.Where(t => !t.IsCustom && !t.IsBankOnly).ToDictionary(t => t.SoundName);
        var replacementTargets = new HashSet<TrackItemViewModel>();
        var plan = new List<(PackTrack Entry, TrackItemViewModel? Target, ImportedAudio Audio)>();
        foreach (var e in pack.Manifest.Tracks)
        {
            if (string.IsNullOrWhiteSpace(e.File) || e.File != Path.GetFileName(e.File))
                throw new InvalidDataException($"Track '{e.DisplayName ?? e.Id}' has an invalid audio path.");
            var path = Path.Combine(pack.Dir, e.File);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0)
                throw new IOException($"Audio file for '{e.DisplayName ?? e.Id}' is missing or empty: {path}");

            TrackItemViewModel? target = null;
            if (mapping.TryGetValue(e.Id, out var soundName) && soundName is not null
                && (!defaultByName.TryGetValue(soundName, out target) || !replacementTargets.Add(target)))
            {
                throw new InvalidDataException($"Replacement target '{soundName}' is unavailable or selected more than once.");
            }
            var audio = PrepareImportedAudio(e, path, target?.UsesMilliseconds ?? version == 2,
                target?.UsesTrackBreakDown ?? (version is 2 or 3));
            plan.Add((e, target, audio));
        }

        var additions = new List<TrackItemViewModel>();
        HashSet<ulong>? occupied = null;
        var nextSeq = _nextSeq;
        foreach (var (entry, target, audio) in plan)
        {
            if (target is not null) continue;
            occupied ??= new HashSet<ulong>(GetOccupiedTrackIds());
            var soundName = AllocateCustomSoundName(occupied, ref nextSeq);
            additions.Add(CreateImportedTrack(soundName, version, entry, audio));
        }
        var updatedTracks = additions.Count > 0 ? Tracks.Concat(additions).ToArray() : null;

        if (_nowPlaying is not null && replacementTargets.Contains(_nowPlaying)) StopPlayback();
        MarkDirty();
        foreach (var (entry, target, audio) in plan)
        {
            if (target is not null) ReplaceImportedTrack(target, entry, audio);
        }
        if (updatedTracks is not null)
        {
            foreach (var track in additions) LockCustomSource(track.SourcePath!);
            _occupiedTrackIds = occupied;
            _nextSeq = nextSeq;
            Tracks.Reset(updatedTracks);
        }
        else
        {
            RefreshFilter();
        }
        RecalcProjectedSize();
    }

    private CancellationTokenSource? _buildCancellation;
    public bool CanCancelBuild => _buildCancellation is { IsCancellationRequested: false };

    public void CancelBuild()
    {
        _buildCancellation?.Cancel();
        OnPropertyChanged(nameof(CanCancelBuild));
    }

    public async Task BuildAsync()
    {
        RefreshToolAvailability();
        if (!CanChangeSelection) return;
        if (_resourceError is { } resourceError)
        {
            Status = resourceError;
            return;
        }

        if (SelectedStation is null || SelectedLanguage is null || SelectedVariant is null)
        {
            return;
        }

        var missing = Tools.MissingForBuild().ToList();
        if (missing.Count > 0)
        {
            Status = string.Format(Str.StatusBuildMissingFmt, string.Join(", ", missing));
            return;
        }

        UpdatePlaylistMinimum();
        Recount();
        if (PlaylistLow)
        {
            Status = PlaylistTip;
            return;
        }

        var station = SelectedStation;
        var bankName = station.BankName(SelectedVariant);
        var gamePath = Settings.GamePath;
        if (GameScanner.IsGameProcessRunning(gamePath))
        {
            Status = Str.StatusBuildGameRunning;
            PendingDialogTitle = Str.DlgGameRunningTitle;
            PendingErrorDialog = Str.StatusBuildGameRunning;
            return;
        }
        var resources = _radioResources ?? GameScanner.ReadRadioResources(gamePath);
        var bankPath = resources.BankPath(bankName);
        if (bankPath is null)
        {
            Status = string.Format(Str.StatusBuildBankNotFoundFmt, bankName);
            return;
        }

        if (_loadedBankSnapshot is null || !string.Equals(bankPath, _loadedBankPath, StringComparison.OrdinalIgnoreCase))
        {
            Status = Str.StatusBuildBankChanged;
            return;
        }

        var refFile = ViewLanguageFile();
        var radioPath = refFile is null ? null : resources.LanguagePath(refFile);
        if (refFile is null || radioPath is null)
        {
            Status = Str.StatusBuildNoRadioInfo;
            return;
        }

        var otherFiles = SelectedLanguage.FileName == AllLanguagesFile
            ? Languages.Where(l => l.FileName != AllLanguagesFile && l.FileName != refFile).Select(l => l.FileName).ToList()
            : [];
        var lockedFiles = FileGuard.Locked(
            new string?[] { bankPath, radioPath }
                .Concat(otherFiles.Select(resources.LanguagePath)));

        if (lockedFiles.Count > 0)
        {
            Status = Str.StatusFilesInUse;
            PendingDialogTitle = Str.DlgFilesInUseTitle;
            PendingErrorDialog = Str.DlgFilesInUseBody;
            return;
        }
        
        var items = Tracks.Select(t =>
        {
            var customReplacing = t.IsCustom && t.ReplacementPath is not null;
            var newCustom = (t.IsCustom && t.SourcePath is not null) || customReplacing;
            var replacing = !t.IsCustom && t.ReplacementPath is not null;
            var encodeSrc = customReplacing ? t.ReplacementPath
                : newCustom ? t.SourcePath
                : replacing ? t.ReplacementPath
                : null;
            return new BuildItem(
                t.SoundName, newCustom, encodeSrc, t.Title, t.Artist, t.GainDb, t.Enabled,
                t.Markers, replacing, t.RestoreFromBak,
                MarkerSampleRate: t.SampleRate > 0 ? t.SampleRate : t.BankSampleRate,
                Normalization: t.Normalization,
                BankId: t.BankId);
        }).ToList();
        var settings = Settings;

        using var cancellation = new CancellationTokenSource();
        var ct = cancellation.Token;
        _buildCancellation = cancellation;
        IsBuilding = true;
        OnPropertyChanged(nameof(CanCancelBuild));
        StopPlayback();
        Status = string.Format(Str.StatusProcessingFmt, bankName);
        Log.Line($"=== BUILD {bankName} (station #{station.Number}) ===");
        
        void Progress(string m) => Dispatcher.UIThread.Post(() =>
        {
            if (_buildCancellation != cancellation || ct.IsCancellationRequested) return;
            var tab = m.IndexOf('\t');
            if (tab >= 0)
            {
                Status = m[..tab];
                StatusDetail = m[(tab + 1)..];
            }
            else
            {
                Status = m;
                StatusDetail = "";
            }
        });

        try
        {
            await StopLoudnessAnalysisAsync();
            var (addedSamples, written, savedXml, dead) = await Task.Run(() => BuildGameFilesAsync(
                gamePath, station, bankName, bankPath, radioPath, otherFiles, resources, items, settings, Progress, ct,
                () => Dispatcher.UIThread.Invoke(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    _buildCancellation = null;
                    OnPropertyChanged(nameof(CanCancelBuild));
                    Status = Str.StatusBuildPublishing;
                    StatusDetail = "";
                })));
            if (written == 0 && savedXml == 0)
            {
                HasUnsavedChanges = false;
                Status = Str.StatusBuildNoChanges;
                return;
            }
            if (written > 0) Log.Line($"wrote {written:N0} bytes -> {bankPath}");
            Log.Line($"radio.write.ok bank={bankName} savedXml={savedXml} removed={dead}");

            if (dead > 0)
            {
                Log.Line($"reconcile: removed {dead} dead track(s) total");
            }

            var nCustom = addedSamples.Count(a => !a.IsReplacement);
            var restoredNames = items.Where(i => i.RestoreFromBak).Select(i => i.SoundName).ToHashSet();
            var nRepl = addedSamples.Count(a => a.IsReplacement && !restoredNames.Contains(a.SoundName));
            var status = string.Format(Str.StatusBuiltFmt, bankName, items.Count)
                     + (nCustom > 0 ? string.Format(Str.StatusBuiltCustomFmt, nCustom) : "")
                     + (nRepl > 0 ? string.Format(Str.StatusBuiltReplacedFmt, nRepl) : "")
                     + (savedXml > 1 ? string.Format(Str.StatusBuiltXmlFmt, savedXml) : "")
                     + (dead > 0 ? string.Format(Str.StatusBuiltCleanedFmt, dead) : "");

            _radioForFile = null;
            _radioResources = resources;
            _restoreAvailabilityGeneration++;
            SetCanRestoreAll(true);
            try { await LoadAsync(); }
            catch (Exception ex) { Log.Line("reload after build failed: " + ex); }
            Status = status;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Status = Str.StatusBuildCancelled;
        }
        catch (BankTooLargeException ex)
        {
            Log.Line("BUILD ERROR: " + ex);
            Status = Str.StatusBuildTooLarge;
            PendingDialogTitle = Str.DlgBankTooLargeTitle;
            PendingErrorDialog = ex.Message;
        }
        catch (Exception ex)
        {
            Log.Line("BUILD ERROR: " + ex);
            Status = string.Format(Str.StatusBuildErrorFmt, ex.Message);
        }
        finally
        {
            _buildCancellation = null;
            OnPropertyChanged(nameof(CanCancelBuild));
            IsBuilding = false;
            StatusDetail = "";
            WorkDirs.Clean();
        }
    }
    
    private static HashSet<ulong> CollectOccupiedTrackIds(GameScanner.RadioResources resources, Func<string, RadioInfo> readRadio)
    {
        var occupied = new HashSet<ulong>();
        foreach (var name in resources.Banks)
            occupied.UnionWith(FevBank.ReadStblIdsFromFile(resources.BankPath(name)!));
        foreach (var language in resources.Languages)
            occupied.UnionWith(readRadio(resources.LanguagePath(language)!).CustomSoundNames().Select(Lookup.SoundNameToId));
        return occupied;
    }

    private static ulong BuildItemId(BuildItem item) => item.BankId ?? Lookup.SoundNameToId(item.SoundName);

    private static bool HasRadioIdentity(BuildItem item) =>
        item.BankId is null || item.BankId == Lookup.SoundNameToId(item.SoundName);

    private async Task<(IReadOnlyList<AddedSample> Added, long Written, int SavedXml, int Dead)> BuildGameFilesAsync(
        string gamePath, StationInfo station, string bankName, string bankPath, string refPath, IReadOnlyList<string> otherFiles,
        GameScanner.RadioResources resources, List<BuildItem> items, AppSettings settings, Action<string> progress,
        CancellationToken ct, Action? beginCommit)
    {
        ct.ThrowIfCancellationRequested();
        using var transaction = new GameFileTransaction(gamePath);
        transaction.EnsureGameClosed();

        using var bank = File.OpenRead(bankPath);
        var snapshot = _loadedBankSnapshot;
        if (snapshot is not null && snapshot.Stamp != BankFileStamp.Read(bank))
            throw new IOException(Str.StatusBuildBankChanged);

        var originalIds = FevBank.ReadCompleteStblIds(bank);
        if (snapshot is not null && !snapshot.Ids.SetEquals(originalIds))
            throw new IOException(Str.StatusBuildBankChanged);

        var radios = new Dictionary<string, RadioInfo>(StringComparer.OrdinalIgnoreCase);
        RadioInfo ReadRadio(string path)
        {
            if (radios.TryGetValue(path, out var radio)) return radio;
            radio = RadioInfo.Load(path);
            transaction.ExpectRadio(path, radio);
            radios.Add(path, radio);
            return radio;
        }

        var refRadio = ReadRadio(refPath);
        if (_loadedRadio is { } loadedRadio && !refRadio.SourceHash.AsSpan().SequenceEqual(loadedRadio.SourceHash))
            throw new IOException(string.Format(Str.StatusBuildRadioChangedFmt, Path.GetFileName(refPath)));

        var originalReference = new XDocument(refRadio.Document);
        var newTracks = items.Where(item => item.IsNewCustom && !originalIds.Contains(BuildItemId(item))).ToArray();
        if (newTracks.Length > 0)
        {
            var occupied = CollectOccupiedTrackIds(resources, ReadRadio);
            if (newTracks.FirstOrDefault(item => occupied.Contains(BuildItemId(item))) is { } conflict)
                throw new IOException($"Track ID is already in use: {conflict.SoundName}. Reload the station and add the track again.");
        }
        var bankChanges = items.Count != originalIds.Count
            || items.Any(item => item.IsNewCustom || item.IsReplacement || item.RestoreFromBak)
            || !originalIds.SetEquals(items.Select(BuildItemId));
        IReadOnlyList<AddedSample> addedSamples = [];
        var outputIds = originalIds;
        long written = 0;
        if (bankChanges)
        {
            var stagedBank = transaction.Stage(bankPath);
            addedSamples = await BankBuildService.BuildToStageAsync(bankPath, stagedBank, items, settings, Log.Line, progress,
                    Log.IsVerbose, useBackupSources: false, ct)
                .ConfigureAwait(false);
            outputIds = FevBank.ReadCompleteStblIdsFromFile(stagedBank)
                ?? throw new InvalidDataException($"Could not verify the rebuilt bank: {bankName}");
            written = new FileInfo(stagedBank).Length;
        }

        var currentIds = outputIds;
        var idsByBank = new Dictionary<string, HashSet<ulong>?>(StringComparer.OrdinalIgnoreCase) { [bankName] = currentIds };
        var removed = originalIds.Where(id => !currentIds.Contains(id)).ToHashSet();
        var refStation = refRadio.StationByNumber(station.Number)
            ?? throw new InvalidOperationException($"station #{station.Number} not found in RadioInfo");
        var enabled = items.Where(item => HasRadioIdentity(item) && refStation.IsEnabled(item.SoundName) != item.Enabled)
            .ToDictionary(item => item.SoundName, item => item.Enabled);
        RestoreMissingCustomMetadata(items, refStation, station.Number, refPath, bankPath, resources, ReadRadio);

        var restored = items.Where(item => item.RestoreFromBak).Select(item => item.SoundName).ToHashSet(StringComparer.Ordinal);
        var refBackup = ReadTempoBackup(refPath, restored);
        var (changedMeta, changedMarkers) = ApplyRadioInfo(refRadio, station, bankName, addedSamples, items);
        RestoreTempo(refStation, refBackup, restored);
        var dead = ReconcileStation(refStation, resources, idsByBank, refStation.TrackBankNames().Any() ? null : removed);

        var markerRates = items.ToDictionary(i => i.SoundName, i => i.MarkerSampleRate);
        var sharedChanges = addedSamples.Count > 0 || changedMarkers.Count > 0 || removed.Count > 0;
        if (sharedChanges)
        {
            foreach (var editor in refRadio.StationsForBank(bankName, station.Number))
            {
                ApplySharedAudio(editor, addedSamples, changedMarkers, markerRates, restored);
                RestoreTempo(editor, refBackup, restored);
                if (removed.Count > 0) dead += ReconcileStation(editor, resources, idsByBank, removed);
            }
        }

        var savedXml = 0;
        if (!XNode.DeepEquals(originalReference, refRadio.Document))
        {
            transaction.StageRadio(refRadio, refPath);
            savedXml++;
        }

        var requested = otherFiles.Select(file => resources.LanguagePath(file)
            ?? throw new FileNotFoundException($"RadioInfo not found: {file}")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> sharedPaths = sharedChanges
            ? resources.Languages.Select(file => resources.LanguagePath(file)!)
            : [];
        foreach (var path in requested.Union(sharedPaths, StringComparer.OrdinalIgnoreCase)
                     .Where(path => !string.Equals(path, refPath, StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            var radio = ReadRadio(path);
            var original = new XDocument(radio.Document);
            var backup = ReadTempoBackup(path, restored);
            var editor = requested.Contains(path) ? radio.StationByNumber(station.Number) : null;
            if (editor is not null)
            {
                ApplyLocalizedRadio(editor, refStation, bankName, items.Count > 0, currentIds, addedSamples,
                    changedMeta, changedMarkers, markerRates, enabled, restored);
                RestoreTempo(editor, backup, restored);
                dead += ReconcileStation(editor, resources, idsByBank, editor.TrackBankNames().Any() ? null : removed);
            }
            if (sharedChanges)
            {
                foreach (var consumer in radio.StationsForBank(bankName, editor is null ? null : station.Number))
                {
                    ApplySharedAudio(consumer, addedSamples, changedMarkers, markerRates, restored);
                    RestoreTempo(consumer, backup, restored);
                    if (removed.Count > 0) dead += ReconcileStation(consumer, resources, idsByBank, removed);
                }
            }
            if (XNode.DeepEquals(original, radio.Document)) continue;
            transaction.StageRadio(radio, path);
            savedXml++;
        }

        ct.ThrowIfCancellationRequested();
        if (bankChanges) transaction.StageOriginal(bankPath, !FevBank.LooksModified(bankPath), ct);
        bank.Dispose();
        beginCommit?.Invoke();
        ct.ThrowIfCancellationRequested();
        transaction.Commit();
        return (addedSamples, written, savedXml, dead);
    }

    private static void RestoreMissingCustomMetadata(List<BuildItem> items, RadioStationEditor editor,
        int stationNumber, string refPath, string bankPath, GameScanner.RadioResources resources, Func<string, RadioInfo> readRadio)
    {
        Dictionary<ulong, FevBank.BankTrack>? samples = null;
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (item.IsNewCustom || !HasRadioIdentity(item) || !item.SoundName.StartsWith(Naming.CustomPrefix, StringComparison.Ordinal)
                || editor.GetSampleMeta(item.SoundName) is not null)
                continue;

            var title = item.DisplayName == item.SoundName ? null : item.DisplayName;
            if (!item.Enabled && title is null && item.Artist is null && item.Markers is not { Count: > 0 }) continue;
            var copied = resources.Languages.Select(file => resources.LanguagePath(file)!)
                .Where(path => !string.Equals(path, refPath, StringComparison.OrdinalIgnoreCase))
                .Select(path => readRadio(path).StationByNumber(stationNumber))
                .OfType<RadioStationEditor>().Any(source => editor.RestoreTrackFrom(source, item.SoundName));
            if (!copied)
            {
                samples ??= FevBank.ReadTrackInfoFromFile(bankPath).ToDictionary(sample => sample.Id);
                if (!samples.TryGetValue(BuildItemId(item), out var sample) || sample.SampleRate <= 0)
                    throw new InvalidDataException($"No audio is available for {item.SoundName}.");
                editor.AddCustom(item.SoundName, sample.Frames, sample.SampleRate, title, item.Artist);
            }
            items[index] = item with { DisplayName = title };
        }
    }

    private static RadioInfo? ReadTempoBackup(string path, IReadOnlySet<string> restoredNames) =>
        restoredNames.Count > 0 && File.Exists(path + ".bak") ? RadioInfo.Load(path + ".bak") : null;

    private static void RestoreTempo(RadioStationEditor editor, RadioInfo? backup, IReadOnlySet<string> restoredNames)
    {
        if (backup is null || editor.Number is not { } number || backup.StationByNumber(number) is not { } original) return;
        foreach (var name in restoredNames) editor.RestoreTempoFrom(original, name);
    }

    private static void ApplyLocalizedRadio(
        RadioStationEditor editor, RadioStationEditor reference, string bankName, bool bankHasContent,
        IReadOnlySet<ulong> bankIds, IReadOnlyList<AddedSample> addedSamples,
        IReadOnlyDictionary<string, (string? DisplayName, string? Artist)> changedMeta,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> changedMarkers,
        IReadOnlyDictionary<string, int> markerRates, IReadOnlyDictionary<string, bool> enabled,
        IReadOnlySet<string> restoredNames)
    {
        if (bankHasContent) editor.RegisterBank(bankName);
        else editor.UnregisterBank(bankName);
        var copied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in reference.CustomSoundNames())
        {
            if (bankIds.Contains(Lookup.SoundNameToId(name)) && editor.GetSampleMeta(name) is null
                && editor.RestoreTrackFrom(reference, name))
                copied.Add(name);
        }

        foreach (var (soundName, isEnabled) in enabled) editor.SetEnabled(soundName, isEnabled);

        foreach (var sample in addedSamples.Where(sample => !copied.Contains(sample.SoundName)))
        {
            if (!editor.ApplyReplacement(sample.SoundName, sample.Frames, sample.SampleRate)) continue;
            if (!sample.IsReplacement) editor.ClearReplacement(sample.SoundName);
            editor.SetSampleMeta(sample.SoundName, sample.DisplayName, sample.Artist,
                clearMissingArtist: sample.IsReplacement);
        }
        foreach (var soundName in restoredNames) editor.ClearReplacement(soundName);

        foreach (var (soundName, meta) in changedMeta) editor.SetSampleMeta(soundName, meta.DisplayName, meta.Artist);
        foreach (var (soundName, markers) in changedMarkers) editor.SetMarkers(soundName, markers, markerRates.GetValueOrDefault(soundName));
    }

    private static void ApplySharedAudio(
        RadioStationEditor editor, IReadOnlyList<AddedSample> addedSamples,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> changedMarkers,
        IReadOnlyDictionary<string, int> markerRates, IReadOnlySet<string> restoredNames)
    {
        foreach (var sample in addedSamples)
        {
            if (editor.ApplyReplacement(sample.SoundName, sample.Frames, sample.SampleRate)
                && (!sample.IsReplacement || restoredNames.Contains(sample.SoundName)))
                editor.ClearReplacement(sample.SoundName);
        }
        foreach (var (soundName, markers) in changedMarkers)
            editor.SetMarkers(soundName, markers, markerRates.GetValueOrDefault(soundName));
    }

    private static Dictionary<string, (string? DisplayName, string? Artist)> ApplySampleMeta(
        RadioStationEditor editor, IReadOnlyList<BuildItem> items)
    {
        var changed = new Dictionary<string, (string?, string?)>();
        foreach (var it in items)
        {
            if (editor.GetSampleMeta(it.SoundName) is not { } cur)
            {
                continue;
            }

            var nameChanged =
                it.DisplayName is not null
                && !string.Equals(cur.DisplayName, it.DisplayName, StringComparison.Ordinal)
                && !(cur.DisplayName is null && string.Equals(it.DisplayName, it.SoundName, StringComparison.Ordinal));

            var artist = it.Artist ?? (it.RestoreFromBak ? "" : null);
            var artistChanged =
                artist is not null
                && !string.Equals(cur.Artist, artist, StringComparison.Ordinal);

            if (nameChanged || artistChanged)
            {
                var meta = (DisplayName: nameChanged ? it.DisplayName : null, Artist: artistChanged ? artist : null);
                editor.SetSampleMeta(it.SoundName, meta.DisplayName, meta.Artist);
                changed[it.SoundName] = meta;
            }
        }
        return changed;
    }

    private static (Dictionary<string, (string? DisplayName, string? Artist)> Meta,
                     Dictionary<string, IReadOnlyDictionary<string, long>> Markers) ApplyRadioInfo(
        RadioInfo radio, StationInfo station, string bankName,
        IReadOnlyList<AddedSample> added, List<BuildItem> items)
    {
        var editor = radio.StationByNumber(station.Number)
            ?? throw new InvalidOperationException($"station #{station.Number} not found in RadioInfo");

        if (items.Count > 0)
        {
            editor.RegisterBank(bankName);
        }
        else
        {
            editor.UnregisterBank(bankName);
        }
        
        foreach (var a in added)
        {
            Log.Line($"radio.track.metadata sound={a.SoundName} frames={a.Frames} rate={a.SampleRate} replacement={a.IsReplacement} enabled={a.Enabled}");
            if (a.IsReplacement)
            {
                editor.ApplyReplacement(a.SoundName, a.Frames, a.SampleRate);
                editor.SetSampleMeta(a.SoundName, a.DisplayName, a.Artist, clearMissingArtist: true);
            }
            else
            {
                editor.AddCustom(a.SoundName, a.Frames, a.SampleRate, a.DisplayName, a.Artist);
            }
        }

        foreach (var it in items.Where(i => i.RestoreFromBak))
        {
            editor.ClearReplacement(it.SoundName);
        }

        var changed = ApplySampleMeta(editor, items);
        var changedMarkers = new Dictionary<string, IReadOnlyDictionary<string, long>>();
        var radioItems = items.Where(HasRadioIdentity).ToList();

        foreach (var it in radioItems)
        {
            if (editor.IsEnabled(it.SoundName) != it.Enabled)
                editor.SetEnabled(it.SoundName, it.Enabled);
        }

        foreach (var it in radioItems)
        {
            if (it.Markers is { } mk)
            {
                var adapted = MarkerPresetService.AdaptMarkers(mk, editor.UsesMilliseconds, it.MarkerSampleRate, editor.UsesTrackBreakDown);
                if (!MarkersEqual(editor.GetMarkers(it.SoundName, it.MarkerSampleRate), adapted))
                {
                    changedMarkers[it.SoundName] = adapted;
                    editor.SetMarkers(it.SoundName, adapted, it.MarkerSampleRate);
                    if (Log.IsVerbose)
                    {
                        Log.Line($"radio.track.markers sound={it.SoundName} sourceRate={it.MarkerSampleRate} " +
                            $"xmlMilliseconds={editor.UsesMilliseconds} positions={JsonSerializer.Serialize(editor.GetMarkers(it.SoundName))}");
                    }
                }
            }
        }

        return (changed, changedMarkers);
    }

    private static bool MarkersEqual(IReadOnlyDictionary<string, long>? a, IReadOnlyDictionary<string, long>? b)
    {
        if (b is null || b.Count == 0) return true;
        if (a is null) return false;
        foreach (var (k, v) in b)
        {
            if (!a.TryGetValue(k, out var av) || av != v) return false;
        }
        return true;
    }
}
