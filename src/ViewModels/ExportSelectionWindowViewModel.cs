using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FH6RB.Assets;

namespace FH6RB.ViewModels;

public sealed partial class ExportRow : ObservableObject
{
    public TrackItemViewModel Track { get; }

    [ObservableProperty] private bool _selected;

    public string Badge => Track.IsCustom ? Str.BadgeCustom
        : Track.Replaced || Track.IsReplacing ? Str.BadgeReplaced
        : Str.BadgeDefault;

    public ExportRow(TrackItemViewModel track, bool selected)
    {
        Track = track;
        _selected = selected;
    }
}

public sealed partial class ExportSelectionWindowViewModel : ObservableObject
{
    public ObservableCollection<ExportRow> Rows { get; } = [];

    public bool Saved { get; private set; }

    [ObservableProperty] private string _fileName;

    public bool HasAny => Rows.Count > 0 && Rows.Any(r => r.Selected);

    public int SelectedCount => Rows.Count(r => r.Selected);

    public bool AllSelected => Rows.Count > 0 && Rows.All(r => r.Selected);
    public bool NoneSelected => Rows.All(r => !r.Selected);

    public bool? SelectAllState => AllSelected ? true : NoneSelected ? false : null;

    public ExportSelectionWindowViewModel(IReadOnlyList<TrackItemViewModel> tracks, string defaultFileName)
    {
        _fileName = defaultFileName;

        foreach (var t in tracks)
        {
            var pre = t.IsCustom || t.Replaced || t.IsReplacing;
            var row = new ExportRow(t, pre);
            row.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(HasAny));
                OnPropertyChanged(nameof(SelectedCount));
                OnPropertyChanged(nameof(AllSelected));
                OnPropertyChanged(nameof(NoneSelected));
                OnPropertyChanged(nameof(SelectAllState));
            };
            Rows.Add(row);
        }
    }

    public IReadOnlyList<TrackItemViewModel> Selected
        => Rows.Where(r => r.Selected).Select(r => r.Track).ToList();

    public void SelectAll(bool value)
    {
        foreach (var r in Rows)
        {
            r.Selected = value;
        }
    }

    public void MarkSaved() => Saved = true;
}
