using Avalonia.Controls;
using Avalonia.Interactivity;
using FH6RB;
using FH6RB.Assets;
using FH6RB.Services;
using FH6RB.ViewModels;

namespace FH6RB.Views;

public partial class MarkerPresetLoadDialog : Window
{
    private MarkerPresetsViewModel Vm => (MarkerPresetsViewModel) DataContext!;

    public string? SelectedPresetName { get; private set; }

    public MarkerPresetLoadDialog()
    {
        InitializeComponent();
        DataContext = new MarkerPresetsViewModel();
    }

    public static async Task<string?> ShowAsync(Window owner)
    {
        var dlg = new MarkerPresetLoadDialog();
        await dlg.ShowDialog(owner);
        return dlg.SelectedPresetName;
    }

    private void OnLoad(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MarkerPresetRow row })
        {
            return;
        }

        SelectedPresetName = row.Preset.Name;
        Close();
    }

    private void OnDelete(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(() => DeleteAsync(sender), "delete preset", this);

    private async Task DeleteAsync(object? sender)
    {
        if (sender is not Button { Tag: MarkerPresetRow row })
        {
            return;
        }

        if (!await MessageDialog.ShowAsync(this, Str.PresetDeleteTitle,
                string.Format(Str.PresetDeleteBodyFmt, row.Preset.Name), Str.BtnDelete, Str.BtnCancel))
        {
            return;
        }

        MarkerPresetService.Delete(row.Preset.Name);
        Vm.Refresh();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
