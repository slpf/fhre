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
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        WorkDirs.Clean();
        AudioDecoder.ClearAll();
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

            window.Opened += async (_, _) => await CheckRequiredToolsAsync(window);

            desktop.Exit += (_, _) =>
            {
                (window.DataContext as MainWindowViewModel)?.Shutdown();
                AudioDecoder.ClearAll();
            };

            if (!GameScanner.IsValid(settings.GamePath))
            {
                window.Opened += async (_, _) => await window.ShowSettingsAsync(firstRun: true);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task CheckRequiredToolsAsync(MainWindow owner)
    {
        if (!Tools.HasFfmpeg || !Tools.HasFsbankcl)
        {
            var missing = new List<string>();
            if (!Tools.HasFfmpeg) missing.Add("ffmpeg");
            if (!Tools.HasFsbankcl) missing.Add("fsbank");
            await MessageDialog.ShowAsync(owner,
                Str.DlgMissingToolsTitle,
                string.Format(Str.DlgMissingToolsBody, string.Join(", ", missing)),
                Str.BtnOk);
        }
        else if (!Tools.HasVgmstream)
        {
            await MessageDialog.ShowAsync(owner,
                Str.DlgMissingVgmstreamTitle,
                Str.DlgMissingVgmstreamBody,
                Str.BtnOk);
        }
    }
}
