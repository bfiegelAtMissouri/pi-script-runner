using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using ScriptRunner.App.Services;
using ScriptRunner.App.ViewModels;
using ScriptRunner.App.Views;
using Xunit;

namespace ScriptRunner.Tests;

/// <summary>
/// Many added files: the list scrolls instead of pushing the output off the screen, its handle changes its height,
/// and the height is kept for the next start. Saves a picture as ui-file-list.png in the test output folder.
/// </summary>
public sealed class FileListTests
{
    [Fact]
    public async Task ManyFilesScrollAndTheListResizes()
    {
        string settingsFolder = Directory.CreateTempSubdirectory("script-runner-file-list").FullName;
        var platform = new PlatformServices(new UiTests.RecordingSpeech(), new UiTests.SilentAudio(), new NoSecretStore(),
                                            settingsFolder);
        ScriptRunner.App.App.Platform ??= platform;     // the shared app starts with whichever test is first
        Dispatcher ui = await HeadlessApp.StartAsync();

        await ui.InvokeAsync(() =>
        {
            var model = new MainViewModel(platform);
            var view = new MainView { DataContext = model };
            var window = new Window { Width = 1000, Height = 700, Content = view };
            window.Show();
            var list = view.FindControl<ScrollViewer>("FileListScroll")!;
            var output = view.FindControl<TextBox>("OutputBox")!;
            model.AddFiles(new[] { ("file_01.py", new byte[] { 0x23 }) });
            Dispatcher.UIThread.RunJobs();
            double outputWithOneFile = output.Bounds.Height, oneFile = list.Bounds.Height;

            // 39 more: the list stops growing at its height and scrolls, so the output loses no more than that.
            model.AddFiles(Enumerable.Range(2, 39).Select(i => ($"file_{i:D2}.py", new byte[] { 0x23 })));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(MainViewModel.DefaultFileListHeight, list.Bounds.Height);
            Assert.True(list.Extent.Height > 10 * list.Bounds.Height, "40 files should need scrolling");
            Assert.Equal(outputWithOneFile - (MainViewModel.DefaultFileListHeight - oneFile), output.Bounds.Height, 0.5);

            // Drag the handle 100 px down: the list grows by 100.
            var grip = view.FindControl<Border>("FileListGrip")!;
            Point start = grip.TranslatePoint(new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), window)!.Value;
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + new Point(0, 50));
            window.MouseMove(start + new Point(0, 100));
            window.MouseUp(start + new Point(0, 100), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(MainViewModel.DefaultFileListHeight + 100, model.FileListHeight);
            Assert.Equal(MainViewModel.DefaultFileListHeight + 100, list.Bounds.Height);

            // Far down: stopped at two thirds of the screen, so the output keeps room.
            start = grip.TranslatePoint(new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), window)!.Value;
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + new Point(0, 2000));
            window.MouseUp(start + new Point(0, 2000), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.True(list.Bounds.Height <= view.Bounds.Height * 2 / 3 + 1);
            Assert.True(output.Bounds.Height > 0);

            // Far up: stopped at the smallest height.
            start = grip.TranslatePoint(new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), window)!.Value;
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start - new Point(0, 2000));
            window.MouseUp(start - new Point(0, 2000), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(MainViewModel.SmallestFileListHeight, model.FileListHeight);
            Assert.Equal(MainViewModel.SmallestFileListHeight, list.Bounds.Height);

            // A comfortable height, kept for the next start; Restore defaults puts it back.
            model.FileListHeight = 180;
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Save(Path.Combine(AppContext.BaseDirectory, "ui-file-list.png"),
                                                new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            window.Close();

            var again = new MainViewModel(platform);
            Assert.Equal(180, again.FileListHeight);
            again.ResetSettingsCommand.Execute(null);
            Assert.Equal(MainViewModel.DefaultFileListHeight, again.FileListHeight);
        });

        Directory.Delete(settingsFolder, true);
    }
}
