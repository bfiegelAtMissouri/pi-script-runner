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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSsh), nameof(SecretLabel))]
    public partial int SelectedModeIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    public partial string Host { get; set; } = "";

    [ObservableProperty]
    public partial string Port { get; set; } = "";

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

    [ObservableProperty]
    public partial Bitmap? CurrentImage { get; set; }

    [ObservableProperty]
    public partial string CurrentImageName { get; set; } = "";

    [ObservableProperty]
    public partial bool SpeakSay { get; set; }

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

    [RelayCommand]
    private async Task RunAsync()
    {
        ErrorText = "";
        if (!int.TryParse(Port, out int port) || port is < 1 or > 65535)
        {
            ErrorText = "The port must be a number from 1 to 65535.";
            return;
        }
        if (string.IsNullOrEmpty(Secret))
        {
            ErrorText = IsSsh ? "Enter the Pi account's password." : "Enter the Pi's PIN.";
            return;
        }
        SaveSettings();
        var wanted = new ConnectionSettings(IsSsh ? ConnectionMode.Ssh : ConnectionMode.Http,
            Host.Trim(), port, User.Trim(), Secret);
        try
        {
            if (connection is null || wanted != connectionSettings)
            {
                await CloseConnectionAsync();
                connection = ConnectionMode.Ssh == wanted.Mode
                    ? new SshRunnerConnection(wanted, settings)
                    : new HttpRunnerConnection(wanted);
                connection.EventReceived += e => ui.Post(() => OnRunnerEvent(e));
                connectionSettings = wanted;
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
            ui.Post(() =>
            {
                CurrentImage?.Dispose();
                CurrentImage = bitmap;
                CurrentImageName = fileName;
            });
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
        CurrentImage?.Dispose();
        CurrentImage = null;
        CurrentImageName = "";
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
            await connection.DisposeAsync();
            connection = null;
            connectionSettings = null;
        }
    }
}
