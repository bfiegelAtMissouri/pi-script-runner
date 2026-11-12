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

    /// <summary>
    /// True when the file stays on this computer instead of going to the Pi. Local .py files are imported there, and
    /// the robot's script calls their functions with remote_execute().
    /// </summary>
    [ObservableProperty]
    public partial bool IsLocal { get; set; }
}

/// <summary>The app's only screen: connection, files, Run and Stop, output, input, and images.</summary>
public sealed partial class MainViewModel : ViewModelBase
{
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
        LoadLimits();
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

    /// <summary>Every file the scripts have shown, oldest first, kept across runs until Clear all.</summary>
    public ObservableCollection<ShownFile> ShownFiles { get; } = new();

    /// <summary>Which of ShownFiles is on screen, or -1 when there are none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentShown), nameof(CurrentImage), nameof(CurrentShownName),
        nameof(CurrentShownNote), nameof(CurrentPreview), nameof(IsCurrentCard), nameof(ShownPosition),
        nameof(HasShownFiles), nameof(CanShowPrevious), nameof(CanShowNext))]
    public partial int ShownIndex { get; set; } = -1;

    public ShownFile? CurrentShown => 0 <= ShownIndex && ShownIndex < ShownFiles.Count ? ShownFiles[ShownIndex] : null;

    /// <summary>The picture on screen, or null when the current file is shown as a card.</summary>
    public Bitmap? CurrentImage => CurrentShown?.Bitmap;

    public string CurrentShownName => CurrentShown?.Name ?? "";

    public string CurrentShownNote => CurrentShown?.Note ?? "";

    /// <summary>The first lines of a text file, or "" when there is no preview.</summary>
    public string CurrentPreview => CurrentShown?.Preview ?? "";

    /// <summary>True when the current file is shown as a card (not a picture).</summary>
    public bool IsCurrentCard => CurrentShown is { IsImage: false };

    /// <summary>For example "2 of 5".</summary>
    public string ShownPosition => HasShownFiles ? $"{ShownIndex + 1} of {ShownFiles.Count}" : "";

    public bool HasShownFiles => 0 < ShownFiles.Count;

    public bool CanShowPrevious => 0 < ShownIndex;

    public bool CanShowNext => ShownIndex < ShownFiles.Count - 1;

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
            var entry = new FileEntry(name, data) { IsLocal = existing?.IsLocal ?? false };   // a replaced file keeps its place
            entry.PropertyChanged += (_, e) =>
            {
                if (nameof(FileEntry.IsLocal) == e.PropertyName)
                {
                    RefreshScripts();           // a local file cannot be the script that runs on the Pi
                }
            };
            Files.Add(entry);
        }
        RefreshScripts();
        string? newest = added.Select(file => file.Name).LastOrDefault(name => Scripts.Contains(name));
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
            List<RunFile> files = Files.Where(f => !f.IsLocal).Select(f => new RunFile(f.Name, f.Data)).ToList();
            await StartLocalFunctionsAsync(Files.Where(f => f.IsLocal).Select(f => new RunFile(f.Name, f.Data)).ToList());
            StatusText = "Connecting…";
            await connection.StartAsync(files, SelectedScript!);
            SaveRememberedSecret();
        }
        catch (RunnerException error)
        {
            IsRunning = false;
            StatusText = "Ready";
            ErrorText = error.Message;
            await StopLocalFunctionsAsync();
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
        if (0 < pendingCalls)
        {
            heldInput.Add(line);        // sent once the laptop has answered, so it is not taken as the answer
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
            CommandResult result = await PiCommand.RunAsync(target with { Mode = ConnectionMode.Ssh }, settings, command,
                TimeSpan.FromSeconds(piCommandSeconds));
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
                _ = StopLocalFunctionsAsync();
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
                    Append((ShownFile.LooksLikeImage(show.FileName) ? "🖼 " : "📄 ") + show.FileName + "\n");
                    if (connection is not null)
                    {
                        showQueue = ShowFileAfterAsync(showQueue, connection, show.FileName);
                    }
                    break;
                case CallItem call:
                    if (connection is not null)
                    {
                        QueueCall(connection, call.RequestJson);
                    }
                    break;
            }
        }
    }

    private async Task PlayAsync(string fileName)
    {
        try
        {
            byte[] data = await connection!.GetFileAsync(fileName, LargestFileBytes);
            await platform.Audio.PlayAsync(fileName, data);
        }
        catch (Exception error) when (error is RunnerException or IOException or InvalidOperationException)
        {
            ui.Post(() => { Append($"[Could not play {fileName}: {error.Message}]\n"); ShowOutput(); });
        }
    }

    /// <summary>Shown files are downloaded one after another, so they appear in the order the script showed them.</summary>
    private Task showQueue = Task.CompletedTask;

    private async Task ShowFileAfterAsync(Task previous, IRunnerConnection source, string fileName)
    {
        await previous;                 // never throws: every failure below is reported instead
        try
        {
            byte[] data = await source.GetFileAsync(fileName, LargestFileBytes);
            ShownFile shown = ShownFile.Create(fileName, data);
            ui.Post(() => AddShown(shown));
        }
        catch (Exception error)
        {
            ui.Post(() => { Append($"[Could not show {fileName}: {error.Message}]\n"); ShowOutput(); });
        }
    }

    private void Append(string text)
    {
        committed.Append(text);
        if (outputCharactersKept < committed.Length)
        {
            committed.Remove(0, committed.Length - outputCharactersKept);
        }
    }

    private void ShowOutput() => OutputText = committed + parser.LiveText;

    private void ClearOutput()
    {
        committed.Clear();
        parser = new OutputParser();
        OutputText = "";
    }

    /// <summary>Adds a file at the end and shows it, dropping the oldest past the number kept.</summary>
    private void AddShown(ShownFile shown)
    {
        ShownFiles.Add(shown);
        DropOldestShown();
        ShownIndex = ShownFiles.Count - 1;
        OnShownFilesChanged();
    }

    private void DropOldestShown()
    {
        while (shownFilesKept < ShownFiles.Count)
        {
            ShownFile oldest = ShownFiles[0];
            ShownFiles.RemoveAt(0);
            oldest.Dispose();
        }
        if (ShownFiles.Count <= ShownIndex)
        {
            ShownIndex = ShownFiles.Count - 1;
        }
    }

    /// <summary>Updates everything shown about the files, after ShownFiles itself changed.</summary>
    private void OnShownFilesChanged()
    {
        foreach (string name in new[] { nameof(CurrentShown), nameof(CurrentImage), nameof(CurrentShownName),
                     nameof(CurrentShownNote), nameof(CurrentPreview), nameof(IsCurrentCard), nameof(ShownPosition),
                     nameof(HasShownFiles), nameof(CanShowPrevious), nameof(CanShowNext) })
        {
            OnPropertyChanged(name);
        }
    }

    [RelayCommand]
    private void PreviousShown()
    {
        if (CanShowPrevious)
        {
            --ShownIndex;
        }
    }

    [RelayCommand]
    private void NextShown()
    {
        if (CanShowNext)
        {
            ++ShownIndex;
        }
    }

    [RelayCommand]
    private void ClearShown()
    {
        List<ShownFile> old = ShownFiles.ToList();
        ShownFiles.Clear();
        ShownIndex = -1;
        OnShownFilesChanged();
        foreach (ShownFile shown in old)
        {
            shown.Dispose();
        }
    }

    /// <summary>Updates the list of scripts in place, so the selected script stays selected.</summary>
    private void RefreshScripts()
    {
        var names = Files.Where(f => f.IsScript && !f.IsLocal).Select(f => f.Name).ToList();
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
            IRunnerConnection closing = connection;
            connection = null;              // first, so events from the closing connection are ignored
            await StopLocalFunctionsAsync();
            connectionSettings = null;
            IsConnected = false;
            await closing.DisposeAsync();
        }
    }
}
