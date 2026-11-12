using System.Reflection;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace ScriptRunner.Core;

/// <summary>Remembers each Pi's SSH host key fingerprint the first time it is seen (trust on first use).</summary>
public interface IHostKeyStore
{
    /// <summary>The fingerprint saved for this host and port, or null if none is saved.</summary>
    string? Find(string host, int port);

    void Remember(string host, int port, string fingerprint);
}

/// <summary>
/// Runs scripts on a Pi directly over SSH; nothing needs to be installed on the Pi. The files are uploaded with
/// SFTP to ~/script_runner_ssh/run, and the script runs in a terminal session (so input() works) under flock, so
/// it cannot run at the same time as a script started from the Pi's runner server.
/// </summary>
public sealed class SshRunnerConnection : IRunnerConnection
{
    private const string RemoteDirectory = "script_runner_ssh/run";
    private const string ShimName = "_script_runner_shim.py";
    private const string RunScriptName = "_script_runner_run.sh";
    private const string HelperName = "runner_io.py";
    private const string SharedMediaDirectory = "/tmp/";      // play() and show() may also use files here
    private const string StartMarker = "@@runner-ssh-start\n";
    private const string ExitMarker = "@@runner-ssh-exit ";

    /// <summary>Matches only the Python process running the user's script (see Remote/_script_runner_shim.py).</summary>
    private const string ScriptProcessPattern = "'^python3 -u " + ShimName + " '";

    private readonly ConnectionSettings settings;
    private readonly IHostKeyStore hostKeys;
    private readonly SemaphoreSlim connectLock = new(1, 1);
    private SshClient? ssh;
    private SftpClient? sftp;
    private ShellStream? shell;
    private volatile bool running;
    private volatile bool stopRequested;
    private int stopping;                   // 1 while a stop sequence runs, so a second Stop does not start another
    private CancellationTokenSource? runTimer;

    public SshRunnerConnection(ConnectionSettings settings, IHostKeyStore hostKeys)
    {
        this.settings = settings;
        this.hostKeys = hostKeys;
    }

    public event Action<RunnerEvent>? EventReceived;

    public bool IsRunning => running;

    public async Task StartAsync(IReadOnlyList<RunFile> files, string mainFile, CancellationToken cancellationToken = default)
    {
        if (running)
        {
            throw new RunnerException("A script is already running. Stop it first.");
        }
        string main = SafeFileName(mainFile);
        await ConnectAsync(cancellationToken).ConfigureAwait(false);

        // A fresh folder for each run, holding the user's files plus the two helper files.
        Execute($"rm -rf ~/{RemoteDirectory} && mkdir -p ~/{RemoteDirectory}");
        try
        {
            foreach (RunFile file in files)
            {
                string name = SafeFileName(file.Name);
                if (ShimName != name && HelperName != name && RunScriptName != name)
                {
                    Upload(name, file.Data);
                }
            }
            Upload(HelperName, ReadResource(HelperName));
            Upload(ShimName, ReadResource(ShimName));
            Upload(RunScriptName, Encoding.UTF8.GetBytes(RunScript(main)));
        }
        catch (SshException error)
        {
            throw new RunnerException("Could not copy the files to the Pi: " + error.Message, error);
        }

        stopRequested = false;
        running = true;
        shell = ssh!.CreateShellStream("dumb", 200, 50, 0, 0, 64 * 1024);

        // exec replaces the login shell with a non-interactive one running the run script. (An interactive shell
        // abandons the rest of a command line when a program ends by Ctrl+C, which would lose the exit marker.)
        string command = $"exec sh ~/{RemoteDirectory}/{RunScriptName}\n";
        shell.Write(command);
        shell.Flush();

        Raise(new StartedEvent(main));
        var reader = new Thread(ReadOutput) { IsBackground = true, Name = "SSH output" };
        reader.Start(shell);
        StartTimeLimit();
    }

    public Task SendInputAsync(string line, CancellationToken cancellationToken = default)
    {
        ShellStream? current = shell;
        if (!running || current is null)
        {
            throw new RunnerException("No script is running.");
        }
        current.Write(line + "\n");      // the terminal echoes it, so no EchoEvent is needed
        current.Flush();
        return Task.CompletedTask;
    }

