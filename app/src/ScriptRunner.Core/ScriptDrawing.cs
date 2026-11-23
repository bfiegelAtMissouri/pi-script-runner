using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ScriptRunner.Core;

/// <summary>A shape from runner_io's draw object, in the drawing's units (x to the right, y up).</summary>
/// <param name="Op">line, polyline, polygon, rect, circle, text, marker, sector, or arc.</param>
/// <param name="Points">The points of a line, polyline, polygon, or marker; empty for the others.</param>
/// <param name="Width">Line width in pixels.</param>
/// <param name="Size">Marker width or text size, in pixels.</param>
public sealed record DrawShape(string Op, IReadOnlyList<(double X, double Y)> Points, double X, double Y, double W,
                               double H, double R, string Text, string Color, string Fill, double Width, double Size,
                               string Marker, string? Name)
{
    /// <summary>A sector's or arc's middle direction, in compass degrees (0 up, clockwise).</summary>
    public double Direction { get; init; }

    /// <summary>A sector's or arc's width, in degrees (more than 0, less than 360).</summary>
    public double Spread { get; init; }

    /// <summary>A sector's inner radius (0 for a wedge from its point).</summary>
    public double Inner { get; init; }

    /// <summary>From 0 (invisible) to 1 (solid).</summary>
    public double Opacity { get; init; } = 1;
}

/// <summary>A runner_io draw message.</summary>
public abstract record DrawCommand
{
    /// <summary>A draw message's command, or null when the JSON is not a valid one.</summary>
    public static DrawCommand? Parse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (JsonValueKind.Object != root.ValueKind || !root.TryGetProperty("op", out JsonElement op) ||
                JsonValueKind.String != op.ValueKind)
            {
                return null;
            }
            return op.GetString() switch
            {
                "canvas" => ReadCanvas(root),
                "clear" => new ClearShapes(),
                "remove" => String(root, "name") is { } name ? new RemoveShape(name) : null,
                { } kind when ScriptDrawing.Ops.Contains(kind) => ReadShape(kind, root) is { } shape ? new AddShape(shape) : null,
                _ => null,
            };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static DrawCommand? ReadCanvas(JsonElement root)
    {
        (double, double, double, double)? bounds = null;
        if (root.TryGetProperty("bounds", out JsonElement edges) && JsonValueKind.Null != edges.ValueKind)
        {
            if (JsonValueKind.Array != edges.ValueKind || 4 != edges.GetArrayLength())
            {
                return null;
            }
            double[] e = edges.EnumerateArray().Select(edge => edge.GetDouble()).ToArray();
            if (!e.All(double.IsFinite) || e[1] <= e[0] || e[3] <= e[2])
            {
                return null;
            }
            bounds = (e[0], e[1], e[2], e[3]);
        }
        double? grid = null;
        if (root.TryGetProperty("grid", out JsonElement step) && JsonValueKind.Null != step.ValueKind)
        {
            double value = step.GetDouble();
            if (!double.IsFinite(value) || value <= 0)
            {
                return null;
            }
            grid = value;
        }
        return new SetCanvas(bounds, grid, ScriptDrawing.CheckColor(String(root, "background"), "none"));
    }

