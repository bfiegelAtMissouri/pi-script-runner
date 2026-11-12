using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace ScriptRunner.Core;

/// <summary>
/// The user's local files, loaded in a Python process on this computer, whose functions the robot's script calls
/// with runner_io.remote_execute(). One instance serves one run. Calls must be made one at a time.
/// </summary>
public sealed class LocalFunctions : IAsyncDisposable
{
    private const string HostName = "_local_host.py";

    private readonly Process process;
    private readonly string folder;
    private readonly TimeSpan callTimeout;
    private bool stopped;

    private LocalFunctions(Process process, string folder, TimeSpan callTimeout, IReadOnlyList<string> functions)
    {
        this.process = process;
        this.folder = folder;
        this.callTimeout = callTimeout;
        Functions = functions;
        DownloadsFolder = Directory.CreateDirectory(Path.Combine(folder, "_from_robot")).FullName;
    }

    /// <summary>Names of the functions the robot may call.</summary>
    public IReadOnlyList<string> Functions { get; }

    /// <summary>Where files sent with shared_runner_file() are saved for the functions to open.</summary>
    public string DownloadsFolder { get; }

    /// <summary>
    /// Writes the local files to a new temporary folder, starts Python there, and imports them. Throws
    /// <see cref="RunnerException"/> with a message for the user if Python cannot be found or a file cannot be loaded.
    /// Text the functions print is passed to <paramref name="printed"/>, one line at a time, from another thread.
    /// </summary>
    public static async Task<LocalFunctions> StartAsync(LocalPython python, IReadOnlyList<RunFile> files,
        Action<string> printed, TimeSpan loadTimeout, TimeSpan callTimeout, CancellationToken cancellationToken = default)
    {
        var modules = new List<string>();
        foreach (RunFile file in files.Where(f => f.Name.EndsWith(".py", StringComparison.OrdinalIgnoreCase)))
        {
            string module = Path.GetFileNameWithoutExtension(file.Name);
            if (!IsModuleName(module))
            {
                throw new RunnerException($"The local file {file.Name} needs a name Python can import: letters, " +
                                          "digits, and _ only, not starting with a digit.");
            }
            modules.Add(module);
        }

        string folder = Directory.CreateTempSubdirectory("script-runner-local-").FullName;
        foreach (RunFile file in files)
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, Path.GetFileName(file.Name)), file.Data, cancellationToken)
                .ConfigureAwait(false);
        }
        await File.WriteAllBytesAsync(Path.Combine(folder, HostName), ReadHost(), cancellationToken).ConfigureAwait(false);

        var start = new ProcessStartInfo(python.FileName)
        {
            WorkingDirectory = folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (string argument in python.Arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(HostName);
        foreach (string module in modules)
        {
            start.ArgumentList.Add(module);
        }
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new RunnerException("Python did not start.");
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            DeleteFolder(folder);
            throw new RunnerException($"Could not start Python ({python}): {error.Message}. " +
                                      "Install Python, or choose it under Settings, Local Python.", error);
        }

        var errorText = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }
            lock (errorText)
            {
                errorText.AppendLine(e.Data);
            }
            printed(e.Data);
        };
        process.BeginErrorReadLine();

        string? ready;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            limit.CancelAfter(loadTimeout);
            try
            {
                ready = await process.StandardOutput.ReadLineAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Stop(process, folder);
                throw new RunnerException($"The local files did not finish loading within {(int)loadTimeout.TotalSeconds} s " +
                                          "(see Settings, Local load time limit).");
            }
        }

        JsonNode? message = ready is null ? null : TryParse(ready);
        if (message?["ready"]?.GetValue<bool>() is true)
        {
            var functions = message["functions"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
            return new LocalFunctions(process, folder, callTimeout, functions);
        }

        Stop(process, folder);
        string reason = message?["error"]?.GetValue<string>()
            ?? $"Python ({python}) stopped before loading the local files.\n{errorText}".TrimEnd();
        throw new RunnerException(reason);
    }

    /// <summary>
    /// Calls a function and returns its reply: {"id", "ok": true, "value"} or {"id", "ok": false, "error"}. Never
    /// throws for the function's own failures; those come back as a reply the robot's script raises as an error.
    /// </summary>
    public async Task<JsonObject> CallAsync(int id, string function, JsonArray args, CancellationToken cancellationToken = default)
    {
        if (stopped || process.HasExited)
        {
            return Failure(id, "The local functions have stopped. Run the script again to restart them.");
        }
        var request = new JsonObject { ["id"] = id, ["function"] = function, ["args"] = args };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(callTimeout);
        try
        {
            await process.StandardInput.WriteLineAsync(request.ToJsonString().AsMemory(), limit.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(limit.Token).ConfigureAwait(false);
            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync(limit.Token).ConfigureAwait(false);
                if (line is null)
                {
                    stopped = true;
                    return Failure(id, $"Python on the laptop stopped while running {function}.");
                }
                if (TryParse(line) is JsonObject reply && reply["id"]?.GetValue<int>() == id)
                {
                    return reply;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A late answer would be mistaken for the next call's, so the functions are stopped for this run.
            stopped = true;
            Stop(process, null);
            return Failure(id, $"{function} did not finish within {(int)callTimeout.TotalSeconds} s on the laptop " +
                               "(see Settings, Local call time limit). The local functions are stopped for this run.");
        }
        catch (IOException error)
        {
            stopped = true;
            return Failure(id, $"Python on the laptop stopped while running {function}: {error.Message}");
        }
    }

    public static JsonObject Failure(int id, string error) => new() { ["id"] = id, ["ok"] = false, ["error"] = error };

    public ValueTask DisposeAsync()
    {
        stopped = true;
        Stop(process, folder);
        return ValueTask.CompletedTask;
    }

    private static bool IsModuleName(string name) =>
        0 < name.Length && !char.IsDigit(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || '_' == c);

    private static JsonNode? TryParse(string line)
    {
        try
        {
            return JsonNode.Parse(line);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static void Stop(Process process, string? folder)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // already gone
        }
        if (folder is not null)
        {
            DeleteFolder(folder);
        }
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // a file may still be open while Python exits; the temporary folder is cleaned up by the system later
        }
    }

    private static byte[] ReadHost()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(HostName)
            ?? throw new InvalidOperationException("Missing embedded resource " + HostName);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}

