using System.Text.Json;
using ScriptRunner.App.ViewModels;
using ScriptRunner.Core;
using Xunit;

namespace ScriptRunner.Tests;

/// <summary>
/// The parts that help students without a robot: reading Python errors, plot() messages and data, and the examples
/// built into the app.
/// </summary>
public sealed class StudentHelpTests
{
    private static readonly string[] Mine = { "square.py", "robot_api.py" };

    [Fact]
    public void FindsTheErrorAndTheLineInTheStudentsFile()
    {
        string output = """
            Starting
            Traceback (most recent call last):
              File "/home/pi/.pi-script-runner/_script_runner_shim.py", line 40, in <module>
                runpy.run_path(main, run_name="__main__")
              File "/home/pi/.pi-script-runner/square.py", line 12, in <module>
                robot.drive(spead, 0, 2)
                            ^^^^^
            NameError: name 'spead' is not defined. Did you mean: 'speed'?

            """;
        PythonError? error = PythonErrors.Find(output, Mine);
        Assert.NotNull(error);
        Assert.Equal("NameError", error.Kind);
        Assert.Equal("name 'spead' is not defined. Did you mean: 'speed'?", error.Message);
        Assert.Equal(new TracebackFrame("/home/pi/.pi-script-runner/square.py", 12), error.Frame);
        Assert.Contains("spelling", PythonErrors.Hint(error, new[] { ("square.py", false) }));
    }

    [Fact]
    public void PrefersTheStudentsFileOverALibrary()
    {
        string output = """
            Traceback (most recent call last):
              File "/home/pi/x/square.py", line 7, in <module>
                robot = Robot()
              File "/home/pi/x/robot_api.py", line 90, in __init__
                self._initialize_distance_sensor()
              File "/home/pi/robot_env/lib/python3.11/site-packages/qwiic_vl53l1x.py", line 600, in sensor_init
                x = self._i2c.readByte(self.address, 0x01)
            OSError: [Errno 121] Remote I/O error
            """;
        PythonError error = PythonErrors.Find(output, Mine)!;
        Assert.Equal("OSError", error.Kind);
        Assert.Equal(new TracebackFrame("/home/pi/x/robot_api.py", 90), error.Frame);
        Assert.Contains("Qwiic", PythonErrors.Hint(error, Array.Empty<(string, bool)>()));
    }

    [Fact]
    public void ReadsASyntaxErrorAndTheLastOfChainedErrors()
    {
        string syntax = """
              File "/home/pi/x/square.py", line 5
                if x = 3:
                     ^
            SyntaxError: invalid syntax
            """;
        PythonError error = PythonErrors.Find(syntax, Mine)!;
        Assert.Equal(("SyntaxError", 5), (error.Kind, error.Frame!.Line));

        string chained = """
            Traceback (most recent call last):
              File "/home/pi/x/square.py", line 3, in <module>
                value = int(text)
            ValueError: invalid literal for int() with base 10: 'abc'

            During handling of the above exception, another exception occurred:

            Traceback (most recent call last):
              File "/home/pi/x/square.py", line 5, in <module>
                print(1 / 0)
            ZeroDivisionError: division by zero
            """;
        error = PythonErrors.Find(chained, Mine)!;
        Assert.Equal(("ZeroDivisionError", 5), (error.Kind, error.Frame!.Line));
    }

    [Fact]
    public void StopIsNotAnErrorAndPlainTextIsNotOne()
    {
        string stopped = """
            Traceback (most recent call last):
              File "/home/pi/x/square.py", line 9, in <module>
                time.sleep(1)
            KeyboardInterrupt
            """;
        Assert.Null(PythonErrors.Find(stopped, Mine));
        Assert.Null(PythonErrors.Find("ValueError: this is just a line the script printed\n", Mine));
        Assert.Null(PythonErrors.Find("", Mine));
    }

