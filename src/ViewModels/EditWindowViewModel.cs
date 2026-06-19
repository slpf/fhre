using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FH6RB.Assets;
using FH6RB.Core;
using FH6RB.Services;

namespace FH6RB.ViewModels;

public sealed partial class MarkerField : ObservableObject
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public int SampleRate { get; init; }
    public long SampleLength { get; init; }

    [ObservableProperty] private long _position;
    [ObservableProperty] private bool _highlighted;

    public bool IsOff => Position < 0;
    
    public string SecondsText
    {
        get => Position < 0 ? ""
            : SampleRate > 0
                ? ((double) Position / SampleRate).ToString("0.###", CultureInfo.InvariantCulture)
                : Position.ToString(CultureInfo.InvariantCulture);
        set
        {
            var t = value?.Trim();

            if (string.IsNullOrEmpty(t))
            {
                Position = -1;
                return;
            }

            if (t.EndsWith('%'))
            {
                var p = t[..^1].Trim().Replace(',', '.');

                if (SampleLength > 0 && double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) && pct >= 0)
                {
                    Position = Math.Clamp((long) Math.Round(pct / 100.0 * SampleLength), 0, SampleLength - 1);
                }

                return;
            }

            if (SampleRate > 0 && double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) && sec >= 0)
            {
                Position = (long) Math.Round(sec * SampleRate);
            }
        }
    }

    partial void OnPositionChanged(long value)
    {
        OnPropertyChanged(nameof(IsOff));
        OnPropertyChanged(nameof(SecondsText));
    }
}

public sealed class MarkerGroup
{
    public string Title { get; init; } = "";
    public ObservableCollection<MarkerField> Fields { get; } = [];
}

public sealed partial class EditWindowViewModel : ObservableObject
{
    public string SoundName { get; }
    public string FullName { get; }

    public bool ShowVolume { get; }
    public bool CanEditMarkers { get; }
    public long SampleLength { get; }
    public int SampleRate { get; }
    public double TotalSeconds => SampleRate > 0 ? SampleLength / (double) SampleRate : 0;

    [ObservableProperty] private float[]? _peaks;

    public string? WavPath { get; set; }
    public AppSettings? Settings { get; set; }

    public Func<Task<(float[]? Peaks, string? Wav)>>? PeaksLoader { get; set; }
    private bool _peaksLoaded;

    public IEnumerable<MarkerField> AllMarkers => Groups.SelectMany(g => g.Fields);

    public async Task EnsurePeaksAsync()
    {
        if (_peaksLoaded || PeaksLoader is null)
        {
            return;
        }
        
        _peaksLoaded = true;
        
        try
        {
            var (p, w) = await PeaksLoader();
            Peaks = p;
            WavPath = w;
        }
        catch
        {
            Peaks = null;
        }
    }

    [ObservableProperty] private string _title;
    [ObservableProperty] private string? _artist;
    [ObservableProperty] private double _gainDb;

    public ObservableCollection<MarkerGroup> Groups { get; } = [];
    public bool Saved { get; private set; }

    public void MarkSaved() => Saved = true;

    private static readonly (string Group, string[] Names)[] Schema =
    [
        (Str.GrpCore, ["TrackStart", "End"]),
        (Str.GrpDjDrops, ["DJStart", "DJDrop", "DJSegment", "TrackDrop"]),
        (Str.GrpTrackLoops, ["TrackLoopStart", "TrackLoopEnd", "PostDrop", "PostRaceLoopStart", "PostRaceLoopEnd"]),
    ];

