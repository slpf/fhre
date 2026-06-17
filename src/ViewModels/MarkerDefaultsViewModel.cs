using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FH6RB.Core;
using FH6RB.Services;

namespace FH6RB.ViewModels;

public sealed partial class MarkerDefaultField : ObservableObject
{
    public string Name { get; init; } = "";

    [ObservableProperty] private string _spec = "";
}

public sealed partial class MarkerDefaultsViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    public ObservableCollection<MarkerDefaultField> Fields { get; } = [];

    public bool Saved { get; private set; }

    public MarkerDefaultsViewModel(AppSettings settings)
    {
        _settings = settings;

        foreach (var (name, _) in MarkerDefaults.Order)
        {
            Fields.Add(new MarkerDefaultField { Name = name, Spec = MarkerDefaults.Get(name) });
        }
    }

    public void RestoreDefaults()
    {
        foreach (var f in Fields)
        {
            f.Spec = MarkerDefaults.Default(f.Name);
        }
    }

    public void Save()
    {
        var specs = Fields.ToDictionary(f => f.Name, f => f.Spec?.Trim() ?? "");

        MarkerDefaults.Apply(specs);
        _settings.MarkerDefaults = MarkerDefaults.Snapshot();
        SettingsService.Save(_settings);

        Saved = true;
    }
}
