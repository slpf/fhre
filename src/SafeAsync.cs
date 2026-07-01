using Avalonia.Controls;
using FH6RB.Views;

namespace FH6RB;

public static class SafeAsync
{
    public static async void Run(Func<Task> action, string label, Window? owner = null)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            Program.LogCrash(ex, label);

            if (owner is not null)
            {
                try
                {
                    await MessageDialog.ShowAsync(owner, "Error",
                        $"Something went wrong ({label}):\n\n{ex.Message}");
                }
                catch
                {
                    // ignore
                }
            }
        }
    }
}
