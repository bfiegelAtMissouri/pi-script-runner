namespace ScriptRunner.Core;

/// <summary>How the app reaches the Raspberry Pi.</summary>
public enum ConnectionMode
{
    /// <summary>Through the runner server installed on the Pi (runner/script_runner.py).</summary>
    Http,

    /// <summary>Directly over SSH; nothing needs to be installed on the Pi.</summary>
    Ssh,
}

/// <summary>Where and how to connect. The secret (PIN or password) is held only in memory.</summary>
public sealed record ConnectionSettings(
    ConnectionMode Mode,
    string Host,
    int Port,
    string User,
    string Secret,
    int MaxSeconds = ConnectionSettings.DefaultMaxSeconds)
{
    public const int DefaultHttpPort = 8000;
    public const int DefaultSshPort = 22;
    public const int DefaultMaxSeconds = 300;
    public const string DefaultUser = "pi";

    /// <summary>Never include the secret when settings are printed or logged.</summary>
    public override string ToString() => $"{Mode} {User}@{Host}:{Port}";
}

/// <summary>A file to upload with the script: the script itself, or a sound or image it uses.</summary>
public sealed record RunFile(string Name, byte[] Data);

/// <summary>Something that happened during a run, in the order it happened.</summary>
public abstract record RunnerEvent;

/// <summary>Output from the script (standard output and standard error); not split on line boundaries.</summary>
public sealed record OutputEvent(string Text) : RunnerEvent;

/// <summary>A line of input the user sent, as it should appear in the output.</summary>
public sealed record EchoEvent(string Text) : RunnerEvent;

/// <summary>A message from Script Runner itself (time limit, stopping, connection problems).</summary>
public sealed record NoticeEvent(string Text) : RunnerEvent;

/// <summary>The script started.</summary>
public sealed record StartedEvent(string FileName) : RunnerEvent;

/// <summary>The script ended. <paramref name="Code"/> is null when no exit code is known.</summary>
public sealed record ExitedEvent(int? Code, bool Stopped, string? Message = null) : RunnerEvent;

/// <summary>A connection to one Raspberry Pi that runs one script at a time.</summary>
public interface IRunnerConnection : IAsyncDisposable
{
    /// <summary>Raised for every run event, on a background thread.</summary>
    event Action<RunnerEvent>? EventReceived;

    /// <summary>True from a successful <see cref="StartAsync"/> until the <see cref="ExitedEvent"/>.</summary>
    bool IsRunning { get; }

    /// <summary>Uploads the files and starts <paramref name="mainFile"/>. Throws <see cref="RunnerException"/>
    /// with a message for the user when the run cannot start.</summary>
    Task StartAsync(IReadOnlyList<RunFile> files, string mainFile, CancellationToken cancellationToken = default);

    /// <summary>Sends one line to the script's input() (a newline is added).</summary>
    Task SendInputAsync(string line, CancellationToken cancellationToken = default);

    /// <summary>Delivers Ctrl+C to the script, then terminates it if it does not stop.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Downloads a file from the run's folder, for play() and show().</summary>
    Task<byte[]> GetFileAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>A problem to show the user, with a message written for them.</summary>
public sealed class RunnerException : Exception
{
    public RunnerException(string message) : base(message) { }

    public RunnerException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Values shared with the Pi side (runner/script_runner.py and runner/runner_io.py).</summary>
public static class Protocol
{
    /// <summary>Lines starting with this are runner_io messages (say, play, show).</summary>
    public const string Tag = "@@runner ";

    /// <summary>Held while any script runs, by the Pi server and by SSH mode, so only one runs at a time.</summary>
    public const string LockFile = "/tmp/pi-script-runner.lock";

    /// <summary>Exit code from flock when another script already holds the lock.</summary>
    public const int LockBusyExitCode = 75;

    /// <summary>Seconds to wait after Ctrl+C, and again after terminating, before the next step.</summary>
    public static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(3);
}
