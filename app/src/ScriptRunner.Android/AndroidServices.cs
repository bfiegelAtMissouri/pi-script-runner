using Android.Content;
using Android.Media;
using Android.Security.Keystore;
using Android.Speech.Tts;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using ScriptRunner.App.Services;

namespace ScriptRunner.Android;

/// <summary>Android text-to-speech; messages are queued and spoken in order.</summary>
internal sealed class AndroidSpeech : Java.Lang.Object, ISpeechService, TextToSpeech.IOnInitListener
{
    private readonly TextToSpeech engine;
    private readonly List<string> waiting = new();
    private bool ready;
    private int utterance;

    public AndroidSpeech(Context context) => engine = new TextToSpeech(context, this);

    public void OnInit(OperationResult status)
    {
        lock (waiting)
        {
            ready = OperationResult.Success == status;
            if (ready)
            {
                foreach (string text in waiting)
                {
                    SpeakNow(text);
                }
            }
            waiting.Clear();
        }
    }

    public void Speak(string text)
    {
        lock (waiting)
        {
            if (ready)
            {
                SpeakNow(text);
            }
            else
            {
                waiting.Add(text);              // the engine is still starting
            }
        }
    }

    public void StopSpeaking()
    {
        lock (waiting)
        {
            waiting.Clear();
        }
        engine.Stop();
    }

    private void SpeakNow(string text) =>
        engine.Speak(text, QueueMode.Add, null, "script-runner-" + Interlocked.Increment(ref utterance));
}

/// <summary>Android sound playback (.wav, .mp3, .ogg) with MediaPlayer.</summary>
internal sealed class AndroidAudio : IAudioService
{
    private readonly Context context;

    public AndroidAudio(Context context) => this.context = context;

    public async Task PlayAsync(string fileName, byte[] data)
    {
        string path = Path.Combine(context.CacheDir!.AbsolutePath, "sound-" + Guid.NewGuid().ToString("N") + Path.GetExtension(fileName));
        await File.WriteAllBytesAsync(path, data);
        var finished = new TaskCompletionSource();
        var player = new MediaPlayer();
        try
        {
            player.Completion += (_, _) => finished.TrySetResult();
            player.Error += (_, e) => finished.TrySetException(new InvalidOperationException($"cannot play this file ({e.What})"));
            player.SetDataSource(path);
            player.Prepare();
            player.Start();
            await finished.Task;
        }
        finally
        {
            player.Release();
            File.Delete(path);
        }
    }
}

/// <summary>
/// PINs and passwords encrypted with an AES key held in the Android Keystore (the key never leaves the
/// Keystore), stored in this app's private preferences. Any failure is reported as "not saved".
/// </summary>
internal sealed class AndroidKeystoreSecretStore : ISecretStore
{
    private const string KeyAlias = "script-runner-secrets";
    private const string KeystoreName = "AndroidKeyStore";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int TagBits = 128;

    private readonly ISharedPreferences preferences;

    public AndroidKeystoreSecretStore(Context context) =>
        preferences = context.GetSharedPreferences("script-runner-secrets", FileCreationMode.Private)!;

    public bool IsAvailable => true;

    public string? Load(string key)
    {
        string? stored = preferences.GetString(key, null);
        if (stored is null)
        {
            return null;
        }
        try
        {
            byte[] packed = Convert.FromBase64String(stored);
            byte ivLength = packed[0];
            byte[] iv = packed[1..(1 + ivLength)];
            byte[] encrypted = packed[(1 + ivLength)..];
            Cipher cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.DecryptMode, GetOrCreateKey(), new GCMParameterSpec(TagBits, iv));
            return System.Text.Encoding.UTF8.GetString(cipher.DoFinal(encrypted)!);
        }
        catch (Exception error) when (error is Java.Lang.Exception or FormatException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    public bool Save(string key, string secret)
    {
        try
        {
            Cipher cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.EncryptMode, GetOrCreateKey());
            byte[] iv = cipher.GetIV()!;
            byte[] encrypted = cipher.DoFinal(System.Text.Encoding.UTF8.GetBytes(secret))!;
            byte[] packed = new byte[1 + iv.Length + encrypted.Length];
            packed[0] = (byte)iv.Length;
            iv.CopyTo(packed, 1);
            encrypted.CopyTo(packed, 1 + iv.Length);
            return preferences.Edit()!.PutString(key, Convert.ToBase64String(packed))!.Commit();
        }
        catch (Java.Lang.Exception)
        {
            return false;
        }
    }

    public void Delete(string key) => preferences.Edit()!.Remove(key)!.Commit();

    private static IKey GetOrCreateKey()
    {
        KeyStore store = KeyStore.GetInstance(KeystoreName)!;
        store.Load(null);
        if (store.GetKey(KeyAlias, null) is IKey existing)
        {
            return existing;
        }
        KeyGenerator generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeystoreName)!;
        generator.Init(new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .Build());
        return generator.GenerateKey()!;
    }
}