    private static DrawShape? ReadShape(string op, JsonElement root)
    {
        var points = new List<(double, double)>();
        if (root.TryGetProperty("points", out JsonElement list))
        {
            foreach (JsonElement point in list.EnumerateArray())
            {
                if (2 != point.GetArrayLength())
                {
                    return null;
                }
                points.Add((point[0].GetDouble(), point[1].GetDouble()));
            }
        }
        int fewest = op switch { "line" => 2, "polyline" => 2, "polygon" => 3, "marker" => 1, _ => 0 };
        if (points.Count < fewest || ("line" == op && 2 != points.Count) || ScriptDrawing.MostPoints < points.Count ||
            !points.All(p => double.IsFinite(p.Item1) && double.IsFinite(p.Item2)))
        {
            return null;
        }
        double Number(string name, double fallback) =>
            root.TryGetProperty(name, out JsonElement value) && JsonValueKind.Number == value.ValueKind ? value.GetDouble() : fallback;
        double x = Number("x", 0), y = Number("y", 0), w = Number("w", 0), h = Number("h", 0), r = Number("r", 0);
        double direction = Number("direction", 0), spread = Number("spread", 0), inner = Number("inner", 0);
        if (!new[] { x, y, w, h, r, direction, spread, inner }.All(double.IsFinite) || w < 0 || h < 0 || r < 0)
        {
            return null;
        }
        if (op is "sector" or "arc" && (spread <= 0 || 360 <= spread || inner < 0 || (0 < inner && r <= inner)))
        {
            return null;
        }
        string marker = String(root, "shape") ?? "circle";
        if ("marker" == op && !ScriptDrawing.MarkerShapes.Contains(marker))
        {
            return null;
        }
        double width = Number("width", 2), size = Number("size", "text" == op ? 14 : 10), opacity = Number("opacity", 1);
        if (!double.IsFinite(width) || !double.IsFinite(size) || !double.IsFinite(opacity))
        {
            return null;
        }
        return new DrawShape(op, points, x, y, w, h, r, String(root, "text") ?? "",
                             ScriptDrawing.CheckColor(String(root, "color"), "black"), ScriptDrawing.CheckColor(String(root, "fill"), "none"),
                             Math.Clamp(width, 0.5, 50), "text" == op ? Math.Clamp(size, 4, 96) : Math.Clamp(size, 2, 200),
                             marker, String(root, "name"))
        {
            Direction = direction,
            Spread = spread,
            Inner = "sector" == op ? inner : 0,
            Opacity = Math.Clamp(opacity, 0, 1),
        };
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && JsonValueKind.String == value.ValueKind ? value.GetString() : null;
}

/// <summary>
/// draw.canvas(): fixed edges (or null to fit everything drawn), the grid step (or null), and the background color
/// ("none" for transparent).
/// </summary>
public sealed record SetCanvas((double XMin, double XMax, double YMin, double YMax)? Bounds, double? Grid,
                               string Background = "none") : DrawCommand;

/// <summary>draw.clear().</summary>
public sealed record ClearShapes : DrawCommand;

/// <summary>draw.remove(name).</summary>
public sealed record RemoveShape(string Name) : DrawCommand;

/// <summary>A shape to add (replacing the shape with the same name, if it has one).</summary>
public sealed record AddShape(DrawShape Shape) : DrawCommand;

/// <summary>Where the drawing's units land in pixels: x to the right, y up, the same scale both ways, centered.</summary>
public sealed class DrawingFrame
{
    public DrawingFrame((double XMin, double XMax, double YMin, double YMax) bounds, double width, double height, double scale)
    {
        (XMin, XMax, YMin, YMax) = bounds;
        Scale = scale;
        Left = (width - (XMax - XMin) * scale) / 2;
        Top = (height - (YMax - YMin) * scale) / 2;
    }

    public double XMin { get; }
    public double XMax { get; }
    public double YMin { get; }
    public double YMax { get; }
    public double Scale { get; }
    public double Left { get; }
    public double Top { get; }

    public double X(double x) => Left + (x - XMin) * Scale;

    public double Y(double y) => Top + (YMax - y) * Scale;
}

/// <summary>
/// The picture a run draws with runner_io's draw object. It is drawn by the app's DrawingView and saved by
/// <see cref="ToSvg"/>, which writes the same text as runner_io's save_svg(). Not thread-safe (used on the UI thread).
/// </summary>
public sealed class ScriptDrawing
{
    /// <summary>Shapes kept; the oldest are dropped past this (runner_io keeps the same number).</summary>
    public const int MostShapes = 20000;

    /// <summary>Points in one shape.</summary>
    public const int MostPoints = 5000;

    /// <summary>The margin, in pixels, around the edges shown in the saved picture.</summary>
    public const double Margin = 20;

    /// <summary>The saved picture fits in this many pixels each way, inside the margin.</summary>
    private const double SvgBox = 760;

    /// <summary>Grid lines each way; with more, the grid is left out.</summary>
    private const int MostGridLines = 200;

    public static readonly IReadOnlySet<string> Ops =
        new HashSet<string> { "line", "polyline", "polygon", "rect", "circle", "text", "marker", "sector", "arc" };

    public static readonly IReadOnlySet<string> MarkerShapes =
        new HashSet<string> { "circle", "square", "triangle", "diamond", "x", "plus" };

