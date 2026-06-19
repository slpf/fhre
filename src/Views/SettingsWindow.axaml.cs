using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FH6RB.Assets;
using FH6RB.ViewModels;

namespace FH6RB.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    private SettingsWindowViewModel Vm => (SettingsWindowViewModel)DataContext!;

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Str.PickGameFolder,
            AllowMultiple = false,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            Vm.GamePath = path;
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        Vm.Commit();
        Close();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnRestore(object? sender, RoutedEventArgs e) => Vm.RestoreBackups();

    private async void OnResetSettings(object? sender, RoutedEventArgs e)
    {
        var ok = await MessageDialog.ShowAsync(this, Str.DlgResetSettingsTitle, Str.DlgResetSettingsBody,
            okText: Str.DlgResetSettingsOk, cancelText: Str.DlgResetSettingsCancel);

        if (ok)
        {
            Vm.ResetSettings();
        }
    }
}
