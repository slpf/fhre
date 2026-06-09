using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FH6RB.ViewModels;

public sealed partial class MarkerField : ObservableObject
{
    public string Name { get; init; } = "";

    [ObservableProperty] private long _position;

    public bool IsOff => Position == -1;

    partial void OnPositionChanged(long value) => OnPropertyChanged(nameof(IsOff));
}

public sealed class MarkerGroup
{
    public string Title { get; init; } = "";
    public ObservableCollection<MarkerField> Fields { get; } = [];
}

public sealed partial class EditWindowViewModel : ObservableObject
{
    public string SoundName { get; }
    
    public bool ShowVolume { get; }

    [ObservableProperty] private string _title;
    [ObservableProperty] private string? _artist;
    [ObservableProperty] private double _gainDb;

    public ObservableCollection<MarkerGroup> Groups { get; } = [];
    public bool Saved { get; private set; }

    public void MarkSaved() => Saved = true;
    
    private static readonly (string Group, string[] Names)[] Schema =
    [
        ("Core", ["VeryStart", "TrackStart", "End"]),
        ("DJ / Drops", ["DJDrop", "TrackDrop", "DJSegment", "PostDrop", "DJStart", "StingerStart"]),
        ("Track loops", ["TrackLoopStart", "TrackLoopEnd", "PostRaceLoopStart", "PostRaceLoopEnd"]),
        ("Extra loops", ["Loop1Start", "Loop1End", "Loop2Start", "Loop2End", "Loop3Start", "Loop3End", "Loop4Start", "Loop4End", "Loop5Start", "Loop5End"]),
        ("Sections", ["Section1", "Section2", "Section3", "Section4", "Section5"]),
        ("Other", ["BinkTransition"]),
    ];

    public EditWindowViewModel(TrackItemViewModel track, IReadOnlyDictionary<string, long>? markers = null)
    {
        SoundName = track.SoundName;
        ShowVolume = track.IsUnbuilt;
        _title = track.Title;
        _artist = track.Artist;
        _gainDb = track.GainDb ?? 0;

        var end = track.SampleLength > 0 ? track.SampleLength - 1 : -1;

        foreach (var (group, names) in Schema)
        {
            var grp = new MarkerGroup { Title = group };

            foreach (var name in names)
            {
                long position;
                if (markers is not null && markers.TryGetValue(name, out var value))
                {
                    position = value;
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

                grp.Fields.Add(new MarkerField { Name = name, Position = position });
            }

            Groups.Add(grp);
        }
    }

    public string GainText => $"{(GainDb > 0 ? "+" : "")}{GainDb:0.0} dB";

    partial void OnGainDbChanged(double value) => OnPropertyChanged(nameof(GainText));

    public void Apply(TrackItemViewModel track)
    {
        track.Title = Title;
        track.Artist = Artist;

        if (track.IsCustom)
        {
            track.GainDb = Math.Abs(GainDb) < 0.05 ? null : GainDb;
        }
    }
}
