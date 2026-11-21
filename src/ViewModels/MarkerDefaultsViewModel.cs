using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FH6RB.Assets;
using FH6RB.Core;
using FH6RB.Services;

namespace FH6RB.ViewModels;

public sealed partial class MarkerDefaultField : ObservableObject
{
    public string Name { get; init; } = "";
    public string SettingName { get; init; } = "";

    [ObservableProperty] private string _spec = "";
}

public sealed partial class MarkerDefaultsViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    public ObservableCollection<MarkerDefaultField> Fields { get; } = [];

    public bool Saved { get; private set; }
    public string Hint { get; }

    public MarkerDefaultsViewModel(AppSettings settings)
    {
        _settings = settings;
        var version = GameScanner.DetectVersion(settings.GamePath);
        var usesMilliseconds = version == 2;
        var usesTrackBreakDown = version is 2 or 3;
        Hint = usesMilliseconds ? Str.HintMarkerDefaultsHorizon2 : Str.HintMarkerDefaults;

        foreach (var (name, _) in MarkerDefaults.Order)
        {
            var displayName = usesTrackBreakDown && name == "PostDrop" ? "TrackBreakDown" : name;
            if (usesMilliseconds && !EditWindowViewModel.MarkerNamesFor(true).Contains(displayName)) continue;
            Fields.Add(new MarkerDefaultField
            {
                Name = displayName,
                SettingName = name,
                Spec = MarkerDefaults.Get(name),
            });
        }
    }

    public void RestoreDefaults()
    {
        foreach (var f in Fields)
        {
            f.Spec = MarkerDefaults.Default(f.SettingName);
        }
    }

    public void Save()
    {
        var specs = MarkerDefaults.Snapshot();
        foreach (var field in Fields)
        {
            var spec = field.Spec?.Trim() ?? "";
            if (string.Equals(spec, Str.WatermarkOff, StringComparison.OrdinalIgnoreCase)) spec = MarkerDefaults.Off;
            if (spec.Length > 0 && !MarkerDefaults.TryParse(spec, out _, out _, out _))
                throw new FormatException(string.Format(Str.ErrMarkerDefaultFmt, field.Name));
            specs[field.SettingName] = spec;
        }

        SettingsService.Update(_settings, draft => draft.MarkerDefaults = specs);
        MarkerDefaults.Apply(specs);

        Saved = true;
    }
}
