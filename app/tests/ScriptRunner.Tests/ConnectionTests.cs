using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ScriptRunner.Core;
using Xunit;

namespace ScriptRunner.Tests;

/// <summary>
/// The same end-to-end session run through both connection types against real servers:
/// HTTP against runner/script_runner.py (started by the test), and SSH against an SSH server named by
/// environment variables (skipped when they are not set):
///   SCRIPT_RUNNER_TEST_SSH_HOST, SCRIPT_RUNNER_TEST_SSH_PORT, SCRIPT_RUNNER_TEST_SSH_USER,
///   SCRIPT_RUNNER_TEST_SSH_PASSWORD
/// The SSH account needs python3, flock, and pkill (as on Raspberry Pi OS).
/// </summary>
public sealed class ConnectionTests
{
    private const string TestPin = "4321";

    private const string SessionScript = """
        import os, shutil, time
        from runner_io import say, show
        try:
            speed = input("Enter speed: ")
            print("speed is", speed)
            say("Starting the square")
            show("dot.png")
            shutil.copy("dot.png", "TMP_COPY")
            show("TMP_COPY")
            for side in range(100):
                print("side", side)
                time.sleep(0.5)
        except KeyboardInterrupt:
            print("User ended by pressing Ctrl+C")
        finally:
            if os.path.exists("TMP_COPY"):
                os.remove("TMP_COPY")
            print("FINALLY: cleanup ran")
        """;

    [Fact]
    public async Task HttpSession()
    {
        string? python = FindPython();
        Assert.SkipWhen(python is null, "python3 is not installed");
        using var server = PiServer.Start(python!, TestPin);
        var settings = new ConnectionSettings(ConnectionMode.Http, "127.0.0.1", server.Port, "", TestPin);
        await using var connection = new HttpRunnerConnection(settings);
        await RunSessionAsync(connection);
    }

    [Fact]
    public async Task HttpWrongPinIsReported()
    {
        string? python = FindPython();
        Assert.SkipWhen(python is null, "python3 is not installed");
        using var server = PiServer.Start(python!, TestPin);
        var settings = new ConnectionSettings(ConnectionMode.Http, "127.0.0.1", server.Port, "", "0000");
        await using var connection = new HttpRunnerConnection(settings);
        var error = await Assert.ThrowsAsync<RunnerException>(
            () => connection.StartAsync(new[] { Script("a.py", "print(1)") }, "a.py", TestContext.Current.CancellationToken));
        Assert.Equal("Wrong PIN.", error.Message);
    }

    [Fact]
    public async Task SshSession()
    {
        ConnectionSettings? settings = SshSettings();
        Assert.SkipWhen(settings is null, "SCRIPT_RUNNER_TEST_SSH_* environment variables are not set");
        await using var connection = new SshRunnerConnection(settings!, new MemoryHostKeys());
        await RunSessionAsync(connection);
    }

