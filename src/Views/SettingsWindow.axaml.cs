using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FH6RB;
using FH6RB.Assets;
using FH6RB.ViewModels;

namespace FH6RB.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as SettingsWindowViewModel)?.CancelValidation();
    }

    private SettingsWindowViewModel Vm => (SettingsWindowViewModel)DataContext!;
    internal Func<Task<bool>>? ConfirmGamePathChange { get; set; }
    private bool _saving;

    private void OnBrowse(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(BrowseAsync, "browse", this);

    private async Task BrowseAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Str.PickGameFolder,
            AllowMultiple = false,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            if (string.Equals(path, Vm.GamePath, StringComparison.Ordinal))
                await Vm.ValidateAsync();
            else
                Vm.GamePath = path;
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(SaveAsync, "save settings", this);

    private async Task SaveAsync()
    {
        if (_saving) return;
        _saving = true;
        try
        {
            if (await Vm.TryCommitAsync(ConfirmGamePathChange)) Close();
        }
        finally
        {
            _saving = false;
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnResetSettings(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(ResetSettingsAsync, "reset settings", this);

    private async Task ResetSettingsAsync()
    {
        var ok = await MessageDialog.ShowAsync(this, Str.DlgResetSettingsTitle, Str.DlgResetSettingsBody,
            okText: Str.DlgResetSettingsOk, cancelText: Str.DlgResetSettingsCancel);

        if (ok)
        {
            Vm.ResetSettings();
        }
    }
}
