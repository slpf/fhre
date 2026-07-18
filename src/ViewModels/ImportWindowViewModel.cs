using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
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
    public bool HasAny => Rows.Count > 0 && Rows.Any(r => r.Selected);
    public int SelectedCount => Rows.Count(r => r.Selected);
    public bool AllSelected => Rows.Count > 0 && Rows.All(r => r.Selected);
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
                OnPropertyChanged(nameof(HasAny));
                OnPropertyChanged(nameof(SelectedCount));
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
        foreach (var r in Rows)
        {
            r.Selected = value;
        }
    }

    public void MarkSaved()
    {
        var selectedDefaults = Rows.Where(r => r.Selected).Select(r => r.DefaultTrack).ToList();
        var result = new Dictionary<string, string?>();

        for (var i = 0; i < _imported.Count; i++)
        {
            result[_imported[i].Id] = i < selectedDefaults.Count ? selectedDefaults[i].SoundName : null;
        }

        Result = result;
        Saved = true;
    }
}
