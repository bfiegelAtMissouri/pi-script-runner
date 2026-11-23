using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ScriptRunner.Core;

namespace ScriptRunner.App.Views;

/// <summary>
/// Draws the picture from runner_io's draw object the same way as its saved SVG, on white where the background is
/// transparent (as a web browser shows the SVG file): the view fits the
/// canvas() edges (or everything drawn) with the same scale both ways, and y points up.
/// </summary>
public sealed class DrawingView : Control
{
    public static readonly StyledProperty<ScriptDrawing?> DrawingProperty =
        AvaloniaProperty.Register<DrawingView, ScriptDrawing?>(nameof(Drawing));

    /// <summary>A smaller margin than the saved picture's, so a short pane still shows a useful drawing.</summary>
    private const double ScreenMargin = 10;

    private static readonly IBrush Paper = Brushes.White;
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#DDDDDD")), 1);
    private static readonly IPen EdgePen = new Pen(new SolidColorBrush(Color.Parse("#BBBBBB")), 1);
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#888888"));
    private static readonly Typeface Sans = new("Arial, Helvetica, Liberation Sans, DejaVu Sans, sans-serif");

    private readonly Dictionary<string, IBrush?> brushes = new();
    private ScriptDrawing? subscribed;
    private bool redrawQueued;

