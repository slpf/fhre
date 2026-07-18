using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FH6RB;
using FH6RB.Assets;
using FH6RB.Services;
using FH6RB.ViewModels;

namespace FH6RB.Views;

public partial class MarkerPresetLoadDialog : Window
{
    private MarkerPresetsViewModel Vm => (MarkerPresetsViewModel) DataContext!;

    public string? SelectedRelPath { get; private set; }

    public MarkerPresetLoadDialog()
    {
        InitializeComponent();
        DataContext = new MarkerPresetsViewModel();
    }

    public static async Task<string?> ShowAsync(Window owner)
    {
        var dlg = new MarkerPresetLoadDialog();
        await dlg.ShowDialog(owner);
        return dlg.SelectedRelPath;
    }

    private void OnBack(object? sender, RoutedEventArgs e) => Vm.GoBack();

    private void OnEntryPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: MarkerEntry entry } && entry.IsFolder)
        {
            Vm.Enter(entry.Name);
            e.Handled = true;
        }
    }

    private void OnLoadFile(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MarkerEntry f })
        {
            SelectedRelPath = f.RelPath;
            Close();
        }
    }

    private void OnDeleteFolder(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(() => DeleteFolderAsync(sender), "delete folder", this);

    private async Task DeleteFolderAsync(object? sender)
    {
        if (sender is not Button { Tag: MarkerEntry f })
        {
            return;
        }

        if (!await MessageDialog.ShowAsync(this, Str.MarkersDeleteFolderTitle, Str.MarkersDeleteFolderBody,
            okText: Str.BtnDelete, cancelText: Str.BtnCancel))
        {
            return;
        }

        MarkerPresetService.DeleteFolder(f.RelPath);
        Vm.Refresh();
    }

    private void OnDeleteFile(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(() => DeleteFileAsync(sender), "delete preset", this);

    private async Task DeleteFileAsync(object? sender)
    {
        if (sender is not Button { Tag: MarkerEntry f })
        {
            return;
        }

        if (!await MessageDialog.ShowAsync(this, Str.PresetDeleteTitle,
            string.Format(Str.PresetDeleteBodyFmt, f.Name), Str.BtnDelete, Str.BtnCancel))
        {
            return;
        }

        MarkerPresetService.DeleteByPath(f.RelPath);
        Vm.Refresh();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
