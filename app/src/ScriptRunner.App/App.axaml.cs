using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ScriptRunner.App.Services;
using ScriptRunner.App.ViewModels;
using ScriptRunner.App.Views;

namespace ScriptRunner.App;

public partial class App : Application
{
    /// <summary>Set by the desktop or Android app before Avalonia starts.</summary>
    public static PlatformServices? Platform { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

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
            window.Closing += async (_, _) => await viewModel.ShutdownAsync();
            desktop.MainWindow = window;
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new MainView { DataContext = viewModel };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
