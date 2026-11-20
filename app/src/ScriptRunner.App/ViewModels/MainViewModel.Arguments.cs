using CommunityToolkit.Mvvm.ComponentModel;
using ScriptRunner.Core;

namespace ScriptRunner.App.ViewModels;

/// <summary>
/// Command-line arguments: set on the Settings page for each script (by file name), and passed to it on every run
/// as sys.argv[1:]. The main screen has no field for them; the output's first line shows them when there are some.
/// </summary>
public sealed partial class MainViewModel
{
    private bool loadingArguments;

    /// <summary>The arguments for the selected script, as typed on the Settings page.</summary>
    [ObservableProperty]
    public partial string ScriptArgumentsText { get; set; } = "";

    /// <summary>For example "Arguments for room_map.py".</summary>
    public string ArgumentsLabel => SelectedScript is null ? "Arguments (add a script first)" : $"Arguments for {SelectedScript}";

    public bool CanSetArguments => SelectedScript is not null;

    partial void OnSelectedScriptChanged(string? value)
    {
        loadingArguments = true;
        ScriptArgumentsText = value is null ? "" : settings.GetScriptArguments(value);
        loadingArguments = false;
        OnPropertyChanged(nameof(ArgumentsLabel));
        OnPropertyChanged(nameof(CanSetArguments));
    }

    partial void OnScriptArgumentsTextChanged(string value)
    {
        if (loadingArguments || SelectedScript is null)
        {
            return;
        }
        try
        {
            ScriptArguments.Split(value);
            SettingsError = "";
            settings.SetScriptArguments(SelectedScript, value.Trim());
        }
        catch (FormatException error)
        {
            SettingsError = $"{ArgumentsLabel}: {error.Message}";
        }
    }

    /// <summary>The selected script's saved arguments, or null (with ErrorText set) when they cannot be read.</summary>
    private IReadOnlyList<string>? ArgumentsForRun(string script)
    {
        try
        {
            return ScriptArguments.Split(settings.GetScriptArguments(script));
        }
        catch (FormatException error)
        {
            ErrorText = $"The arguments for {script} (Settings) cannot be used: {error.Message}";
            return null;
        }
    }
}