    /// <summary>Longest reply sent as typed input; a terminal accepts at most 4095 characters per line.</summary>
    private const int LongestReplyLine = 1000;
    private int replyFiles;

    public Task SendReplyAsync(string replyJson, CancellationToken cancellationToken = default)
    {
        ShellStream? current = shell;
        if (!running || current is null)
        {
            throw new RunnerException("No script is running.");
        }
        string line = Protocol.Tag + "reply " + replyJson;
        if (LongestReplyLine < line.Length)
        {
            string name = $"_runner_reply_{Interlocked.Increment(ref replyFiles)}.json";
            try
            {
                Upload(name, Encoding.UTF8.GetBytes(replyJson));
            }
            catch (SshException error)
            {
                throw new RunnerException("Could not send the laptop's answer to the Pi: " + error.Message, error);
            }
            line = Protocol.Tag + "reply-file " + name;
        }
        current.Write(line + "\n");
        current.Flush();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!running)
        {
            throw new RunnerException("No script is running.");
        }
        stopRequested = true;
        await StopSequenceAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops waiting for a script that could not be confirmed stopped, so the app is never stuck in "Stopping".
    /// Closing the terminal session also sends the script SIGHUP, which the shim turns into Ctrl+C.
    /// </summary>
    private void Abandon(string message)
    {
        if (!running)
        {
            return;
        }
        runTimer?.Cancel();
        running = false;
        ShellStream? current = shell;
        shell = null;
        Raise(new ExitedEvent(null, true, message));
        CloseInBackground(current);
    }

    /// <summary>
    /// Closes a terminal session without waiting for it. SSH.NET can deadlock when the app closes a session at the
    /// moment the Pi closes it too (for example, just as a stopped script ends), so the app never waits on this.
    /// </summary>
    private static void CloseInBackground(IDisposable? resource)
    {
        if (resource is not null)
        {
            _ = Task.Run(resource.Dispose);
        }
    }

    public Task<byte[]> GetFileAsync(string name, long maxBytes = long.MaxValue, CancellationToken cancellationToken = default)
    {
        SftpClient client = sftp ?? throw new RunnerException("Not connected to the Pi.");
        string path = RemoteMediaPath(name);
        return Task.Run(() =>
        {
            try
            {
                long size = client.GetAttributes(path).Size;
                if (maxBytes < size)
                {
                    throw FileSizes.TooLarge(name, size, maxBytes);
                }
                using var buffer = new MemoryStream();
                client.DownloadFile(path, buffer);
                return buffer.ToArray();
            }
            catch (SshException error)
            {
                throw new RunnerException($"Could not get {name} from the Pi: {error.Message}", error);
            }
        }, cancellationToken);
    }

    /// <summary>Longest the app waits for the connection to close before leaving it to close in the background.</summary>
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(3);

    public async ValueTask DisposeAsync()
    {
        runTimer?.Cancel();
        ShellStream? currentShell = shell;
        SftpClient? currentSftp = sftp;
        SshClient? currentSsh = ssh;
        shell = null;
        // Closing the terminal sends SIGHUP, which the shim turns into Ctrl+C. Closing runs on a worker thread and
        // is not waited for past the timeout, so a stuck connection can never freeze the app.
        Task close = Task.Run(() =>
        {
            currentShell?.Dispose();
            currentSftp?.Dispose();
            currentSsh?.Dispose();
        });
        await Task.WhenAny(close, Task.Delay(CloseTimeout)).ConfigureAwait(false);
        connectLock.Dispose();
    }

