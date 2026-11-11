using Renci.SshNet;
using Renci.SshNet.Common;

namespace ScriptRunner.Core;

/// <summary>The result of a command run on the Pi.</summary>
public sealed record CommandResult(int? ExitCode, string Output);

/// <summary>
/// Runs single commands on the Pi over SSH, each on its own short-lived connection. Because these never share the
/// connection a script runs on, they still work when that connection is busy or stuck (for example, to stop
/// everything).
/// </summary>
public static class PiCommand
{
    /// <summary>Longest a single command may run before the app gives up waiting for it.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Sends Ctrl+C to every Python program this account started by name (scripts from this app, and scripts piped
    /// in with "python3 -"), waits 2 seconds so their finally: blocks can run (for example, to stop motors), then
    /// force-quits any that remain, and lists what is still running. The runner server, which systemd starts as
    /// /usr/bin/python3, is not affected.
    /// </summary>
    public const string ForceStopAllPython =
        "me=$(id -u); " +
        "pkill -INT -u \"$me\" -f '^python3( |$)'; sleep 2; " +
        "pkill -KILL -u \"$me\" -f '^python3( |$)'; sleep 0.5; " +
        "pgrep -a -u \"$me\" -f '^python3( |$)' || echo 'No Python programs of yours are running.'";

    public static async Task<CommandResult> RunAsync(ConnectionSettings settings, IHostKeyStore hostKeys, string command,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var checker = new HostKeyChecker(settings, hostKeys);
        using var client = new SshClient(SshSupport.CreateConnectionInfo(settings));
        client.HostKeyReceived += checker.Check;
        await SshSupport.ConnectAsync(client, settings, checker, cancellationToken).ConfigureAwait(false);
        using SshCommand run = client.CreateCommand(command);
        run.CommandTimeout = timeout ?? DefaultTimeout;
        try
        {
            await run.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SshOperationTimeoutException error)
        {
            throw new RunnerException($"The command did not finish within {(int)run.CommandTimeout.TotalSeconds} s.", error);
        }
        catch (SshException error)
        {
            throw new RunnerException("The command could not be run: " + error.Message, error);
        }
        return new CommandResult(run.ExitStatus, run.Result + run.Error);
    }
}

/// <summary>Trust on first use: remembers each Pi's host key and refuses a changed one.</summary>
internal sealed class HostKeyChecker
{
    private readonly ConnectionSettings settings;
    private readonly IHostKeyStore hostKeys;

    public HostKeyChecker(ConnectionSettings settings, IHostKeyStore hostKeys)
    {
        this.settings = settings;
        this.hostKeys = hostKeys;
    }

    /// <summary>Set when a connection was refused because the host key changed.</summary>
    public string? Problem { get; private set; }

    public void Check(object? sender, HostKeyEventArgs e)
    {
        string fingerprint = "SHA256:" + e.FingerPrintSHA256;
        string? saved = hostKeys.Find(settings.Host, settings.Port);
        if (saved is null)
        {
            hostKeys.Remember(settings.Host, settings.Port, fingerprint);
            e.CanTrust = true;
        }
        else if (saved == fingerprint)
        {
            e.CanTrust = true;
        }
        else
        {
            e.CanTrust = false;
            Problem =
                $"The SSH identity (host key) of {settings.Host} has changed since this app last connected. " +
                "This is expected if the Pi's system was reinstalled; otherwise another computer may be using " +
                "that address. To trust the new identity, use \"Forget saved SSH key\" and connect again.";
        }
    }
}

/// <summary>Connection details and error messages shared by every SSH connection the app makes.</summary>
internal static class SshSupport
{
    public static ConnectionInfo CreateConnectionInfo(ConnectionSettings settings)
    {
        var keyboard = new KeyboardInteractiveAuthenticationMethod(settings.User);
        keyboard.AuthenticationPrompt += (_, prompt) =>
        {
            foreach (AuthenticationPrompt question in prompt.Prompts)
            {
                question.Response = settings.Secret;
            }
        };
        return new ConnectionInfo(settings.Host, settings.Port, settings.User,
            new PasswordAuthenticationMethod(settings.User, settings.Secret), keyboard)
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    /// <summary>Connects, turning failures into messages for the user.</summary>
    public static async Task ConnectAsync(BaseClient client, ConnectionSettings settings, HostKeyChecker checker,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SshAuthenticationException error)
        {
            throw new RunnerException($"The Pi did not accept the user name or password for {settings.User}.", error);
        }
        catch (Exception error) when (error is SshException or System.Net.Sockets.SocketException or TimeoutException)
        {
            throw new RunnerException(checker.Problem ?? $"Cannot connect to {settings.Host}: {error.Message}", error);
        }
    }
}
