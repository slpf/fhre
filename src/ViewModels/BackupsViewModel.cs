using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FH6RB.Assets;
using FH6RB.Services;

namespace FH6RB.ViewModels;

public sealed class BackupRow
{
    public required BackupEntry Entry { get; init; }
    public bool CanRestore { get; init; }

    public string Name => Entry.Manifest.Name;

    public string CreatedLocal =>
        DateTime.TryParse(Entry.Manifest.CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
            ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : Entry.Manifest.CreatedUtc;

    public string Info => string.Format(Str.BackupInfoFmt,
        Entry.Manifest.GameLabel, Entry.Manifest.StationNumber, Entry.Manifest.StationName);

    public string Meta => string.Format(Str.BackupMetaFmt,
        Entry.Manifest.TrackCount, Entry.Manifest.CustomCount, CreatedLocal);
}

public sealed partial class BackupsViewModel : ObservableObject
{
    private readonly StationInfo? _station;

    public string GamePath { get; }

    public ObservableCollection<BackupRow> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    public BackupsViewModel(string gamePath, StationInfo? station)
    {
        GamePath = gamePath;
        _station = station;
        Refresh();
    }

    public void Refresh()
    {
        Items.Clear();

        foreach (var e in StationBackupService.List())
        {
            var can = _station is not null && StationBackupService.Matches(e.Manifest, GamePath, _station);
            Items.Add(new BackupRow { Entry = e, CanRestore = can });
        }

        OnPropertyChanged(nameof(HasItems));
    }
}
