using System.Diagnostics;
using ScriptRunner.App.Views;
using ScriptRunner.Core;
using Xunit;

namespace ScriptRunner.Tests;

/// <summary>
/// runner_io's draw object and the app's copy of the drawing: the app must save the same SVG as save_svg() writes on
/// the Pi, and show every color runner_io accepts.
/// </summary>
public sealed class DrawingTests
{
    /// <summary>Draws every kind of shape, names, remove, and clear, then saves; argv[1] is the SVG file.</summary>
    private const string Script = """
        import sys
        sys.path.insert(0, sys.argv[2])
        from runner_io import draw
        mode = sys.argv[3]
        if "fixed" == mode:
            draw.canvas(-0.25, 2.25, 0, 2, grid=0.5, background="ivory")
        draw.rect(0, 0, 2, 1.5, color="gray", fill="whitesmoke", width=1)
        draw.rect(1.8, 1.4, -0.4, -0.3, color="#336699")
        draw.polygon([(0.2, 0.2), (0.6, 0.2), (0.4, 0.6)], fill="lightblue")
        draw.circle(1.5, 1.2, 0.2, fill="#fc0", width=3.3)
        draw.line(0, 0, 2, 1.5, color="green", width=1)
        draw.polyline([(0, 1), (1, 1.25), (2, 1)], color="darkgrey")
        draw.text(0.1, 1.4, 'Room <A> & "B"', size=12.5)
        for i, shape in enumerate(["circle", "square", "triangle", "diamond", "x", "plus"]):
            draw.marker(0.2 + 0.3 * i, 0.9, shape=shape, color="red", size=7 + i)
        draw.markers([(1.0, 0.3), (1.1, 0.35), (1.2, 0.4)], shape="square", size=4, color="purple")
        draw.sector(0.3, 0.3, 1.1, 37.5, 27, color="steelblue", fill="lightskyblue", width=1)
        draw.sector(1.6, 0.4, 0.5, 200, 250, fill="bisque", inner=0.2, angles="math")
        draw.arc(0.3, 0.3, 0.8, 37.5, 200, color="red", width=3)
        draw.sector(0.3, 0.3, 1.0, 80, 27, color="none", fill="lightskyblue", opacity=0.2)
        draw.markers([(0.5, 1.2), (0.6, 1.25)], shape="triangle", color="green", opacity=0.45)
        draw.text(0.5, 1.3, "faint <text>", opacity=0.3333)
        draw.line(0, 2, 2, 0, opacity=0)
        draw.marker(1, 1, shape="triangle", color="blue", name="robot")
        draw.text(1, 1.1, "gone", name="label")
        draw.marker(1.25, 1.05, shape="triangle", color="blue", name="robot")
        draw.remove("label")
        if "clear" == mode:
            draw.clear()
            draw.marker(0.123456, -7.891011, shape="plus")
        if "load" == mode:                      # a later run: start over, load, and carry on
            draw.save(sys.argv[1] + ".json")
            draw.clear()
            draw.canvas()
            draw.load(sys.argv[1] + ".json")
            draw.marker(1.5, 0.5, shape="triangle", color="blue", name="robot")
        draw.save_svg(sys.argv[1])
        """;

