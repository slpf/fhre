using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using FH6RB.Core;
using FH6RB.Services;

namespace FH6RB.ViewModels;

public sealed record LangOption(string Code, string FileName)
{
    public string Display => FileName == MainWindowViewModel.AllLanguagesFile ? "All languages" : FileName;
}

public sealed partial class MainWindowViewModel : ObservableObject
{
    public AppSettings Settings { get; }

    public const string AllLanguagesFile = "*";

    public ObservableCollection<LangOption> Languages { get; } = [];
    public ObservableCollection<StationInfo> Stations { get; } = [];
    public ObservableCollection<string> Variants { get; } = [];
    public ObservableCollection<TrackItemViewModel> Tracks { get; } = [];

    [ObservableProperty] private LangOption? _selectedLanguage;
    [ObservableProperty] private StationInfo? _selectedStation;
    [ObservableProperty] private string? _selectedVariant;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isBuilding;
    [ObservableProperty] private bool _isAddingTracks;

    public bool IsBusy => IsBuilding || IsAddingTracks;
    partial void OnIsBuildingChanged(bool value) => OnPropertyChanged(nameof(IsBusy));
    partial void OnIsAddingTracksChanged(bool value) => OnPropertyChanged(nameof(IsBusy));
    public string? PendingErrorDialog { get; set; }

    [ObservableProperty] private string _status = "Ready.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusDetail))]
    private string _statusDetail = "";

    public bool HasStatusDetail => !string.IsNullOrEmpty(StatusDetail);
    [ObservableProperty] private string _countText = "";

    private RadioInfo? _radio;
    private string? _radioForFile;
    private int _nextSeq;
    private bool _suppressReload;
    private int _loadGen;
    
    private readonly PlaybackService _player = new();
    private TrackItemViewModel? _nowPlaying;
    private string? _loadedBankPath;
    private DispatcherTimer? _tick;
    private int _playGen;
    private bool _suppressSeek;

    [ObservableProperty] private bool _transportVisible;
    [ObservableProperty] private bool _transportPlaying;
    [ObservableProperty] private string _nowTitle = "";
    [ObservableProperty] private string? _nowArtist;
    [ObservableProperty] private bool _nowShowVolume;
    [ObservableProperty] private double _positionSeconds;
    [ObservableProperty] private double _durationSeconds = 1;
    [ObservableProperty] private string _nowPositionText = "0:00";
    [ObservableProperty] private string _nowDurationText = "0:00";
    [ObservableProperty] private double _nowGainDb;
    [ObservableProperty] private string _nowGainText = "0.0 dB";

    public TrackItemViewModel? NowPlaying => _nowPlaying;

    public MainWindowViewModel(AppSettings settings)
    {
        Settings = settings;

        _player.Ended += () => Dispatcher.UIThread.Post(StopPlayback);
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) =>
        {
            _suppressSeek = true;
            PositionSeconds = _player.Position.TotalSeconds;
            NowPositionText = Fmt(_player.Position);
            _suppressSeek = false;
        };