    private static readonly Dictionary<string, string> Descriptions = new()
    {
        ["TrackStart"] = Str.MkTrackStart,
        ["End"] = Str.MkEnd,
        ["DJStart"] = Str.MkDjStart,
        ["DJDrop"] = Str.MkDjDrop,
        ["DJSegment"] = Str.MkDjSegment,
        ["TrackDrop"] = Str.MkTrackDrop,
        ["PostDrop"] = Str.MkPostDrop,
        ["TrackLoopStart"] = Str.MkTrackLoopStart,
        ["TrackLoopEnd"] = Str.MkTrackLoopEnd,
        ["PostRaceLoopStart"] = Str.MkPostRaceLoopStart,
        ["PostRaceLoopEnd"] = Str.MkPostRaceLoopEnd,
    };

    private MarkerField? _postDrop;
    private MarkerField? _postRaceLoopStart;
    private bool _suppressFloor;

    public EditWindowViewModel(TrackItemViewModel track)
    {
        SoundName = track.SoundName;
        FullName = track.Title + " - " + track.Artist + " (" + track.SoundName + ")";
        ShowVolume = track.UsesFileSource;
        _title = track.Title;
        _artist = track.Artist;
        _gainDb = track.GainDb ?? 0;

        var rate = track.SampleRate;
        var len = track.SampleLength;
        
        CanEditMarkers = len > 0 && rate > 0;
        SampleLength = len;
        SampleRate = rate;

        var end = len > 0 ? len - 1 : -1;
        var auto = len > 0 ? RadioStationEditor.ComputeAutoMarkers(len, rate) : null;
        var existing = track.Markers;

        foreach (var (group, names) in Schema)
        {
            var grp = new MarkerGroup { Title = group };

            foreach (var name in names)
            {
                long position;
                
                if (existing is not null && existing.TryGetValue(name, out var ev))
                {
                    position = ev;
                }
                else if (auto is not null && auto.TryGetValue(name, out var av))
                {
                    position = av;
                }
                else
                {
                    position = name switch
                    {
                        "TrackStart" => 0,
                        "End" => end,
                        _ => -1,
                    };
                }

                grp.Fields.Add(new MarkerField { Name = name, Description = Descriptions.GetValueOrDefault(name, ""), SampleRate = rate, SampleLength = len, Position = position });
            }

            Groups.Add(grp);
        }

        _postDrop = AllMarkers.FirstOrDefault(f => f.Name == "PostDrop");
        _postRaceLoopStart = AllMarkers.FirstOrDefault(f => f.Name == "PostRaceLoopStart");

        if (_postDrop is not null && _postRaceLoopStart is not null)
        {
            _postDrop.PropertyChanged += OnMarkerMoved;
            _postRaceLoopStart.PropertyChanged += OnMarkerMoved;
        }
    }

    private void OnMarkerMoved(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressFloor || e.PropertyName != nameof(MarkerField.Position))
        {
            return;
        }

        if (_postDrop is { Position: >= 0 } pd && _postRaceLoopStart is { Position: >= 0 } prls
            && pd.Position > prls.Position)
        {
            pd.Position = prls.Position;
        }
    }

    public void ResetMarkersToDefaults()
    {
        if (!CanEditMarkers)
        {
            return;
        }

        var def = RadioStationEditor.ComputeAutoMarkers(SampleLength, SampleRate);

        _suppressFloor = true;

        foreach (var f in AllMarkers)
        {
            f.Position = def.TryGetValue(f.Name, out var p) ? p : -1;
        }

        _suppressFloor = false;
    }

    public string GainText => $"{(GainDb > 0 ? "+" : "")}{GainDb:0.0} dB";

    partial void OnGainDbChanged(double value) => OnPropertyChanged(nameof(GainText));

    public void Apply(TrackItemViewModel track)
    {
        track.Title = Title;
        track.Artist = Artist;

        if (track.UsesFileSource)
        {
            track.GainDb = Math.Abs(GainDb) < 0.05 ? null : GainDb;
        }

        if (!CanEditMarkers)
        {
            return;
        }
        
        var dict = new Dictionary<string, long>();
        
        foreach (var g in Groups)
        {
            foreach (var f in g.Fields)
            {
                dict[f.Name] = f.Position;
            }
        }

        track.Markers = dict;
    }
}
