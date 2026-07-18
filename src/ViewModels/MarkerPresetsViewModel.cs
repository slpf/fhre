using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FH6RB.Services;

namespace FH6RB.ViewModels;

public sealed class MarkerEntry
{
    public required string Name { get; init; }
    public required bool IsFolder { get; init; }
    public required string RelPath { get; init; }
    public MarkerPreset? Preset { get; init; }

    public bool IsFile => !IsFolder;

    public string Meta
    {
        get
        {
            if (IsFolder) return "";
            if (Preset is { Modified: var m } && m != default) return m.ToString("yyyy-MM-dd HH:mm");
            return "";
        }
    }
}

public sealed partial class MarkerPresetsViewModel : ObservableObject
{
    private string _relDir = "";

    public ObservableCollection<MarkerEntry> Entries { get; } = [];

    public bool HasItems => Entries.Count > 0;

    public bool CanGoBack => !string.IsNullOrEmpty(_relDir);

    public string CurrentFolder
    {
        get
        {
            if (string.IsNullOrEmpty(_relDir)) return "Markers";
            var parts = _relDir.Split('/');
            return parts[^1];
        }
    }

    public string RelDir => _relDir;

    [ObservableProperty] private string _search = "";

    partial void OnSearchChanged(string value) => Refresh();

    public MarkerPresetsViewModel()
    {
        Refresh();
    }

    public void Refresh()
    {
        var q = (Search ?? "").Trim();

        Entries.Clear();

        foreach (var d in MarkerPresetService.ListSubdirs(_relDir))
        {
            if (q.Length > 0 && !d.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            Entries.Add(new MarkerEntry
            {
                Name = d,
                IsFolder = true,
                RelPath = CombineRel(_relDir, d),
            });
        }

        foreach (var p in MarkerPresetService.ListIn(_relDir))
        {
            if (q.Length > 0 && !p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            Entries.Add(new MarkerEntry
            {
                Name = p.Name,
                IsFolder = false,
                RelPath = CombineRel(_relDir, p.Name + ".json"),
                Preset = p,
            });
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CurrentFolder));
    }

    public void Enter(string name)
    {
        _relDir = CombineRel(_relDir, name);
        Refresh();
    }

    public void GoBack()
    {
        if (!CanGoBack) return;
        _relDir = Path.GetDirectoryName(_relDir) ?? "";
        Refresh();
    }

    private static string CombineRel(string parent, string child)
        => string.IsNullOrEmpty(parent) ? child : parent + "/" + child;
}