    public ScriptDrawing? Drawing
    {
        get => GetValue(DrawingProperty);
        set => SetValue(DrawingProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (DrawingProperty == change.Property)
        {
            if (subscribed is not null)
            {
                subscribed.Changed -= QueueRedraw;
            }
            subscribed = Drawing;
            if (subscribed is not null)
            {
                subscribed.Changed += QueueRedraw;
            }
            InvalidateVisual();
        }
    }

    /// <summary>Redraws once per frame however many shapes arrive in it.</summary>
    private void QueueRedraw()
    {
        if (redrawQueued)
        {
            return;
        }
        redrawQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            redrawQueued = false;
            InvalidateVisual();
        }, Avalonia.Threading.DispatcherPriority.Render);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(Paper, null, bounds);
        ScriptDrawing? drawing = Drawing;
        if (drawing is null || bounds.Width < 2 || bounds.Height < 2)
        {
            return;
        }
        using var clip = context.PushClip(bounds);
        if (BrushFor(drawing.Background) is { } background)
        {
            context.DrawRectangle(background, null, bounds);
        }
        DrawingFrame frame = drawing.FrameFor(bounds.Width, bounds.Height, ScreenMargin);
        if (ScriptDrawing.GridLines(drawing.Grid, frame) is var (xs, ys))
        {
            double left = frame.X(frame.XMin), right = frame.X(frame.XMax), top = frame.Y(frame.YMax), bottom = frame.Y(frame.YMin);
            foreach (double x in xs)
            {
                context.DrawLine(GridPen, new Point(frame.X(x), top), new Point(frame.X(x), bottom));
            }
            foreach (double y in ys)
            {
                context.DrawLine(GridPen, new Point(left, frame.Y(y)), new Point(right, frame.Y(y)));
            }
            context.DrawRectangle(null, EdgePen, new Rect(left, top, right - left, bottom - top));
            foreach (double x in xs)
            {
                DrawText(context, ScriptDrawing.F(x, 3), frame.X(x) + 2, bottom - 3, 10, LabelBrush);
            }
            foreach (double y in ys)
            {
                DrawText(context, ScriptDrawing.F(y, 3), left + 2, frame.Y(y) - 2, 10, LabelBrush);
            }
        }
        foreach (DrawShape shape in drawing.Shapes)
        {
            DrawShape(context, shape, frame);
        }
    }

    private void DrawShape(DrawingContext context, DrawShape shape, DrawingFrame frame)
    {
        if (shape.Opacity <= 0)
        {
            return;
        }
        if ("marker" == shape.Op && shape.Opacity < 1)
        {
            foreach ((double x, double y) in shape.Points)          // each see-through, as in the SVG
            {
                using (context.PushOpacity(shape.Opacity))
                {
                    DrawMarker(context, shape.Marker, frame.X(x), frame.Y(y), shape.Size, BrushFor(shape.Color));
                }
            }
            return;
        }
        if (shape.Opacity < 1)
        {
            using (context.PushOpacity(shape.Opacity))
            {
                DrawSolidShape(context, shape, frame);
            }
            return;
        }
        DrawSolidShape(context, shape, frame);
    }

    private void DrawSolidShape(DrawingContext context, DrawShape shape, DrawingFrame frame)
    {
        IBrush? color = BrushFor(shape.Color);
        if ("text" == shape.Op)
        {
            if (color is not null)
            {
                DrawText(context, shape.Text, frame.X(shape.X), frame.Y(shape.Y), shape.Size, color);
            }
            return;
        }
        if ("marker" == shape.Op)
        {
            foreach ((double x, double y) in shape.Points)
            {
                DrawMarker(context, shape.Marker, frame.X(x), frame.Y(y), shape.Size, color);
            }
            return;
        }
        IPen? pen = color is null ? null
            : new Pen(color, shape.Width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        IBrush? fill = BrushFor(shape.Fill);
        switch (shape.Op)
        {
            case "line":
                if (pen is not null)
                {
                    context.DrawLine(pen, Pixel(shape.Points[0], frame), Pixel(shape.Points[1], frame));
                }
                break;
            case "polyline":
            case "polygon":
                var geometry = new StreamGeometry();
                using (StreamGeometryContext g = geometry.Open())
                {
                    g.BeginFigure(Pixel(shape.Points[0], frame), "polygon" == shape.Op);
                    foreach ((double, double) point in shape.Points.Skip(1))
                    {
                        g.LineTo(Pixel(point, frame));
                    }
                    g.EndFigure("polygon" == shape.Op);
                }
                context.DrawGeometry("polygon" == shape.Op ? fill : null, pen, geometry);
                break;
            case "sector":
            case "arc":
                context.DrawGeometry("sector" == shape.Op ? fill : null, pen, WedgeGeometry(shape, frame));
                break;
            case "rect":
                context.DrawRectangle(fill, pen, new Rect(frame.X(shape.X), frame.Y(shape.Y + shape.H),
                                                          shape.W * frame.Scale, shape.H * frame.Scale));
                break;
            default:
                double r = shape.R * frame.Scale;
                context.DrawEllipse(fill, pen, new Point(frame.X(shape.X), frame.Y(shape.Y)), r, r);
                break;
        }
    }

    /// <summary>A sector or arc, the same path as its SVG: arcs of up to 180 degrees each.</summary>
    private static StreamGeometry WedgeGeometry(DrawShape shape, DrawingFrame frame)
    {
        double start = shape.Direction - shape.Spread / 2;
        int pieces = shape.Spread <= 180 ? 1 : 2;
        Point At(double radius, double compass)
        {
            (double x, double y) = ScriptDrawing.Along(shape.X, shape.Y, radius, compass);
            return new Point(frame.X(x), frame.Y(y));
        }
        void Sweep(StreamGeometryContext g, double radius, bool clockwise)
        {
            var size = new Size(radius * frame.Scale, radius * frame.Scale);
            for (int step = 1; step <= pieces; ++step)
            {
                int k = clockwise ? step : pieces - step;
                g.ArcTo(At(radius, start + shape.Spread * k / pieces), size, 0, false,
                        clockwise ? SweepDirection.Clockwise : SweepDirection.CounterClockwise);
            }
        }
        var geometry = new StreamGeometry();
        using StreamGeometryContext g = geometry.Open();
        if ("arc" == shape.Op)
        {
            g.BeginFigure(At(shape.R, start), false);
            Sweep(g, shape.R, true);
            g.EndFigure(false);
        }
        else
        {
            g.BeginFigure(0 < shape.Inner ? At(shape.Inner, start) : new Point(frame.X(shape.X), frame.Y(shape.Y)), true);
            g.LineTo(At(shape.R, start));
            Sweep(g, shape.R, true);
            if (0 < shape.Inner)
            {
                g.LineTo(At(shape.Inner, start + shape.Spread));
                Sweep(g, shape.Inner, false);
            }
            g.EndFigure(true);
        }
        return geometry;
    }

    private static void DrawMarker(DrawingContext context, string marker, double x, double y, double size, IBrush? color)
    {
        if (color is null)
        {
            return;
        }
        double h = size / 2;
        switch (marker)
        {
            case "circle":
                context.DrawEllipse(color, null, new Point(x, y), h, h);
                break;
            case "square":
                context.DrawRectangle(color, null, new Rect(x - h, y - h, size, size));
                break;
            case "x":
            case "plus":
                var pen = new Pen(color, 2, lineCap: PenLineCap.Round);
                if ("x" == marker)
                {
                    context.DrawLine(pen, new Point(x - h, y - h), new Point(x + h, y + h));
                    context.DrawLine(pen, new Point(x - h, y + h), new Point(x + h, y - h));
                }
                else
                {
                    context.DrawLine(pen, new Point(x - h, y), new Point(x + h, y));
                    context.DrawLine(pen, new Point(x, y - h), new Point(x, y + h));
                }
                break;
            default:
                var geometry = new StreamGeometry();
                using (StreamGeometryContext g = geometry.Open())
                {
                    (double X, double Y)[] corners = ScriptDrawing.MarkerCorners(marker, x, y, size);
                    g.BeginFigure(new Point(corners[0].X, corners[0].Y), true);
                    foreach ((double cx, double cy) in corners.Skip(1))
                    {
                        g.LineTo(new Point(cx, cy));
                    }
                    g.EndFigure(true);
                }
                context.DrawGeometry(color, null, geometry);
                break;
        }
    }

    /// <summary>Text whose baseline starts at (x, y), as SVG places it.</summary>
    private static void DrawText(DrawingContext context, string text, double x, double y, double size, IBrush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Sans, size, brush);
        context.DrawText(formatted, new Point(x, y - formatted.Baseline));
    }

    private static Point Pixel((double X, double Y) point, DrawingFrame frame) => new(frame.X(point.X), frame.Y(point.Y));

    /// <summary>A brush for a checked color, or null for "none".</summary>
    private IBrush? BrushFor(string color)
    {
        if (!brushes.TryGetValue(color, out IBrush? brush))
        {
            brush = ColorFor(color) is { } parsed ? new SolidColorBrush(parsed) : null;
            brushes[color] = brush;
        }
        return brush;
    }

    /// <summary>A color runner_io accepts (a web color name or #hex), or null for "none" or a name not known here.</summary>
    public static Color? ColorFor(string color)
    {
        string name = "rebeccapurple" == color ? "#663399" : color.Replace("grey", "gray", StringComparison.Ordinal);
        return "none" != color && Color.TryParse(name, out Color parsed) ? parsed : null;
    }
}
