using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FH6RB.Views;

public partial class MessageDialog : Window
{
    public MessageDialog() => InitializeComponent();

    public static async Task<bool> ShowAsync(Window owner, string title, string message,
        string okText = "OK", string? cancelText = null)
    {
        var d = new MessageDialog { Title = title };
        d.MessageText.Text = message;
        d.OkButton.Content = okText;

        if (cancelText is null)
        {
            d.CancelButton.IsVisible = false;
        }
        else
        {
            d.CancelButton.Content = cancelText;
        }

        return await d.ShowDialog<bool>(owner);
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
