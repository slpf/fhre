using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FH6RB.Assets;
using FH6RB.Core;
using FH6RB.Services;

namespace FH6RB.ViewModels;

public enum MarkerValueUnit
{
    Seconds,
    Samples,
    Percent
}

public sealed partial class MarkerField : ObservableObject
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public int SampleRate { get; init; }
    public long SampleLength { get; init; }
    public long InitialPosition { get; init; }
    public string? LoopEndName { get; init; }
    public string? LoopReturnToName { get; init; }
    public bool CanLoop => LoopEndName is not null || LoopReturnToName is not null;
    public bool CanSuggest => LoopEndName is not null && LoopReturnToName is null;
    public string PlayLoopTip => Name switch
    {
        "TrackDrop" => Str.TipPlayTdlsLoop,
        "TrackLoopStart" => Str.TipPlayTlsLoop,
        "PostDrop" => Str.TipPlayPdlsLoop,
        "TrackBreakDown" => Str.TipPlayBreakDownLoop,
        "PostRaceLoopStart" => Str.TipPlayPlsLoop,
        _ => Str.TipPlayMarkerLoop,
    };

    [ObservableProperty] private long _position;
    [ObservableProperty] private bool _highlighted;
    [ObservableProperty] private MarkerValueUnit _displayUnit;
    [ObservableProperty] private bool _locked;

    public bool IsOff => Position < 0;
    public bool Editable => !Locked;

    public string ValueTip => Position < 0
        ? Str.MarkerValueOff
        : string.Format(CultureInfo.InvariantCulture, Str.MarkerValueTipFmt,
            Position,
            SampleRate > 0 ? (double) Position / SampleRate : 0,
            SampleLength > 0 ? (double) Position / SampleLength * 100 : 0);

    public void Revert() => Position = InitialPosition;
    public void Reset()
    {
        var defaultName = Name switch
        {
            "TrackBreakDown" => "PostDrop",
            "StingerStart" => "DJStart",
            _ => Name,
        };
        var position = MarkerDefaults.Resolve(MarkerDefaults.Get(defaultName), SampleLength, SampleRate);
        Position = Name == "StingerStart" && position >= 0
            ? Math.Max(0, position - SampleRate)
            : position;
    }
    
    public string SecondsText
    {
        get
        {
            if (Position < 0)
            {
                return "";
            }

            return DisplayUnit switch
            {
                MarkerValueUnit.Samples => Position.ToString(CultureInfo.InvariantCulture) + "s",
                MarkerValueUnit.Percent => SampleLength > 0
                    ? ((double) Position / SampleLength * 100).ToString("0.###", CultureInfo.InvariantCulture) + "%"
                    : "",
                _ => SampleRate > 0
                    ? ((double) Position / SampleRate).ToString("0.###", CultureInfo.InvariantCulture)
                    : Position.ToString(CultureInfo.InvariantCulture)
            };
        }
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

                if (SampleLength > 0 && double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                {
                    var off = (long) Math.Round(pct / 100.0 * SampleLength);
                    Position = Math.Clamp(pct < 0 ? SampleLength + off : off, 0, SampleLength - 1);
                }

                return;
            }

            if (t.EndsWith('s') || t.EndsWith('S'))
            {
                var sp = t[..^1].Trim().Replace(',', '.');

                if (SampleLength > 0 && double.TryParse(sp, NumberStyles.Float, CultureInfo.InvariantCulture, out var smp))
                {
                    var off = (long) Math.Round(smp);
                    Position = Math.Clamp(smp < 0 ? SampleLength + off : off, 0, SampleLength - 1);
                }

                return;
            }

            if (SampleRate > 0 && double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var sec))
            {
                Position = sec < 0
                    ? Math.Clamp(SampleLength + (long) Math.Round(sec * SampleRate), 0, SampleLength - 1)
                    : (long) Math.Round(sec * SampleRate);
            }
        }
    }

    partial void OnPositionChanged(long value)
    {
        OnPropertyChanged(nameof(IsOff));
        OnPropertyChanged(nameof(SecondsText));
        OnPropertyChanged(nameof(ValueTip));
    }

    partial void OnDisplayUnitChanged(MarkerValueUnit value) => OnPropertyChanged(nameof(SecondsText));

    partial void OnLockedChanged(bool value) => OnPropertyChanged(nameof(Editable));
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
    public bool ShowNormalization { get; }
    public bool CanEditMarkers { get; }
    public long SampleLength { get; }
    public int SampleRate { get; }
    public bool UsesMilliseconds { get; }
    public bool UsesTrackBreakDown { get; }
    public double TotalSeconds => SampleRate > 0 ? SampleLength / (double) SampleRate : 0;

    [ObservableProperty] private float[]? _peaks;

    public string? WavPath { get; set; }
    public AppSettings? Settings { get; set; }

    public Func<Task<(float[]? Peaks, string? Wav)>>? PeaksLoader { get; set; }

    [ObservableProperty] private bool _isSuggesting;
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

    private readonly NormalizationOverride _globalNormalization;
    private NormalizationOverride? _customNormalization;
    private readonly IReadOnlyDictionary<string, long>? _savedMarkers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditNormalization))]
    [NotifyPropertyChangedFor(nameof(CanEditNormalizationTargets))]
    private bool _useGlobalNormalization;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditNormalizationTargets))]
    private bool _loudnessNormalize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetLufsText))]
    private double _targetLufs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetTruePeakText))]
    private double _targetTruePeak;

    [ObservableProperty] private bool _dynamicNormalization;

    public bool CanEditNormalization => ShowNormalization && !UseGlobalNormalization;
    public bool CanEditNormalizationTargets => CanEditNormalization && LoudnessNormalize;
    public string TargetLufsText => $"{TargetLufs:0.#} LUFS";
    public string TargetTruePeakText => $"{TargetTruePeak:0.0} dBTP";

    public ObservableCollection<MarkerGroup> Groups { get; } = [];
    public bool Saved { get; private set; }

    public void MarkSaved() => Saved = true;

    private static readonly (string Group, string[] Names)[] Schema =
    [
        (Str.GrpCore, ["TrackStart", "End"]),
        (Str.GrpDjDrops, ["DJDrop", "DJStart", "DJSegment", "TrackDrop"]),
        (Str.GrpTrackLoops, ["TrackLoopStart", "TrackLoopEnd", "PostDrop", "PostRaceLoopStart", "PostRaceLoopEnd"]),
    ];

    private static readonly (string Group, string[] Names)[] Horizon2Schema =
    [
        (Str.GrpCore, ["TrackStart", "End"]),
        (Str.GrpDjDrops, ["DJDrop", "DJStart", "StingerStart", "TrackDrop", "TrackBreakDown"]),
    ];

    private static readonly (string Group, string[] Names)[] Horizon3Schema = Schema
        .Select(group => (group.Group, group.Names.Select(name => name == "PostDrop" ? "TrackBreakDown" : name).ToArray()))
        .ToArray();

    public static readonly HashSet<string> EditorMarkerNames = new(Schema.SelectMany(s => s.Names));
    private static readonly HashSet<string> Horizon2MarkerNames = new(Horizon2Schema.SelectMany(s => s.Names));
    private static readonly HashSet<string> Horizon3MarkerNames = new(Horizon3Schema.SelectMany(s => s.Names));

    public static IReadOnlySet<string> MarkerNamesFor(bool usesMilliseconds, bool usesTrackBreakDown = false) =>
        usesMilliseconds ? Horizon2MarkerNames : usesTrackBreakDown ? Horizon3MarkerNames : EditorMarkerNames;

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
        ["TrackBreakDown"] = Str.MkTrackBreakDown,
        ["StingerStart"] = Str.MkStingerStart,
    };

    public EditWindowViewModel(TrackItemViewModel track, AppSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        SoundName = track.SoundName;
        FullName = track.Title + " - " + track.Artist + " (" + track.SoundName + ")";
        ShowVolume = track.UsesFileSource;
        ShowNormalization = track.UsesFileSource && !track.IsFileSourceFsb;
        Settings = settings;
        var global = settings ?? new AppSettings();
        _globalNormalization = new NormalizationOverride(global.LoudnessNormalize,
            AppSettings.ClampTargetLufs(global.TargetLufs), AppSettings.ClampTargetTruePeak(global.TargetTruePeak),
            global.DynamicNormalization);
        _customNormalization = track.Normalization;
        _useGlobalNormalization = track.Normalization is null;
        SetNormalizationDraft(_customNormalization ?? _globalNormalization);
        _title = track.Title;
        _artist = track.Artist;
        _gainDb = track.GainDb ?? 0;
        UsesMilliseconds = track.UsesMilliseconds;
        UsesTrackBreakDown = track.UsesTrackBreakDown;

        var rate = track.SampleRate;
        var len = track.SampleLength;
        
        CanEditMarkers = len > 0 && rate > 0;
        SampleLength = len;
        SampleRate = rate;

        var end = len > 0 ? len - 1 : -1;
        var auto = len > 0 ? RadioStationEditor.ComputeAutoMarkers(len, rate, UsesMilliseconds, UsesTrackBreakDown) : null;
        var existing = track.Markers is { } markers
            ? MarkerPresetService.AdaptMarkers(markers, UsesMilliseconds, rate, UsesTrackBreakDown)
            : null;
        _savedMarkers = track.UsesFileSource ? null : existing;

        foreach (var (group, names) in UsesMilliseconds ? Horizon2Schema : UsesTrackBreakDown ? Horizon3Schema : Schema)
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

                grp.Fields.Add(new MarkerField { Name = name,
                    Description = UsesMilliseconds && name == "TrackDrop" ? Str.MkTrackDropHorizon2
                        : !UsesMilliseconds && name == "TrackBreakDown" ? Str.MkTrackBreakDownHorizon3
                        : Descriptions.GetValueOrDefault(name, ""),
                    SampleRate = rate, SampleLength = len, Position = position, InitialPosition = position,
                    LoopEndName = UsesMilliseconds ? null : name switch { "TrackLoopStart" => "TrackLoopEnd", "PostRaceLoopStart" => "PostRaceLoopEnd", "TrackDrop" => "TrackLoopEnd", "PostDrop" or "TrackBreakDown" => "PostRaceLoopEnd", _ => null },
                    LoopReturnToName = UsesMilliseconds ? null : name switch { "TrackDrop" => "TrackLoopStart", "PostDrop" or "TrackBreakDown" => "PostRaceLoopStart", _ => null } });
            }

            Groups.Add(grp);
        }
    }

    private void SetNormalizationDraft(NormalizationOverride settings)
    {
        LoudnessNormalize = settings.LoudnessNormalize;
        TargetLufs = AppSettings.ClampTargetLufs(settings.TargetLufs);
        TargetTruePeak = AppSettings.ClampTargetTruePeak(settings.TargetTruePeak);
        DynamicNormalization = settings.DynamicNormalization;
    }

    private NormalizationOverride ReadNormalizationDraft() => new(LoudnessNormalize,
        AppSettings.ClampTargetLufs(TargetLufs), AppSettings.ClampTargetTruePeak(TargetTruePeak), DynamicNormalization);

    partial void OnUseGlobalNormalizationChanged(bool value)
    {
        if (value)
        {
            _customNormalization = ReadNormalizationDraft();
            SetNormalizationDraft(_globalNormalization);
        }
        else
        {
            SetNormalizationDraft(_customNormalization ?? _globalNormalization);
        }
    }

    public void ResetMarkersToDefaults()
    {
        if (!CanEditMarkers)
        {
            return;
        }

        var def = RadioStationEditor.ComputeAutoMarkers(SampleLength, SampleRate, UsesMilliseconds, UsesTrackBreakDown);

        foreach (var f in AllMarkers)
        {
            f.Position = def.TryGetValue(f.Name, out var p) ? p : -1;
        }
    }

    public int ApplyMarkerPositions(IReadOnlyDictionary<string, long> positions, int presetSampleRate = 0)
    {
        if (!CanEditMarkers) return 0;
        var max = UsesMilliseconds ? SampleLength : SampleLength - 1;
        var hits = 0;
        var scale = presetSampleRate > 0 && presetSampleRate != SampleRate
            ? (double)SampleRate / presetSampleRate
            : 1.0;
        var adapted = MarkerPresetService.AdaptMarkers(positions, UsesMilliseconds,
            presetSampleRate > 0 ? presetSampleRate : SampleRate, UsesTrackBreakDown);
        foreach (var f in AllMarkers)
        {
            if (adapted.TryGetValue(f.Name, out var p))
            {
                var scaled = p < 0 ? -1 : scale != 1.0 ? (long) Math.Round(p * scale) : p;
                f.Position = scaled < 0 ? -1 : Math.Clamp(scaled, 0, max);
                hits++;
            }
        }
        return hits;
    }

    public Dictionary<string, long> CollectMarkerPositions()
    {
        var dict = new Dictionary<string, long>();
        foreach (var f in AllMarkers)
        {
            if (f.Position >= 0 || UsesMilliseconds) dict[f.Name] = f.Position;
        }
        return dict;
    }

    public string GainText => $"{(GainDb > 0 ? "+" : "")}{GainDb:0.0} dB";

    partial void OnGainDbChanged(double value) => OnPropertyChanged(nameof(GainText));

    public IReadOnlyList<string> ValidateMarkers()
    {
        var errors = new List<string>();
        long Pos(string name) => AllMarkers.FirstOrDefault(f => f.Name == name)?.Position ?? -1;
        bool Unchanged(string first, string second) => _savedMarkers is not null
            && _savedMarkers.TryGetValue(first, out var originalFirst) && originalFirst == Pos(first)
            && _savedMarkers.TryGetValue(second, out var originalSecond) && originalSecond == Pos(second);

        var tls = Pos("TrackLoopStart");
        var tle = Pos("TrackLoopEnd");
        if (tls >= 0 && tle >= 0 && tls >= tle && !Unchanged("TrackLoopStart", "TrackLoopEnd"))
        {
            errors.Add(Str.ErrTrackLoopOrder);
        }

        var prls = Pos("PostRaceLoopStart");
        var prle = Pos("PostRaceLoopEnd");
        if (prls >= 0 && prle >= 0 && prls >= prle && !Unchanged("PostRaceLoopStart", "PostRaceLoopEnd"))
        {
            errors.Add(Str.ErrPostRaceLoopOrder);
        }

        var td = Pos("TrackDrop");
        if (td >= 0 && tle >= 0 && td > tle && !Unchanged("TrackDrop", "TrackLoopEnd"))
        {
            errors.Add(Str.ErrTrackDropAfterLoop);
        }

        var postDrop = UsesTrackBreakDown ? "TrackBreakDown" : "PostDrop";
        var pd = Pos(postDrop);
        if (pd >= 0 && prle >= 0 && pd > prle && !Unchanged(postDrop, "PostRaceLoopEnd"))
        {
            errors.Add(UsesTrackBreakDown ? Str.ErrBreakDownAfterLoop : Str.ErrPostDropAfterLoop);
        }

        return errors;
    }

    public void Apply(TrackItemViewModel track)
    {
        track.Title = Title;
        track.Artist = Artist;

        if (track.UsesFileSource)
        {
            track.GainDb = Math.Abs(GainDb) < 0.05 ? null : GainDb;
        }

        if (ShowNormalization && track.UsesFileSource && !track.IsFileSourceFsb)
        {
            track.Normalization = UseGlobalNormalization ? null : ReadNormalizationDraft();
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
