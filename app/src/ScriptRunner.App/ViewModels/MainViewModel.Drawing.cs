using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptRunner.Core;

namespace ScriptRunner.App.ViewModels;

/// <summary>The picture a script draws with runner_io's draw object.</summary>
public sealed partial class MainViewModel
{
    public ScriptDrawing Drawing { get; } = new();

    /// <summary>True when the script has drawn something; the drawing is hidden otherwise.</summary>
    [ObservableProperty]
    public partial bool HasDrawing { get; set; }

    /// <summary>Erases the drawing and hides it until the script draws again.</summary>
    [RelayCommand]
    private void ClearDrawing()
    {
        Drawing.Reset();
        HasDrawing = false;
    }

    private void ApplyDraw(DrawCommand command)
    {
        Drawing.Apply(command);
        HasDrawing = true;
    }
}
