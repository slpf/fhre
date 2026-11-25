using CommunityToolkit.Mvvm.ComponentModel;
using FH6RB.Assets;
using FH6RB.Core;
using FH6RB.Services;

namespace FH6RB.ViewModels;

public enum TrackPlayState { Idle, Loading, Playing, Paused }

public sealed partial class TrackItemViewModel : ObservableObject
{
    public string SoundName { get; }
    public TrackOrigin Origin { get; }
    public bool IsCustom => Origin == TrackOrigin.Custom;
    public bool UsesMilliseconds { get; }
    public bool UsesTrackBreakDown { get; }
    public ulong? BankId { get; }
    public bool IsBankOnly { get; }
    public bool CanEdit => !IsBankOnly;
    public bool CanToggle => !IsBankOnly;
    public string EditTip => IsBankOnly ? Str.TipMissingTrackInfo : Str.TipEdit;
    public string ToggleTip => IsBankOnly ? Str.TipMissingTrackInfo : Enabled ? Str.TipDisable : Str.TipEnable;
    
    public bool CanDelete => IsCustom;
    public bool CanRestoreDefault => !IsCustom && (IsReplacing || Replaced);
    public bool RestoreFromBak { get; set; }
    
    public bool IsUnbuilt => IsCustom && SourcePath is not null;

    public long SampleLength { get; set; }
    public int SampleRate { get; set; }
    public long BankSampleLength { get; set; }
    public int BankSampleRate { get; set; }
    public int SubIndex { get; set; } = -1;
    public long BankBytes { get; set; }
    public int RestoreBakIndex { get; set; } = -1;
    [ObservableProperty] private string? _restoreFsbPath;
    
    public bool CanPlay => HasDecoder
        && (UsesFileSource || RestoreFsbPath is not null || SubIndex >= 0 && SampleLength > 0);

    public bool CanEditMarkers => !IsBankOnly && CanPlay && !MarkersLoading;
    private bool NeedsBankDecoder => !UsesFileSource || IsFileSourceFsb;
    private string DecoderName => NeedsBankDecoder ? "vgmstream" : "ffmpeg";
    private bool HasDecoder => NeedsBankDecoder ? Tools.HasVgmstream : Tools.HasFfmpeg;
    public string PlayTip => HasDecoder ? Str.TipPlay : string.Format(Str.TipMissingToolsFmt, DecoderName);
    public string MarkersTip => IsBankOnly ? Str.TipMissingTrackInfo
        : HasDecoder ? Str.TipMarkers : string.Format(Str.TipMissingToolsFmt, DecoderName);
    public string ReplaceTip => IsBankOnly ? Str.TipMissingTrackInfo : CanReplace ? Str.TipReplaceAudio
        : string.Format(Str.TipMissingToolsFmt, string.Join(", ", Tools.MissingForAddTracks()));

