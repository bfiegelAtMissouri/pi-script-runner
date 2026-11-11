using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScriptRunner.Core;

/// <summary>
/// Runs scripts through the runner server installed on the Pi (runner/script_runner.py), using its HTTP API
/// (docs/http-api.md). Output arrives as Server-Sent Events; a dropped stream reconnects and resumes.
/// </summary>
public sealed class HttpRunnerConnection : IRunnerConnection
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);

    private readonly HttpClient http;
    private readonly CancellationTokenSource lifetime = new();
    private string runId = "";
    private int nextSeq;
    private volatile bool running;
    private bool stopRequested;

    public HttpRunnerConnection(ConnectionSettings settings, HttpMessageHandler? handler = null)
    {
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = new Uri($"http://{FormatHost(settings.Host)}:{settings.Port}/");
        http.Timeout = Timeout.InfiniteTimeSpan;           // the event stream stays open for the whole run
        http.DefaultRequestHeaders.Add("X-PIN", settings.Secret);   // sent as a header, never in a URL
    }

    public event Action<RunnerEvent>? EventReceived;

    public bool IsRunning => running;

    public async Task StartAsync(IReadOnlyList<RunFile> files, string mainFile, CancellationToken cancellationToken = default)
    {
        var fileArray = new JsonArray();
        foreach (RunFile file in files)
        {
            fileArray.Add((JsonNode)new JsonObject
            {
                ["name"] = file.Name,
                ["data"] = Convert.ToBase64String(file.Data),
            });
        }
        var body = new JsonObject { ["files"] = fileArray, ["main"] = mainFile };
        JsonNode reply = await PostAsync("api/run", body, cancellationToken).ConfigureAwait(false);

        runId = reply["run"]?.GetValue<string>() ?? throw new RunnerException("The Pi gave an unexpected reply.");
        nextSeq = 0;
        stopRequested = false;
        running = true;
        _ = Task.Run(() => StreamEventsAsync(runId, lifetime.Token));
    }

    public async Task SendInputAsync(string line, CancellationToken cancellationToken = default)
    {
        await PostAsync("api/input", new JsonObject { ["text"] = line }, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        stopRequested = true;
        await PostAsync("api/stop", new JsonObject(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> GetFileAsync(string name, CancellationToken cancellationToken = default)
    {
        string url = "api/media?name=" + Uri.EscapeDataString(name);
        try
        {
            using HttpResponseMessage response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new RunnerException($"Could not get {name} from the Pi.");
            }
            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            throw new RunnerException($"Could not get {name} from the Pi: {error.Message}", error);
        }
    }

    public ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        http.Dispose();
        lifetime.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<JsonNode> PostAsync(string path, JsonObject body, CancellationToken cancellationToken)
    {
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(path, content, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            throw new RunnerException($"Cannot reach the Pi at {http.BaseAddress}: {error.Message}", error);
        }
        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonNode? reply = null;
            try
            {
                reply = JsonNode.Parse(string.IsNullOrEmpty(text) ? "{}" : text);
            }
            catch (JsonException)
            {
                // reported below
            }
            if (response.IsSuccessStatusCode && reply is not null)
            {
                return reply;
            }
            string? message = reply?["error"]?.GetValue<string>();
            if (HttpStatusCode.Forbidden == response.StatusCode)
            {
                message = "Wrong PIN.";
            }
            throw new RunnerException(message ?? $"The Pi replied with an error ({(int)response.StatusCode}).");
        }
    }

    private async Task StreamEventsAsync(string run, CancellationToken cancellationToken)
    {
        while (running && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                string url = "api/events?run=" + Uri.EscapeDataString(run) + "&since=" + nextSeq;
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                using HttpResponseMessage response = await http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? line;
                while (null != (line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)))
                {
                    if (line.StartsWith("data: ", StringComparison.Ordinal) && HandleEvent(line[6..]))
                    {
                        return;                                  // the script exited
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error) when (error is HttpRequestException or IOException)
            {
                // The stream dropped; reconnect below and resume from nextSeq.
            }
            if (running)
            {
                try
                {
                    await Task.Delay(ReconnectDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Handles one event from the stream. Returns true when the run has ended.</summary>
    private bool HandleEvent(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string type = root.GetProperty("type").GetString() ?? "";
        if ("hello" == type)
        {
            return false;
        }
        int seq = root.GetProperty("seq").GetInt32();
        if (seq < nextSeq)
        {
            return false;                                        // already seen before a reconnect
        }
        nextSeq = seq + 1;
        string text = root.TryGetProperty("text", out JsonElement t) ? t.GetString() ?? "" : "";
        switch (type)
        {
            case "out":
                Raise(new OutputEvent(text));
                return false;
            case "echo":
                Raise(new EchoEvent(text));
                return false;
            case "notice":
                Raise(new NoticeEvent(text));
                return false;
            case "status":
                string state = root.GetProperty("state").GetString() ?? "";
                if ("running" == state)
                {
                    Raise(new StartedEvent(root.TryGetProperty("file", out JsonElement f) ? f.GetString() ?? "" : ""));
                    return false;
                }
                int? code = root.TryGetProperty("code", out JsonElement c) && JsonValueKind.Number == c.ValueKind
                    ? c.GetInt32() : null;
                bool stopped = stopRequested ||
                    (root.TryGetProperty("reason", out JsonElement r) && "stopped" == r.GetString());
                running = false;
                Raise(new ExitedEvent(code, stopped, 0 < text.Length ? text : null));
                return true;
            default:
                return false;
        }
    }

    private void Raise(RunnerEvent runnerEvent) => EventReceived?.Invoke(runnerEvent);

    private static string FormatHost(string host) =>
        host.Contains(':') && !host.StartsWith('[') ? "[" + host + "]" : host;     // IPv6 literal
}
