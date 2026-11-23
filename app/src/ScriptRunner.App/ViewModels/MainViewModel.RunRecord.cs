using System.Text;
using ScriptRunner.Core;

namespace ScriptRunner.App.ViewModels;

/// <summary>
/// A record of the latest run for "Save run": when each line of output appeared, which robot and files it used, and
/// how it ended.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Most lines kept for the record; the oldest are dropped past this.</summary>
    private const int RecordLinesKept = 50_000;

    private readonly List<(DateTime When, string Line)> recordLines = new();
    private readonly StringBuilder recordPartial = new();
    private DateTime? recordStarted;
    private string recordHeading = "";
    private string recordEnding = "";

    /// <summary>The files sent with the latest run, as they were when it started.</summary>
    public IReadOnlyList<(string Name, byte[] Data, bool IsLocal)> RecordFiles { get; private set; } =
        Array.Empty<(string, byte[], bool)>();

    public bool HasRunRecord => recordStarted is not null;

    private void StartRecord(ConnectionSettings target, string script, IReadOnlyList<string> arguments)
    {
        recordLines.Clear();
        recordPartial.Clear();
        recordStarted = DateTime.Now;
        recordEnding = "";
        RecordFiles = Files.Select(file => (file.Name, file.Data, file.IsLocal)).ToList();
        var heading = new StringBuilder();
        heading.AppendLine("Script Runner run");
        heading.AppendLine($"Started:  {recordStarted:yyyy-MM-dd HH:mm:ss}");
        heading.AppendLine($"Robot:    {target.Host} (user {target.User})");
        heading.AppendLine($"Script:   {script}");
        if (0 < arguments.Count)
        {
            heading.AppendLine($"Arguments: {ScriptArguments.Show(arguments)}");
        }
        heading.AppendLine("Files:    " + string.Join(", ", RecordFiles.Select(f => f.Name + (f.IsLocal ? " (Local)" : ""))));
        recordHeading = heading.ToString();
        OnPropertyChanged(nameof(HasRunRecord));
    }

    /// <summary>Notes the time of each line of output as it is shown.</summary>
    private void RecordText(string text)
    {
        if (recordStarted is null)
        {
            return;
        }
        foreach (char c in text)
        {
            if ('\n' == c)
            {
                recordLines.Add((DateTime.Now, recordPartial.ToString()));
                recordPartial.Clear();
            }
            else
            {
                recordPartial.Append(c);
            }
        }
        if (RecordLinesKept < recordLines.Count)
        {
            recordLines.RemoveRange(0, recordLines.Count - RecordLinesKept);
        }
    }

    private void EndRecord(string ending) => recordEnding = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {ending}";

    /// <summary>The run's output with the time of each line (seconds from the start), after a heading.</summary>
    public string BuildRunRecord()
    {
        var text = new StringBuilder(recordHeading);
        if (0 < recordEnding.Length)
        {
            text.AppendLine($"Ended:    {recordEnding}");
        }
        if (0 < ErrorReportTitle.Length)
        {
            text.AppendLine($"Error:    {ErrorReportTitle}");
        }
        text.AppendLine();
        text.AppendLine("Output (seconds from the start):");
        DateTime start = recordStarted ?? DateTime.Now;
        IEnumerable<(DateTime When, string Line)> lines = recordLines;
        if (0 < recordPartial.Length)
        {
            lines = lines.Append((DateTime.Now, recordPartial.ToString()));
        }
        foreach ((DateTime when, string line) in lines)
        {
            text.AppendLine($"{(when - start).TotalSeconds,8:0.0}  {line}");
        }
        return text.ToString();
    }
}
