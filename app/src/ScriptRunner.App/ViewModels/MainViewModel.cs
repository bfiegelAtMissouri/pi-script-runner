using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptRunner.App.Services;
using ScriptRunner.Core;

namespace ScriptRunner.App.ViewModels;

/// <summary>A file to upload with the script.</summary>
public sealed partial class FileEntry : ObservableObject
{
    public FileEntry(string name, byte[] data)
    {
        Name = name;
        Data = data;
    }

    public string Name { get; }

    public byte[] Data { get; }

    public bool IsScript => Name.EndsWith(".py", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The app's only screen: connection, files, Run and Stop, output, input, and images.</summary>
public sealed partial class MainViewModel : ViewModelBase
{
    private const int MaxOutputChars = 200_000;
    private const int SshModeIndex = 0;
    private const int HttpModeIndex = 1;

    private readonly PlatformServices platform;

    /// <summary>The dispatcher of the thread that owns this screen; connection events are handled there.</summary>
    private readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
    private readonly SettingsStore settings;
    private readonly StringBuilder committed = new();
    private OutputParser parser = new();
    private IRunnerConnection? connection;
    private ConnectionSettings? connectionSettings;

    public MainViewModel(PlatformServices platform)
    {
        this.platform = platform;
        settings = new SettingsStore(platform.SettingsFolder);
        SelectedModeIndex = "Http" == settings.Get("mode", "Ssh") ? HttpModeIndex : SshModeIndex;
        Host = settings.Get("host", "");
        User = settings.Get("user", ConnectionSettings.DefaultUser);
        Port = settings.Get(PortSettingName, DefaultPort.ToString());
        TimeLimitSeconds = settings.Get("timeLimitSeconds", ConnectionSettings.DefaultMaxSeconds.ToString());
        SpeakSay = settings.GetBool("speakSay", true);
        SpeakAll = settings.GetBool("speakAll", false);
        RememberSecret = settings.GetBool("rememberSecret", false);
        LoadRememberedSecret();
        StatusText = "Ready";
    }

    public static string Title { get; } = "Script Runner " + (Assembly.GetEntryAssembly() ?? typeof(MainViewModel).Assembly)
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    public IReadOnlyList<string> Modes { get; } = new[] { "SSH (nothing to install on the Pi)", "HTTP (runner installed on the Pi)" };

    public ObservableCollection<FileEntry> Files { get; } = new();

    public ObservableCollection<string> Scripts { get; } = new();

    public bool CanRememberSecret => platform.Secrets.IsAvailable;

    public bool IsSsh => SshModeIndex == SelectedModeIndex;

    public string SecretLabel => IsSsh ? "Password" : "PIN";

    public bool CanRun => !IsRunning && !string.IsNullOrWhiteSpace(SelectedScript) && !string.IsNullOrWhiteSpace(Host);

    /// <summary>True while the app has a connection to a Pi open (from the first Run until Disconnect).</summary>
    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSsh), nameof(SecretLabel))]
    public partial int SelectedModeIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    public partial string Host { get; set; } = "";

    [ObservableProperty]
    public partial string Port { get; set; } = "";

    /// <summary>Seconds before a run is stopped automatically; 0 means no limit.</summary>
    [ObservableProperty]
    public partial string TimeLimitSeconds { get; set; } = "";

    [ObservableProperty]
    public partial string User { get; set; } = "";

    [ObservableProperty]
    public partial string Secret { get; set; } = "";

    [ObservableProperty]
    public partial bool RememberSecret { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    public partial string? SelectedScript { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial string OutputText { get; set; } = "";

    [ObservableProperty]
    public partial string InputText { get; set; } = "";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial string ErrorText { get; set; } = "";

    /// <summary>Most images kept; when a new one arrives past this, the oldest is dropped.</summary>
    public const int MaxImages = 100;

    /// <summary>Every image the scripts have shown, oldest first, kept across runs until Clear images.</summary>
    public ObservableCollection<ShownImage> Images { get; } = new();

    /// <summary>Which of Images is on screen, or -1 when there are none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentShownImage), nameof(CurrentImage), nameof(CurrentImageName),
        nameof(ImagePosition), nameof(HasImages), nameof(CanShowPreviousImage), nameof(CanShowNextImage))]
    public partial int ImageIndex { get; set; } = -1;

    public ShownImage? CurrentShownImage => 0 <= ImageIndex && ImageIndex < Images.Count ? Images[ImageIndex] : null;

    public Bitmap? CurrentImage => CurrentShownImage?.Bitmap;

    public string CurrentImageName => CurrentShownImage?.Name ?? "";

    /// <summary>For example "2 of 5".</summary>
    public string ImagePosition => HasImages ? $"{ImageIndex + 1} of {Images.Count}" : "";

    public bool HasImages => 0 < Images.Count;

    public bool CanShowPreviousImage => 0 < ImageIndex;

    public bool CanShowNextImage => ImageIndex < Images.Count - 1;

    [ObservableProperty]
    public partial bool SpeakSay { get; set; }

    [ObservableProperty]
    public partial string PiCommandText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendPiCommand))]
    public partial bool IsPiCommandRunning { get; set; }

    public bool CanSendPiCommand => !IsPiCommandRunning;

    [ObservableProperty]
    public partial bool SpeakAll { get; set; }

    private string PortSettingName => IsSsh ? "sshPort" : "httpPort";

    private int DefaultPort => IsSsh ? ConnectionSettings.DefaultSshPort : ConnectionSettings.DefaultHttpPort;

    private string SecretKey => $"{(IsSsh ? "ssh" : "http")}:{User.Trim()}@{Host.Trim()}:{Port.Trim()}";

    /// <summary>Adds files chosen in the file picker or dropped on the window. A file with the same name is replaced.</summary>
    public void AddFiles(IEnumerable<(string Name, byte[] Data)> added)
    {
        foreach ((string name, byte[] data) in added)
        {
            FileEntry? existing = Files.FirstOrDefault(f => f.Name == name);
            if (existing is not null)
            {
                Files.Remove(existing);
            }
            Files.Add(new FileEntry(name, data));
        }
        RefreshScripts();
        string? newest = added.Select(file => file.Name).LastOrDefault(name => name.EndsWith(".py", StringComparison.OrdinalIgnoreCase));
        if (newest is not null)
        {
            SelectedScript = newest;
        }
    }

    [RelayCommand]
    private void RemoveFile(FileEntry file)
    {
        Files.Remove(file);
        RefreshScripts();
    }

    /// <summary>The connection settings from the screen, or null (with ErrorText set) if something is missing.</summary>
    private ConnectionSettings? ReadSettings()
    {
        ErrorText = "";
        if (string.IsNullOrWhiteSpace(Host))
        {
            ErrorText = "Enter the Pi's IP address.";
            return null;
        }
        if (!int.TryParse(Port, out int port) || port is < 1 or > 65535)
        {
            ErrorText = "The port must be a number from 1 to 65535.";
            return null;
        }
        if (string.IsNullOrEmpty(Secret))
        {
            ErrorText = IsSsh ? "Enter the Pi account's password." : "Enter the Pi's PIN.";
            return null;
        }
        if (!int.TryParse(TimeLimitSeconds, out int maxSeconds) || maxSeconds is < 0 or > ConnectionSettings.LongestMaxSeconds)
        {
            ErrorText = $"The time limit must be a number of seconds from 0 (no limit) to {ConnectionSettings.LongestMaxSeconds}.";
            return null;
        }
        SaveSettings();
        return new ConnectionSettings(IsSsh ? ConnectionMode.Ssh : ConnectionMode.Http,
            Host.Trim(), port, User.Trim(), Secret, maxSeconds);
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        ConnectionSettings? wanted = ReadSettings();
        if (wanted is null)
        {
            return;
        }
        try
        {
            if (connection is null || wanted != connectionSettings)
            {
                await CloseConnectionAsync();
                IRunnerConnection created = ConnectionMode.Ssh == wanted.Mode
                    ? new SshRunnerConnection(wanted, settings)
                    : new HttpRunnerConnection(wanted);
                // Events from a connection the app has since closed are ignored, so they cannot end a newer run.
                created.EventReceived += e => ui.Post(() =>
                {
                    if (ReferenceEquals(created, connection))
                    {
                        OnRunnerEvent(e);
                    }
                });
                connection = created;
                connectionSettings = wanted;
                IsConnected = true;
            }
            platform.Speech.StopSpeaking();
            ClearOutput();
            StatusText = "Connecting…";
            IsRunning = true;
            List<RunFile> files = Files.Select(f => new RunFile(f.Name, f.Data)).ToList();
            await connection.StartAsync(files, SelectedScript!);
            SaveRememberedSecret();
        }
        catch (RunnerException error)
        {
            IsRunning = false;
            StatusText = "Ready";
            ErrorText = error.Message;
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        platform.Speech.StopSpeaking();
        if (connection is null || !IsRunning)
        {
            return;
        }
        StatusText = "Stopping…";
        try
        {
            await connection.StopAsync();
        }
        catch (RunnerException error)
        {
            ErrorText = error.Message;
        }
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        string line = InputText;
        InputText = "";
        if (connection is null || !IsRunning)
        {
            return;
        }
        try
        {
            await connection.SendInputAsync(line);
        }
        catch (RunnerException error)
        {
            ErrorText = error.Message;
        }
    }

    /// <summary>Runs one command on the Pi (SSH mode), on its own connection, and shows its output.</summary>
    [RelayCommand]
    private async Task RunPiCommandAsync()
    {
        string command = PiCommandText.Trim();
        if (0 == command.Length)
        {
            return;
        }
        await RunOnPiAsync("$ " + command, command);
    }

    /// <summary>
    /// Stops every Python program this account started on the Pi, even when the app does not know about it:
    /// Ctrl+C first (so finally: blocks can stop motors), then force-quit after 2 seconds.
    /// </summary>
    [RelayCommand]
    private async Task ForceStopAsync()
    {
        platform.Speech.StopSpeaking();
        await RunOnPiAsync("Force stop all Python", PiCommand.ForceStopAllPython);
        if (IsRunning)
        {
            // The script's session normally ends by itself once the script is gone; if it does not, close it.
            for (int waited = 0; IsRunning && waited < 30; ++waited)
            {
                await Task.Delay(100);
            }
            if (IsRunning)
            {
                await CloseConnectionAsync();
                IsRunning = false;
                StatusText = "Ready";
                Append("[The app closed its connection to the stopped script.]\n");
                ShowOutput();
            }
        }
    }

    /// <summary>
    /// Closes the connection to the Pi at any time and returns the app to Ready. A running script is stopped first:
    /// in SSH mode closing the session sends it Ctrl+C; in HTTP mode the app asks the server to stop it.
    /// </summary>
    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (connection is null)
        {
            return;
        }
        platform.Speech.StopSpeaking();
        bool wasRunning = IsRunning;
        if (wasRunning && connection is HttpRunnerConnection)
        {
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await connection.StopAsync(limit.Token);
            }
            catch (Exception error) when (error is RunnerException or OperationCanceledException)
            {
                Append("[Could not ask the Pi to stop the script: " + error.Message + "]\n");
            }
        }
        Handle(parser.Flush());
        await CloseConnectionAsync();
        IsRunning = false;
        StatusText = "Ready";
        Append(wasRunning
            ? "\n[Disconnected from the Pi. The script was sent Ctrl+C. If the robot is still moving, use \"Force stop all Python\".]\n"
            : "\n[Disconnected from the Pi.]\n");
        ShowOutput();
    }

    private async Task RunOnPiAsync(string title, string command)
    {
        ConnectionSettings? target = ReadSettings();
        if (target is null)
        {
            return;
        }
        IsPiCommandRunning = true;
        Handle(parser.Flush());
        Append("\n[" + title + "]\n");
        ShowOutput();
        try
        {
            CommandResult result = await PiCommand.RunAsync(target with { Mode = ConnectionMode.Ssh }, settings, command);
            Append(result.Output.EndsWith('\n') || 0 == result.Output.Length ? result.Output : result.Output + "\n");
            Append($"[exit code {result.ExitCode?.ToString() ?? "unknown"}]\n");
        }
        catch (RunnerException error)
        {
            ErrorText = error.Message;
        }
        finally
        {
            IsPiCommandRunning = false;
            ShowOutput();
        }
    }

    [RelayCommand]
    private void Clear()
    {
        ClearOutput();
        ErrorText = "";
    }

    [RelayCommand]
    private void ForgetHostKey()
    {
        if (int.TryParse(Port, out int port))
        {
            settings.Forget(Host.Trim(), port);
            ErrorText = "";
            StatusText = $"Forgot the saved SSH key for {Host.Trim()}. The next connection saves the new one.";
        }
    }

    /// <summary>Closes the connection when the app closes; a running script receives Ctrl+C.</summary>
    public Task ShutdownAsync() => CloseConnectionAsync();

    partial void OnSelectedModeIndexChanged(int value)
    {
        Port = settings.Get(PortSettingName, DefaultPort.ToString());
        LoadRememberedSecret();
    }

    partial void OnRememberSecretChanged(bool value)
    {
        settings.SetBool("rememberSecret", value);
        if (!value)
        {
            platform.Secrets.Delete(SecretKey);
        }
    }

    partial void OnSpeakSayChanged(bool value) => settings.SetBool("speakSay", value);

    partial void OnSpeakAllChanged(bool value) => settings.SetBool("speakAll", value);

    private void OnRunnerEvent(RunnerEvent runnerEvent)
    {
        switch (runnerEvent)
        {
            case OutputEvent output:
                Handle(parser.Feed(output.Text));
                break;
            case EchoEvent echo:
                TextItem? prompt = parser.CommitPartial();
                if (prompt is not null)
                {
                    Append(prompt.Text);
                }
                Append(echo.Text);
                break;
            case NoticeEvent notice:
                Handle(parser.Flush());
                Append("[" + notice.Text + "]\n");
                break;
            case StartedEvent started:
                StatusText = "Running " + started.FileName;
                break;
            case ExitedEvent exited:
                Handle(parser.Flush());
                string ending = exited.Message ?? ((exited.Stopped ? "Stopped" : "Finished") +
                    (exited.Code is null ? "" : $" (exit code {exited.Code})"));
                Append("\n[" + ending + "]\n");
                IsRunning = false;
                StatusText = "Ready";
                break;
        }
        ShowOutput();
    }

    private void Handle(IReadOnlyList<OutputItem> items)
    {
        foreach (OutputItem item in items)
        {
            switch (item)
            {
                case TextItem text:
                    Append(text.Text);
                    if (SpeakAll && 0 < text.Text.Trim().Length)
                    {
                        platform.Speech.Speak(text.Text.Trim());
                    }
                    break;
                case SayItem say:
                    Append("🔊 " + say.Text + "\n");
                    if (SpeakSay || SpeakAll)
                    {
                        platform.Speech.Speak(say.Text);
                    }
                    break;
                case PlayItem play:
                    Append("♪ " + play.FileName + "\n");
                    _ = PlayAsync(play.FileName);
                    break;
                case ShowItem show:
                    Append("🖼 " + show.FileName + "\n");
                    _ = ShowImageAsync(show.FileName);
                    break;
            }
        }
    }

    private async Task PlayAsync(string fileName)
    {
        try
        {
            byte[] data = await connection!.GetFileAsync(fileName);
            await platform.Audio.PlayAsync(fileName, data);
        }
        catch (Exception error) when (error is RunnerException or IOException or InvalidOperationException)
        {
            ui.Post(() => { Append($"[Could not play {fileName}: {error.Message}]\n"); ShowOutput(); });
        }
    }

    private async Task ShowImageAsync(string fileName)
    {
        try
        {
            byte[] data = await connection!.GetFileAsync(fileName);
            var bitmap = new Bitmap(new MemoryStream(data));
            ui.Post(() => AddImage(new ShownImage(fileName, data, bitmap)));
        }
        catch (Exception error)
        {
            string reason = fileName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                ? "SVG images are shown in the browser page only; use .png or .jpg in the app"
                : error.Message;
            ui.Post(() => { Append($"[Could not show {fileName}: {reason}]\n"); ShowOutput(); });
        }
    }

    private void Append(string text)
    {
        committed.Append(text);
        if (MaxOutputChars < committed.Length)
        {
            committed.Remove(0, committed.Length - MaxOutputChars);
        }
    }

    private void ShowOutput() => OutputText = committed + parser.LiveText;

    private void ClearOutput()
    {
        committed.Clear();
        parser = new OutputParser();
        OutputText = "";
    }

    /// <summary>Adds an image at the end and shows it, dropping the oldest past MaxImages.</summary>
    private void AddImage(ShownImage image)
    {
        Images.Add(image);
        while (MaxImages < Images.Count)
        {
            Bitmap oldest = Images[0].Bitmap;
            Images.RemoveAt(0);
            oldest.Dispose();
        }
        ImageIndex = Images.Count - 1;
        OnImagesChanged();
    }

    /// <summary>Updates everything shown about the images, after Images itself changed.</summary>
    private void OnImagesChanged()
    {
        foreach (string name in new[] { nameof(CurrentShownImage), nameof(CurrentImage), nameof(CurrentImageName),
                     nameof(ImagePosition), nameof(HasImages), nameof(CanShowPreviousImage), nameof(CanShowNextImage) })
        {
            OnPropertyChanged(name);
        }
    }

    [RelayCommand]
    private void PreviousImage()
    {
        if (CanShowPreviousImage)
        {
            --ImageIndex;
        }
    }

    [RelayCommand]
    private void NextImage()
    {
        if (CanShowNextImage)
        {
            ++ImageIndex;
        }
    }

    [RelayCommand]
    private void ClearImages()
    {
        List<ShownImage> old = Images.ToList();
        Images.Clear();
        ImageIndex = -1;
        OnImagesChanged();
        foreach (ShownImage image in old)
        {
            image.Bitmap.Dispose();
        }
    }

    /// <summary>Updates the list of scripts in place, so the selected script stays selected.</summary>
    private void RefreshScripts()
    {
        var names = Files.Where(f => f.IsScript).Select(f => f.Name).ToList();
        foreach (string gone in Scripts.Where(name => !names.Contains(name)).ToList())
        {
            Scripts.Remove(gone);
        }
        foreach (string added in names.Where(name => !Scripts.Contains(name)))
        {
            Scripts.Add(added);
        }
        if (SelectedScript is null || !Scripts.Contains(SelectedScript))
        {
            SelectedScript = Scripts.LastOrDefault();
        }
        OnPropertyChanged(nameof(CanRun));
    }

    private void SaveSettings()
    {
        settings.Set("mode", IsSsh ? "Ssh" : "Http");
        settings.Set("host", Host.Trim());
        settings.Set("user", User.Trim());
        settings.Set(PortSettingName, Port.Trim());
        settings.Set("timeLimitSeconds", TimeLimitSeconds.Trim());
    }

    private void LoadRememberedSecret()
    {
        Secret = RememberSecret && platform.Secrets.IsAvailable ? platform.Secrets.Load(SecretKey) ?? "" : "";
    }

    private void SaveRememberedSecret()
    {
        if (RememberSecret && platform.Secrets.IsAvailable && !platform.Secrets.Save(SecretKey, Secret))
        {
            ErrorText = "The password could not be saved in this computer's secure storage; you will need to type it again.";
        }
    }

    private async Task CloseConnectionAsync()
    {
        if (connection is not null)
        {
            IRunnerConnection closing = connection;
            connection = null;              // first, so events from the closing connection are ignored
            connectionSettings = null;
            IsConnected = false;
            await closing.DisposeAsync();
        }
    }
}

/// <summary>An image a script showed: the name it used, the file's bytes (for saving), and the decoded picture.</summary>
public sealed record ShownImage(string Name, byte[] Data, Bitmap Bitmap)
{
    /// <summary>The name without any folder, for saving (for example "photo.jpg" for "/tmp/photo.jpg").</summary>
    public string FileName => Path.GetFileName(Name.Replace('\\', '/'));
}
