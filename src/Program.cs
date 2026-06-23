using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Avalonia;

[assembly: AssemblyProduct("Forza Horizon Radio Extender")]
[assembly: AssemblyTitle("Forza Horizon Radio Extender")]
[assembly: AssemblyDescription("Forza Horizon Radio Extender")]
[assembly: AssemblyCopyright("Chisou")]
[assembly: AssemblyVersion("0.3.6")]
[assembly: AssemblyFileVersion("0.3.6")]
[assembly: AssemblyInformationalVersion("0.3.6")]

namespace FH6RB;

internal static class Program
{
    [ModuleInitializer]
    internal static void InitAssemblyResolver()
    {
        var libs = Path.Combine(AppContext.BaseDirectory, "libs");
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var dll = Path.Combine(libs, name.Name + ".dll");
            return File.Exists(dll) ? ctx.LoadFromAssemblyPath(dll) : null;
        };
    }

    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception, "AppDomain");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash(e.Exception, "UnobservedTask");
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            LogCrash(ex, "Startup");
            throw;
        }
    }

    private static void LogCrash(Exception? ex, string source)
    {
        if (ex is null)
        {
            return;
        }

        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
