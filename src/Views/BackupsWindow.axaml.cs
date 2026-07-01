using Avalonia.Controls;
using Avalonia.Interactivity;
using FH6RB;
using FH6RB.Assets;
using FH6RB.Services;
using FH6RB.ViewModels;

namespace FH6RB.Views;

public partial class BackupsWindow : Window
{
    private BackupsViewModel Vm => (BackupsViewModel) DataContext!;

    public BackupEntry? RestoreTarget { get; private set; }

    public BackupsWindow() => InitializeComponent();

    private void OnRestore(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(() => RestoreAsync(sender), "restore", this);

    private async Task RestoreAsync(object? sender)
    {
        if (sender is not Button { Tag: BackupRow row })
        {
            return;
        }

        var m = row.Entry.Manifest;
        var body = string.Format(Str.BackupRestoreBodyFmt, m.Name, m.GameLabel, m.StationNumber, m.StationName,
            row.CreatedLocal, m.TrackCount, m.CustomCount);

        if (!await MessageDialog.ShowAsync(this, Str.BackupRestoreTitle, body, Str.BtnRestore, Str.BtnCancel))
        {
            return;
        }

        RestoreTarget = row.Entry;
        Close();
    }

    private void OnDelete(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(() => DeleteAsync(sender), "delete backup", this);

    private async Task DeleteAsync(object? sender)
    {
        if (sender is not Button { Tag: BackupRow row })
        {
            return;
        }

        if (!await MessageDialog.ShowAsync(this, Str.BackupDeleteTitle,
                string.Format(Str.BackupDeleteBodyFmt, row.Entry.Manifest.Name), Str.BtnDelete, Str.BtnCancel))
        {
            return;
        }

        StationBackupService.Delete(row.Entry);
        Vm.Refresh();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
