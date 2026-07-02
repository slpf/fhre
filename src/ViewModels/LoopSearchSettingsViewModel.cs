using CommunityToolkit.Mvvm.ComponentModel;
using FH6RB.Core;
using FH6RB.Services;

namespace FH6RB.ViewModels;

public sealed partial class LoopSearchSettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private LoopStage _stages;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualEnabled))]
    private bool _autoTune;

    public bool ManualEnabled => !AutoTune;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoteDeviationText))]
    private double _noteDeviation;

    public string NoteDeviationText => $"{NoteDeviation:0.000}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MinMatchText))]
    private double _minMatch;

    public string MinMatchText => $"{MinMatch * 100:0}%";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BorderSimilarityText))]
    private double _borderSimilarity;

    public string BorderSimilarityText => $"{BorderSimilarity:0.00}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransitionSmoothnessText))]
    private double _transitionSmoothness;

    public string TransitionSmoothnessText => $"{TransitionSmoothness:0.00}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoudnessDifferenceText))]
    private double _loudnessDifference;

    public string LoudnessDifferenceText => $"{LoudnessDifference:0.00}";

    [ObservableProperty] private bool _preEmphasis;
    [ObservableProperty] private bool _multiResolution;
    [ObservableProperty] private bool _disablePruning;
    [ObservableProperty] private bool _useHarmonicChroma;
    [ObservableProperty] private bool _requireOnsetAlignment;

    public bool StageBorderFilter { get => (_stages & LoopStage.BorderFilter) != 0; set => UpdateStage(LoopStage.BorderFilter, value); }
    public bool StageSmoothnessFilter { get => (_stages & LoopStage.SmoothnessFilter) != 0; set => UpdateStage(LoopStage.SmoothnessFilter, value); }
    public bool StageFluxFilter { get => (_stages & LoopStage.FluxFilter) != 0; set => UpdateStage(LoopStage.FluxFilter, value); }
    public bool StageXCorr { get => (_stages & LoopStage.XCorr) != 0; set => UpdateStage(LoopStage.XCorr, value); }
    public bool StageZeroCrossingSnap { get => (_stages & LoopStage.ZeroCrossingSnap) != 0; set => UpdateStage(LoopStage.ZeroCrossingSnap, value); }
    public bool StageCyclicity { get => (_stages & LoopStage.Cyclicity) != 0; set => UpdateStage(LoopStage.Cyclicity, value); }
    public bool StagePhase { get => (_stages & LoopStage.Phase) != 0; set => UpdateStage(LoopStage.Phase, value); }
    public bool StageBarSnap { get => (_stages & LoopStage.BarSnap) != 0; set => UpdateStage(LoopStage.BarSnap, value); }
    public bool StagePhraseSnap { get => (_stages & LoopStage.PhraseSnap) != 0; set => UpdateStage(LoopStage.PhraseSnap, value); }

    private bool UpdateStage(LoopStage f, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? prop = null)
    {
        var nv = value ? _stages | f : _stages & ~f;
        if (nv == _stages) return false;
        _stages = nv;
        OnPropertyChanged(prop);
        return true;
    }

    public bool Saved { get; private set; }

    public LoopSearchSettingsViewModel(AppSettings settings)
    {
        _settings = settings;
        _stages = settings.LoopStages;
        _autoTune = settings.LoopAutoTune;
        _noteDeviation = settings.LoopNoteDeviation;
        _minMatch = settings.LoopMinMatch;
        _borderSimilarity = settings.LoopBorderSimilarity;
        _transitionSmoothness = settings.LoopTransitionSmoothness;
        _loudnessDifference = settings.LoopLoudnessDifference;
        _useHarmonicChroma = settings.LoopUseHarmonicChroma;
        _requireOnsetAlignment = settings.LoopRequireOnsetAlignment;
        _preEmphasis = settings.LoopPreEmphasis;
        _multiResolution = settings.LoopMultiResolution;
        _disablePruning = settings.LoopDisablePruning;
    }

    public void Save()
    {
        _settings.LoopAutoTune = AutoTune;
        _settings.LoopNoteDeviation = NoteDeviation;
        _settings.LoopMinMatch = MinMatch;
        _settings.LoopBorderSimilarity = BorderSimilarity;
        _settings.LoopTransitionSmoothness = TransitionSmoothness;
        _settings.LoopLoudnessDifference = LoudnessDifference;
        _settings.LoopUseHarmonicChroma = UseHarmonicChroma;
        _settings.LoopRequireOnsetAlignment = RequireOnsetAlignment;
        _settings.LoopPreEmphasis = PreEmphasis;
        _settings.LoopMultiResolution = MultiResolution;
        _settings.LoopDisablePruning = DisablePruning;
        _settings.LoopStages = _stages;
        SettingsService.Save(_settings);
        Saved = true;
    }
}