    private readonly List<DrawShape> shapes = new();

    public IReadOnlyList<DrawShape> Shapes => shapes;

    public (double XMin, double XMax, double YMin, double YMax)? Bounds { get; private set; }

    public double? Grid { get; private set; }

    /// <summary>The color behind the picture, or "none" for transparent (the saved SVG has no background).</summary>
    public string Background { get; private set; } = "none";

    /// <summary>True when nothing has been drawn and no canvas set.</summary>
    public bool IsEmpty => 0 == shapes.Count && Bounds is null && Grid is null && "none" == Background;

    /// <summary>Raised after each change.</summary>
    public event Action? Changed;

    public void Apply(DrawCommand command)
    {
        switch (command)
        {
            case SetCanvas canvas:
                Bounds = canvas.Bounds;
                Grid = canvas.Grid;
                Background = canvas.Background;
                break;
            case ClearShapes:
                shapes.Clear();
                break;
            case RemoveShape remove:
                shapes.RemoveAll(shape => remove.Name == shape.Name);
                break;
            case AddShape add:
                if (add.Shape.Name is { } name)
                {
                    shapes.RemoveAll(shape => name == shape.Name);
                }
                shapes.Add(add.Shape);
                if (MostShapes < shapes.Count)
                {
                    shapes.RemoveRange(0, shapes.Count - MostShapes);
                }
                break;
        }
        Changed?.Invoke();
    }

    /// <summary>Erases everything, including the canvas settings (a new run).</summary>
    public void Reset()
    {
        shapes.Clear();
        Bounds = null;
        Grid = null;
        Background = "none";
        Changed?.Invoke();
    }

    /// <summary>The edges shown: those set with canvas(), or everything drawn with a little room around it.</summary>
    public (double XMin, double XMax, double YMin, double YMax) View()
    {
        if (Bounds is { } fixedBounds)
        {
            return fixedBounds;
        }
        var xs = new List<double>();
        var ys = new List<double>();
        foreach (DrawShape shape in shapes)
        {
            if (0 < shape.Points.Count)
            {
                xs.AddRange(shape.Points.Select(p => p.X));
                ys.AddRange(shape.Points.Select(p => p.Y));
            }
            else if ("rect" == shape.Op)
            {
                xs.AddRange(new[] { shape.X, shape.X + shape.W });
                ys.AddRange(new[] { shape.Y, shape.Y + shape.H });
            }
            else if ("circle" == shape.Op)
            {
                xs.AddRange(new[] { shape.X - shape.R, shape.X + shape.R });
                ys.AddRange(new[] { shape.Y - shape.R, shape.Y + shape.R });
            }
            else if (shape.Op is "sector" or "arc")
            {
                foreach ((double px, double py) in WedgeExtent(shape))
                {
                    xs.Add(px);
                    ys.Add(py);
                }
            }
            else
            {
                xs.Add(shape.X);
                ys.Add(shape.Y);
            }
        }
        if (0 == xs.Count)
        {
            xs.AddRange(new[] { 0.0, 1.0 });
            ys.AddRange(new[] { 0.0, 1.0 });
        }
        double xmin = xs.Min(), xmax = xs.Max(), ymin = ys.Min(), ymax = ys.Max();
        double pad = 0.05 * Math.Max(xmax - xmin, ymax - ymin);
        if (0 == pad)
        {
            pad = 1;
        }
        return (xmin - pad, xmax + pad, ymin - pad, ymax + pad);
    }

    /// <summary>The frame that fits the view in an area this many pixels across, inside a margin.</summary>
    public DrawingFrame FrameFor(double width, double height, double margin = Margin)
    {
        var view = View();
        double spanX = view.XMax - view.XMin, spanY = view.YMax - view.YMin;
        double scale = Math.Min(Math.Max(1, width - 2 * margin) / spanX, Math.Max(1, height - 2 * margin) / spanY);
        return new DrawingFrame(view, width, height, scale);
    }

