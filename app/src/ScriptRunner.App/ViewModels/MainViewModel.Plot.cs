using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ScriptRunner.App.ViewModels;

/// <summary>The live graph of the numbers a script sends with runner_io.plot().</summary>
public sealed partial class MainViewModel
{
    public PlotData Plot { get; } = new();

    /// <summary>True when there is something to graph; the graph is hidden otherwise.</summary>
    [ObservableProperty]
    public partial bool HasPlot { get; set; }

    [RelayCommand]
    private void ClearPlot()
    {
        Plot.Clear();
        HasPlot = false;
    }

    private void AddPlotPoint(IReadOnlyList<KeyValuePair<string, double>> values)
    {
        Plot.Add(values);
        HasPlot = true;
    }
}
