using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using ScriptRunner.App.Services;
using ScriptRunner.App.ViewModels;
using ScriptRunner.App.Views;
using Xunit;

namespace ScriptRunner.Tests;

/// <summary>
/// The real screen and view model, run headless against the SSH test server (see <see cref="ConnectionTests"/>):
/// files, Run, input, say(), Stop, and the output shown. Saves a picture of the window as ui-session.png in the
/// test output folder.
/// </summary>
public sealed class UiTests
{
    [Fact]
    public async Task SshSessionThroughTheScreen()
    {
        string? host = Environment.GetEnvironmentVariable("SCRIPT_RUNNER_TEST_SSH_HOST");
        string? password = Environment.GetEnvironmentVariable("SCRIPT_RUNNER_TEST_SSH_PASSWORD");
        Assert.SkipWhen(string.IsNullOrEmpty(host) || string.IsNullOrEmpty(password),
            "SCRIPT_RUNNER_TEST_SSH_* environment variables are not set");

        string settingsFolder = Directory.CreateTempSubdirectory("script-runner-ui").FullName;
        var speech = new RecordingSpeech();
        ScriptRunner.App.App.Platform = new PlatformServices(speech, new SilentAudio(), new NoSecretStore(), settingsFolder);

        Dispatcher ui = await HeadlessApp.StartAsync();
        CancellationToken cancel = TestContext.Current.CancellationToken;
        MainViewModel? model = null;
        Window? window = null;

        // Each step runs on the app's UI thread, whose message loop keeps running between steps (as in the real
        // app), so work the connection posts to it is processed while the test waits on its own thread.
        await ui.InvokeAsync(() =>
        {
            model = new MainViewModel(ScriptRunner.App.App.Platform);
            window = new Window { Width = 1000, Height = 700, Content = new MainView { DataContext = model } };
            window.Show();
            model.SelectedModeIndex = 0;                                       // SSH
            model.Host = host!;
            model.Port = Environment.GetEnvironmentVariable("SCRIPT_RUNNER_TEST_SSH_PORT") ?? "22";
            model.User = Environment.GetEnvironmentVariable("SCRIPT_RUNNER_TEST_SSH_USER") ?? "pi";
            model.Secret = password!;
            model.AddFiles(new[]
            {
                ("hello.py", Encoding.UTF8.GetBytes(
                    "import time\nfrom runner_io import say\nname = input('Name? ')\nsay('Hello ' + name)\n" +
                    "try:\n    while True:\n        print('tick')\n        time.sleep(0.3)\nfinally:\n    print('cleanup done')\n")),
            });
            Assert.True(model.CanRun);
            Assert.Equal("hello.py", model.SelectedScript);
            model.RunCommand.Execute(null);
        }, DispatcherPriority.Normal, cancel);
        MainViewModel viewModel = model!;

        await WaitUntil(ui, viewModel, () => viewModel.OutputText.Contains("Name? ", StringComparison.Ordinal));
        await ui.InvokeAsync(() =>
        {
            viewModel.InputText = "Ada";
            viewModel.SendCommand.Execute(null);
        }, DispatcherPriority.Normal, cancel);
        await WaitUntil(ui, viewModel, () => viewModel.OutputText.Contains("tick", StringComparison.Ordinal));
        await ui.InvokeAsync(() =>
        {
            Assert.Contains("Name? Ada", viewModel.OutputText, StringComparison.Ordinal);
            Assert.Contains("🔊 Hello Ada", viewModel.OutputText, StringComparison.Ordinal);
            Assert.DoesNotContain("@@runner", viewModel.OutputText, StringComparison.Ordinal);
            Assert.Equal(new[] { "Hello Ada" }, speech.Spoken);
            Assert.True(viewModel.IsRunning);
            viewModel.StopCommand.Execute(null);
        }, DispatcherPriority.Normal, cancel);
        await WaitUntil(ui, viewModel, () => !viewModel.IsRunning);
        await ui.InvokeAsync(() =>
        {
            Assert.Contains("cleanup done", viewModel.OutputText, StringComparison.Ordinal);
            Assert.Contains("[Stopped", viewModel.OutputText, StringComparison.Ordinal);
            Assert.Empty(viewModel.ErrorText);
            viewModel.PiCommandText = "echo hello-from-the-pi";
            viewModel.RunPiCommandCommand.Execute(null);
        }, DispatcherPriority.Normal, cancel);
        await WaitUntil(ui, viewModel, () => viewModel.OutputText.Contains("hello-from-the-pi\n[exit code 0]", StringComparison.Ordinal));

        // Disconnect while a script waits for input, then run again on a new connection.
        await ui.InvokeAsync(() =>
        {
            Assert.True(viewModel.IsConnected);
            viewModel.ClearCommand.Execute(null);
            viewModel.RunCommand.Execute(null);
        }, DispatcherPriority.Normal, cancel);
        await WaitUntil(ui, viewModel, () => viewModel.OutputText.Contains("Name? ", StringComparison.Ordinal));
        await ui.InvokeAsync(() => viewModel.DisconnectCommand.Execute(null), DispatcherPriority.Normal, cancel);
        await WaitUntil(ui, viewModel, () => viewModel.OutputText.Contains("[Disconnected from the Pi.", StringComparison.Ordinal));
        await ui.InvokeAsync(() =>
        {
            Assert.False(viewModel.IsRunning);
            Assert.False(viewModel.IsConnected);
            Assert.True(viewModel.CanRun);
            viewModel.ClearCommand.Execute(null);
            viewModel.RunCommand.Execute(null);
        }, DispatcherPriority.Normal, cancel);
        await WaitUntil(ui, viewModel, () => viewModel.OutputText.Contains("Name? ", StringComparison.Ordinal));
        await ui.InvokeAsync(() => viewModel.DisconnectCommand.Execute(null), DispatcherPriority.Normal, cancel);
        await WaitUntil(ui, viewModel, () => !viewModel.IsConnected);
        await ui.InvokeAsync(() =>
        {
            ui.RunJobs();
            using var frame = window!.CaptureRenderedFrame();
            frame?.Save(Path.Combine(AppContext.BaseDirectory, "ui-session.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            window!.Close();
        }, DispatcherPriority.Normal, cancel);
        await viewModel.ShutdownAsync();

        string saved = File.ReadAllText(Path.Combine(settingsFolder, "settings.json"));
        Assert.DoesNotContain(password!, saved, StringComparison.Ordinal);          // the password is never saved
        Directory.Delete(settingsFolder, true);
    }

    private static async Task WaitUntil(Dispatcher ui, MainViewModel model, Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            (bool done, string state) = await ui.InvokeAsync(
                () => (condition(), $"status: {model.StatusText}\nerror: {model.ErrorText}\noutput:\n{model.OutputText}"));
            if (done)
            {
                return;
            }
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "Timed out waiting for the screen to update.\n" + state);
            await Task.Delay(50);
        }
    }

    private sealed class RecordingSpeech : ISpeechService
    {
        public List<string> Spoken { get; } = new();

        public void Speak(string text) => Spoken.Add(text);

        public void StopSpeaking() { }
    }

    private sealed class SilentAudio : IAudioService
    {
        public Task PlayAsync(string fileName, byte[] data) => Task.CompletedTask;
    }
}

/// <summary>
/// The shared app on Avalonia's headless platform (rendering with Skia so frames can be captured), running its
/// message loop on its own thread for the rest of the test run, like a real app's UI thread.
/// </summary>
public static class HeadlessApp
{
    private static readonly Lazy<Task<Dispatcher>> Started = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<ScriptRunner.App.App>()
                    .UseSkia()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                    .SetupWithoutStarting();
                ready.SetResult(Dispatcher.UIThread);
                Dispatcher.UIThread.MainLoop(CancellationToken.None);
            }
            catch (Exception error)
            {
                ready.TrySetException(error);
            }
        })
        { IsBackground = true, Name = "Headless UI" };
        thread.Start();
        return ready.Task;
    });

    public static Task<Dispatcher> StartAsync() => Started.Value;
}
