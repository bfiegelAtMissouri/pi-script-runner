using System.Text;

namespace ScriptRunner.Core;

/// <summary>An item found in a script's output.</summary>
public abstract record OutputItem;

/// <summary>Text to display.</summary>
public sealed record TextItem(string Text) : OutputItem;

/// <summary>runner_io.say(): text to speak.</summary>
public sealed record SayItem(string Text) : OutputItem;

/// <summary>runner_io.play(): a sound file to play.</summary>
public sealed record PlayItem(string FileName) : OutputItem;

/// <summary>runner_io.show(): a file to display and keep.</summary>
public sealed record ShowItem(string FileName) : OutputItem;

/// <summary>runner_io.remote_execute(): a request to call a local function, as JSON.</summary>
public sealed record CallItem(string RequestJson) : OutputItem;

/// <summary>
/// Splits a script's output into text to display and runner_io messages (lines starting with
/// <see cref="Protocol.Tag"/>). Output arrives in arbitrary chunks, so complete lines are returned by
/// <see cref="Feed"/>, and the unfinished last line (for example an input() prompt) is available as
/// <see cref="LiveText"/> to show until it is finished. Not thread-safe.
/// </summary>
public sealed class OutputParser
{
    private readonly StringBuilder pending = new();

    /// <summary>The unfinished last line, minus any part that may turn out to be a runner_io tag.</summary>
    public string LiveText
    {
        get
        {
            string text = pending.ToString().Replace("\r", "", StringComparison.Ordinal);
            int at = text.IndexOf('@');
            if (0 <= at)
            {
                string tail = text[at..];
                if (Protocol.Tag.StartsWith(tail, StringComparison.Ordinal) ||
                    tail.StartsWith(Protocol.Tag, StringComparison.Ordinal))
                {
                    return text[..at];
                }
            }
            return text;
        }
    }

    /// <summary>Adds output and returns the items from every line it completes.</summary>
    public IReadOnlyList<OutputItem> Feed(string chunk)
    {
        var items = new List<OutputItem>();
        pending.Append(chunk);
        string text = pending.ToString();
        int start = 0;
        int newline;
        while (0 <= (newline = text.IndexOf('\n', start)))
        {
            ParseLine(text[start..newline], items);
            start = newline + 1;
        }
        pending.Remove(0, start);
        return items;
    }

    /// <summary>Ends the unfinished line, for example when the script exits.</summary>
    public IReadOnlyList<OutputItem> Flush()
    {
        var items = new List<OutputItem>();
        if (0 < pending.Length)
        {
            string line = pending.ToString();
            pending.Clear();
            ParseLine(line, items);
        }
        return items;
    }

    /// <summary>
    /// Returns the unfinished line as text without ending it, so typed input that follows stays on the same
    /// line as its prompt. Returns null when there is nothing to commit or the line may be a tag.
    /// </summary>
    public TextItem? CommitPartial()
    {
        string text = pending.ToString().Replace("\r", "", StringComparison.Ordinal);
        if (0 == text.Length || text.Contains('@'))
        {
            return null;
        }
        pending.Clear();
        return new TextItem(text);
    }

    private static void ParseLine(string line, List<OutputItem> items)
    {
        line = line.TrimEnd('\r');             // terminals (SSH mode) end lines with \r\n
        int at = line.IndexOf(Protocol.Tag, StringComparison.Ordinal);
        if (at < 0)
        {
            items.Add(new TextItem(line + "\n"));
            return;
        }
        if (0 < at)
        {
            items.Add(new TextItem(line[..at] + "\n"));
        }
        string rest = line[(at + Protocol.Tag.Length)..];
        int space = rest.IndexOf(' ');
        string kind = space < 0 ? rest : rest[..space];
        string value = space < 0 ? "" : rest[(space + 1)..];
        switch (kind)
        {
            case "say":
                items.Add(new SayItem(value));
                break;
            case "play":
                items.Add(new PlayItem(value));
                break;
            case "show":
                items.Add(new ShowItem(value));
                break;
            case "call":
                items.Add(new CallItem(value));
                break;
            case "reply":
            case "reply-file":
                break;                          // an answer sent to the script, echoed by the terminal: not shown
            default:
                items.Add(new TextItem(line + "\n"));
                break;
        }
    }
}
