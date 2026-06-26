using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
}
