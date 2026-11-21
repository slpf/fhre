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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = SettingsService.Load();
            MarkerDefaults.Apply(settings.MarkerDefaults);
            var vm = new MainWindowViewModel(settings);
            var window = new MainWindow { DataContext = vm };

            WindowMemory.Restore(window, settings, "Main");
            window.Closing += (_, _) => WindowMemory.Save(window, settings, "Main");

            desktop.MainWindow = window;

            window.Opened += (_, _) => SafeAsync.Run(async () =>
            {
                await Task.WhenAll(vm.ReloadFromGameAsync(), Task.Run(() =>
                {
                    AppPaths.CleanStaleSessions("preview");
                    AppPaths.CleanStaleSessions("restore");
                    AppPaths.CleanStaleSessions("import");
                }));
                if (!window.IsVisible) return;

                await CheckRequiredToolsAsync(window);
                if (!window.IsVisible) return;
                if (settings.ReadFailure is { } readError)
                {
                    await MessageDialog.ShowAsync(window, Str.DlgSettingsReadTitle,
                        string.Format(Str.DlgSettingsReadBodyFmt, readError.Message));
                    return;
                }
                if (vm.GameRecoveryError is { } recoveryError)
                {
                    await MessageDialog.ShowAsync(window, Str.DlgGameRecoveryTitle, recoveryError);
                }
                else if (!vm.HasValidGamePath)
                {
                    await vm.RestoreAvailability;
                    if (window.IsVisible && !vm.HasValidGamePath && !vm.CanRestoreAll)
                        await window.ShowSettingsAsync(firstRun: true);
                }
                else await window.CheckRunningGameAsync();
            }, "startup", window);

            desktop.Exit += (_, _) =>
            {
                vm.Shutdown();
                AudioDecoder.ClearAll();
                TrackPack.CleanupImport();
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