/// <summary>How to start Python on this computer: a program and the arguments that come before the script.</summary>
public sealed record LocalPython(string FileName, IReadOnlyList<string> Arguments)
{
    public override string ToString() => string.Join(" ", new[] { FileName }.Concat(Arguments));

    /// <summary>
    /// The Python to use: <paramref name="chosen"/> if set (a path or a command name), otherwise the first found of
    /// the Windows launcher "py -3", python3, and python, looking also in the usual install folders, which an app
    /// started from the macOS Finder does not have on its PATH. Null if none is found.
    /// </summary>
    public static LocalPython? Find(string? chosen)
    {
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            return new LocalPython(chosen.Trim(), Array.Empty<string>());
        }
        if (OperatingSystem.IsWindows() && FindOnPath("py.exe") is { } launcher)
        {
            return new LocalPython(launcher, new[] { "-3" });
        }
        foreach (string name in OperatingSystem.IsWindows() ? new[] { "python.exe" } : new[] { "python3", "python" })
        {
            if (FindOnPath(name) is { } found)
            {
                return new LocalPython(found, Array.Empty<string>());
            }
        }
        return null;
    }

    private static string? FindOnPath(string name)
    {
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).ToList();
        if (OperatingSystem.IsMacOS())
        {
            folders.AddRange(new[] { "/opt/homebrew/bin", "/usr/local/bin", "/Library/Frameworks/Python.framework/Versions/Current/bin" });
        }
        foreach (string folder in folders.Where(f => 0 < f.Length))
        {
            string candidate = Path.Combine(folder, name);
            // The Microsoft Store's python.exe placeholder (in WindowsApps) only offers to install Python.
            if (File.Exists(candidate) && !candidate.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }
        return null;
    }
}
