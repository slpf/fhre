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
#if !DEBUG
        RunTestsButton.IsVisible = false;
#endif
    }

    public event EventHandler? RunTestsRequested;

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        Vm.Save();
        Close();
    }

    private void OnRunTests(object? sender, RoutedEventArgs e)
    {
        RunTestsRequested?.Invoke(this, EventArgs.Empty);
        Close();
    }
}