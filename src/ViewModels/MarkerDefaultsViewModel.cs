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
        var usesMilliseconds = GameScanner.DetectVersion(settings.GamePath) == 2;
        Hint = usesMilliseconds ? Str.HintMarkerDefaultsHorizon2 : Str.HintMarkerDefaults;

        foreach (var (name, _) in MarkerDefaults.Order)
        {
            var displayName = usesMilliseconds && name == "PostDrop" ? "TrackBreakDown" : name;
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
        var specs = Fields.ToDictionary(f => f.SettingName, f => f.Spec?.Trim() ?? "");

        MarkerDefaults.Apply(specs);
        _settings.MarkerDefaults = MarkerDefaults.Snapshot();
        SettingsService.Save(_settings);

        Saved = true;
    }
}
