using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace ScriptRunner.Android;

/// <summary>The Android entry point: starts the shared app (Avalonia 12's Android application class).</summary>
[Application]
public class AndroidApp : AvaloniaAndroidApplication<App.App>
{
    protected AndroidApp(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
    {
    }

    public override void OnCreate()
    {
        App.App.Platform = new App.Services.PlatformServices(
            new AndroidSpeech(this), new AndroidAudio(this), new AndroidKeystoreSecretStore(this),
            FilesDir!.AbsolutePath);
        App.App.InstallErrorHandling(App.App.Platform.SettingsFolder);
        // Errors Android reports on its own threads; recorded before the app closes.
        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
            App.Services.DiagnosticLog.RecordError("Android", e.Exception, appWillClose: true);
        base.OnCreate();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder).WithInterFont();
}
