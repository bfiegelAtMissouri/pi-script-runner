using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext!;

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
        var added = new List<(string, byte[])>();
        foreach (IStorageFile file in files)
        {
            await using Stream stream = await file.OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            added.Add((file.Name, buffer.ToArray()));
        }
        ViewModel.AddFiles(added);
    }

    private async void OnSaveImage(object? sender, RoutedEventArgs e)
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top is null || ViewModel.CurrentShownImage is not { } image)
        {
            return;
        }
        try
        {
            IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save image",
                SuggestedFileName = image.FileName,
                DefaultExtension = Path.GetExtension(image.FileName).TrimStart('.'),
                ShowOverwritePrompt = true,
            });
            if (file is not null)
            {
                await WriteAsync(file, image.Data);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ViewModel.ErrorText = "Could not save the image: " + error.Message;
        }
    }

    /// <summary>Saves every image into a chosen folder. Repeated names get a number, and existing files are kept.</summary>
    private async void OnSaveAllImages(object? sender, RoutedEventArgs e)
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top is null || !ViewModel.HasImages)
        {
            return;
        }
        try
        {
            IReadOnlyList<IStorageFolder> picked = await top.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = "Choose a folder for the images" });
            if (0 == picked.Count)
            {
                return;
            }
            IStorageFolder folder = picked[0];
            int saved = 0;
            foreach (ShownImage image in ViewModel.Images.ToList())
            {
                string name = await UnusedNameAsync(folder, image.FileName);
                IStorageFile? file = await folder.CreateFileAsync(name);
                if (file is not null)
                {
                    await WriteAsync(file, image.Data);
                    ++saved;
                }
            }
            ViewModel.ErrorText = "";
            ViewModel.StatusText = $"Saved {saved} image{(1 == saved ? "" : "s")}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ViewModel.ErrorText = "Could not save the images: " + error.Message;
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
}
