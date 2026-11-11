using System.Diagnostics;
using System.Threading.Channels;
using ScriptRunner.App.Services;

namespace ScriptRunner.Desktop.Platform;

/// <summary>Speech through a command-line program (macOS "say", Linux "spd-say"), one message at a time.</summary>
internal sealed class CommandSpeech : ISpeechService
{
    private readonly string command;
    private readonly Func<string, string[]> arguments;
    private readonly bool textOnStandardInput;
    private readonly Channel<string> queue = Channel.CreateUnbounded<string>();
    private Process? current;
    private int generation;

    public CommandSpeech(string command, Func<string, string[]> arguments, bool textOnStandardInput = false)
    {
        this.command = command;
        this.arguments = arguments;
        this.textOnStandardInput = textOnStandardInput;
        if (0 < command.Length)
        {
            _ = Task.Run(SpeakQueuedAsync);
        }
    }

    public void Speak(string text) => queue.Writer.TryWrite(generation + "\n" + text);

    public void StopSpeaking()
    {
        Interlocked.Increment(ref generation);           // queued messages from before now are skipped
        try
        {
            current?.Kill();
        }
        catch (InvalidOperationException)
        {
            // already finished
        }
    }

    private async Task SpeakQueuedAsync()
    {
        await foreach (string item in queue.Reader.ReadAllAsync())
        {
            int newline = item.IndexOf('\n');
            if (int.Parse(item[..newline]) != generation)
            {
                continue;
            }
            string text = item[(newline + 1)..];
            var start = new ProcessStartInfo(command) { RedirectStandardInput = textOnStandardInput };
            foreach (string argument in arguments(text))
            {
                start.ArgumentList.Add(argument);        // passed as separate arguments, never through a shell
            }
            try
            {
                using Process process = Process.Start(start)!;
                current = process;
                if (textOnStandardInput)
                {
                    await process.StandardInput.WriteAsync(text);
                    process.StandardInput.Close();
                }
                await process.WaitForExitAsync();
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                // speech is a convenience; keep going
            }
            finally
            {
                current = null;
            }
        }
    }
}

/// <summary>Sound through a command-line player (macOS "afplay", Linux "paplay").</summary>
internal sealed class CommandAudio : IAudioService
{
    private readonly string command;

    public CommandAudio(string command) => this.command = command;

    public async Task PlayAsync(string fileName, byte[] data)
    {
        if (0 == command.Length)
        {
            throw new InvalidOperationException("no sound player is installed");
        }
        string path = Path.Combine(Path.GetTempPath(), "script-runner-" + Guid.NewGuid().ToString("N") + Path.GetExtension(fileName));
        await File.WriteAllBytesAsync(path, data);
        try
        {
            var start = new ProcessStartInfo(command) { ArgumentList = { path } };
            using Process process = Process.Start(start)!;
            await process.WaitForExitAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
