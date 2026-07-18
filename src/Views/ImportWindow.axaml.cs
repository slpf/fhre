using Avalonia.Controls;
using Avalonia.Interactivity;
using FH6RB.ViewModels;

namespace FH6RB.Views;

public partial class ImportWindow : Window
{
    public ImportWindow() => InitializeComponent();

    private ImportWindowViewModel Vm => (ImportWindowViewModel) DataContext!;

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        Vm.MarkSaved();
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnNext(object? sender, RoutedEventArgs e) => Vm.Next();

    private void OnBack(object? sender, RoutedEventArgs e) => Vm.Back();

    private void OnToggleAll(object? sender, RoutedEventArgs e) => Vm.SelectAll(!Vm.AllSelected);
}