    [Fact]
    public void ExplainsMissingModules()
    {
        PythonError Missing(string module) => new("ModuleNotFoundError", $"No module named '{module}'", null);
        Assert.Contains("picamera2", PythonErrors.Hint(Missing("picamera"), Array.Empty<(string, bool)>()));
        Assert.Contains("Add robot_api.py", PythonErrors.Hint(Missing("robot_api"), Array.Empty<(string, bool)>()));
        Assert.Contains("marked Local", PythonErrors.Hint(Missing("helpers"), new[] { ("helpers.py", true) }));
        Assert.Contains("add helpers.py", PythonErrors.Hint(Missing("helpers"), Array.Empty<(string, bool)>()));
        Assert.Contains("64-bit", PythonErrors.Hint(Missing("torch"), Array.Empty<(string, bool)>()));
    }

    [Fact]
    public void ReadsPlotMessages()
    {
        var parser = new OutputParser();
        IReadOnlyList<OutputItem> items = parser.Feed(
            "@@runner plot {\"distance\": 412.0, \"light\": 87}\n@@runner plot not json\n@@runner plot {\"name\": \"x\"}\n");
        var plot = Assert.IsType<PlotItem>(items[0]);
        Assert.Equal(new[] { new KeyValuePair<string, double>("distance", 412), new KeyValuePair<string, double>("light", 87) },
                     plot.Values);
        Assert.IsType<TextItem>(items[1]);          // not a plot message: shown as it is
        Assert.IsType<TextItem>(items[2]);          // no numbers in it
    }

    [Fact]
    public void PlotDataKeepsTimesAndSavesCsv()
    {
        var data = new PlotData();
        var start = new DateTime(2026, 11, 19, 13, 0, 0);
        data.Add(new[] { new KeyValuePair<string, double>("distance", 400) }, start);
        data.Add(new[] { new KeyValuePair<string, double>("distance", 350), new KeyValuePair<string, double>("a, b", 2.5) },
                 start.AddSeconds(1.5));
        Assert.Equal(new[] { "distance", "a, b" }, data.Names);
        Assert.Equal("seconds,distance,\"a, b\"\n0,400,\n1.5,350,2.5\n", data.ToCsv().Replace("\r\n", "\n"));
        data.Clear();
        Assert.True(data.IsEmpty);
    }

    [Fact]
    public void EveryExampleIsListedAndBuiltIntoTheApp()
    {
        string folder = Path.Combine(ConnectionTests.RepoRoot(), "examples");
        IReadOnlyList<Example> examples = ExampleCatalog.Examples;
        Assert.NotEmpty(examples);
        var listed = examples.SelectMany(example => example.AllFiles.Select(file => file.Path)).ToHashSet();
        foreach (string script in Directory.GetFiles(folder, "*.py", SearchOption.AllDirectories))
        {
            string path = Path.GetRelativePath(folder, script).Replace('\\', '/');
            Assert.True(listed.Contains(path), $"{path} is not in examples/examples.json");
        }
        foreach (string path in listed)
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(folder, path)), ExampleCatalog.Read(path));
        }
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "examples.json")));
        Assert.Equal(manifest.RootElement.GetProperty("examples").GetArrayLength(), examples.Count);
    }

    [Fact]
    public void UsingAnExampleAddsItsFilesWithLocalSet()
    {
        string settingsFolder = Directory.CreateTempSubdirectory("script-runner-examples").FullName;
        var model = new MainViewModel(new ScriptRunner.App.Services.PlatformServices(
            new UiTests.RecordingSpeech(), new UiTests.SilentAudio(), new ScriptRunner.App.Services.NoSecretStore(), settingsFolder));
        model.AddFiles(new[] { ("old.py", new byte[] { 0x23 }) });
        Example detect = ExampleCatalog.Examples.Single(example => "remote/robot_detect.py" == example.Script);
        model.UseExampleCommand.Execute(detect);
        Assert.Equal(new[] { ("robot_detect.py", false), ("robot_api.py", false), ("remote_vision.py", true) },
                     model.Files.Select(file => (file.Name, file.IsLocal)));
        Assert.Equal("robot_detect.py", model.SelectedScript);
        Assert.False(model.IsExamplesOpen);
        Directory.Delete(settingsFolder, true);
    }
}
