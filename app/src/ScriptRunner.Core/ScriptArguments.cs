using System.Text;

namespace ScriptRunner.Core;

/// <summary>
/// Command-line arguments for a script, written as on a command line: separated by spaces, with quotes around an
/// argument that contains spaces. The script receives them in sys.argv[1:].
/// </summary>
public static class ScriptArguments
{
    /// <summary>Most characters allowed, all arguments together.</summary>
    public const int LongestText = 4096;

    /// <summary>
    /// Splits the text into arguments. Spaces separate arguments; 'single quotes' keep everything inside as it is;
    /// "double quotes" do too, except that \" and \\ stand for " and \; outside quotes, \ keeps the next character
    /// as it is. Throws <see cref="FormatException"/> with a message for the user when a quote is not closed or the
    /// text holds a character a command line cannot (a line break or other control character).
    /// </summary>
    public static IReadOnlyList<string> Split(string text)
    {
        if (LongestText < text.Length)
        {
            throw new FormatException($"The arguments are longer than {LongestText} characters.");
        }
        if (text.Any(c => char.IsControl(c) && '\t' != c))
        {
            throw new FormatException("The arguments must be on one line.");
        }
        var arguments = new List<string>();
        var current = new StringBuilder();
        bool inArgument = false;
        for (int i = 0; i < text.Length; ++i)
        {
            char c = text[i];
            if (' ' == c || '\t' == c)
            {
                if (inArgument)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    inArgument = false;
                }
                continue;
            }
            inArgument = true;
            if ('\'' == c)
            {
                int end = text.IndexOf('\'', i + 1);
                if (end < 0)
                {
                    throw new FormatException("A ' quote is not closed.");
                }
                current.Append(text, i + 1, end - i - 1);
                i = end;
            }
            else if ('"' == c)
            {
                for (++i; ; ++i)
                {
                    if (text.Length <= i)
                    {
                        throw new FormatException("A \" quote is not closed.");
                    }
                    if ('"' == text[i])
                    {
                        break;
                    }
                    if ('\\' == text[i] && i + 1 < text.Length && text[i + 1] is '"' or '\\')
                    {
                        ++i;
                    }
                    current.Append(text[i]);
                }
            }
            else if ('\\' == c && i + 1 < text.Length)
            {
                current.Append(text[++i]);
            }
            else
            {
                current.Append(c);
            }
        }
        if (inArgument)
        {
            arguments.Add(current.ToString());
        }
        return arguments;
    }

    /// <summary>The arguments written back as text, quoted where needed, for showing in the output.</summary>
    public static string Show(IReadOnlyList<string> arguments) =>
        string.Join(" ", arguments.Select(argument =>
            0 < argument.Length && !argument.Any(c => char.IsWhiteSpace(c) || c is '\'' or '"' or '\\')
                ? argument
                : "\"" + argument.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""));
}
