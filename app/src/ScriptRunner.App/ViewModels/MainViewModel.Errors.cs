using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using ScriptRunner.Core;

namespace ScriptRunner.App.ViewModels;

/// <summary>When a script ends with a Python error: which line of which file, the line itself, and a hint.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Most of the run's text kept for finding its error (the end of the output is what matters).</summary>
    private const int RunTextKept = 100_000;

    /// <summary>The text output of the current run, for finding the error it ended with.</summary>
    private readonly StringBuilder runText = new();

    /// <summary>For example "NameError in square.py, line 12: name 'spead' is not defined", or "" when none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorReport))]
    public partial string ErrorReportTitle { get; set; } = "";

    /// <summary>The line the error points to, as in the file, for example "12 |     robot.drive(spead, 0, 2)".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorReportCode))]
    public partial string ErrorReportCode { get; set; } = "";

    /// <summary>What usually causes the error and what to try, or "".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorReportHint))]
    public partial string ErrorReportHint { get; set; } = "";

    public bool HasErrorReport => 0 < ErrorReportTitle.Length;

    public bool HasErrorReportCode => 0 < ErrorReportCode.Length;

    public bool HasErrorReportHint => 0 < ErrorReportHint.Length;

    private void RecordRunText(string text)
    {
        runText.Append(text);
        if (RunTextKept < runText.Length)
        {
            runText.Remove(0, runText.Length - RunTextKept);
        }
    }

    private void ClearErrorReport()
    {
        runText.Clear();
        ErrorReportTitle = "";
        ErrorReportCode = "";
        ErrorReportHint = "";
    }

    /// <summary>After a run ends by itself: explains the Python error it ended with, if any.</summary>
    private void ReportRunError()
    {
        var names = Files.Select(file => file.Name).ToList();
        PythonError? error = PythonErrors.Find(runText.ToString(), names);
        if (error is null)
        {
            return;
        }
        string where = error.Frame is { } frame ? $" in {frame.FileName}, line {frame.Line}" : "";
        ErrorReportTitle = error.Kind + where + (0 < error.Message.Length ? ": " + error.Message : "");
        ErrorReportCode = error.Frame is { } at ? SourceLine(at) : "";
        ErrorReportHint = PythonErrors.Hint(error, Files.Select(file => (file.Name, file.IsLocal)).ToList());
    }

    /// <summary>The line an error points to, from the added file, or "" when it is not one of them.</summary>
    private string SourceLine(TracebackFrame frame)
    {
        FileEntry? file = Files.FirstOrDefault(f => f.Name == frame.FileName);
        if (file is null)
        {
            return "";
        }
        string[] lines = Encoding.UTF8.GetString(file.Data).Replace("\r\n", "\n").Split('\n');
        return 1 <= frame.Line && frame.Line <= lines.Length ? $"{frame.Line} | {lines[frame.Line - 1].TrimEnd()}" : "";
    }
}
