using Avalonia.Controls;
using Avalonia.Interactivity;
using FH6RB.ViewModels;

namespace FH6RB.Views;

public partial class MarkerDefaultsWindow : Window
{
    private MarkerDefaultsViewModel Vm => (MarkerDefaultsViewModel) DataContext!;

    public MarkerDefaultsWindow()
    {
        InitializeComponent();
    }

    private void OnRestore(object? sender, RoutedEventArgs e) => Vm.RestoreDefaults();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        Vm.Save();
        Close();
    }
}
