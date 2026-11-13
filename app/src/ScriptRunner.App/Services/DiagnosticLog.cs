using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace ScriptRunner.App.Services;

/// <summary>
/// A record of what the app did, kept so a crash can be explained afterward. Each day's activity goes to
/// logs/app-YYYYMMDD.log in the settings folder, and every unexpected error writes logs/crash-YYYYMMDD-HHMMSS.txt with
/// the error's full details and the most recent activity. Passwords, PINs, and script output are never written.
/// Thread-safe; logging never throws.
/// </summary>
public static class DiagnosticLog
{
    /// <summary>Activity lines kept in memory for crash reports.</summary>
    private const int RecentLineCount = 300;

    /// <summary>Log files older than this are deleted when the app starts.</summary>
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(14);

    private const string SeenMarkerName = "last-seen-crash.txt";

    private static readonly object Gate = new();
    private static readonly Queue<string> Recent = new();

    /// <summary>Errors already recorded, so one that surfaces again (for example as an unobserved task) is not repeated.</summary>
    private static readonly ConditionalWeakTable<Exception, object> Recorded = new();

    /// <summary>The logs folder, or null before <see cref="Start"/>.</summary>
    public static string? Folder { get; private set; }

    public static string Version { get; } = (Assembly.GetEntryAssembly() ?? typeof(DiagnosticLog).Assembly)
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>Raised (on any thread) when an unexpected error was caught and recorded; the text is for the user.</summary>
    public static event Action<string>? ErrorRecorded;

    /// <summary>Starts logging into <paramref name="settingsFolder"/>/logs and deletes old log files.</summary>
    public static void Start(string settingsFolder)
    {
        try
        {
            Folder = Directory.CreateDirectory(Path.Combine(settingsFolder, "logs")).FullName;
            foreach (string file in Directory.GetFiles(Folder, "*.*"))
            {
                if (SeenMarkerName != Path.GetFileName(file) && KeepFor < DateTime.Now - File.GetLastWriteTime(file))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Folder = null;              // logging continues in memory only
        }
        Write($"Started Script Runner {Version} on {RuntimeInformation.OSDescription} " +
              $"({RuntimeInformation.OSArchitecture}), {RuntimeInformation.FrameworkDescription}");
    }

    /// <summary>Records one line of activity.</summary>
    public static void Write(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {message}";
        lock (Gate)
        {
            Recent.Enqueue(line);
            while (RecentLineCount < Recent.Count)
            {
                Recent.Dequeue();
            }
            if (Folder is not null)
            {
                try
                {
                    File.AppendAllText(Path.Combine(Folder, $"app-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // keep the line in memory only
                }
            }
        }
    }

    /// <summary>
    /// Records an unexpected error: a crash report file with its full details, and a line in the activity log. Tells
    /// the user through <see cref="ErrorRecorded"/> unless the app is about to close. Returns the report's path, or
    /// null if it could not be written.
    /// </summary>
    public static string? RecordError(string where, Exception error, bool appWillClose = false, bool tellUser = true)
    {
        IEnumerable<Exception> parts = error is AggregateException many ? many.InnerExceptions : new[] { error };
        if (parts.All(part => Recorded.TryGetValue(part, out _)))
        {
            Write($"Already recorded error seen again in {where}: {error.GetBaseException().Message}");
            return null;
        }
        foreach (Exception part in parts)
        {
            Recorded.AddOrUpdate(part, where);
        }
        Write($"{(appWillClose ? "CRASH" : "ERROR")} in {where}: {error.GetType().FullName}: {error.Message}");
        string? path = WriteReport(where, error, appWillClose);
        if (!appWillClose && tellUser)
        {
            try
            {
                ErrorRecorded?.Invoke($"Something went wrong ({error.Message}). The app kept running; details were " +
                                      "saved (Settings, Diagnostics).");
            }
            catch (Exception)
            {
                // reporting must never cause another error
            }
        }
        return path;
    }

    /// <summary>The newest crash report, or null if there is none.</summary>
    public static string? LatestReport()
    {
        if (Folder is null)
        {
            return null;
        }
        try
        {
            return Directory.GetFiles(Folder, "crash-*.txt").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The newest crash report if it appeared since the last time this was asked (for example, since the app last
    /// started), so the user can be told once. Null otherwise.
    /// </summary>
    public static string? TakeUnseenReport()
    {
        string? latest = LatestReport();
        if (latest is null || Folder is null)
        {
            return null;
        }
        string marker = Path.Combine(Folder, SeenMarkerName);
        try
        {
            string seen = File.Exists(marker) ? File.ReadAllText(marker).Trim() : "";
            if (Path.GetFileName(latest) == seen)
            {
                return null;
            }
            File.WriteAllText(marker, Path.GetFileName(latest));
            return latest;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? WriteReport(string where, Exception error, bool appWillClose)
    {
        var report = new StringBuilder();
        report.AppendLine($"Script Runner {Version}: {(appWillClose ? "the app closed because of this error" : "error caught; the app kept running")}");
        report.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"Where: {where}");
        report.AppendLine($"System: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), {RuntimeInformation.FrameworkDescription}");
        report.AppendLine();
        report.AppendLine(error.ToString());
        report.AppendLine();
        report.AppendLine("Recent activity (oldest first):");
        lock (Gate)
        {
            foreach (string line in Recent)
            {
                report.AppendLine(line);
            }
        }
        if (Folder is null)
        {
            return null;
        }
        try
        {
            string path = Path.Combine(Folder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
            File.WriteAllText(path, report.ToString());
            return path;
        }
        catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
