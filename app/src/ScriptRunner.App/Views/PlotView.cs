using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ScriptRunner.App.ViewModels;

namespace ScriptRunner.App.Views;

/// <summary>
/// Draws the numbers from runner_io.plot() as lines over time, one color per name, with the latest value of each
/// in the legend. The vertical scale fits the values shown; the newest <see cref="RowsDrawn"/> calls are drawn.
/// </summary>
public sealed class PlotView : Control
{
    public static readonly StyledProperty<PlotData?> DataProperty =
        AvaloniaProperty.Register<PlotView, PlotData?>(nameof(Data));

    /// <summary>The newest calls drawn; older ones stay in the data (and in its CSV) but are not drawn.</summary>
    public const int RowsDrawn = 1000;

    private const double Left = 56, Right = 12, Top = 26, Bottom = 22;

    // Distinct line colors (the common "category 10" palette), readable on light and dark backgrounds.
    private static readonly IBrush[] Colors = new[] { "#1F77B4", "#D62728", "#2CA02C", "#FF7F0E", "#9467BD",
            "#8C564B", "#E377C2", "#17BECF" }
        .Select(color => (IBrush)new SolidColorBrush(Color.Parse(color))).ToArray();

    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#808080"));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#808080"), 0.35), 1);

    private PlotData? subscribed;

    public PlotData? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (DataProperty == change.Property)
        {
            if (subscribed is not null)
            {
                subscribed.Changed -= InvalidateVisual;
            }
            subscribed = Data;
            if (subscribed is not null)
            {
                subscribed.Changed += InvalidateVisual;
            }
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        PlotData? data = Data;
        Rect area = new(Left, Top, Math.Max(1, Bounds.Width - Left - Right), Math.Max(1, Bounds.Height - Top - Bottom));
        if (data is null || data.IsEmpty)
        {
            return;
        }
        IReadOnlyList<PlotRow> all = data.Rows;
        int first = Math.Max(0, all.Count - RowsDrawn);
        var rows = new List<PlotRow>(all.Count - first);
        for (int i = first; i < all.Count; ++i)
        {
            rows.Add(all[i]);
        }

        double timeFrom = rows[0].Seconds, timeTo = Math.Max(rows[^1].Seconds, timeFrom + 1);
        double low = double.MaxValue, high = double.MinValue;
        foreach (PlotRow row in rows)
        {
            foreach (double value in row.Values.Values)
            {
                low = Math.Min(low, value);
                high = Math.Max(high, value);
            }
        }
        if (high - low < 1e-9)
        {
            low -= 1;
            high += 1;
        }
        double pad = (high - low) * 0.05;
        low -= pad;
        high += pad;

        double X(double seconds) => area.Left + (seconds - timeFrom) / (timeTo - timeFrom) * area.Width;
        double Y(double value) => area.Bottom - (value - low) / (high - low) * area.Height;

        // Grid lines with their values, and the time at each end.
        int steps = area.Height < 120 ? 2 : 4;                     // fewer labels when the graph is short
        for (int step = 0; step <= steps; ++step)
        {
            double value = low + (high - low) * step / steps;
            double y = Y(value);
            context.DrawLine(GridPen, new Point(area.Left, y), new Point(area.Right, y));
            FormattedText label = Text(value.ToString("G4", CultureInfo.CurrentCulture), TextBrush);
            context.DrawText(label, new Point(area.Left - 6 - label.Width, y - label.Height / 2));
        }
        context.DrawText(Text($"{timeFrom:0.#} s", TextBrush), new Point(area.Left, area.Bottom + 4));
        FormattedText end = Text($"{timeTo:0.#} s", TextBrush);
        context.DrawText(end, new Point(area.Right - end.Width, area.Bottom + 4));

        // One line per name, through the calls that gave it a value, with a dot on the latest value.
        double legendX = area.Left;
        for (int index = 0; index < data.Names.Count; ++index)
        {
            string name = data.Names[index];
            IBrush brush = Colors[index % Colors.Length];
            var pen = new Pen(brush, 2);
            Point? previous = null;
            double? latest = null;
            using (context.PushClip(area.Inflate(2)))
            {
                foreach (PlotRow row in rows)
                {
                    if (!row.Values.TryGetValue(name, out double value))
                    {
                        continue;
                    }
                    var point = new Point(X(row.Seconds), Y(value));
                    if (previous is { } from)
                    {
                        context.DrawLine(pen, from, point);
                    }
                    previous = point;
                    latest = value;
                }
                if (previous is { } newest)
                {
                    context.DrawEllipse(brush, null, newest, 3, 3);       // marks the latest value
                }
            }
            string legend = latest is { } last ? $"{name}: {last.ToString("G6", CultureInfo.CurrentCulture)}" : name;
            FormattedText text = Text(legend, brush);
            context.DrawRectangle(brush, null, new Rect(legendX, 8, 10, 10));
            context.DrawText(text, new Point(legendX + 14, 13 - text.Height / 2));
            legendX += 14 + text.Width + 18;
        }
    }

    private static FormattedText Text(string text, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, brush);
}