    public void RefreshToolAvailability()
    {
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(CanReplace));
        OnPropertyChanged(nameof(CanEditMarkers));
        OnPropertyChanged(nameof(PlayTip));
        OnPropertyChanged(nameof(ReplaceTip));
        OnPropertyChanged(nameof(MarkersTip));
    }

    [ObservableProperty] private TrackPlayState _playState = TrackPlayState.Idle;

    public bool IsLoadingPlay => PlayState == TrackPlayState.Loading;
    public bool IsPlayingNow => PlayState == TrackPlayState.Playing;
    public bool ShowPlayIcon => PlayState is TrackPlayState.Idle or TrackPlayState.Paused;

    partial void OnPlayStateChanged(TrackPlayState value)
    {
        OnPropertyChanged(nameof(IsLoadingPlay));
        OnPropertyChanged(nameof(IsPlayingNow));
        OnPropertyChanged(nameof(ShowPlayIcon));
    }
    
    [ObservableProperty] private string? _sourcePath;

    [ObservableProperty] private string? _replacementPath;

    [ObservableProperty] private bool _markersLoading;

    partial void OnMarkersLoadingChanged(bool value) => OnPropertyChanged(nameof(CanEditMarkers));

    [ObservableProperty] private bool _replaced;
    public bool IsReplacing => ReplacementPath is not null;
    public bool ShowRplBadge => IsReplacing;
    public bool ShowCstBadge => Replaced && !IsReplacing && !IsCustom;
    public bool CanReplace => !IsBankOnly && Tools.CanAddTracks;
    public bool UsesFileSource => IsUnbuilt || IsReplacing;
    public string? FileSource => IsReplacing ? ReplacementPath : SourcePath;
    public bool IsFileSourceFsb => UsesFileSource
        && FileSource!.EndsWith(".fsb", StringComparison.OrdinalIgnoreCase);

    partial void OnReplacementPathChanged(string? value)
    {
        if (value is not null)
        {
            RestoreFromBak = false;
            RestoreBakIndex = -1;
            RestoreFsbPath = null;
        }
        OnPropertyChanged(nameof(IsReplacing));
        OnPropertyChanged(nameof(ShowRplBadge));
        OnPropertyChanged(nameof(ShowCstBadge));
        OnPropertyChanged(nameof(UsesFileSource));
        OnPropertyChanged(nameof(FileSource));
        OnPropertyChanged(nameof(IsFileSourceFsb));
        RefreshToolAvailability();
        OnPropertyChanged(nameof(CanRestoreDefault));
    }

    partial void OnReplacedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowCstBadge));
        OnPropertyChanged(nameof(CanRestoreDefault));
    }

    public Dictionary<string, long>? Markers { get; set; }

    private bool _replaceAllMarkers;
    private HashSet<string>? _editedMarkerNames;
    internal bool? ReplaceMetadata { get; set; }

    internal void SetAllMarkers(Dictionary<string, long>? markers)
    {
        Markers = markers;
        _replaceAllMarkers = true;
        _editedMarkerNames = null;
    }

    internal void SetEditedMarkers(Dictionary<string, long> markers, IEnumerable<string> names)
    {
        Markers = markers;
        (_editedMarkerNames ??= new(StringComparer.Ordinal)).UnionWith(names);
    }

    public BuildItem CreateBuildItem()
    {
        var preserveMarkers = IsReplacing && ReplaceMetadata.HasValue && !_replaceAllMarkers;
        var markers = Markers;
        if (preserveMarkers)
            markers = _editedMarkerNames is null ? null : markers?.Where(m => _editedMarkerNames.Contains(m.Key)).ToDictionary();
        return new BuildItem(SoundName, IsCustom && UsesFileSource, UsesFileSource ? FileSource : null, Title, Artist, GainDb,
            Enabled, markers, !IsCustom && IsReplacing, RestoreFromBak,
            MarkerSampleRate: SampleRate > 0 ? SampleRate : BankSampleRate, Normalization: Normalization,
            BankId: BankId, ReplaceMetadata: IsReplacing ? ReplaceMetadata : null, PreserveMarkers: preserveMarkers);
    }

    public void StageReplacement(string sourcePath, string title, string? artist, double durationSeconds,
        bool resetMarkers, bool replaceMetadata = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var markerRate = SampleRate > 0 ? SampleRate : BankSampleRate;
        var duration = double.IsFinite(durationSeconds) && durationSeconds > 0 ? durationSeconds : 0;
        var sampleRate = !UsesMilliseconds && !resetMarkers && Markers is not null && markerRate > 0
            ? markerRate : 48000;
        var sampleLength = checked((long)Math.Round(duration * sampleRate));
        if (resetMarkers)
        {
            SetAllMarkers(null);
        }
        else if (UsesMilliseconds && markerRate > 0 && markerRate != 48000 && Markers is { } markers)
        {
            Markers = markers.ToDictionary(m => m.Key,
                m => m.Value < 0 ? -1 : checked((long) Math.Round(m.Value * (48000.0 / markerRate))));
        }
        ReplaceMetadata = replaceMetadata || (IsReplacing && ReplaceMetadata != false);
        ReplacementPath = sourcePath;
        if (replaceMetadata)
        {
            Title = title;
            Artist = artist;
        }
        SampleRate = sampleRate;
        SampleLength = sampleLength;
        PendingDurationSeconds = duration > 0 ? duration : null;
    }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string? _artist;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private double? _gainDb;
    [ObservableProperty] private NormalizationOverride? _normalization;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InfoLine))]
    [NotifyPropertyChangedFor(nameof(LoudnessTip))]
    private string _loudnessText = Str.TrackLoudnessPending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoudnessTip))]
    private string? _loudnessError;

    public string? LoudnessTip => LoudnessText.Length == 0 ? null
        : LoudnessError is { } error ? Str.TipTrackLoudness + "\n\n" + error : Str.TipTrackLoudness;

    public bool HasArtist => !string.IsNullOrWhiteSpace(Artist);

    public TrackItemViewModel(RadioTrack track, double? gainDb = null)
    {
        SoundName = track.SoundName;
        Origin = track.Origin;
        UsesMilliseconds = track.UsesMilliseconds;
        UsesTrackBreakDown = track.UsesTrackBreakDown || track.UsesMilliseconds;
        BankId = track.BankId;
        IsBankOnly = BankId is { } id && id != Lookup.SoundNameToId(SoundName);
        SampleLength = track.SampleLength;
        SampleRate = track.SampleRate;
        BankSampleLength = track.SampleLength;
        BankSampleRate = track.SampleRate;
        SubIndex = track.SubIndex;
        BankBytes = track.BankBytes;
        Replaced = track.Replaced;
        Markers = track.Markers is { } m ? new Dictionary<string, long>(m) : null;

        _title = track.DisplayName ?? track.SoundName;
        _artist = track.Artist;
        _enabled = track.Enabled;
        _gainDb = gainDb;
    }

    [ObservableProperty] private double? _pendingDurationSeconds;

    public string DurationText
    {
        get
        {
            double seconds;
            if (SampleRate > 0 && SampleLength > 0)
            {
                seconds = SampleLength / (double)SampleRate;
            }
            else if (PendingDurationSeconds is { } pd && pd > 0)
            {
                seconds = pd;
            }
            else
            {
                return "--:--";
            }

            var s = (int)seconds;
            return $"{s / 60}:{s % 60:00}";
        }
    }

    partial void OnPendingDurationSecondsChanged(double? value)
    {
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(InfoLine));
    }
    
    public string InfoLine
    {
        get
        {
            var parts = new List<string> { DurationText };

            if (GainDb is { } gain && Math.Abs(gain) >= 0.05)
            {
                parts.Add($"{(gain > 0 ? "+" : "")}{gain:0.0} dB");
            }

            parts.Add(SoundName);
            if (LoudnessText.Length > 0) parts.Add(LoudnessText);
            return string.Join("   ·   ", parts);
        }
    }

    partial void OnGainDbChanged(double? value) => OnPropertyChanged(nameof(InfoLine));
    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(InfoLine));
    partial void OnArtistChanged(string? value) => OnPropertyChanged(nameof(HasArtist));
    partial void OnEnabledChanged(bool value) => OnPropertyChanged(nameof(ToggleTip));
}
