using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FH6RB.Assets;
using FH6RB.Core;
using FH6RB.Services;
using FH6RB.ViewModels;
using FH6RB.Views;

namespace FH6RB;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools(); 
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        WorkDirs.Clean();
        AudioDecoder.ClearAll();
        TrackPack.CleanupImport();
        LoopFinder.ClearCache();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = SettingsService.Load();
            MarkerDefaults.Apply(settings.MarkerDefaults);
            var window = new MainWindow
            {
                DataContext = new MainWindowViewModel(settings)
            };

            WindowMemory.Restore(window, settings, "Main");
            window.Closing += (_, _) => WindowMemory.Save(window, settings, "Main");

            desktop.MainWindow = window;

            window.Opened += async (_, _) =>
            {
                await CheckRequiredToolsAsync(window);
                if (!GameScanner.IsValid(settings.GamePath))
                {
                    await window.ShowSettingsAsync(firstRun: true);
                }
            };

            desktop.Exit += (_, _) =>
            {
                (window.DataContext as MainWindowViewModel)?.Shutdown();
                AudioDecoder.ClearAll();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task CheckRequiredToolsAsync(MainWindow owner)
    {
        var missing = Tools.MissingRequired().ToList();
        if (missing.Count > 0)
        {
            Log.Line($"tools.missing root=\"{Tools.Root}\" tools={string.Join(", ", missing)}");
            await MessageDialog.ShowAsync(owner,
                Str.DlgMissingToolsTitle,
                string.Format(Str.DlgMissingToolsBody, string.Join(", ", missing)),
                Str.BtnOk);
        }
    }
}