    /// <summary>The x and y of the grid lines inside the edges, or null for no grid.</summary>
    public static (List<double> Xs, List<double> Ys)? GridLines(double? step, DrawingFrame frame)
    {
        if (step is not { } g)
        {
            return null;
        }
        double firstX = Math.Ceiling(frame.XMin / g - 1e-9), lastX = Math.Floor(frame.XMax / g + 1e-9);
        double firstY = Math.Ceiling(frame.YMin / g - 1e-9), lastY = Math.Floor(frame.YMax / g + 1e-9);
        if (MostGridLines < lastX - firstX + 1 || MostGridLines < lastY - firstY + 1)
        {
            return null;
        }
        var xs = new List<double>();
        var ys = new List<double>();
        for (double k = firstX; k <= lastX; ++k)
        {
            xs.Add(k * g);
        }
        for (double k = firstY; k <= lastY; ++k)
        {
            ys.Add(k * g);
        }
        return (xs, ys);
    }

    /// <summary>The drawing as SVG text, the same as runner_io's save_svg() writes.</summary>
    public string ToSvg()
    {
        var view = View();
        double spanX = view.XMax - view.XMin, spanY = view.YMax - view.YMin;
        double scale = Math.Min(SvgBox / spanX, SvgBox / spanY);
        double width = spanX * scale + 2 * Margin, height = spanY * scale + 2 * Margin;
        var frame = new DrawingFrame(view, width, height, scale);
        var svg = new StringBuilder();
        void Line(string text) => svg.Append(text).Append('\n');
        Line($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(width)}\" height=\"{F(height)}\" viewBox=\"0 0 {F(width)} {F(height)}\">");
        if ("none" != Background)
        {
            Line($"<rect width=\"{F(width)}\" height=\"{F(height)}\" fill=\"{Background}\"/>");
        }
        if (GridLines(Grid, frame) is var (xs, ys))
        {
            double left = frame.X(frame.XMin), right = frame.X(frame.XMax), top = frame.Y(frame.YMax), bottom = frame.Y(frame.YMin);
            Line("<g stroke=\"#DDDDDD\" stroke-width=\"1\">");
            foreach (double x in xs)
            {
                Line($"<line x1=\"{F(frame.X(x))}\" y1=\"{F(top)}\" x2=\"{F(frame.X(x))}\" y2=\"{F(bottom)}\"/>");
            }
            foreach (double y in ys)
            {
                Line($"<line x1=\"{F(left)}\" y1=\"{F(frame.Y(y))}\" x2=\"{F(right)}\" y2=\"{F(frame.Y(y))}\"/>");
            }
            Line("</g>");
            Line($"<rect x=\"{F(left)}\" y=\"{F(top)}\" width=\"{F(right - left)}\" height=\"{F(bottom - top)}\" fill=\"none\" stroke=\"#BBBBBB\" stroke-width=\"1\"/>");
            Line("<g font-family=\"sans-serif\" font-size=\"10\" fill=\"#888888\">");
            foreach (double x in xs)
            {
                Line($"<text x=\"{F(frame.X(x) + 2)}\" y=\"{F(bottom - 3)}\">{F(x, 3)}</text>");
            }
            foreach (double y in ys)
            {
                Line($"<text x=\"{F(left + 2)}\" y=\"{F(frame.Y(y) - 2)}\">{F(y, 3)}</text>");
            }
            Line("</g>");
        }
        foreach (DrawShape shape in shapes)
        {
            ShapeSvg(shape, frame, element => Line(WithOpacity(element, shape.Opacity)));
        }
        svg.Append("</svg>\n");
        return svg.ToString();
    }

