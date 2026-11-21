using Avalonia.Controls;
using Avalonia.Interactivity;
using FH6RB.ViewModels;

namespace FH6RB.Views;

public partial class LoopSearchSettingsWindow : Window
{
    private LoopSearchSettingsViewModel Vm => (LoopSearchSettingsViewModel) DataContext!;

    public LoopSearchSettingsWindow()
    {
        InitializeComponent();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        SafeAsync.Run(() =>
        {
            Vm.Save();
            Close();
            return Task.CompletedTask;
        }, "save loop search settings", this);
    }
}
