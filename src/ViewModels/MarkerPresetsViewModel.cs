using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FH6RB.Assets;
using FH6RB.Services;

namespace FH6RB.ViewModels;

public sealed class MarkerPresetRow
{
    public required MarkerPreset Preset { get; init; }

    public string Name => Preset.Name;

    public string Info => string.Format(Str.PresetInfoFmt, Preset.Markers.Count);

    public string Meta => Preset.Modified == default
        ? string.Empty
        : Preset.Modified.ToString("yyyy-MM-dd HH:mm");
}

public sealed partial class MarkerPresetsViewModel : ObservableObject
{
    public ObservableCollection<MarkerPresetRow> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    public MarkerPresetsViewModel() => Refresh();

    public void Refresh()
    {
        Items.Clear();

        foreach (var p in MarkerPresetService.List())
        {
            Items.Add(new MarkerPresetRow { Preset = p });
        }

        OnPropertyChanged(nameof(HasItems));
    }
}