    private static void ShapeSvg(DrawShape shape, DrawingFrame frame, Action<string> line)
    {
        string color = shape.Color;
        if ("marker" == shape.Op)
        {
            foreach ((double x, double y) in shape.Points)
            {
                line(MarkerSvg(shape.Marker, frame.X(x), frame.Y(y), shape.Size, color));
            }
            return;
        }
        if ("text" == shape.Op)
        {
            line($"<text x=\"{F(frame.X(shape.X))}\" y=\"{F(frame.Y(shape.Y))}\" font-family=\"sans-serif\" font-size=\"{F(shape.Size)}\" fill=\"{color}\">{Escape(shape.Text)}</text>");
            return;
        }
        string stroke = $"stroke=\"{color}\" stroke-width=\"{F(shape.Width)}\"";
        switch (shape.Op)
        {
            case "line":
                (double x1, double y1) = shape.Points[0];
                (double x2, double y2) = shape.Points[1];
                line($"<line x1=\"{F(frame.X(x1))}\" y1=\"{F(frame.Y(y1))}\" x2=\"{F(frame.X(x2))}\" y2=\"{F(frame.Y(y2))}\" {stroke} stroke-linecap=\"round\"/>");
                break;
            case "polyline":
            case "polygon":
                string points = string.Join(" ", shape.Points.Select(p => $"{F(frame.X(p.X))},{F(frame.Y(p.Y))}"));
                string fill = "polyline" == shape.Op ? "none" : shape.Fill;
                line($"<{shape.Op} points=\"{points}\" fill=\"{fill}\" {stroke} stroke-linecap=\"round\" stroke-linejoin=\"round\"/>");
                break;
            case "sector":
            case "arc":
                line(WedgeSvg(shape, frame, stroke));
                break;
            case "rect":
                line($"<rect x=\"{F(frame.X(shape.X))}\" y=\"{F(frame.Y(shape.Y + shape.H))}\" width=\"{F(shape.W * frame.Scale)}\" height=\"{F(shape.H * frame.Scale)}\" fill=\"{shape.Fill}\" {stroke}/>");
                break;
            default:
                line($"<circle cx=\"{F(frame.X(shape.X))}\" cy=\"{F(frame.Y(shape.Y))}\" r=\"{F(shape.R * frame.Scale)}\" fill=\"{shape.Fill}\" {stroke}/>");
                break;
        }
    }

    /// <summary>An SVG element with an opacity attribute added when it is see-through, as runner_io adds it.</summary>
    private static string WithOpacity(string element, double opacity)
    {
        if (1 <= opacity)
        {
            return element;
        }
        string attribute = $" opacity=\"{F(opacity, 3)}\"";
        if (element.EndsWith("/>", StringComparison.Ordinal))
        {
            return element[..^2] + attribute + "/>";
        }
        int at = element.IndexOf('>', StringComparison.Ordinal);       // <text ...>words</text>
        return element[..at] + attribute + element[at..];
    }

    /// <summary>The point <paramref name="radius"/> away from (x, y) in a compass direction (0 up, clockwise).</summary>
    public static (double X, double Y) Along(double x, double y, double radius, double compassDegrees)
    {
        double angle = compassDegrees * (Math.PI / 180);           // as Python's math.radians() computes it
        return (x + radius * Math.Sin(angle), y + radius * Math.Cos(angle));
    }

    /// <summary>Points whose extent is the sector's or arc's: its ends, and where its edge is farthest each way.</summary>
    private static List<(double X, double Y)> WedgeExtent(DrawShape shape)
    {
        double start = shape.Direction - shape.Spread / 2;
        var points = new List<(double, double)>
        {
            Along(shape.X, shape.Y, shape.R, start), Along(shape.X, shape.Y, shape.R, start + shape.Spread),
        };
        foreach (double compass in new[] { 0.0, 90, 180, 270 })
        {
            if (Modulo(compass - start, 360) <= shape.Spread)
            {
                points.Add(Along(shape.X, shape.Y, shape.R, compass));
            }
        }
        if ("sector" == shape.Op)
        {
            if (0 < shape.Inner)
            {
                points.Add(Along(shape.X, shape.Y, shape.Inner, start));
                points.Add(Along(shape.X, shape.Y, shape.Inner, start + shape.Spread));
            }
            else
            {
                points.Add((shape.X, shape.Y));
            }
        }
        return points;
    }

    /// <summary>Python's %: the result has the sign of the divisor.</summary>
    private static double Modulo(double value, double divisor) => value - divisor * Math.Floor(value / divisor);

