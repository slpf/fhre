using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FH6RB.Views;

public partial class InputDialog : Window
{
    public InputDialog() => InitializeComponent();

    public static async Task<string?> ShowAsync(Window owner, string title, string watermark,
        string? defaultValue = null)
    {
        var d = new InputDialog { Title = title };
        d.Input.Watermark = watermark;
        if (!string.IsNullOrEmpty(defaultValue)) d.Input.Text = defaultValue;
        return await d.ShowDialog<string?>(owner);
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var t = Input.Text?.Trim();
        Close(string.IsNullOrEmpty(t) ? null : t);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close((string?) null);
}
