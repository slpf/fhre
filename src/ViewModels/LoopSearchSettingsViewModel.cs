using CommunityToolkit.Mvvm.ComponentModel;
using FH6RB.Services;

namespace FH6RB.ViewModels;

public sealed partial class LoopSearchSettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;

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
    [ObservableProperty] private bool _useSsmNomination;
    [ObservableProperty] private bool _requireOnsetAlignment;

    public bool Saved { get; private set; }

    public LoopSearchSettingsViewModel(AppSettings settings)
    {
        _settings = settings;
        _autoTune = settings.LoopAutoTune;
        _noteDeviation = settings.LoopNoteDeviation;
        _minMatch = settings.LoopMinMatch;
        _borderSimilarity = settings.LoopBorderSimilarity;
        _transitionSmoothness = settings.LoopTransitionSmoothness;
        _loudnessDifference = settings.LoopLoudnessDifference;
        _useHarmonicChroma = settings.LoopUseHarmonicChroma;
        _useSsmNomination = settings.LoopUseSsmNomination;
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
        _settings.LoopUseSsmNomination = UseSsmNomination;
        _settings.LoopRequireOnsetAlignment = RequireOnsetAlignment;
        _settings.LoopPreEmphasis = PreEmphasis;
        _settings.LoopMultiResolution = MultiResolution;
        _settings.LoopDisablePruning = DisablePruning;
        // LoopMinSeconds is no longer exposed in the UI; persisted value (if any) is ignored.
        SettingsService.Save(_settings);
        Saved = true;
    }
}
