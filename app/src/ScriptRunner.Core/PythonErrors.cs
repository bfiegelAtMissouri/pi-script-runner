using System.Text.RegularExpressions;

namespace ScriptRunner.Core;

/// <summary>One "File ..., line N" entry of a Python traceback.</summary>
public sealed record TracebackFrame(string Path, int Line)
{
    /// <summary>The file's name without its folder, for example "square.py".</summary>
    public string FileName => Path.Replace('\\', '/').Split('/')[^1];
}

/// <summary>The error a Python program ended with, read from its output.</summary>
/// <param name="Kind">The exception's type without its module, for example "NameError" or "SerialException".</param>
/// <param name="Message">What Python said after the type, or "".</param>
/// <param name="Frame">Where it happened: the deepest place in the user's own files when there is one, else the
/// deepest place listed, or null when the output gave none.</param>
public sealed record PythonError(string Kind, string Message, TracebackFrame? Frame);

/// <summary>Finds the error at the end of a Python program's output and explains the common ones.</summary>
public static partial class PythonErrors
{
    [GeneratedRegex("""^\s*File "(?<path>[^"]+)", line (?<line>\d+)""")]
    private static partial Regex FrameLine();

    // An exception line starts at the left margin: a dotted name ending in Error, Exception, Exit, or Interrupt,
    // optionally followed by ": message".
    [GeneratedRegex("""^(?<kind>[A-Za-z_][\w.]*(?:Error|Exception|Exit|Interrupt))(?::\s?(?<message>.*))?$""")]
    private static partial Regex ExceptionLine();

    [GeneratedRegex("""No module named '(?<module>[^'.]+)""")]
    private static partial Regex MissingModule();

    /// <summary>
    /// The last error in a program's output, or null if it did not end with one. A program stopped with Ctrl+C
    /// (KeyboardInterrupt) did not fail, so that is not an error.
    /// </summary>
    /// <param name="userFiles">Names of the user's files, so the place shown is in their code rather than in a
    /// library.</param>
    public static PythonError? Find(string output, IReadOnlyCollection<string> userFiles)
    {
        string[] lines = output.Replace("\r\n", "\n").Split('\n');
        int errorAt = -1;
        Match error = Match.Empty;
        for (int i = lines.Length - 1; 0 <= i && errorAt < 0; --i)
        {
            Match match = ExceptionLine().Match(lines[i]);
            if (match.Success && LooksLikeTraceback(lines, i))
            {
                errorAt = i;
                error = match;
            }
        }
        if (errorAt < 0)
        {
            return null;
        }
        string kind = error.Groups["kind"].Value.Split('.')[^1];
        if (kind is "KeyboardInterrupt" or "SystemExit")
        {
            return null;
        }

        // The frames of that error: back to its "Traceback" heading (a syntax error may have none).
        var frames = new List<TracebackFrame>();
        for (int i = errorAt - 1; 0 <= i && !lines[i].StartsWith("Traceback (most recent call last)", StringComparison.Ordinal); --i)
        {
            Match frame = FrameLine().Match(lines[i]);
            if (frame.Success)
            {
                frames.Insert(0, new TracebackFrame(frame.Groups["path"].Value, int.Parse(frame.Groups["line"].Value)));
            }
            if (i < errorAt - 200)
            {
                break;                  // far enough: a traceback is not this long
            }
        }
        TracebackFrame? where = frames.LastOrDefault(f => userFiles.Contains(f.FileName)) ?? frames.LastOrDefault();
        return new PythonError(kind, error.Groups["message"].Value.Trim(), where);
    }

