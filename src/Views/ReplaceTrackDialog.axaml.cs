using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FH6RB.Views;

public partial class ReplaceTrackDialog : Window
{
    public ReplaceTrackDialog() => InitializeComponent();

    internal static Task<(bool ReplaceMetadata, bool ResetMarkers)?> ShowAsync(Window owner) =>
        new ReplaceTrackDialog().ShowDialog<(bool, bool)?>(owner);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnReplace(object? sender, RoutedEventArgs e) =>
        Close((ReplaceMetadata.IsChecked == true, ReplaceMarkers.IsChecked == true));
}
