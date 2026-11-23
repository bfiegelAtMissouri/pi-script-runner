using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using ScriptRunner.App.Services;
using ScriptRunner.App.ViewModels;
using ScriptRunner.App.Views;
using Xunit;

namespace ScriptRunner.Tests;

/// <summary>
/// Reload, plot(), draw, the error report, the run record, and the robot check, through the real screen against the SSH
/// test server (see <see cref="ConnectionTests"/>). Saves a picture as ui-student-help.png in the test output folder.
/// </summary>
public sealed class StudentHelpUiTests
{
    private const string Main = """
        import time
        from runner_io import plot, draw
        import helper
        draw.canvas(0, 2, 0, 1.5, grid=0.5)
        draw.rect(0.2, 0.2, 0.6, 0.4, color="gray", fill="lightblue")
        for i in range(5):
            plot(distance=100 + 10 * i, light=50 - i)
            draw.marker(0.3 * i + 0.2, 1.0, shape="triangle", color="red", size=14, name="robot")
            time.sleep(0.1)
        draw.text(1.0, 0.3, "done", size=16)
        print("version", helper.VERSION)
        print(1 / 0 if 2 == helper.VERSION else "ok")
        """;

    [Fact]
    public async Task ReloadPlotErrorsAndRobotCheck()
    {
        string? host = Environment.GetEnvironmentVariable("SCRIPT_RUNNER_TEST_SSH_HOST");
        string? password = Environment.GetEnvironmentVariable("SCRIPT_RUNNER_TEST_SSH_PASSWORD");
        Assert.SkipWhen(string.IsNullOrEmpty(host) || string.IsNullOrEmpty(password),
            "SCRIPT_RUNNER_TEST_SSH_* environment variables are not set");

        string settingsFolder = Directory.CreateTempSubdirectory("script-runner-help").FullName;
        string work = Directory.CreateTempSubdirectory("script-runner-help-files").FullName;
        string mainPath = Path.Combine(work, "main.py"), helperPath = Path.Combine(work, "helper.py");
        File.WriteAllText(mainPath, Main);
        File.WriteAllText(helperPath, "VERSION = 1\n");
        var platform = new PlatformServices(new UiTests.RecordingSpeech(), new UiTests.SilentAudio(), new NoSecretStore(),
                                            settingsFolder);
        ScriptRunner.App.App.Platform ??= platform;
        Dispatcher ui = await HeadlessApp.StartAsync();
        CancellationToken cancel = TestContext.Current.CancellationToken;
        MainViewModel? model = null;
        Window? window = null;

        static AddedFile FromDisk(string path) =>
            new(Path.GetFileName(path), File.ReadAllBytes(path), () => File.ReadAllBytesAsync(path));

        await ui.InvokeAsync(() =>
        {
            model = new MainViewModel(platform);
            window = new Window { Width = 1000, Height = 760, Content = new MainView { DataContext = model } };
            window.Show();
            model.SelectedModeIndex = 0;
            model.Host = host!;
            model.Port = Environment.GetEnvironmentVariable("SCRIPT_RUNNER_TEST_SSH_PORT") ?? "22";
            model.User = Environment.GetEnvironmentVariable("SCRIPT_RUNNER_TEST_SSH_USER") ?? "pi";
            model.Secret = password!;
            model.AddFiles(new[] { FromDisk(mainPath), FromDisk(helperPath) });
            model.SelectedScript = "main.py";
            model.RunCommand.Execute(null);
        }, DispatcherPriority.Normal, cancel);
        MainViewModel viewModel = model!;

        // First run: plotted, no error.
        await UiTests.WaitUntil(ui, viewModel, () => viewModel.OutputText.Contains("[Finished", StringComparison.Ordinal));
        await ui.InvokeAsync(() =>
        {
            Assert.Contains("version 1", viewModel.OutputText, StringComparison.Ordinal);
            Assert.DoesNotContain("@@runner", viewModel.OutputText, StringComparison.Ordinal);
            Assert.True(viewModel.HasPlot);
            Assert.Equal(5, viewModel.Plot.Rows.Count);
            Assert.Equal(new[] { "distance", "light" }, viewModel.Plot.Names);
            Assert.True(viewModel.HasDrawing);
            Assert.Equal(new[] { "rect", "marker", "text" }, viewModel.Drawing.Shapes.Select(shape => shape.Op));
            Assert.Equal((1.4, 1.0), viewModel.Drawing.Shapes[1].Points[0]);
            Assert.False(viewModel.HasErrorReport);
        }, DispatcherPriority.Normal, cancel);

        // The helper is edited "in another editor": Run uses the new version, says so, and explains the error.
        File.WriteAllText(helperPath, "VERSION = 2\n");
        await ui.InvokeAsync(() => viewModel.RunCommand.Execute(null), DispatcherPriority.Normal, cancel);
        await UiTests.WaitUntil(ui, viewModel, () => viewModel.OutputText.Contains("[Finished", StringComparison.Ordinal)
                                                     && viewModel.HasErrorReport);
        await ui.InvokeAsync(() =>
        {
            Assert.Contains("[Reloaded the changed files: helper.py]", viewModel.OutputText, StringComparison.Ordinal);
            Assert.Contains("version 2", viewModel.OutputText, StringComparison.Ordinal);
            Assert.Equal("ZeroDivisionError in main.py, line 12: division by zero", viewModel.ErrorReportTitle);
            Assert.Equal("12 | print(1 / 0 if 2 == helper.VERSION else \"ok\")", viewModel.ErrorReportCode);
            Assert.Contains("divided by zero", viewModel.ErrorReportHint, StringComparison.Ordinal);
            Assert.StartsWith("updated", viewModel.Files.Single(f => "helper.py" == f.Name).Note, StringComparison.Ordinal);
            string record = viewModel.BuildRunRecord();
            Assert.Contains("Script:   main.py", record, StringComparison.Ordinal);
            Assert.Contains("Error:    ZeroDivisionError in main.py, line 12", record, StringComparison.Ordinal);
            Assert.Matches(@"\n +\d+\.\d  version 2\n", record);
            Assert.Equal("VERSION = 2\n", System.Text.Encoding.UTF8.GetString(viewModel.RecordFiles.Single(f => "helper.py" == f.Name).Data));
            ui.RunJobs();
            using var frame = window!.CaptureRenderedFrame();
            frame?.Save(Path.Combine(AppContext.BaseDirectory, "ui-student-help.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }, DispatcherPriority.Normal, cancel);

        // Reload on its own, then a file that is gone: nothing runs, and the student is told what to do.
        File.WriteAllText(helperPath, "VERSION = 3\n");
        await ui.InvokeAsync(() => viewModel.ReloadFilesCommand.Execute(null), DispatcherPriority.Normal, cancel);
        await UiTests.WaitUntil(ui, viewModel, () => viewModel.StatusText.StartsWith("Reloaded helper.py", StringComparison.Ordinal));
        File.Delete(helperPath);
        await ui.InvokeAsync(() => viewModel.RunCommand.Execute(null), DispatcherPriority.Normal, cancel);
        await UiTests.WaitUntil(ui, viewModel, () => viewModel.ErrorText.Contains("Could not read helper.py again", StringComparison.Ordinal));
        await ui.InvokeAsync(() => Assert.False(viewModel.IsRunning), DispatcherPriority.Normal, cancel);

        // The robot check runs and reports, whatever hardware the test server lacks.
        await ui.InvokeAsync(() => viewModel.CheckRobotCommand.Execute(null), DispatcherPriority.Normal, cancel);
        await UiTests.WaitUntil(ui, viewModel, () => viewModel.OutputText.Contains("[exit code", StringComparison.Ordinal));
        await ui.InvokeAsync(() =>
        {
            Assert.Contains("Robot check: ", viewModel.OutputText, StringComparison.Ordinal);
            Assert.Contains("Python:   3.", viewModel.OutputText, StringComparison.Ordinal);
            Assert.Contains("RVR battery", viewModel.OutputText, StringComparison.Ordinal);
            Assert.Contains("[exit code 0]", viewModel.OutputText, StringComparison.Ordinal);
            window!.Close();
        }, DispatcherPriority.Normal, cancel);
        await viewModel.ShutdownAsync();
        Directory.Delete(settingsFolder, true);
        Directory.Delete(work, true);
    }
}
