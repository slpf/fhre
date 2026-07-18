using Avalonia.Controls;
using Avalonia.Interactivity;
using FH6RB.ViewModels;

namespace FH6RB.Views;

public partial class ExportSelectionWindow : Window
{
    public ExportSelectionWindow() => InitializeComponent();

    private ExportSelectionWindowViewModel Vm => (ExportSelectionWindowViewModel) DataContext!;

    private void OnExport(object? sender, RoutedEventArgs e)
    {
        Vm.MarkSaved();
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnToggleAll(object? sender, RoutedEventArgs e) => Vm.SelectAll(!Vm.AllSelected);
}
