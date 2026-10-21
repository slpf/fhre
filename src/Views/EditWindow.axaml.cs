using Avalonia.Controls;
using Avalonia.Interactivity;
using FH6RB;
using FH6RB.Assets;
using FH6RB.ViewModels;

namespace FH6RB.Views;

public partial class EditWindow : Window
{
    public EditWindow() => InitializeComponent();

    private EditWindowViewModel Vm => (EditWindowViewModel)DataContext!;

    private void OnSave(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(SaveAsync, "save track", this);

    private async Task SaveAsync()
    {
        var errors = Vm.ValidateMarkers();
        if (errors.Count > 0)
        {
            await MessageDialog.ShowAsync(this, Str.DlgMarkerValidationTitle, string.Join("\n", errors));
            return;
        }

        Vm.MarkSaved();
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnWaveform(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(WaveformAsync, "waveform", this);

    private async Task WaveformAsync()
    {
        if (!Vm.CanEditMarkers || Vm.PeaksLoader is null && Vm.WavPath is null) return;
        await Vm.EnsurePeaksAsync();
        if (Vm.WavPath is null || Vm.Peaks is null) return;
        var w = new WaveformWindow { DataContext = Vm };
        await w.ShowDialog(this);
    }
}
