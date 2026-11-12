using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptRunner.Core;

namespace ScriptRunner.App.ViewModels;

/// <summary>The Settings page: limits the user can change. Each is saved as it is changed.</summary>
public sealed partial class MainViewModel
{
    public const int DefaultShownFilesKept = 100;
    public const int DefaultLargestFileMegabytes = 20;
    public const int DefaultOutputCharactersKept = 200_000;
    public static readonly int DefaultPiCommandSeconds = (int)PiCommand.DefaultTimeout.TotalSeconds;
    public const int DefaultLaptopLoadSeconds = 120;
    public const int DefaultLaptopCallSeconds = 60;

    private int shownFilesKept = DefaultShownFilesKept;
    private int outputCharactersKept = DefaultOutputCharactersKept;
    private int piCommandSeconds = DefaultPiCommandSeconds;
    private int laptopLoadSeconds = DefaultLaptopLoadSeconds;
    private int laptopCallSeconds = DefaultLaptopCallSeconds;

    /// <summary>Largest file show() or play() downloads.</summary>
    private long LargestFileBytes { get; set; } = DefaultLargestFileMegabytes * 1024L * 1024L;

    [ObservableProperty]
    public partial bool IsSettingsOpen { get; set; }

    /// <summary>What is wrong with a value on the Settings page, or "".</summary>
    [ObservableProperty]
    public partial string SettingsError { get; set; } = "";

    /// <summary>Most files kept beside the output; the oldest is dropped when another arrives.</summary>
    [ObservableProperty]
    public partial string ShownFilesKeptText { get; set; } = "";

    /// <summary>Largest file show() and play() download, in megabytes.</summary>
    [ObservableProperty]
    public partial string LargestFileMegabytesText { get; set; } = "";

    /// <summary>Most characters of output kept on screen; older output is dropped.</summary>
    [ObservableProperty]
    public partial string OutputCharactersKeptText { get; set; } = "";

    /// <summary>Longest a command in the Pi command box may run, in seconds.</summary>
    [ObservableProperty]
    public partial string PiCommandSecondsText { get; set; } = "";

    /// <summary>The Python that runs Laptop files: a path or command; blank finds one automatically.</summary>
    [ObservableProperty]
    public partial string LaptopPythonText { get; set; } = "";

    /// <summary>Longest the Laptop files may take to load at the start of a run, in seconds.</summary>
    [ObservableProperty]
    public partial string LaptopLoadSecondsText { get; set; } = "";

    /// <summary>Longest one laptop function call may take, in seconds.</summary>
    [ObservableProperty]
    public partial string LaptopCallSecondsText { get; set; } = "";

    [RelayCommand]
    private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    [RelayCommand]
    private void ResetSettings()
    {
        TimeLimitSeconds = ConnectionSettings.DefaultMaxSeconds.ToString();
        ShownFilesKeptText = DefaultShownFilesKept.ToString();
        LargestFileMegabytesText = DefaultLargestFileMegabytes.ToString();
        OutputCharactersKeptText = DefaultOutputCharactersKept.ToString();
        PiCommandSecondsText = DefaultPiCommandSeconds.ToString();
        LaptopPythonText = "";
        LaptopLoadSecondsText = DefaultLaptopLoadSeconds.ToString();
        LaptopCallSecondsText = DefaultLaptopCallSeconds.ToString();
        SettingsError = "";
    }

    private void LoadLimits()
    {
        TimeLimitSeconds = settings.Get("timeLimitSeconds", ConnectionSettings.DefaultMaxSeconds.ToString());
        ShownFilesKeptText = settings.Get("shownFilesKept", DefaultShownFilesKept.ToString());
        LargestFileMegabytesText = settings.Get("largestFileMegabytes", DefaultLargestFileMegabytes.ToString());
        OutputCharactersKeptText = settings.Get("outputCharactersKept", DefaultOutputCharactersKept.ToString());
        PiCommandSecondsText = settings.Get("piCommandSeconds", DefaultPiCommandSeconds.ToString());
        LaptopPythonText = settings.Get("laptopPython", "");
        LaptopLoadSecondsText = settings.Get("laptopLoadSeconds", DefaultLaptopLoadSeconds.ToString());
        LaptopCallSecondsText = settings.Get("laptopCallSeconds", DefaultLaptopCallSeconds.ToString());
        SettingsError = "";
    }

    partial void OnTimeLimitSecondsChanged(string value)
    {
        if (TryLimit(value, 0, ConnectionSettings.LongestMaxSeconds, "The time limit", out _))
        {
            settings.Set("timeLimitSeconds", value.Trim());
        }
    }

    partial void OnShownFilesKeptTextChanged(string value)
    {
        if (TryLimit(value, 1, 10_000, "Files kept", out int kept))
        {
            shownFilesKept = kept;
            settings.Set("shownFilesKept", value.Trim());
            DropOldestShown();
            OnShownFilesChanged();
        }
    }

    partial void OnLargestFileMegabytesTextChanged(string value)
    {
        if (TryLimit(value, 1, 2048, "The largest file", out int megabytes))
        {
            LargestFileBytes = megabytes * 1024L * 1024L;
            settings.Set("largestFileMegabytes", value.Trim());
        }
    }

    partial void OnOutputCharactersKeptTextChanged(string value)
    {
        if (TryLimit(value, 1_000, 50_000_000, "Output kept", out int characters))
        {
            outputCharactersKept = characters;
            settings.Set("outputCharactersKept", value.Trim());
        }
    }

    partial void OnPiCommandSecondsTextChanged(string value)
    {
        if (TryLimit(value, 1, 3600, "The command time limit", out int seconds))
        {
            piCommandSeconds = seconds;
            settings.Set("piCommandSeconds", value.Trim());
        }
    }

    partial void OnLaptopPythonTextChanged(string value) => settings.Set("laptopPython", value.Trim());

    partial void OnLaptopLoadSecondsTextChanged(string value)
    {
        if (TryLimit(value, 1, 3600, "The Laptop load time limit", out int seconds))
        {
            laptopLoadSeconds = seconds;
            settings.Set("laptopLoadSeconds", value.Trim());
        }
    }

    partial void OnLaptopCallSecondsTextChanged(string value)
    {
        if (TryLimit(value, 1, 3600, "The Laptop call time limit", out int seconds))
        {
            laptopCallSeconds = seconds;
            settings.Set("laptopCallSeconds", value.Trim());
        }
    }

    /// <summary>
    /// Reads a whole number in [low, high]. When it is not one, says so on the Settings page and returns false, and
    /// the previous value stays in effect.
    /// </summary>
    private bool TryLimit(string text, int low, int high, string what, out int value)
    {
        if (int.TryParse(text.Trim(), out value) && low <= value && value <= high)
        {
            SettingsError = "";
            return true;
        }
        SettingsError = $"{what} must be a whole number from {low:N0} to {high:N0}.";
        return false;
    }
}