    // ---- connection ---------------------------------------------------------------------------------------

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ssh is { IsConnected: true } && sftp is { IsConnected: true })
            {
                return;
            }
            ssh?.Dispose();
            sftp?.Dispose();
            var checker = new HostKeyChecker(settings, hostKeys);
            ConnectionInfo info = SshSupport.CreateConnectionInfo(settings);
            ssh = new SshClient(info);
            sftp = new SftpClient(info);
            ssh.HostKeyReceived += checker.Check;
            sftp.HostKeyReceived += checker.Check;
            await SshSupport.ConnectAsync(ssh, settings, checker, cancellationToken).ConfigureAwait(false);
            await SshSupport.ConnectAsync(sftp, settings, checker, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            connectLock.Release();
        }
    }

    // ---- running ------------------------------------------------------------------------------------------

    private void ReadOutput(object? state)
    {
        var stream = (ShellStream)state!;
        Decoder decoder = new UTF8Encoding(false).GetDecoder();
        var bytes = new byte[8192];
        var chars = new char[8192 + 16];
        var pending = new StringBuilder();
        bool started = false;
        int? exitCode = null;
        try
        {
            int count;
            while (0 < (count = stream.Read(bytes, 0, bytes.Length)))
            {
                int length = decoder.GetChars(bytes, 0, count, chars, 0);
                pending.Append(chars, 0, length);
                pending.Replace("\r\n", "\n");
                if (!started)
                {
                    string text = pending.ToString();
                    int at = text.IndexOf(StartMarker, StringComparison.Ordinal);
                    if (at < 0)
                    {
                        continue;                         // login messages and the echoed command: discard
                    }
                    pending.Remove(0, at + StartMarker.Length);
                    started = true;
                }
                exitCode = EmitOutput(pending);
                if (exitCode.HasValue)
                {
                    break;
                }
            }
        }
        catch (Exception error) when (error is ObjectDisposedException or SshException or IOException)
        {
            // the connection closed; reported below
        }
        Finish(exitCode);
    }

    /// <summary>Raises output up to the exit marker. Returns the exit code once the marker has arrived.</summary>
    private int? EmitOutput(StringBuilder pending)
    {
        string text = pending.ToString();
        int at = text.IndexOf(ExitMarker, StringComparison.Ordinal);
        if (0 <= at)
        {
            int end = text.IndexOf('\n', at);
            if (end < 0)
            {
                return null;                              // wait for the rest of the marker line
            }
            string before = text[..at];
            if (before.EndsWith('\n'))
            {
                before = before[..^1];                    // the newline printed ahead of the marker
            }
            RaiseOutput(before);
            pending.Clear();
            return int.TryParse(text[(at + ExitMarker.Length)..end].Trim(), out int code) ? code : -1;
        }

        // Hold back a trailing part that may be the start of the marker ("\n@@runner-ssh-exit "). A lone newline is
        // not held: a runner_io line (such as remote_execute's call) must arrive whole, since the script then waits.
        int keep = 0;
        string marker = "\n" + ExitMarker;
        for (int length = Math.Min(marker.Length, text.Length); 1 < length; --length)
        {
            if (text.EndsWith(marker[..length], StringComparison.Ordinal))
            {
                keep = length;
                break;
            }
        }
        RaiseOutput(text[..^keep]);
        pending.Remove(0, text.Length - keep);
        return null;
    }

    private void Finish(int? exitCode)
    {
        if (!running)
        {
            return;
        }
        runTimer?.Cancel();
        running = false;
        // The terminal session is not closed here: the Pi closes it when the run script ends (just after the exit
        // marker, or already, if the stream ended). Closing it from this side at the same moment can deadlock SSH.NET.
        shell = null;
        if (Protocol.LockBusyExitCode == exitCode)
        {
            Raise(new ExitedEvent(null, false,
                "Another script is already running on this Pi (possibly started another way). Try again when it ends."));
        }
        else if (exitCode is null)
        {
            Raise(new ExitedEvent(null, stopRequested,
                "The connection to the Pi closed. If the script was still running, it received Ctrl+C."));
        }
        else
        {
            Raise(new ExitedEvent(exitCode, stopRequested));
        }
    }

    private void StartTimeLimit()
    {
        runTimer?.Dispose();
        runTimer = null;
        if (0 == settings.MaxSeconds)
        {
            return;                                       // no limit
        }
        runTimer = new CancellationTokenSource();
        CancellationToken token = runTimer.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.MaxSeconds), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (running)
            {
                Raise(new NoticeEvent($"Time limit of {settings.MaxSeconds} s reached; stopping the script."));
                stopRequested = true;
                await StopSequenceAsync(CancellationToken.None).ConfigureAwait(false);
            }
        });
    }

    /// <summary>
    /// Ctrl+C to the script's Python process only (so its finally: block runs), then terminate, then kill. Each
    /// signal is sent over its own short-lived connection, so a busy or stuck script connection cannot block it.
    /// </summary>
    private async Task StopSequenceAsync(CancellationToken cancellationToken)
    {
        if (0 != Interlocked.Exchange(ref stopping, 1))
        {
            return;                                     // a stop is already in progress
        }
        try
        {
            (string signal, string? notice)[] steps =
            {
                ("INT", null),
                ("TERM", "The script did not stop after Ctrl+C; terminating it."),
                ("KILL", "The script is still running; forcing it to quit."),
            };
            foreach ((string signal, string? notice) in steps)
            {
                if (!running)
                {
                    return;
                }
                if (notice is not null)
                {
                    Raise(new NoticeEvent(notice));
                }
                try
                {
                    await PiCommand.RunAsync(settings, hostKeys, $"pkill -{signal} -f {ScriptProcessPattern}; true",
                        TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                }
                catch (RunnerException error)
                {
                    Raise(new NoticeEvent("Could not send the stop signal: " + error.Message));
                }
                DateTime deadline = DateTime.UtcNow + Protocol.StopGrace;
                while (running && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }
            Abandon("The script could not be confirmed stopped, so the app stopped waiting for it. " +
                    "If the robot is still running, use \"Force stop all Python\".");
        }
        finally
        {
            Interlocked.Exchange(ref stopping, 0);
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    /// <summary>
    /// The shell script that runs the user's script. The markers around the output are split by quotes here, so
    /// only printf's output contains them, never an echoed command.
    /// </summary>
    internal static string RunScript(string main) =>
        "printf '%s\\n' '@@runner-ssh-''start'\n" +
        $"cd \"$HOME/{RemoteDirectory}\" || exit 1\n" +
        $"flock -n -E {Protocol.LockBusyExitCode} {Protocol.LockFile} python3 -u {ShimName} {ShellQuote(main)}\n" +
        "printf '\\n%s%s\\n' '@@runner-ssh-''exit ' \"$?\"\n";

    private void Execute(string command)
    {
        SshClient client = ssh ?? throw new RunnerException("Not connected to the Pi.");
        using SshCommand result = client.RunCommand(command);
        if (0 != result.ExitStatus)
        {
            throw new RunnerException($"The Pi could not run a setup command: {result.Error.Trim()}");
        }
    }

    private void Upload(string name, byte[] data)
    {
        using var stream = new MemoryStream(data);
        sftp!.UploadFile(stream, RemoteDirectory + "/" + name, true);
    }

    private void Raise(RunnerEvent runnerEvent) => EventReceived?.Invoke(runnerEvent);

    private void RaiseOutput(string text)
    {
        if (0 < text.Length)
        {
            Raise(new OutputEvent(text));
        }
    }

    /// <summary>A plain file name: no folders, not hidden. Throws for anything else.</summary>
    internal static string SafeFileName(string name)
    {
        if (0 == name.Length || name.StartsWith('.') || name.Contains('/') || name.Contains('\\'))
        {
            throw new RunnerException($"\"{name}\" is not a usable file name.");
        }
        return name;
    }

    /// <summary>
    /// The SFTP path for a file named by play() or show(): an absolute path in /tmp as given, or a plain file name in
    /// the run folder. Throws for anything else.
    /// </summary>
    internal static string RemoteMediaPath(string name)
    {
        if (name.StartsWith(SharedMediaDirectory, StringComparison.Ordinal) && SharedMediaDirectory.Length < name.Length
            && !name.Split('/').Contains(".."))
        {
            return name;
        }
        return RemoteDirectory + "/" + SafeFileName(name);
    }

    /// <summary>Quotes a value for the remote shell (POSIX single quotes).</summary>
    internal static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static byte[] ReadResource(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Missing embedded resource " + name);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
