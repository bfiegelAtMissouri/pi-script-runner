using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ScriptRunner.App.Services;
using ScriptRunner.App.ViewModels;
using ScriptRunner.App.Views;

namespace ScriptRunner.App;

public partial class App : Application
{
    /// <summary>Set by the desktop or Android app before Avalonia starts.</summary>
    public static PlatformServices? Platform { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Starts the diagnostic log and records every unexpected error with its details. Errors on the user interface
    /// thread are reported to the user and the app keeps running; an error on another thread that would end the app
    /// is recorded before it closes. Called by the desktop and Android apps before Avalonia starts.
    /// </summary>
    public static void InstallErrorHandling(string settingsFolder)
    {
        DiagnosticLog.Start(settingsFolder);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            DiagnosticLog.RecordError("a background thread",
                e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString()), e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // A failed task nobody waited for does not stop the app; it is recorded but not shown to the user.
            DiagnosticLog.RecordError("a background task", e.Exception, tellUser: false);
            e.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            DiagnosticLog.RecordError("the user interface", e.Exception);
            e.Handled = true;
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        PlatformServices platform = Platform ?? throw new InvalidOperationException("App.Platform was not set.");
        var viewModel = new MainViewModel(platform);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new Window
            {
                Title = MainViewModel.Title,
                Width = 1000,
                Height = 760,
                MinWidth = 420,
                MinHeight = 480,
                Content = new MainView { DataContext = viewModel },
            };
            window.Closing += async (_, _) =>
            {
                DiagnosticLog.Write("Window closing");
                await viewModel.ShutdownAsync();
            };
            desktop.MainWindow = window;
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new MainView { DataContext = viewModel };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