    /// <summary>A sector or arc as an SVG path, as runner_io writes it.</summary>
    private static string WedgeSvg(DrawShape shape, DrawingFrame frame, string stroke)
    {
        double start = shape.Direction - shape.Spread / 2;
        int pieces = shape.Spread <= 180 ? 1 : 2;
        string Point(double radius, double compass)
        {
            (double px, double py) = Along(shape.X, shape.Y, radius, compass);
            return $"{F(frame.X(px))} {F(frame.Y(py))}";
        }
        string Sweep(double radius, bool clockwise)
        {
            string size = F(radius * frame.Scale);
            var text = new StringBuilder();
            IEnumerable<int> steps = clockwise ? Enumerable.Range(1, pieces) : Enumerable.Range(0, pieces).Reverse();
            foreach (int k in steps)
            {
                text.Append($"A{size} {size} 0 0 {(clockwise ? 1 : 0)} {Point(radius, start + shape.Spread * k / pieces)}");
            }
            return text.ToString();
        }
        if ("arc" == shape.Op)
        {
            return $"<path d=\"M{Point(shape.R, start)}{Sweep(shape.R, true)}\" fill=\"none\" {stroke} stroke-linecap=\"round\"/>";
        }
        string d = 0 < shape.Inner
            ? $"M{Point(shape.Inner, start)}L{Point(shape.R, start)}{Sweep(shape.R, true)}L{Point(shape.Inner, start + shape.Spread)}{Sweep(shape.Inner, false)}Z"
            : $"M{F(frame.X(shape.X))} {F(frame.Y(shape.Y))}L{Point(shape.R, start)}{Sweep(shape.R, true)}Z";
        return $"<path d=\"{d}\" fill=\"{shape.Fill}\" {stroke} stroke-linejoin=\"round\"/>";
    }

    private static string MarkerSvg(string marker, double x, double y, double size, string color)
    {
        double h = size / 2;
        switch (marker)
        {
            case "circle":
                return $"<circle cx=\"{F(x)}\" cy=\"{F(y)}\" r=\"{F(h)}\" fill=\"{color}\"/>";
            case "square":
                return $"<rect x=\"{F(x - h)}\" y=\"{F(y - h)}\" width=\"{F(size)}\" height=\"{F(size)}\" fill=\"{color}\"/>";
            case "x":
            case "plus":
                string d = "x" == marker
                    ? $"M{F(x - h)} {F(y - h)}L{F(x + h)} {F(y + h)}M{F(x - h)} {F(y + h)}L{F(x + h)} {F(y - h)}"
                    : $"M{F(x - h)} {F(y)}L{F(x + h)} {F(y)}M{F(x)} {F(y - h)}L{F(x)} {F(y + h)}";
                return $"<path d=\"{d}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"2\" stroke-linecap=\"round\"/>";
            default:
                return $"<polygon points=\"{string.Join(" ", MarkerCorners(marker, x, y, size).Select(p => $"{F(p.X)},{F(p.Y)}"))}\" fill=\"{color}\"/>";
        }
    }

    /// <summary>The corners of a triangle (pointing up) or diamond marker, in pixels.</summary>
    public static (double X, double Y)[] MarkerCorners(string marker, double x, double y, double size)
    {
        double h = size / 2;
        return "triangle" == marker
            ? new[] { (x, y - h), (x + h, y + h), (x - h, y + h) }
            : new[] { (x, y - h), (x + h, y), (x, y + h), (x - h, y) };
    }

    /// <summary>
    /// A color as runner_io checks it (a web color name, #RGB, #RRGGBB, or none), lowercase; the fallback otherwise.
    /// </summary>
    public static string CheckColor(string? text, string fallback)
    {
        string color = (text ?? "").Trim().ToLowerInvariant();
        bool hex = color.StartsWith('#') && color.Length is 4 or 7 && color[1..].All(Uri.IsHexDigit);
        bool name = 0 < color.Length && color.Length <= 32 && color.All(c => c is >= 'a' and <= 'z');
        return hex || name ? color : fallback;
    }

    /// <summary>A number rounded to <paramref name="places"/> decimals (ties to even), without trailing zeros.</summary>
    public static string F(double value, int places = 2)
    {
        double scale = Math.Pow(10, places);
        long whole = (long)Math.Round(value * scale, MidpointRounding.ToEven);
        string sign = whole < 0 ? "-" : "";
        whole = Math.Abs(whole);
        long unit = (long)scale;
        string text = sign + (whole / unit).ToString(CultureInfo.InvariantCulture);
        long fraction = whole % unit;
        if (0 != fraction)
        {
            text += ("." + fraction.ToString(CultureInfo.InvariantCulture).PadLeft(places, '0')).TrimEnd('0');
        }
        return text;
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);
}
