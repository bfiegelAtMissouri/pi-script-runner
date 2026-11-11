using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Speech.Synthesis;
using System.Text;
using ScriptRunner.App.Services;

namespace ScriptRunner.Desktop.Platform;

/// <summary>Windows speech (System.Speech); messages are queued and spoken in order.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsSpeech : ISpeechService, IDisposable
{
    private readonly SpeechSynthesizer synthesizer = new();

    public WindowsSpeech() => synthesizer.SetOutputToDefaultAudioDevice();

    public void Speak(string text) => synthesizer.SpeakAsync(text);

    public void StopSpeaking() => synthesizer.SpeakAsyncCancelAll();

    public void Dispose() => synthesizer.Dispose();
}

/// <summary>
/// Windows sound. .wav files play with PlaySound, which every Windows computer has. Other formats (.mp3) go
/// through the Media Control Interface, whose "mpegvideo" player is missing on some computers.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsAudio : IAudioService
{
    private const uint SoundFileName = 0x00020000;          // SND_FILENAME: the sound is a file path
    private const uint SoundNoDefault = 0x00000002;         // SND_NODEFAULT: no system beep if the file fails
    private static int next;

    public async Task PlayAsync(string fileName, byte[] data)
    {
        string path = Path.Combine(Path.GetTempPath(), "script-runner-" + Guid.NewGuid().ToString("N") + Path.GetExtension(fileName));
        await File.WriteAllBytesAsync(path, data);
        try
        {
            if (fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                // Synchronous play (no SND_ASYNC) on a worker thread, so the temporary file is deleted only after.
                bool played = await Task.Run(() => PlaySoundW(path, IntPtr.Zero, SoundFileName | SoundNoDefault));
                if (!played)
                {
                    throw new InvalidOperationException("Windows could not play this .wav file");
                }
            }
            else
            {
                await PlayWithMediaControlAsync(path);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task PlayWithMediaControlAsync(string path)
    {
        string alias = "scriptrunner" + Interlocked.Increment(ref next);
        try
        {
            await Task.Run(() =>
            {
                Send($"open \"{path}\" type mpegvideo alias {alias}");
                Send($"play {alias} wait");
            });
        }
        catch (InvalidOperationException error)
        {
            throw new InvalidOperationException(
                $"this computer cannot play {Path.GetExtension(path)} files ({error.Message}); use a .wav file instead", error);
        }
        finally
        {
            mciSendStringW($"close {alias}", null, 0, IntPtr.Zero);
        }
    }

    private static void Send(string command)
    {
        int error = mciSendStringW(command, null, 0, IntPtr.Zero);
        if (0 != error)
        {
            var message = new StringBuilder(256);
            mciGetErrorStringW(error, message, message.Capacity);
            throw new InvalidOperationException(message.ToString());
        }
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySoundW(string sound, IntPtr module, uint flags);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendStringW(string command, StringBuilder? returnValue, int returnLength, IntPtr callback);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool mciGetErrorStringW(int error, StringBuilder text, int length);
}

/// <summary>PINs and passwords in Windows Credential Manager (generic credentials, this user only).</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsCredentialStore : ISecretStore
{
    private const string Prefix = "ScriptRunner:";
    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;

    public bool IsAvailable => true;

    public string? Load(string key)
    {
        if (!CredReadW(Prefix + key, GenericCredential, 0, out IntPtr pointer))
        {
            return null;
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public bool Save(string key, string secret)
    {
        byte[] blob = Encoding.Unicode.GetBytes(secret);
        IntPtr blobPointer = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPointer, blob.Length);
            var credential = new Credential
            {
                Type = GenericCredential,
                TargetName = Prefix + key,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPointer,
                Persist = PersistLocalMachine,
                UserName = Environment.UserName,
            };
            return CredWriteW(ref credential, 0);
        }
        finally
        {
            Marshal.Copy(new byte[blob.Length], 0, blobPointer, blob.Length);     // clear the copy
            Marshal.FreeHGlobal(blobPointer);
        }
    }

    public void Delete(string key) => CredDeleteW(Prefix + key, GenericCredential, 0);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
