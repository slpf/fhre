using Avalonia.Controls;

namespace FH6RB.Services;

public static class WindowMemory
{
    public static void Restore(Window window, AppSettings? settings, string key)
    {
        if (settings?.WindowSizes is not { } sizes || !sizes.TryGetValue(key, out var sz) || sz is null)
        {
            return;
        }

        if (double.IsFinite(sz.W) && sz.W > 0 && window.MinWidth <= window.MaxWidth)
        {
            window.Width = Math.Clamp(sz.W, window.MinWidth, window.MaxWidth);
        }

        if (double.IsFinite(sz.H) && sz.H > 0 && window.MinHeight <= window.MaxHeight)
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
        if (!double.IsFinite(w) || w <= 0)
        {
            return;
        }

        var h = window.Height;
        h = double.IsFinite(h) && h > 0 ? h : 0;
        var previousSizes = settings.WindowSizes;
        if (previousSizes is not null && previousSizes.TryGetValue(key, out var previous) && previous is not null
            && previous.W == w && previous.H == h) return;

        try
        {
            var sizes = previousSizes is null ? new Dictionary<string, WinSize>() : new(previousSizes);
            sizes[key] = new WinSize { W = w, H = h };
            SettingsService.Update(settings, draft => draft.WindowSizes = sizes);
        }
        catch (Exception ex)
        {
            Log.Line($"settings save failed (window memory '{key}'): {ex.Message}");
        }
    }
}
