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
}