        ReloadFromGame();
    }

    public void ReloadFromGame()
    {
        var wasSuppressed = _suppressReload;
        _suppressReload = true;

        Languages.Clear();
        Stations.Clear();
        Variants.Clear();
        _radio = null;
        _radioForFile = null;

        if (!GameScanner.IsValid(Settings.GamePath))
        {
            _suppressReload = wasSuppressed;
            return;
        }

        foreach (var file in GameScanner.LanguageFiles(Settings.GamePath))
        {
            var code = file.Replace("RadioInfo_", "").Replace(".xml", "");
            Languages.Add(new LangOption(code, file));
        }

        if (Languages.Count > 1)
        {
            Languages.Insert(0, new LangOption("ALL", AllLanguagesFile));
        }

        SelectedLanguage = Languages.FirstOrDefault(l => l.FileName == Settings.LastLanguage)
            ?? Languages.FirstOrDefault(l => l.FileName != AllLanguagesFile)
            ?? Languages.FirstOrDefault();

        RebuildStations();

        SelectedStation = Stations.FirstOrDefault(s => s.Prefix == Settings.LastStationBank)
            ?? Stations.FirstOrDefault();

        _suppressReload = wasSuppressed;
        if (!_suppressReload)
        {
            _ = LoadAsync();
        }
    }

    private void EnsureRadio()
    {
        var file = ViewLanguageFile();
        if (file is null)
        {
            _radio = null;
            _radioForFile = null;
            return;
        }

        if (_radioForFile == file && _radio is not null)
        {
            return;
        }

        var path = GameScanner.RadioInfoPathByFile(Settings.GamePath, file);
        try
        {
            _radio = path is not null ? RadioInfo.Load(path) : null;
        }
        catch (Exception ex)
        {
            Log.Line($"RadioInfo load failed ({file}): {ex.Message}");
            _radio = null;
        }

        _radioForFile = file;
    }

    private string? ViewLanguageFile()
    {
        if (SelectedLanguage is null) return null;
        if (SelectedLanguage.FileName != AllLanguagesFile) return SelectedLanguage.FileName;

        var real = Languages.Where(l => l.FileName != AllLanguagesFile).ToList();
        var english = real.FirstOrDefault(l => l.Code.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        return (english ?? real.FirstOrDefault())?.FileName;
    }

    private void RebuildStations()
    {
        EnsureRadio();
        var current = SelectedStation?.Number;
        Stations.Clear();

        foreach (var st in RadioCatalog.Build(Settings.GamePath, _radio))
        {
            Stations.Add(st);
        }

        SelectedStation = Stations.FirstOrDefault(s => s.Number == current) ?? Stations.FirstOrDefault();
    }

    partial void OnSelectedLanguageChanged(LangOption? value)
    {
        var wasSuppressed = _suppressReload;
        _suppressReload = true;
        RebuildStations();
        _suppressReload = wasSuppressed;

        if (!_suppressReload)
        {
            _ = LoadAsync();
        }
    }

    partial void OnSelectedStationChanged(StationInfo? value)
    {
        var wasSuppressed = _suppressReload;
        _suppressReload = true;

        var previous = SelectedVariant;
        Variants.Clear();

        if (value is not null)
        {
            foreach (var v in value.Variants)
            {
                Variants.Add(v);
            }
        }

        SelectedVariant = Variants.Contains(previous ?? "") ? previous : Variants.FirstOrDefault();

        _suppressReload = wasSuppressed;
        if (!_suppressReload)
        {
            _ = LoadAsync();
        }
    }

    partial void OnSelectedVariantChanged(string? value)
    {
        if (!_suppressReload)
        {
            _ = LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        if (SelectedStation is null || SelectedLanguage is null || SelectedVariant is null)
        {
            return;
        }

        var gen = ++_loadGen;
        StopPlayback();
        var station = SelectedStation;
        var bank = station.BankName(SelectedVariant);

        Log.Line($"LoadAsync #{gen}: lang={SelectedLanguage.FileName} station=#{station.Number} variant={SelectedVariant} bank={bank}");

        IsLoading = true;
        Status = $"Loading {bank}…";

        EnsureRadio();
        var radio = _radio;
        var path = GameScanner.BankPath(Settings.GamePath, bank);
        _loadedBankPath = path;

        var result = path is null
            ? new BankReadResult([], "bank file not found")
            : await Task.Run(() => BankReader.ReadTracks(path, radio, station.Number));

        if (gen != _loadGen)
        {
            Log.Line($"LoadAsync #{gen}: superseded by #{_loadGen}, discarding");
            return;
        }

        Tracks.Clear();
        foreach (var rt in result.Tracks)
        {
            Tracks.Add(new TrackItemViewModel(rt));
        }
        
        _nextSeq = Math.Max(NextCustomSeq(Tracks), (_radio?.MaxCustomSeq() ?? -1) + 1);
        IsLoading = false;
        Recount();

        Status = result.Error is { } err
            ? $"{bank}: {err}"
            : $"Loaded {bank}";

        Settings.LastLanguage = SelectedLanguage.FileName;
        Settings.LastStationBank = station.Prefix;
        SettingsService.Save(Settings);
    }
    
    private async Task<(HashSet<ulong> Union, bool Suspicious)> ReadStationBankIdsAsync(StationInfo station, RadioInfo radio)
    {
        var editor = radio.StationByNumber(station.Number);
        if (editor is null)
        {
            return ([], false);
        }

        var paths = editor.TrackBankNames()
            .Select(n => GameScanner.BankPath(Settings.GamePath, n))
            .Where(p => p is not null).Select(p => p!).Distinct().ToList();
        if (paths.Count == 0)
        {
            return ([], false);
        }

        var result = await Task.Run(() =>
        {
            var ids = new HashSet<ulong>();
            var susp = false;
            foreach (var p in paths)
            {
                var part = FevBank.ReadStblIdsFromFile(p);
                if (part.Count == 0 && new FileInfo(p).Length > 506)
                {
                    susp = true;
                }

                ids.UnionWith(part);
            }

            return (ids, susp);
        });

        if (result.susp)
        {
            Log.Line("reconcile: skipped (a non-empty bank yielded no STBL ids)");
        }

        return result;
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
        var custom = Tracks.Count(t => t.IsCustom);
        var on = Tracks.Count(t => t.Enabled);
        CountText = $"{total} tracks · {custom} custom · {on} in playlist";
    }

    [RelayCommand]
    private async Task PlayTrack(TrackItemViewModel? track)
    {
        if (track is null || !track.CanPlay)
        {
            return;
        }

        if (ReferenceEquals(track, _nowPlaying))
        {
            TogglePlay();
            return;
        }

        await StartAsync(track);
    }

    [RelayCommand]
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
            _tick?.Start();
        }
        else
        {
            _tick?.Stop();
        }
    }

    private async Task StartAsync(TrackItemViewModel track)
    {
        var gen = ++_playGen;
        
        _player.Stop();
        _tick?.Stop();
        if (_nowPlaying is not null)
        {
            _nowPlaying.PlayState = TrackPlayState.Idle;
        }

        _nowPlaying = track;
        track.PlayState = TrackPlayState.Loading;
        TransportVisible = true;
        TransportPlaying = false;
        NowTitle = track.Title;
        NowArtist = track.Artist;
        NowShowVolume = track.IsUnbuilt;
        NowGainDb = track.GainDb ?? 0;
        PositionSeconds = 0;
        NowPositionText = "0:00";
        Status = $"Decoding {track.Title}…";

        try
        {
            var bankPath = _loadedBankPath;
            var wav = await Task.Run(() => track.IsUnbuilt
                ? AudioDecoder.DecodeAdded(track.SourcePath!, Settings)
                : AudioDecoder.DecodeBank(bankPath!, track.SubIndex));

            if (gen != _playGen)
            {
                return;
            }
            
            var volDb = track.IsUnbuilt ? track.GainDb ?? 0 : 0;
            _player.Play(wav, volDb);

            track.PlayState = TrackPlayState.Playing;
            TransportPlaying = true;
            DurationSeconds = Math.Max(0.1, _player.Duration.TotalSeconds);
            NowDurationText = Fmt(_player.Duration);
            Status = $"Playing {track.Title}";
            _tick?.Start();
        }
        catch (Exception ex)
        {
            if (gen == _playGen)
            {
                Log.Line("playback error: " + ex);
                Status = "Playback error: " + ex.Message;
                StopPlayback();
            }
        }
    }

    public void StopPlayback()
    {
        _playGen++;
        _tick?.Stop();
        _player.Stop();
        if (_nowPlaying is not null)
        {
            _nowPlaying.PlayState = TrackPlayState.Idle;
        }

        _nowPlaying = null;
        TransportVisible = false;
        TransportPlaying = false;
    }
    
    public void Shutdown()
    {
        StopPlayback();
        _player.Dispose();
    }
    
    public void RefreshNowPlaying()
    {
        if (_nowPlaying is null)
        {
            return;
        }

        NowTitle = _nowPlaying.Title;
        NowArtist = _nowPlaying.Artist;

        if (_nowPlaying.IsUnbuilt)
        {
            NowGainDb = _nowPlaying.GainDb ?? 0;
        }
    }

    partial void OnPositionSecondsChanged(double value)
    {
        if (!_suppressSeek && _player.HasMedia)
        {
            _player.Position = TimeSpan.FromSeconds(value);
            NowPositionText = Fmt(TimeSpan.FromSeconds(value));
        }
    }

    partial void OnNowGainDbChanged(double value)
    {
        NowGainText = $"{(value > 0 ? "+" : "")}{value:0.0} dB";

        if (_nowPlaying is { IsUnbuilt: true })
        {
            _nowPlaying.GainDb = value;
            _player.SetVolumeDb(value);
        }
    }

    private static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    [RelayCommand]
    private void ToggleEnabled(TrackItemViewModel track)
    {
        track.Enabled = !track.Enabled;
        Status = (track.Enabled ? "Enabled: " : "Disabled: ") + track.SoundName;
        Recount();
    }

    [RelayCommand]
    private void Delete(TrackItemViewModel track)
    {
        if (!track.CanDelete)
        {
            return;
        }

        Tracks.Remove(track);
        Status = "Deleted: " + track.SoundName;
        Recount();
    }

    public TrackItemViewModel CreateCustomStub(string sourcePath, string? title = null, string? artist = null,
                                               double durationSeconds = 0)
    {
        var soundName = Naming.MakeSoundName(_nextSeq++);
        const int sampleRate = 48000;
        var track = new RadioTrack
        {
            SoundName = soundName,
            Origin = TrackOrigin.Custom,
            Enabled = true,
            DisplayName = title ?? Path.GetFileNameWithoutExtension(sourcePath),
            Artist = artist,
            SampleRate = sampleRate,
            SampleLength = (long)(durationSeconds * sampleRate),
        };

        return new TrackItemViewModel(track, gainDb: null) { SourcePath = sourcePath };
    }
    
    public async Task BuildAsync()
    {
        if (SelectedStation is null || SelectedLanguage is null || SelectedVariant is null)
        {
            return;
        }

        var missing = Tools.MissingForBuild().ToList();
        if (missing.Count > 0)
        {
            Status = "Build: missing " + string.Join(", ", missing) + " (put them in app folder)";
            return;
        }

        var station = SelectedStation;
        var bankName = station.BankName(SelectedVariant);
        var bankPath = GameScanner.BankPath(Settings.GamePath, bankName);
        if (bankPath is null)
        {
            Status = $"Build: bank file not found ({bankName})";
            return;
        }

        EnsureRadio();
        var radio = _radio;
        var radioPath = ViewLanguageFile() is { } viewFile
            ? GameScanner.RadioInfoPathByFile(Settings.GamePath, viewFile)
            : null;
        if (radio is null || radioPath is null)
        {
            Status = "Build: RadioInfo not available";
            return;
        }
        
        var items = Tracks.Select(t => new BuildItem(
            t.SoundName,
            t.IsCustom && t.SourcePath is not null,
            t.SourcePath, t.Title, t.Artist, t.GainDb, t.Enabled)).ToList();
        var settings = Settings;

        IsBuilding = true;
        StopPlayback();
        Status = $"Processing {bankName}…";
        Log.Line($"=== BUILD {bankName} (station #{station.Number}) ===");
        
        void Progress(string m) => Dispatcher.UIThread.Post(() =>
        {
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
            var (addedSamples, written) = await Task.Run(
                () => BuildAndWriteBankAsync(bankPath, items, settings, Progress));
            Log.Line($"wrote {written:N0} bytes -> {bankPath}");
            
            var refFile = ViewLanguageFile()
                ?? throw new InvalidOperationException("no reference RadioInfo language");
            var otherFiles = SelectedLanguage.FileName == AllLanguagesFile
                ? Languages.Where(l => l.FileName != AllLanguagesFile && l.FileName != refFile)
                    .Select(l => l.FileName).ToList()
                : new List<string>();

            var savedXml = 0;
            var dead = 0;
            
            var refPath = GameScanner.RadioInfoPathByFile(Settings.GamePath, refFile)
                ?? throw new InvalidOperationException($"reference RadioInfo not found: {refFile}");
            var refRadio = RadioInfo.Load(refPath);
            ApplyRadioInfo(refRadio, station, bankName, addedSamples, items);

            var (union, suspicious) = await ReadStationBankIdsAsync(station, refRadio);
            if (!suspicious && union.Count > 0)
            {
                dead += refRadio.StationByNumber(station.Number)
                    ?.ReconcileTracks(union, Lookup.SoundNameToId, Log.Line) ?? 0;
            }

            SaveXmlWithBackup(refRadio, refPath);
            savedXml++;
            Log.Line($"RadioInfo saved (reference): {refFile}");

            var refStation = refRadio.StationByNumber(station.Number);
            
            foreach (var lf in otherFiles)
            {
                if (refStation is null) break;

                var xmlPath = GameScanner.RadioInfoPathByFile(Settings.GamePath, lf);
                if (xmlPath is null) continue;

                RadioInfo localized;
                try
                {
                    localized = RadioInfo.Load(xmlPath);
                }
                catch (Exception ex)
                {
                    Log.Line($"RadioInfo load failed ({lf}): {ex.Message} — skipped");
                    continue;
                }

                var ed = localized.StationByNumber(station.Number);
                if (ed is null)
                {
                    Log.Line($"station #{station.Number} not in {lf} — skipped");
                    continue;
                }

                ed.RegisterBank(bankName);
                ed.SyncCustomsFrom(refStation);

                if (!suspicious && union.Count > 0)
                {
                    dead += ed.ReconcileTracks(union, Lookup.SoundNameToId, Log.Line);
                }

                SaveXmlWithBackup(localized, xmlPath);
                savedXml++;
                Log.Line($"RadioInfo saved: {lf}");
            }

            if (dead > 0)
            {
                Log.Line($"reconcile: removed {dead} dead track(s) total");
            }

            Status = $"Built {bankName}: +{addedSamples.Count} custom, {items.Count} tracks total"
                     + (savedXml > 1 ? $", {savedXml} xml" : "")
                     + (dead > 0 ? $", cleaned {dead} dead" : "");
        }
        catch (BankTooLargeException ex)
        {
            Log.Line("BUILD ERROR: " + ex);
            Status = "Build stopped: bank exceeds the 2 GB limit";
            PendingErrorDialog = ex.Message;
        }
        catch (Exception ex)
        {
            Log.Line("BUILD ERROR: " + ex);
            Status = "Build error: " + ex.Message;
        }
        finally
        {
            IsBuilding = false;
            StatusDetail = "";
            _radioForFile = null;
            WorkDirs.Clean();
            await LoadAsync();
            
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
    }
    
    private static async Task<(IReadOnlyList<AddedSample> Added, long Written)> BuildAndWriteBankAsync(
        string bankPath, IReadOnlyList<BuildItem> items, AppSettings settings, Action<string> progress)
    {
        var bankBak = bankPath + ".bak";
        if (!File.Exists(bankBak)) File.Copy(bankPath, bankBak);

        var tmp = bankPath + ".tmp";
        try
        {
            var added = await BankBuildService.BuildToFileAsync(bankPath, tmp, items, settings, Log.Line, progress).ConfigureAwait(false);
            File.Move(tmp, bankPath, true);
            return (added, new FileInfo(bankPath).Length);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { /* ignore */ }
            }
        }
    }

    private static void SaveXmlWithBackup(RadioInfo radio, string path)
    {
        var bak = path + ".bak";
        if (!File.Exists(bak)) File.Copy(path, bak);
        radio.Save(path);
    }

    private static void ApplyRadioInfo(RadioInfo radio, StationInfo station, string bankName,
                                       IReadOnlyList<AddedSample> added, List<BuildItem> items)
    {
        var editor = radio.StationByNumber(station.Number)
            ?? throw new InvalidOperationException($"station #{station.Number} not found in RadioInfo");

        editor.RegisterBank(bankName);
        
        foreach (var a in added)
        {
            editor.AddCustom(a.SoundName, a.Frames, a.SampleRate, a.DisplayName, a.Artist);
        }

        foreach (var it in items)
        {
            editor.SetEnabled(it.SoundName, it.Enabled);
        }

        var listNames = items.Select(i => i.SoundName).ToHashSet();
        foreach (var sn in editor.CustomSoundNames().Where(sn => !listNames.Contains(sn)).ToList())
        {
            editor.RemoveCustom(sn);
        }
        
        editor.FixCustomMarkers();
    }
}
