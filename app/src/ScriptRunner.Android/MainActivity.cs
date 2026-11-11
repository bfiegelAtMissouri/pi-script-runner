using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace ScriptRunner.Android;

/// <summary>The Android screen that shows the app (Avalonia 12: the app itself starts in <see cref="AndroidApp"/>).</summary>
[Activity(
    Label = "Script Runner",
    Theme = "@style/ScriptRunnerTheme",
    Icon = "@drawable/icon",
    MainLauncher = true,
    WindowSoftInputMode = global::Android.Views.SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode
                           | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize)]
public class MainActivity : AvaloniaMainActivity
{
}