    [Theory]
    [InlineData("auto")]
    [InlineData("fixed")]
    [InlineData("clear")]
    [InlineData("load")]
    public void TheAppSavesTheSameSvgAsThePi(string mode)
    {
        string? python = ConnectionTests.FindPython();
        Assert.SkipWhen(python is null, "python3 is not installed");
        string work = Directory.CreateTempSubdirectory("script-runner-draw").FullName;
        string script = Path.Combine(work, "draw_all.py"), svg = Path.Combine(work, "pi.svg");
        File.WriteAllText(script, Script);
        var start = new ProcessStartInfo(python!) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-I", script, svg, Path.Combine(ConnectionTests.RepoRoot(), "runner"), mode })
        {
            start.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        string errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(0 == process.ExitCode, errors);

        var drawing = new ScriptDrawing();
        var parser = new OutputParser();
        IReadOnlyList<OutputItem> items = parser.Feed(output);
        Assert.All(items, item => Assert.IsType<DrawItem>(item));
        foreach (DrawItem item in items.Cast<DrawItem>())
        {
            drawing.Apply(item.Command);
        }
        Assert.Equal(File.ReadAllText(svg), drawing.ToSvg());
        Assert.Equal("clear" == mode ? 1 : 22, drawing.Shapes.Count);
        Directory.Delete(work, true);
    }

    [Theory]
    [InlineData("{\"op\": \"marker\", \"points\": [], \"shape\": \"circle\"}")]                   // no point
    [InlineData("{\"op\": \"marker\", \"points\": [[1, 2]], \"shape\": \"star\"}")]               // not a shape
    [InlineData("{\"op\": \"line\", \"points\": [[0, 0], [1, 1], [2, 2]]}")]                      // a line has two
    [InlineData("{\"op\": \"circle\", \"x\": 0, \"y\": 0, \"r\": -1}")]
    [InlineData("{\"op\": \"canvas\", \"bounds\": [1, 0, 0, 1]}")]                                  // xmin > xmax
    [InlineData("{\"op\": \"canvas\", \"grid\": 0}")]
    [InlineData("{\"op\": \"sector\", \"x\": 0, \"y\": 0, \"r\": 1, \"direction\": 0, \"spread\": 360}")]   // circle() is for that
    [InlineData("{\"op\": \"sector\", \"x\": 0, \"y\": 0, \"r\": 1, \"direction\": 0, \"spread\": 27, \"inner\": 1}")]
    [InlineData("{\"op\": \"spiral\"}")]
    [InlineData("[1, 2]")]
    [InlineData("not json")]
    public void AMessageThatIsNotADrawingIsShownAsText(string json)
    {
        IReadOnlyList<OutputItem> items = new OutputParser().Feed("@@runner draw " + json + "\n");
        Assert.IsType<TextItem>(Assert.Single(items));
    }

    [Fact]
    public void NamedShapesReplaceAndAutoFitLeavesRoom()
    {
        var drawing = new ScriptDrawing();
        void Draw(string json) => drawing.Apply(DrawCommand.Parse(json)!);
        Draw("{\"op\": \"marker\", \"points\": [[0, 0]], \"shape\": \"circle\", \"name\": \"robot\"}");
        Draw("{\"op\": \"line\", \"points\": [[0, 0], [4, 2]], \"color\": \"Not A Color!\"}");
        Draw("{\"op\": \"marker\", \"points\": [[1, 1]], \"shape\": \"circle\", \"name\": \"robot\"}");
        Assert.Equal(new[] { "line", "marker" }, drawing.Shapes.Select(shape => shape.Op));
        Assert.Equal("black", drawing.Shapes[0].Color);              // an unusable color falls back
        Assert.Equal((-0.2, 4.2, -0.2, 2.2), drawing.View());
        DrawingFrame frame = drawing.FrameFor(460, 300);
        Assert.Equal(420 / 4.4, frame.Scale, 6);                     // the width limits it: 420 / 4.4 < 260 / 2.4
        Assert.Equal(460 / 2.0, frame.X(2.0), 6);                    // centered
        Assert.Equal(300 / 2.0, frame.Y(1.0), 6);
        Assert.True(frame.Y(2) < frame.Y(1));                        // y points up
        drawing.Reset();
        Assert.True(drawing.IsEmpty);
    }

    [Theory]
    [InlineData(0.125, 2, "0.12")]           // ties go to the even digit, as Python's round() does
    [InlineData(0.135, 2, "0.14")]
    [InlineData(-0.004, 2, "0")]
    [InlineData(-1.5, 2, "-1.5")]
    [InlineData(20, 2, "20")]
    [InlineData(0.30000000000000004, 3, "0.3")]
    public void NumbersAreWrittenAsRunnerIoWritesThem(double value, int places, string expected) =>
        Assert.Equal(expected, ScriptDrawing.F(value, places));

    [Fact]
    public void EveryColorRunnerIoAcceptsCanBeShown()
    {
        string? python = ConnectionTests.FindPython();
        Assert.SkipWhen(python is null, "python3 is not installed");
        var start = new ProcessStartInfo(python!) { RedirectStandardOutput = true };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import sys; sys.path.insert(0, sys.argv[1]); import runner_io; print(' '.join(sorted(runner_io._COLOR_NAMES)))");
        start.ArgumentList.Add(Path.Combine(ConnectionTests.RepoRoot(), "runner"));
        using Process process = Process.Start(start)!;
        string[] names = process.StandardOutput.ReadToEnd().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        process.WaitForExit();
        Assert.True(140 < names.Length);
        Assert.All(names.Where(name => "none" != name), name => Assert.NotNull(DrawingView.ColorFor(name)));
        Assert.Null(DrawingView.ColorFor("none"));
        Assert.Equal(Avalonia.Media.Color.Parse("#FFCC00"), DrawingView.ColorFor("#fc0"));
        Assert.Equal(Avalonia.Media.Color.Parse("#336699"), DrawingView.ColorFor("#336699"));
    }
}
