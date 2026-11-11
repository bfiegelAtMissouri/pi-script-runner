using Avalonia;
using ScriptRunner.App;
using ScriptRunner.Desktop.Platform;

namespace ScriptRunner.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        App.App.Platform = DesktopPlatform.Create();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Also used by the Avalonia designer.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App.App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
