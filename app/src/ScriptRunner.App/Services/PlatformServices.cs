namespace ScriptRunner.App.Services;

/// <summary>Speaks text for runner_io.say(). Calls are queued and spoken in order.</summary>
public interface ISpeechService
{
    void Speak(string text);

    void StopSpeaking();
}

/// <summary>Plays a sound file for runner_io.play().</summary>
public interface IAudioService
{
    /// <param name="fileName">The file's name, which gives its type (.wav, .mp3, .ogg).</param>
    Task PlayAsync(string fileName, byte[] data);
}

/// <summary>
/// Keeps PINs and passwords in the operating system's secure store (Windows Credential Manager, macOS Keychain,
/// Android Keystore). Secrets are never written to the settings file.
/// </summary>
public interface ISecretStore
{
    /// <summary>False where no secure store is available; the app then never offers to remember a secret.</summary>
    bool IsAvailable { get; }

    string? Load(string key);

    /// <returns>False if the secret could not be saved.</returns>
    bool Save(string key, string secret);

    void Delete(string key);
}

/// <summary>What each platform's app (desktop or Android) provides to the shared user interface.</summary>
public sealed record PlatformServices(ISpeechService Speech, IAudioService Audio, ISecretStore Secrets, string SettingsFolder);

/// <summary>Used where a platform has no implementation: does nothing.</summary>
public sealed class NoSpeech : ISpeechService
{
    public void Speak(string text) { }

    public void StopSpeaking() { }
}

/// <summary>Used where a platform has no secure store: remembers nothing.</summary>
public sealed class NoSecretStore : ISecretStore
{
    public bool IsAvailable => false;

    public string? Load(string key) => null;

    public bool Save(string key, string secret) => false;

    public void Delete(string key) { }
}
