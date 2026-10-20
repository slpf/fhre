using CommunityToolkit.Mvvm.ComponentModel;
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
    public string? RestoreFsbPath { get; set; }
    
    public bool CanPlay => UsesFileSource
        ? (IsFileSourceFsb ? Tools.HasVgmstream : Tools.HasFfmpeg)
        : SubIndex >= 0 && SampleLength > 0 && Tools.HasVgmstream;

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
    
    public string? SourcePath { get; set; }

    [ObservableProperty] private string? _replacementPath;

    [ObservableProperty] private bool _markersLoading;

    [ObservableProperty] private bool _replaced;
    public bool IsReplacing => ReplacementPath is not null;
    public bool ShowRplBadge => IsReplacing;
    public bool ShowCstBadge => Replaced && !IsReplacing && !IsCustom;
    public bool CanReplace => true;
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
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(CanRestoreDefault));
    }

    partial void OnReplacedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowCstBadge));
        OnPropertyChanged(nameof(CanRestoreDefault));
    }

    public Dictionary<string, long>? Markers { get; set; }

    public void StageReplacement(string sourcePath, string title, string? artist, double durationSeconds,
        bool resetMarkers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var markerRate = SampleRate > 0 ? SampleRate : BankSampleRate;
        if (resetMarkers)
        {
            Markers = null;
        }
        else if (UsesMilliseconds && markerRate > 0 && markerRate != 48000 && Markers is { } markers)
        {
            Markers = markers.ToDictionary(m => m.Key,
                m => m.Value < 0 ? -1 : checked((long) Math.Round(m.Value * (48000.0 / markerRate))));
        }
        ReplacementPath = sourcePath;
        Title = title;
        Artist = artist;
        var duration = double.IsFinite(durationSeconds) && durationSeconds > 0 ? durationSeconds : 0;
        SampleRate = UsesMilliseconds ? 48000 : 0;
        SampleLength = UsesMilliseconds ? checked((long) Math.Round(duration * SampleRate)) : 0;
        PendingDurationSeconds = duration > 0 ? duration : null;
    }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string? _artist;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private double? _gainDb;

    public bool HasArtist => !string.IsNullOrWhiteSpace(Artist);

    public TrackItemViewModel(RadioTrack track, double? gainDb = null)
    {
        SoundName = track.SoundName;
        Origin = track.Origin;
        UsesMilliseconds = track.UsesMilliseconds;
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
            return string.Join("   ·   ", parts);
        }
    }

    partial void OnGainDbChanged(double? value) => OnPropertyChanged(nameof(InfoLine));
    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(InfoLine));
    partial void OnArtistChanged(string? value) => OnPropertyChanged(nameof(HasArtist));
}
