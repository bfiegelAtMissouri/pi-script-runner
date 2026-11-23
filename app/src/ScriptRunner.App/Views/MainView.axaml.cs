using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ScriptRunner.App.Services;
using ScriptRunner.App.ViewModels;

namespace ScriptRunner.App.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        DropArea.AddHandler(DragDrop.DropEvent, OnDrop);
        OutputBox.PropertyChanged += (_, e) =>
        {
            if (TextBox.TextProperty == e.Property)
            {
                OutputBox.CaretIndex = OutputBox.Text?.Length ?? 0;     // keep the newest output in view
            }
        };
        SizeChanged += (_, _) => FitFileList();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel model)
            {
                model.PropertyChanged += (_, e) =>
                {
                    if (nameof(MainViewModel.FileListHeight) == e.PropertyName)
                    {
                        FitFileList();
                    }
                    else if (nameof(MainViewModel.HasPlot) == e.PropertyName ||
                             nameof(MainViewModel.HasDrawing) == e.PropertyName)
                    {
                        ShowGraphs(model.HasPlot, model.HasDrawing);
                    }
                };
                FitFileList();
                ShowGraphs(model.HasPlot, model.HasDrawing);
            }
        };
    }

    /// <summary>
    /// Gives the graph two fifths of the height below the controls (the drawing, which needs more, three fifths) and
    /// the output the rest, or the output all of it when there is neither. The graph and the drawing share the width
    /// when there are both. The splitters change the shares.
    /// </summary>
    private void ShowGraphs(bool hasPlot, bool hasDrawing)
    {
        bool any = hasPlot || hasDrawing;
        RowDefinitions rows = OutputArea.RowDefinitions;
        rows[0].Height = any ? new GridLength(hasDrawing ? 4.5 : 2, GridUnitType.Star) : new GridLength(0);
        rows[0].MinHeight = any ? 100 : 0;
        rows[1].Height = any ? new GridLength(8) : new GridLength(0);
        rows[2].Height = new GridLength(3, GridUnitType.Star);
        rows[2].MinHeight = 80;
        ColumnDefinitions columns = GraphsArea.ColumnDefinitions;
        columns[0].Width = hasPlot ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        columns[1].Width = hasPlot && hasDrawing ? new GridLength(8) : new GridLength(0);
        columns[2].Width = hasDrawing ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    /// <summary>Applies the chosen file list height, kept to two thirds of the screen when the screen is smaller.</summary>
    private void FitFileList()
    {
        if (DataContext is MainViewModel model)
        {
            FileListScroll.MaxHeight = FileListHeightFor(model.FileListHeight);
        }
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext!;

    /// <summary>While the file list's handle is dragged: where the pointer started and how tall the list was then.</summary>
    private (double PointerY, double ListHeight)? fileListDrag;

    private void OnFileListGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        fileListDrag = (e.GetPosition(this).Y, FileListScroll.Bounds.Height);
        e.Pointer.Capture(FileListGrip);
        e.Handled = true;
    }

    private void OnFileListGripMoved(object? sender, PointerEventArgs e)
    {
        if (fileListDrag is { } drag)
        {
            ViewModel.FileListHeight = FileListHeightFor(drag.ListHeight + e.GetPosition(this).Y - drag.PointerY);
        }
    }

    private void OnFileListGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        fileListDrag = null;
        e.Pointer.Capture(null);
    }

    private void OnFileListGripCaptureLost(object? sender, PointerCaptureLostEventArgs e) => fileListDrag = null;

    /// <summary>
    /// A file list height kept between the smallest useful one and two thirds of the screen, so the output always has
    /// room.
    /// </summary>
    private double FileListHeightFor(double wanted)
    {
        double tallest = 0 < Bounds.Height
            ? Math.Max(MainViewModel.SmallestFileListHeight, Bounds.Height * 2 / 3)
            : double.PositiveInfinity;          // not laid out yet
        return Math.Round(Math.Clamp(wanted, MainViewModel.SmallestFileListHeight, tallest));
    }

    private async void OnAddFiles(object? sender, RoutedEventArgs e)
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }
        IReadOnlyList<IStorageFile> picked = await top.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions { Title = "Choose your script and its files", AllowMultiple = true });
        await AddAsync(picked);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        IEnumerable<IStorageItem>? items = e.DataTransfer.TryGetFiles();
        if (items is not null)
        {
            await AddAsync(items.OfType<IStorageFile>());
        }
    }

    private async Task AddAsync(IEnumerable<IStorageFile> files)
    {
        var added = new List<AddedFile>();
        foreach (IStorageFile file in files)
        {
            Func<Task<byte[]>> reader = ReaderFor(file);
            added.Add(new AddedFile(file.Name, await reader(), reader));
        }
        ViewModel.AddFiles(added);
    }

    /// <summary>
    /// Reads a file again from where it was added, for Reload: by its path where it has one (the desktop), otherwise
    /// by opening it again through the system (Android).
    /// </summary>
    private static Func<Task<byte[]>> ReaderFor(IStorageFile file)
    {
        string? path = file.TryGetLocalPath();
        if (path is not null)
        {
            return () => File.ReadAllBytesAsync(path);
        }
        return async () =>
        {
            await using Stream stream = await file.OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            return buffer.ToArray();
        };
    }

    /// <summary>
    /// Saves an example's files into a chosen folder (existing files of the same name are kept; the copies get a
    /// number) and puts those copies in the file list, so they can be changed in an editor and reloaded.
    /// </summary>
    private async void OnCopyExample(object? sender, RoutedEventArgs e)
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top is null || sender is not Control { Tag: Example example })
        {
            return;
        }
        try
        {
            IReadOnlyList<IStorageFolder> picked = await top.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = $"Choose a folder for {example.Name} and its files" });
            if (0 == picked.Count)
            {
                return;
            }
            var added = new List<AddedFile>();
            var renamed = new List<string>();
            foreach (ExampleFile part in example.AllFiles)
            {
                byte[] data = ExampleCatalog.Read(part.Path);
                IStorageFile? existing = await picked[0].GetFileAsync(part.Name);
                byte[]? there = existing is null ? null : await ReaderFor(existing)();
                if (existing is not null && there is not null && data.AsSpan().SequenceEqual(there))
                {
                    added.Add(new AddedFile(part.Name, data, ReaderFor(existing)));   // already there, unchanged
                    continue;
                }
                string name = await UnusedNameAsync(picked[0], part.Name);
                IStorageFile? file = await picked[0].CreateFileAsync(name);
                if (file is null)
                {
                    throw new IOException("Could not create " + name);
                }
                await WriteAsync(file, data);
                if (name != part.Name)
                {
                    renamed.Add($"{part.Name} as {name}");
                }
                added.Add(new AddedFile(part.Name, data, ReaderFor(file)));
            }
            ViewModel.UseExampleFiles(example, added);
            if (0 < renamed.Count)
            {
                ViewModel.StatusText += " The folder already had changed files of the same name, so these were saved " +
                                        "beside them: " + string.Join(", ", renamed) + ".";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ViewModel.ErrorText = "Could not copy the example: " + error.Message;
        }
    }

    /// <summary>Saves the plotted numbers as a CSV file.</summary>
    private async void OnSavePlot(object? sender, RoutedEventArgs e)
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top is null || ViewModel.Plot.IsEmpty)
        {
            return;
        }
        try
        {
            IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save the plot data",
                SuggestedFileName = "plot.csv",
                DefaultExtension = "csv",
                ShowOverwritePrompt = true,
            });
            if (file is not null)
            {
                await WriteAsync(file, Encoding.UTF8.GetBytes(ViewModel.Plot.ToCsv()));
                ViewModel.StatusText = "Saved " + file.Name;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ViewModel.ErrorText = "Could not save the plot data: " + error.Message;
        }
    }

    /// <summary>Saves the drawing as an SVG file.</summary>
    private async void OnSaveDrawing(object? sender, RoutedEventArgs e)
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top is null || !ViewModel.HasDrawing)
        {
            return;
        }
        try
        {
            IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save the drawing",
                SuggestedFileName = Path.GetFileNameWithoutExtension(ViewModel.SelectedScript ?? "drawing") + ".svg",
                DefaultExtension = "svg",
                ShowOverwritePrompt = true,
            });
            if (file is not null)
            {
                await WriteAsync(file, Encoding.UTF8.GetBytes(ViewModel.Drawing.ToSvg()));
                ViewModel.StatusText = "Saved " + file.Name;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ViewModel.ErrorText = "Could not save the drawing: " + error.Message;
        }
    }

    /// <summary>
    /// Saves the latest run in a new folder inside a chosen one: output.txt (the output with the time of each line),
    /// the files sent with it, plot.csv when the script plotted, drawing.svg when it drew, and the files it showed.
    /// </summary>
    private async void OnSaveRun(object? sender, RoutedEventArgs e)
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top is null || !ViewModel.HasRunRecord)
        {
            return;
        }
        try
        {
            IReadOnlyList<IStorageFolder> picked = await top.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = "Choose where to save the run (a new folder is made in it)" });
            if (0 == picked.Count)
            {
                return;
            }
            string script = Path.GetFileNameWithoutExtension(ViewModel.SelectedScript ?? "run");
            string folderName = $"{script} {DateTime.Now:yyyy-MM-dd HHmmss}";
            IStorageFolder folder = await picked[0].CreateFolderAsync(folderName)
                ?? throw new IOException("Could not create the folder " + folderName);
            await SaveNewAsync(folder, "output.txt", Encoding.UTF8.GetBytes(ViewModel.BuildRunRecord()));
            if (!ViewModel.Plot.IsEmpty)
            {
                await SaveNewAsync(folder, "plot.csv", Encoding.UTF8.GetBytes(ViewModel.Plot.ToCsv()));
            }
            if (ViewModel.HasDrawing)
            {
                await SaveNewAsync(folder, "drawing.svg", Encoding.UTF8.GetBytes(ViewModel.Drawing.ToSvg()));
            }
            if (0 < ViewModel.RecordFiles.Count)
            {
                IStorageFolder files = await folder.CreateFolderAsync("files") ?? throw new IOException("Could not create files");
                foreach ((string name, byte[] data, _) in ViewModel.RecordFiles)
                {
                    await SaveNewAsync(files, name, data);
                }
            }
            if (ViewModel.HasShownFiles)
            {
                IStorageFolder shown = await folder.CreateFolderAsync("shown") ?? throw new IOException("Could not create shown");
                foreach (ShownFile file in ViewModel.ShownFiles.ToList())
                {
                    await SaveNewAsync(shown, file.FileName, file.Data);
                }
            }
            ViewModel.ErrorText = "";
            ViewModel.StatusText = "Saved the run in " + folderName;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ViewModel.ErrorText = "Could not save the run: " + error.Message;
        }
    }

    /// <summary>Writes a file under an unused name (name, name-2, name-3, ...).</summary>
    private static async Task SaveNewAsync(IStorageFolder folder, string name, byte[] data)
    {
        IStorageFile file = await folder.CreateFileAsync(await UnusedNameAsync(folder, name))
            ?? throw new IOException("Could not create " + name);
        await WriteAsync(file, data);
    }

    private async void OnSaveShown(object? sender, RoutedEventArgs e)
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top is null || ViewModel.CurrentShown is not { } shown)
        {
            return;
        }
        try
        {
            IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save file",
                SuggestedFileName = shown.FileName,
                DefaultExtension = Path.GetExtension(shown.FileName).TrimStart('.'),
                ShowOverwritePrompt = true,
            });
            if (file is not null)
            {
                await WriteAsync(file, shown.Data);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ViewModel.ErrorText = "Could not save the file: " + error.Message;
        }
    }

    /// <summary>Saves every shown file into a chosen folder. Repeated names get a number, and existing files are kept.</summary>
    private async void OnSaveAllShown(object? sender, RoutedEventArgs e)
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top is null || !ViewModel.HasShownFiles)
        {
            return;
        }
        try
        {
            IReadOnlyList<IStorageFolder> picked = await top.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = "Choose a folder for the files" });
            if (0 == picked.Count)
            {
                return;
            }
            IStorageFolder folder = picked[0];
            int saved = 0;
            foreach (ShownFile shown in ViewModel.ShownFiles.ToList())
            {
                string name = await UnusedNameAsync(folder, shown.FileName);
                IStorageFile? file = await folder.CreateFileAsync(name);
                if (file is not null)
                {
                    await WriteAsync(file, shown.Data);
                    ++saved;
                }
            }
            ViewModel.ErrorText = "";
            ViewModel.StatusText = $"Saved {saved} file{(1 == saved ? "" : "s")}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ViewModel.ErrorText = "Could not save the files: " + error.Message;
        }
    }

    /// <summary>name if the folder has no file by that name, otherwise name-2, name-3, and so on.</summary>
    private static async Task<string> UnusedNameAsync(IStorageFolder folder, string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        string extension = Path.GetExtension(name);
        string candidate = name;
        for (int number = 2; null != await folder.GetFileAsync(candidate); ++number)
        {
            candidate = $"{stem}-{number}{extension}";
        }
        return candidate;
    }

    private static async Task WriteAsync(IStorageFile file, byte[] data)
    {
        await using Stream stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await stream.WriteAsync(data);
    }

    private async void OnOpenLogsFolder(object? sender, RoutedEventArgs e)
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top is null || DiagnosticLog.Folder is null)
        {
            return;
        }
        if (!await top.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(DiagnosticLog.Folder)))
        {
            ViewModel.SettingsError = "Could not open the folder. Its path is shown above.";
        }
    }

    /// <summary>Copies the newest crash report, or today's log when there is no report, to the clipboard.</summary>
    private async void OnCopyLatestReport(object? sender, RoutedEventArgs e)
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        string? folder = DiagnosticLog.Folder;
        if (top?.Clipboard is null || folder is null)
        {
            return;
        }
        string? file = DiagnosticLog.LatestReport() ?? Directory.GetFiles(folder, "app-*.log").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault();
        if (file is null)
        {
            ViewModel.SettingsError = "There is no report or log yet.";
            return;
        }
        await top.Clipboard.SetTextAsync(await File.ReadAllTextAsync(file));
        ViewModel.SettingsError = "";
        ViewModel.StatusText = "Copied " + Path.GetFileName(file);
    }
}
