using Avalonia.Controls;
using Avalonia.Interactivity;
using FH6RB.ViewModels;

namespace FH6RB.Views;

public partial class EditWindow : Window
{
    public EditWindow() => InitializeComponent();

    private EditWindowViewModel Vm => (EditWindowViewModel)DataContext!;

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        Vm.MarkSaved();
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private async void OnWaveform(object? sender, RoutedEventArgs e)
    {
        await Vm.EnsurePeaksAsync();
        var w = new WaveformWindow { DataContext = Vm };
        await w.ShowDialog(this);
    }
}
