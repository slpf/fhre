using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FH6RB.Assets;
using FH6RB.Services;

namespace FH6RB.ViewModels;

public sealed class ImportPreviewRow
{
    public string Title { get; }
    public string? Artist { get; }

    public bool HasArtist => !string.IsNullOrWhiteSpace(Artist);

    public ImportPreviewRow(PackTrack t)
    {
        Title = string.IsNullOrWhiteSpace(t.DisplayName)
            ? (string.IsNullOrWhiteSpace(t.File) ? t.Id : Path.GetFileNameWithoutExtension(t.File))
            : t.DisplayName;
        Artist = t.Artist;
    }
}

public sealed partial class ImportRow : ObservableObject
{
    public TrackItemViewModel DefaultTrack { get; }

    [ObservableProperty] private bool _selected;

    public ImportRow(TrackItemViewModel d, bool selected)
    {
        DefaultTrack = d;
        _selected = selected;
    }
}

public sealed partial class ImportWindowViewModel : ObservableObject
{
    private readonly IReadOnlyList<PackTrack> _imported;

    public IReadOnlyList<ImportPreviewRow> ImportedTracks { get; }
    public ObservableCollection<ImportRow> Rows { get; } = [];
    public bool Saved { get; private set; }
    public bool CanReplace => Rows.Count > 0;
    public bool HasRows => Rows.Count > 0;
    public int SelectedCount => Rows.Count(r => r.Selected);
    public int ReplacementLimit => Math.Min(Rows.Count, _imported.Count);
    public bool CanImport => SelectedCount <= ReplacementLimit;
    public string SelectionSummary => string.Format(Str.ImportSelectionCountFmt, SelectedCount, ReplacementLimit);
    public bool AllSelected => ReplacementLimit > 0 && SelectedCount == ReplacementLimit;
    public bool NoneSelected => Rows.All(r => !r.Selected);
    public bool? SelectAllState => AllSelected ? true : NoneSelected ? false : null;

    [ObservableProperty] private int _step = 1;

    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool ShowNext => Step == 1 && CanReplace;
    public bool ShowBack => Step == 2;
    public bool ShowImport => Step == 2 || !CanReplace;

    public IReadOnlyDictionary<string, string?> Result { get; private set; }
        = new Dictionary<string, string?>();

    public ImportWindowViewModel(IReadOnlyList<PackTrack> imported, IReadOnlyList<TrackItemViewModel> defaults)
    {
        _imported = imported;
        ImportedTracks = imported.Select(t => new ImportPreviewRow(t)).ToList();

        foreach (var d in defaults)
        {
            var row = new ImportRow(d, selected: false);
            row.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(SelectedCount));
                OnPropertyChanged(nameof(SelectionSummary));
                OnPropertyChanged(nameof(CanImport));
                OnPropertyChanged(nameof(AllSelected));
                OnPropertyChanged(nameof(NoneSelected));
                OnPropertyChanged(nameof(SelectAllState));
            };
            Rows.Add(row);
        }
    }

    partial void OnStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsStep1));
        OnPropertyChanged(nameof(IsStep2));
        OnPropertyChanged(nameof(ShowNext));
        OnPropertyChanged(nameof(ShowBack));
        OnPropertyChanged(nameof(ShowImport));
    }

    public void Next()
    {
        if (CanReplace) Step = 2;
    }

    public void Back() => Step = 1;

    public void SelectAll(bool value)
    {
        var count = value ? ReplacementLimit : 0;
        for (var i = 0; i < Rows.Count; i++)
        {
            Rows[i].Selected = i < count;
        }
    }

    public bool MarkSaved()
    {
        if (!CanImport) return false;
        var selectedDefaults = Rows.Where(r => r.Selected).Select(r => r.DefaultTrack).ToList();
        var result = new Dictionary<string, string?>();

        for (var i = 0; i < _imported.Count; i++)
        {
            result[_imported[i].Id] = i < selectedDefaults.Count ? selectedDefaults[i].SoundName : null;
        }

        Result = result;
        Saved = true;
        return true;
    }
}
