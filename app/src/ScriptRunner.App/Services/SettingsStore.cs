using System.Text.Json;
using System.Text.Json.Nodes;
using ScriptRunner.Core;

namespace ScriptRunner.App.Services;

/// <summary>
/// The app's saved settings (settings.json in the app's data folder): the last connection used and the SSH host
/// key fingerprints seen so far. PINs and passwords are never saved here; see <see cref="ISecretStore"/>.
/// </summary>
public sealed class SettingsStore : IHostKeyStore
{
    private readonly string path;
    private readonly object gate = new();
    private JsonObject data;

    public SettingsStore(string folder)
    {
        Directory.CreateDirectory(folder);
        path = Path.Combine(folder, "settings.json");
        data = Read(path);
    }

    public string Get(string name, string fallback)
    {
        lock (gate)
        {
            return data[name] is JsonValue value && value.TryGetValue(out string? text) ? text : fallback;
        }
    }

    public bool GetBool(string name, bool fallback)
    {
        lock (gate)
        {
            return data[name] is JsonValue value && value.TryGetValue(out bool flag) ? flag : fallback;
        }
    }

    public void Set(string name, string value) => Update(() => data[name] = value);

    public void SetBool(string name, bool value) => Update(() => data[name] = value);

    public string? Find(string host, int port)
    {
        lock (gate)
        {
            return data["hostKeys"]?[$"{host}:{port}"]?.GetValue<string>();
        }
    }

    public void Remember(string host, int port, string fingerprint) =>
        Update(() => HostKeys()[$"{host}:{port}"] = fingerprint);

    public void Forget(string host, int port) => Update(() => HostKeys().Remove($"{host}:{port}"));

    private JsonObject HostKeys()
    {
        if (data["hostKeys"] is not JsonObject keys)
        {
            keys = new JsonObject();
            data["hostKeys"] = keys;
        }
        return keys;
    }

    private void Update(Action change)
    {
        lock (gate)
        {
            change();
            try
            {
                File.WriteAllText(path, data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (IOException)
            {
                // Settings are a convenience; failing to save them must not stop the app.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static JsonObject Read(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return new JsonObject();
        }
    }
}
