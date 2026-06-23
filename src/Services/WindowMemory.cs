using Avalonia.Controls;

namespace FH6RB.Services;

public static class WindowMemory
{
    public static void Restore(Window window, AppSettings? settings, string key)
    {
        if (settings is null || !settings.WindowSizes.TryGetValue(key, out var sz))
        {
            return;
        }

        if (sz.W > 0 && window.MinWidth <= window.MaxWidth)
        {
            window.Width = Math.Clamp(sz.W, window.MinWidth, window.MaxWidth);
        }

        if (sz.H > 0 && window.MinHeight <= window.MaxHeight)
        {
            window.Height = Math.Clamp(sz.H, window.MinHeight, window.MaxHeight);
        }
    }

    public static void Save(Window window, AppSettings? settings, string key)
    {
        if (settings is null)
        {
            return;
        }

        var w = window.Width;
        var h = window.Height;

        if (!double.IsFinite(w) || !double.IsFinite(h) || w <= 0 || h <= 0)
        {
            return;
        }

        settings.WindowSizes[key] = new WinSize { W = w, H = h };

        try
        {
            SettingsService.Save(settings);
        }
        catch
        {
            // ignored
        }
    }
}
