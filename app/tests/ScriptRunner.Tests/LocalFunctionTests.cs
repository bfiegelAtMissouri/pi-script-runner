using System.Text;
using Avalonia.Threading;
using ScriptRunner.App.Services;
using ScriptRunner.App.ViewModels;
using ScriptRunner.Core;
using Xunit;

namespace ScriptRunner.Tests;

/// <summary>
/// remote_execute(): a script on the "Pi" (the test SSH server or a local Pi server) calls functions in a local file,
/// run by the app's view model with this computer's Python.
/// </summary>
public sealed class LocalFunctionTests
{
    private const string RobotScript = """
        from runner_io import remote_execute, shared_runner_file, RemoteExecutionError
        print("sum", remote_execute("add", 2, 3))
        open("data.csv", "w").write("a,b\n1,2\n")
        print("described", remote_execute("describe", shared_runner_file("data.csv"), {"label": "x"}))
        print("big", len(remote_execute("big", 5000)))
        try:
            remote_execute("fails")
        except RemoteExecutionError as error:
            print("caught", str(error).splitlines()[-1])
        try:
            remote_execute("system", "echo hacked")
        except RemoteExecutionError as error:
            print("refused", "No local function named system" in str(error))
        print(remote_execute("slow"))
        print("got", input())
        print("DONE")
        """;

    private const string LocalFile = """
        import os
        import time
        from os import system
        print("vision loaded")

        def add(a, b):
            return a + b

        def describe(path, options):
            return [os.path.basename(path), open(path).read(), options["label"], os.path.dirname(path) != os.getcwd()]

        def big(n):
            return "x" * n

        def fails():
            return 1 / 0

        def slow():
            time.sleep(1.5)
            return "slow done"
        """;

    [Fact]
    public async Task SshRemoteExecute()
    {
        string? host = Environment.GetEnvironmentVariable("SCRIPT_RUNNER_TEST_SSH_HOST");
        string? password = Environment.GetEnvironmentVariable("SCRIPT_RUNNER_TEST_SSH_PASSWORD");
        Assert.SkipWhen(string.IsNullOrEmpty(host) || string.IsNullOrEmpty(password),
            "SCRIPT_RUNNER_TEST_SSH_* environment variables are not set");
        Assert.SkipWhen(ConnectionTests.FindPython() is null, "python3 is not installed");
        await RunAsync(0, host!, Environment.GetEnvironmentVariable("SCRIPT_RUNNER_TEST_SSH_PORT") ?? "22",
            Environment.GetEnvironmentVariable("SCRIPT_RUNNER_TEST_SSH_USER") ?? "pi", password!);
    }

    [Fact]
    public async Task HttpRemoteExecute()
    {
        string? python = ConnectionTests.FindPython();
        Assert.SkipWhen(python is null, "python3 is not installed");
        using var server = ConnectionTests.PiServer.Start(python!, ConnectionTests.TestPin);
        await RunAsync(1, "127.0.0.1", server.Port.ToString(), "", ConnectionTests.TestPin);
    }

    private static async Task RunAsync(int modeIndex, string host, string port, string user, string secret)
    {
        string settingsFolder = Directory.CreateTempSubdirectory("script-runner-local-test").FullName;
        ScriptRunner.App.App.Platform = new PlatformServices(new UiTests.RecordingSpeech(), new UiTests.SilentAudio(),
            new NoSecretStore(), settingsFolder);
        Dispatcher ui = await HeadlessApp.StartAsync();
        CancellationToken cancel = TestContext.Current.CancellationToken;
        MainViewModel? model = null;
        await ui.InvokeAsync(() =>
        {
            model = new MainViewModel(ScriptRunner.App.App.Platform);
            model.SelectedModeIndex = modeIndex;
            model.Host = host;
            model.Port = port;
            model.User = user;
            model.Secret = secret;
            model.AddFiles(new[]
            {
                ("vision.py", Encoding.UTF8.GetBytes(LocalFile)),
                ("robot.py", Encoding.UTF8.GetBytes(RobotScript)),
            });
            model.Files.Single(f => "vision.py" == f.Name).IsLocal = true;
            Assert.Equal(new[] { "robot.py" }, model.Scripts);              // a local file is not run on the Pi
            model.RunCommand.Execute(null);
        }, DispatcherPriority.Normal, cancel);
        MainViewModel viewModel = model!;

        await UiTests.WaitUntil(ui, viewModel, () => viewModel.OutputText.Contains("💻 slow", StringComparison.Ordinal));
        await ui.InvokeAsync(() =>
        {
            viewModel.InputText = "typed";              // typed while the laptop is busy: held until it answers
            viewModel.SendCommand.Execute(null);
        }, DispatcherPriority.Normal, cancel);
        await UiTests.WaitUntil(ui, viewModel, () => !viewModel.IsRunning && viewModel.OutputText.Contains("[Finished", StringComparison.Ordinal));

        string output = viewModel.OutputText;
        Assert.Contains("[local] vision loaded", output, StringComparison.Ordinal);
        Assert.Contains("[Local functions: add, big, describe, fails, slow]", output, StringComparison.Ordinal);
        Assert.Contains("sum 5", output, StringComparison.Ordinal);
        Assert.Contains("described ['data.csv', 'a,b\\n1,2\\n', 'x', True]", output, StringComparison.Ordinal);
        Assert.Contains("big 5000", output, StringComparison.Ordinal);       // in SSH mode, longer than a terminal line
        Assert.Contains("caught ZeroDivisionError: division by zero", output, StringComparison.Ordinal);
        Assert.Contains("refused True", output, StringComparison.Ordinal);
        Assert.Contains("slow done", output, StringComparison.Ordinal);
        Assert.Contains("got typed", output, StringComparison.Ordinal);
        Assert.Contains("DONE", output, StringComparison.Ordinal);
        Assert.DoesNotContain("@@runner", output, StringComparison.Ordinal);
        Assert.DoesNotContain("hacked", output, StringComparison.Ordinal);
        Assert.Empty(viewModel.ErrorText);

        await viewModel.ShutdownAsync();
        Directory.Delete(settingsFolder, true);
    }
}
