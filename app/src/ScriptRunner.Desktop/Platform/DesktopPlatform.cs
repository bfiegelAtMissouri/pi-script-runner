using ScriptRunner.App.Services;

namespace ScriptRunner.Desktop.Platform;

/// <summary>Chooses the speech, sound, and secure-storage implementations for the operating system.</summary>
internal static class DesktopPlatform
{
    public static PlatformServices Create()
    {
        string settings = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScriptRunner");
        if (OperatingSystem.IsWindows())
        {
            return new PlatformServices(new WindowsSpeech(), new WindowsAudio(), new WindowsCredentialStore(), settings);
        }
        if (OperatingSystem.IsMacOS())
        {
            return new PlatformServices(
                new CommandSpeech("/usr/bin/say", text => Array.Empty<string>(), textOnStandardInput: true),
                new CommandAudio("/usr/bin/afplay"), new MacKeychainStore(), settings);
        }
        // Linux: speech and sound when the usual command-line tools are installed; no secure store.
        string? spdSay = FindCommand("spd-say");
        CommandSpeech speech = spdSay is not null
            ? new CommandSpeech(spdSay, text => new[] { "--wait", text })
            : new CommandSpeech(FindCommand("espeak") ?? "", text => new[] { text });
        return new PlatformServices(
            speech,
            new CommandAudio(FindCommand("paplay") ?? FindCommand("aplay") ?? ""),
            new NoSecretStore(), settings);
    }

    private static string? FindCommand(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, name))
            .FirstOrDefault(File.Exists);
}
