using System.Text.Json.Nodes;
using ScriptRunner.Core;

namespace ScriptRunner.App.ViewModels;

/// <summary>
/// Laptop functions: .py files marked Laptop are imported on this computer when a run starts, and the robot's script
/// calls their functions with runner_io.remote_execute(). Calls are answered one at a time, in order.
/// </summary>
public sealed partial class MainViewModel
{
    private const string SharedFileMarker = "__shared_runner_file__";

    private LaptopFunctions? laptop;
    private Task callQueue = Task.CompletedTask;
    private int pendingCalls;
    private readonly List<string> heldInput = new();

    /// <summary>Starts the Laptop files for this run, or does nothing when there are none.</summary>
    private async Task StartLaptopFunctionsAsync(IReadOnlyList<RunFile> laptopFiles)
    {
        await StopLaptopFunctionsAsync();
        if (0 == laptopFiles.Count)
        {
            return;
        }
        if (OperatingSystem.IsAndroid())
        {
            throw new RunnerException("Laptop files need Python, which this device does not have. Uncheck Laptop, " +
                                      "or run from a Windows, macOS, or Linux computer.");
        }
        LaptopPython python = LaptopPython.Find(LaptopPythonText)
            ?? throw new RunnerException("Laptop files need Python on this computer, and none was found. Install " +
                                         "Python, or choose it under Settings, Laptop Python.");
        StatusText = "Loading Laptop files…";
        Append($"[Loading Laptop files with {python}]\n");
        ShowOutput();
        laptop = await LaptopFunctions.StartAsync(python, laptopFiles,
            printed => ui.Post(() => { Append("[laptop] " + printed + "\n"); ShowOutput(); }),
            TimeSpan.FromSeconds(laptopLoadSeconds), TimeSpan.FromSeconds(laptopCallSeconds));
        Append("[Laptop functions: " + (0 < laptop.Functions.Count ? string.Join(", ", laptop.Functions) : "none") + "]\n");
        ShowOutput();
    }

    private async Task StopLaptopFunctionsAsync()
    {
        LaptopFunctions? stopping = laptop;
        laptop = null;
        if (stopping is not null)
        {
            await stopping.DisposeAsync();
        }
    }

    /// <summary>Answers a remote_execute() request after any earlier ones, holding typed input until then.</summary>
    private void QueueCall(IRunnerConnection source, string requestJson)
    {
        ++pendingCalls;
        callQueue = AnswerCallAfterAsync(callQueue, source, laptop, requestJson);
    }

    private async Task AnswerCallAfterAsync(Task previous, IRunnerConnection source, LaptopFunctions? functions,
        string requestJson)
    {
        await previous;                 // never throws: every failure below becomes an answer to the script
        int id = 0;
        JsonObject reply;
        try
        {
            JsonObject request = JsonNode.Parse(requestJson)!.AsObject();
            id = request["id"]!.GetValue<int>();
            string function = request["function"]!.GetValue<string>();
            Append("💻 " + function + "\n");
            ShowOutput();
            if (functions is null)
            {
                reply = LaptopFunctions.Failure(id, $"No Laptop files are loaded, so {function} cannot run. Add the " +
                                                    "file that defines it and check Laptop beside it.");
            }
            else
            {
                JsonArray args = JsonNode.Parse(request["args"]?.ToJsonString() ?? "[]")!.AsArray();
                args = (JsonArray)(await ShareRunnerFilesAsync(source, functions, args))!;
                reply = await functions.CallAsync(id, function, args);
            }
        }
        catch (Exception error)
        {
            reply = LaptopFunctions.Failure(id, "The laptop call could not be completed: " + error.Message);
        }

        try
        {
            await source.SendReplyAsync(reply.ToJsonString());
        }
        catch (RunnerException error)
        {
            Append($"[Could not send the laptop's answer: {error.Message}]\n");
            ShowOutput();
        }
        finally
        {
            --pendingCalls;
            if (0 == pendingCalls)
            {
                await SendHeldInputAsync(source);
            }
        }
    }

    /// <summary>
    /// Replaces each shared_runner_file(...) marker in the arguments with the path of a laptop copy of that file,
    /// downloaded from the Pi.
    /// </summary>
    private async Task<JsonNode?> ShareRunnerFilesAsync(IRunnerConnection source, LaptopFunctions functions, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject marker when 1 == marker.Count && marker[SharedFileMarker] is JsonValue pathValue:
            {
                string piPath = pathValue.GetValue<string>();
                byte[] data = await source.GetFileAsync(piPath, LargestFileBytes);
                string local = UnusedPath(functions.DownloadsFolder, Path.GetFileName(piPath.Replace('\\', '/')));
                await File.WriteAllBytesAsync(local, data);
                return JsonValue.Create(local);
            }
            case JsonObject dictionary:
            {
                var copy = new JsonObject();
                foreach ((string key, JsonNode? value) in dictionary.ToList())
                {
                    copy[key] = await ShareRunnerFilesAsync(source, functions, value?.DeepClone());
                }
                return copy;
            }
            case JsonArray list:
            {
                var copy = new JsonArray();
                foreach (JsonNode? item in list.ToList())
                {
                    copy.Add(await ShareRunnerFilesAsync(source, functions, item?.DeepClone()));
                }
                return copy;
            }
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>A path in the folder for the name, adding -2, -3, and so on when that name is taken.</summary>
    private static string UnusedPath(string folder, string name)
    {
        string path = Path.Combine(folder, name);
        for (int number = 2; File.Exists(path); ++number)
        {
            path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)}-{number}{Path.GetExtension(name)}");
        }
        return path;
    }

    private async Task SendHeldInputAsync(IRunnerConnection source)
    {
        List<string> lines = heldInput.ToList();
        heldInput.Clear();
        foreach (string line in lines)
        {
            try
            {
                await source.SendInputAsync(line);
            }
            catch (RunnerException error)
            {
                ErrorText = error.Message;
                return;
            }
        }
    }
}
