using System.Globalization;
using System.Text;

namespace ScriptRunner.App.ViewModels;

/// <summary>
/// The numbers a run sent with runner_io.plot(): one row per call, each with the seconds since the first and a value
/// for each name it gave. Names keep the order they first appeared in. Not thread-safe (used on the UI thread).
/// </summary>
public sealed class PlotData
{
    /// <summary>Most rows kept; the oldest are dropped past this.</summary>
    public const int RowsKept = 100_000;

    private readonly List<string> names = new();
    private readonly List<PlotRow> rows = new();
    private DateTime? started;

    public IReadOnlyList<string> Names => names;

    public IReadOnlyList<PlotRow> Rows => rows;

    public bool IsEmpty => 0 == rows.Count;

    /// <summary>Raised after a row is added or the data is cleared.</summary>
    public event Action? Changed;

    public void Add(IReadOnlyList<KeyValuePair<string, double>> values, DateTime? now = null)
    {
        DateTime time = now ?? DateTime.Now;
        started ??= time;
        var row = new Dictionary<string, double>();
        foreach ((string name, double value) in values)
        {
            if (!names.Contains(name))
            {
                names.Add(name);
            }
            row[name] = value;
        }
        rows.Add(new PlotRow((time - started.Value).TotalSeconds, row));
        if (RowsKept < rows.Count)
        {
            rows.RemoveRange(0, rows.Count - RowsKept);
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        names.Clear();
        rows.Clear();
        started = null;
        Changed?.Invoke();
    }

    /// <summary>The data as CSV: a seconds column, then one column per name; a value a call did not give is blank.</summary>
    public string ToCsv()
    {
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(",", new[] { "seconds" }.Concat(names.Select(Quote))));
        foreach (PlotRow row in rows)
        {
            csv.Append(row.Seconds.ToString("0.###", CultureInfo.InvariantCulture));
            foreach (string name in names)
            {
                csv.Append(',');
                if (row.Values.TryGetValue(name, out double value))
                {
                    csv.Append(value.ToString("R", CultureInfo.InvariantCulture));
                }
            }
            csv.AppendLine();
        }
        return csv.ToString();
    }

    private static string Quote(string name) =>
        name.IndexOfAny(new[] { ',', '"', '\n' }) < 0 ? name : "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

/// <summary>One plot() call: seconds since the first call, and the values it gave by name.</summary>
public sealed record PlotRow(double Seconds, IReadOnlyDictionary<string, double> Values);