    /// <summary>An exception line counts only when a traceback's "File ..." line comes shortly before it.</summary>
    private static bool LooksLikeTraceback(string[] lines, int errorAt)
    {
        for (int i = errorAt - 1; 0 <= i && errorAt - 8 <= i; --i)
        {
            if (FrameLine().IsMatch(lines[i]))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// A short explanation of what usually causes this error and what to try, or "" when there is nothing more
    /// useful to say than the error itself.
    /// </summary>
    /// <param name="files">The files added in the app: their names, and whether each is Local.</param>
    public static string Hint(PythonError error, IReadOnlyCollection<(string Name, bool IsLocal)> files)
    {
        string message = error.Message;
        switch (error.Kind)
        {
            case "ModuleNotFoundError" or "ImportError" when MissingModule().Match(message) is { Success: true } missing:
                return MissingModuleHint(missing.Groups["module"].Value, files);
            case "ImportError":
                return "The module was found, but it does not have the name you imported from it. Check the spelling " +
                       "and capitals, and that you have the right version of the file.";
            case "SyntaxError" when message.Contains("invalid character", StringComparison.Ordinal):
                return "There is a character Python does not accept, often a curly quote or dash pasted from a " +
                       "document. Retype the quotes and dashes on that line.";
            case "SyntaxError":
                return "Python could not read this line, so nothing ran. Look for a missing colon at the end of an " +
                       "if, for, while, or def line, a missing or extra bracket or quote (the cause may be on the " +
                       "line before), or = where == was meant.";
            case "IndentationError":
                return "The spaces at the start of the line do not match the lines around it. Lines in the same " +
                       "block must start at the same place, and a line ending in a colon must be followed by an " +
                       "indented line.";
            case "TabError":
                return "This line mixes tabs and spaces at its start. Use only spaces (most editors can convert tabs " +
                       "to spaces).";
            case "NameError":
                return "Python does not know this name here. Check its spelling and capitals, that it is given a " +
                       "value before this line runs, and that anything it needs is imported.";
            case "AttributeError" when message.Contains("'NoneType'", StringComparison.Ordinal):
                return "Something on this line is None: a function returned nothing, or a variable was never set. " +
                       "Print the values on this line to find which.";
            case "AttributeError":
                return "That object does not have this name. Check the spelling and capitals, and what kind of " +
                       "value the variable holds (print(type(x)) shows it).";
            case "TypeError":
                return "A value is the wrong kind for what the line does with it, or a function got the wrong " +
                       "number of values. Print the values on this line, and check the function's parameters.";
            case "ValueError":
                return "A value is the right kind but not allowed, for example int(\"abc\"). Print the value to see " +
                       "what it really is.";
            case "ZeroDivisionError":
                return "Something was divided by zero. Check the value before dividing, for example with an if.";
            case "IndexError":
                return "A list was asked for a position it does not have. Positions start at 0, so the last is " +
                       "len(list) - 1.";
            case "KeyError":
                return "A dictionary does not have this key. Check the spelling, or use .get() to allow it to be " +
                       "missing.";
            case "FileNotFoundError":
                return "The file is not on the robot. Files your script reads must be added in the app with it " +
                       "(they are copied to the robot each run), or be in /tmp.";
            case "RecursionError":
                return "A function keeps calling itself without stopping. Check the condition that should end it.";
            case "MemoryError":
                return "The robot ran out of memory. Use smaller pictures or lists, or close other programs.";
            case "OSError" when message.Contains("Remote I/O error", StringComparison.Ordinal)
                                || message.Contains("[Errno 121]", StringComparison.Ordinal):
                return "A Qwiic device (distance sensor or display) did not answer. Check that its cable is " +
                       "plugged in at both ends. The robot check shows which devices answer.";
            case "PermissionError" or "SerialException" when message.Contains("/dev/", StringComparison.Ordinal):
                return "Another program is using the robot. An earlier run may still be going: press \"Force stop " +
                       "all Python\", then run again.";
            case "RuntimeError" when message.Contains("distance sensor", StringComparison.OrdinalIgnoreCase):
                return "Check the distance sensor's Qwiic cable at both ends. The robot check shows whether it " +
                       "answers.";
            case "RemoteExecutionError":
                return "A function in one of your Local files failed on the laptop. Its own error is in the message.";
            default:
                return "";
        }
    }

    private static string MissingModuleHint(string module, IReadOnlyCollection<(string Name, bool IsLocal)> files)
    {
        (string Name, bool IsLocal) own = files.FirstOrDefault(f => f.Name == module + ".py");
        if (own.Name is not null && own.IsLocal)
        {
            return $"{own.Name} is marked Local, so it stays on the laptop. Uncheck Local for files the robot's " +
                   "script imports.";
        }
        switch (module)
        {
            case "picamera":
                return "The 64-bit robots use picamera2 instead of picamera (see examples/sensors/camera2_photo.py).";
            case "picamera2":
                return "The 32-bit robots use picamera instead of picamera2 (see examples/sensors/camera_photo.py).";
            case "robot_api":
                return "Add robot_api.py together with your script; it is copied to the robot each run.";
            case "torch" or "torchvision" or "ultralytics" or "pytesseract":
                return $"{module} is installed only on the 64-bit robots.";
            case "runner_io":
                return "runner_io is provided when the script is run from this app; it is not available when the " +
                       "script runs another way.";
        }
        return $"No module named {module} is installed on the robot. If it is one of your own files, add {module}.py " +
               "with your script. Otherwise check the spelling, or ask whether the library is installed on this robot.";
    }
}
