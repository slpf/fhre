using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Avalonia;

[assembly: AssemblyProduct("Forza Horizon Radio Extender")]
[assembly: AssemblyTitle("Forza Horizon Radio Extender")]
[assembly: AssemblyDescription("Forza Horizon Radio Extender")]
[assembly: AssemblyCopyright("Chisou")]
[assembly: AssemblyVersion("0.3.4")]
[assembly: AssemblyFileVersion("0.3.4")]
[assembly: AssemblyInformationalVersion("0.3.4")]

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
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