    [Fact]
    public async Task SshWrongPasswordIsReported()
    {
        ConnectionSettings? settings = SshSettings();
        Assert.SkipWhen(settings is null, "SCRIPT_RUNNER_TEST_SSH_* environment variables are not set");
        await using var connection = new SshRunnerConnection(settings! with { Secret = settings.Secret + "x" }, new MemoryHostKeys());
        var error = await Assert.ThrowsAsync<RunnerException>(
            () => connection.StartAsync(new[] { Script("a.py", "print(1)") }, "a.py", TestContext.Current.CancellationToken));
        Assert.Contains("did not accept", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SshChangedHostKeyIsRefused()
    {
        ConnectionSettings? settings = SshSettings();
        Assert.SkipWhen(settings is null, "SCRIPT_RUNNER_TEST_SSH_* environment variables are not set");
        var keys = new MemoryHostKeys();
        keys.Remember(settings!.Host, settings.Port, "SHA256:not-the-real-key");
        await using var connection = new SshRunnerConnection(settings, keys);
        var error = await Assert.ThrowsAsync<RunnerException>(
            () => connection.StartAsync(new[] { Script("a.py", "print(1)") }, "a.py", TestContext.Current.CancellationToken));
        Assert.Contains("has changed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SshTimeLimitStopsTheScript()
    {
        ConnectionSettings? settings = SshSettings();
        Assert.SkipWhen(settings is null, "SCRIPT_RUNNER_TEST_SSH_* environment variables are not set");
        await using var connection = new SshRunnerConnection(settings! with { MaxSeconds = 3 }, new MemoryHostKeys());
        var log = new EventLog(connection);
        await connection.StartAsync(new[]
        {
            Script("loop.py", "import time\ntry:\n    while True:\n        time.sleep(0.2)\nfinally:\n    print('FINALLY ran')\n"),
        }, "loop.py", TestContext.Current.CancellationToken);
        ExitedEvent exited = await log.WaitForExitAsync(TimeSpan.FromSeconds(20));
        Assert.True(exited.Stopped);
        Assert.Contains("Time limit", log.Notices, StringComparison.Ordinal);
        Assert.Contains("FINALLY ran", log.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SshStopEndsAScriptThatDoesNotCatchCtrlC()
    {
        ConnectionSettings? settings = SshSettings();
        Assert.SkipWhen(settings is null, "SCRIPT_RUNNER_TEST_SSH_* environment variables are not set");
        await using var connection = new SshRunnerConnection(settings!, new MemoryHostKeys());
        await StopUncaughtAsync(connection);
    }

    [Fact]
    public async Task HttpStopEndsAScriptThatDoesNotCatchCtrlC()
    {
        string? python = FindPython();
        Assert.SkipWhen(python is null, "python3 is not installed");
        using var server = PiServer.Start(python!, TestPin);
        await using var connection = new HttpRunnerConnection(
            new ConnectionSettings(ConnectionMode.Http, "127.0.0.1", server.Port, "", TestPin));
        await StopUncaughtAsync(connection);
    }

    [Fact]
    public async Task SshStopEndsAScriptWithBackgroundThreadsWaitingForInput()
    {
        ConnectionSettings? settings = SshSettings();
        Assert.SkipWhen(settings is null, "SCRIPT_RUNNER_TEST_SSH_* environment variables are not set");
        await using var connection = new SshRunnerConnection(settings!, new MemoryHostKeys());
        await StopSdkLikeScriptAsync(connection);
    }

    [Fact]
    public async Task HttpStopEndsAScriptWithBackgroundThreadsWaitingForInput()
    {
        string? python = FindPython();
        Assert.SkipWhen(python is null, "python3 is not installed");
        using var server = PiServer.Start(python!, TestPin);
        await using var connection = new HttpRunnerConnection(
            new ConnectionSettings(ConnectionMode.Http, "127.0.0.1", server.Port, "", TestPin));
        await StopSdkLikeScriptAsync(connection);
    }

    /// <summary>
    /// Like the Sphero SDK: background (non-daemon) threads that never end, and a firmware warning that waits at
    /// input() while the robot object is being created, outside the script's try block.
    /// </summary>
    private static async Task StopSdkLikeScriptAsync(IRunnerConnection connection)
    {
        var log = new EventLog(connection);
        await connection.StartAsync(new[]
        {
            Script("sdk_like.py",
                "import threading, time\n" +
                "def serial_reader():\n    while True:\n        time.sleep(0.1)\n" +
                "threading.Thread(target=serial_reader).start()\n" +
                "print('Press any key to continue...', flush=True)\n" +
                "input()\n" +
                "try:\n    time.sleep(60)\nfinally:\n    print('finally ran')\n"),
        }, "sdk_like.py", TestContext.Current.CancellationToken);
        await log.WaitForOutputAsync("Press any key to continue...");
        await connection.StopAsync(TestContext.Current.CancellationToken);
        ExitedEvent exited = await log.WaitForExitAsync(TimeSpan.FromSeconds(15));
        Assert.True(exited.Stopped);
        Assert.False(connection.IsRunning);
    }

    [Fact]
    public async Task PiCommandReturnsOutputAndExitCode()
    {
        ConnectionSettings? settings = SshSettings();
        Assert.SkipWhen(settings is null, "SCRIPT_RUNNER_TEST_SSH_* environment variables are not set");
        CommandResult result = await PiCommand.RunAsync(settings!, new MemoryHostKeys(), "echo hello; exit 3",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("hello\n", result.Output);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task ForceStopEndsAScriptThatIgnoresCtrlC()
    {
        ConnectionSettings? settings = SshSettings();
        Assert.SkipWhen(settings is null, "SCRIPT_RUNNER_TEST_SSH_* environment variables are not set");
        var keys = new MemoryHostKeys();
        await using var connection = new SshRunnerConnection(settings!, keys);
        var log = new EventLog(connection);
        await connection.StartAsync(new[]
        {
            Script("stubborn.py", "import signal, time\nsignal.signal(signal.SIGINT, signal.SIG_IGN)\n" +
                                  "print('ignoring Ctrl+C', flush=True)\nwhile True:\n    time.sleep(0.2)\n"),
        }, "stubborn.py", TestContext.Current.CancellationToken);
        await log.WaitForOutputAsync("ignoring Ctrl+C");

        CommandResult result = await PiCommand.RunAsync(settings!, keys, PiCommand.ForceStopAllPython,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("No Python programs of yours are running.", result.Output, StringComparison.Ordinal);
        await log.WaitForExitAsync(TimeSpan.FromSeconds(10));
        Assert.False(connection.IsRunning);
    }

    /// <summary>Most beginners' scripts do not catch KeyboardInterrupt; Stop must still end the run cleanly.</summary>
    private static async Task StopUncaughtAsync(IRunnerConnection connection)
    {
        var log = new EventLog(connection);
        await connection.StartAsync(new[]
        {
            Script("plain.py", "import time\nprint('looping', flush=True)\nwhile True:\n    time.sleep(0.2)\n"),
        }, "plain.py", TestContext.Current.CancellationToken);
        await log.WaitForOutputAsync("looping");
        await connection.StopAsync(TestContext.Current.CancellationToken);
        ExitedEvent exited = await log.WaitForExitAsync(TimeSpan.FromSeconds(15));
        Assert.True(exited.Stopped);
        Assert.Contains("KeyboardInterrupt", log.Output, StringComparison.Ordinal);
        Assert.Contains("plain.py", log.Output, StringComparison.Ordinal);                 // the traceback names the script
        Assert.DoesNotContain("_script_runner_shim", log.Output, StringComparison.Ordinal); // but not the helper
        Assert.DoesNotContain("runpy", log.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("terminating", log.Notices, StringComparison.Ordinal);   // Ctrl+C alone was enough
    }

    [Fact]
    public async Task SshLostConnectionStillRunsFinally()
    {
        ConnectionSettings? settings = SshSettings();
        Assert.SkipWhen(settings is null, "SCRIPT_RUNNER_TEST_SSH_* environment variables are not set");
        var connection = new SshRunnerConnection(settings!, new MemoryHostKeys());
        var log = new EventLog(connection);
        await connection.StartAsync(new[]
        {
            Script("loop.py",
                "import time\ntry:\n    print('looping', flush=True)\n    while True:\n        time.sleep(0.2)\n" +
                "finally:\n    open('finally.txt', 'w').write('motors stopped')\n"),
        }, "loop.py", TestContext.Current.CancellationToken);
        await log.WaitForOutputAsync("looping");

        await connection.DisposeAsync();                 // the connection drops without pressing Stop
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        using var check = new Renci.SshNet.SshClient(settings!.Host, settings.Port, settings.User, settings.Secret);
        check.HostKeyReceived += (_, e) => e.CanTrust = true;
        check.Connect();
        using Renci.SshNet.SshCommand result = check.RunCommand("cat ~/script_runner_ssh/run/finally.txt");
        Assert.Equal("motors stopped", result.Result);
    }

    /// <summary>Prompt, input, say/show, file download, Stop delivering Ctrl+C, and the finally: block.</summary>
    private static async Task RunSessionAsync(IRunnerConnection connection)
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        var log = new EventLog(connection);
        byte[] dot = File.ReadAllBytes(Path.Combine(RepoRoot(), "tests", "data", "dot.png"));
        string tmpCopy = $"/tmp/script_runner_test_{Guid.NewGuid():N}.png";      // play() and show() also accept /tmp
        string script = SessionScript.Replace("TMP_COPY", tmpCopy, StringComparison.Ordinal);
        await connection.StartAsync(new[] { Script("square.py", script), new RunFile("dot.png", dot) }, "square.py", cancel);

        await log.WaitForOutputAsync("Enter speed: ");
        await connection.SendInputAsync("30", cancel);
        await log.WaitForOutputAsync("speed is 30");
        await log.WaitForOutputAsync("@@runner show dot.png");
        Assert.Contains("@@runner say Starting the square", log.Output, StringComparison.Ordinal);
        Assert.Equal(dot, await connection.GetFileAsync("dot.png", cancel));
        await log.WaitForOutputAsync("@@runner show " + tmpCopy);
        Assert.Equal(dot, await connection.GetFileAsync(tmpCopy, cancel));
        await log.WaitForOutputAsync("side 1");

        await connection.StopAsync(cancel);
        ExitedEvent exited = await log.WaitForExitAsync(TimeSpan.FromSeconds(15));
        Assert.True(exited.Stopped);
        Assert.Contains("User ended by pressing Ctrl+C", log.Output, StringComparison.Ordinal);
        Assert.Contains("FINALLY: cleanup ran", log.Output, StringComparison.Ordinal);
        Assert.False(connection.IsRunning);
    }

    private static RunFile Script(string name, string text) => new(name, Encoding.UTF8.GetBytes(text));

    private static ConnectionSettings? SshSettings()
    {
        string? host = Environment.GetEnvironmentVariable("SCRIPT_RUNNER_TEST_SSH_HOST");
        string? password = Environment.GetEnvironmentVariable("SCRIPT_RUNNER_TEST_SSH_PASSWORD");
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(password))
        {
            return null;
        }
        int port = int.TryParse(Environment.GetEnvironmentVariable("SCRIPT_RUNNER_TEST_SSH_PORT"), out int p) ? p : 22;
        string user = Environment.GetEnvironmentVariable("SCRIPT_RUNNER_TEST_SSH_USER") ?? ConnectionSettings.DefaultUser;
        return new ConnectionSettings(ConnectionMode.Ssh, host, port, user, password);
    }

    private static string? FindPython()
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string candidate = Path.Combine(dir, "python3");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    internal static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "runner", "script_runner.py")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("Cannot find the repository root.");
    }

    private sealed class MemoryHostKeys : IHostKeyStore
    {
        private readonly ConcurrentDictionary<string, string> keys = new();

        public string? Find(string host, int port) => keys.TryGetValue($"{host}:{port}", out string? key) ? key : null;

        public void Remember(string host, int port, string fingerprint) => keys[$"{host}:{port}"] = fingerprint;
    }

    /// <summary>Collects a connection's events for the assertions.</summary>
    private sealed class EventLog
    {
        private readonly object gate = new();
        private readonly StringBuilder output = new();
        private readonly StringBuilder notices = new();
        private readonly TaskCompletionSource<ExitedEvent> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public EventLog(IRunnerConnection connection) => connection.EventReceived += Add;

        public string Output { get { lock (gate) { return output.ToString(); } } }

        public string Notices { get { lock (gate) { return notices.ToString(); } } }

        public async Task WaitForOutputAsync(string text)
        {
            var watch = Stopwatch.StartNew();
            while (!Output.Contains(text, StringComparison.Ordinal))
            {
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"Timed out waiting for \"{text}\". Output:\n{Output}");
                await Task.Delay(50);
            }
        }

        public async Task<ExitedEvent> WaitForExitAsync(TimeSpan limit) => await exited.Task.WaitAsync(limit);

        private void Add(RunnerEvent runnerEvent)
        {
            lock (gate)
            {
                switch (runnerEvent)
                {
                    case OutputEvent o: output.Append(o.Text); break;
                    case EchoEvent e: output.Append(e.Text); break;
                    case NoticeEvent n: notices.AppendLine(n.Text); break;
                    case ExitedEvent x: exited.TrySetResult(x); break;
                }
            }
        }
    }

    /// <summary>runner/script_runner.py on a free port, with a temporary home folder.</summary>
    private sealed class PiServer : IDisposable
    {
        private readonly Process process;
        private readonly string home;

        private PiServer(Process process, string home, int port)
        {
            this.process = process;
            this.home = home;
            Port = port;
        }

        public int Port { get; }

        public static PiServer Start(string python, string pin)
        {
            int port;
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            string home = Directory.CreateTempSubdirectory("script-runner-home").FullName;
            var start = new ProcessStartInfo(python)
            {
                ArgumentList = { Path.Combine(RepoRoot(), "runner", "script_runner.py"), "--pin", pin, "--port", port.ToString() },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.Environment["HOME"] = home;
            start.Environment["SCRIPT_RUNNER_LOCK_FILE"] = Path.Combine(home, "run.lock");   // not shared with other tests
            Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start python3");
            var server = new PiServer(process, home, port);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                try
                {
                    using var client = new TcpClient();
                    client.Connect(IPAddress.Loopback, port);
                    return server;
                }
                catch (SocketException)
                {
                    Thread.Sleep(100);
                }
            }
            server.Dispose();
            throw new InvalidOperationException("The Pi server did not start.");
        }

        public void Dispose()
        {
            try
            {
                process.Kill(true);
                process.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }
            process.Dispose();
            Directory.Delete(home, true);
        }
    }
}
