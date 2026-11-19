using System.Reflection;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ScriptRunner.App.ViewModels;

/// <summary>A file an example needs: its path in the examples folder, and whether it is added with Local checked.</summary>
public sealed record ExampleFile(string Path, bool IsLocal)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>One of the repository's examples, as listed in examples/examples.json.</summary>
public sealed record Example(string Group, string Script, string Shows, string Needs, IReadOnlyList<ExampleFile> With)
{
    public string Name => Path.GetFileName(Script);

    /// <summary>The script first, then the files it needs.</summary>
    public IReadOnlyList<ExampleFile> AllFiles => new[] { new ExampleFile(Script, false) }.Concat(With).ToList();

    /// <summary>For example "Add with it: robot_api.py, remote_vision.py (Local)", or "".</summary>
    public string WithText => 0 == With.Count ? "" :
        "Comes with: " + string.Join(", ", With.Select(f => f.Name + (f.IsLocal ? " (Local)" : "")));

    public bool HasWith => 0 < With.Count;
}

/// <summary>A heading on the Examples page and its examples.</summary>
public sealed record ExampleGroup(string Name, IReadOnlyList<Example> Examples);

/// <summary>The examples built into the app (the repository's examples folder, embedded when the app is built).</summary>
public static class ExampleCatalog
{
    private const string Prefix = "examples/";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Resources = new(() =>
        typeof(ExampleCatalog).Assembly.GetManifestResourceNames()
            .Select(name => (Resource: name, Path: name.Replace('\\', '/')))      // Windows builds name folders with \
            .Where(entry => entry.Path.StartsWith(Prefix, StringComparison.Ordinal))
            .ToDictionary(entry => entry.Path[Prefix.Length..], entry => entry.Resource));

    public static IReadOnlyList<Example> Examples { get; } = Load();

    /// <summary>An example file's contents.</summary>
    public static byte[] Read(string path)
    {
        if (!Resources.Value.TryGetValue(path, out string? resource))
        {
            throw new FileNotFoundException("The example file is not built into the app.", path);
        }
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static IReadOnlyList<Example> Load()
    {
        try
        {
            using JsonDocument manifest = JsonDocument.Parse(Read("examples.json"));
            var examples = new List<Example>();
            foreach (JsonElement entry in manifest.RootElement.GetProperty("examples").EnumerateArray())
            {
                var with = new List<ExampleFile>();
                if (entry.TryGetProperty("with", out JsonElement files))
                {
                    with.AddRange(files.EnumerateArray().Select(file => new ExampleFile(
                        file.GetProperty("path").GetString()!,
                        file.TryGetProperty("local", out JsonElement local) && local.GetBoolean())));
                }
                examples.Add(new Example(entry.GetProperty("group").GetString()!, entry.GetProperty("script").GetString()!,
                    entry.GetProperty("shows").GetString()!, entry.GetProperty("needs").GetString()!, with));
            }
            return examples;
        }
        catch (Exception error) when (error is FileNotFoundException or JsonException or KeyNotFoundException
                                          or InvalidOperationException)
        {
            return Array.Empty<Example>();
        }
    }
}

/// <summary>The Examples page: choosing an example adds its files, ready to run.</summary>
public sealed partial class MainViewModel
{
    public IReadOnlyList<ExampleGroup> ExampleGroups { get; } = ExampleCatalog.Examples
        .GroupBy(example => example.Group)
        .Select(group => new ExampleGroup(group.Key, group.ToList()))
        .ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMainOpen))]
    public partial bool IsExamplesOpen { get; set; }

    partial void OnIsExamplesOpenChanged(bool value)
    {
        if (value)
        {
            IsSettingsOpen = false;
        }
    }

    partial void OnIsSettingsOpenChanged(bool value)
    {
        if (value)
        {
            IsExamplesOpen = false;
        }
    }

    [RelayCommand]
    private void ToggleExamples() => IsExamplesOpen = !IsExamplesOpen;

    /// <summary>Replaces the file list with an example's files (built into the app, so Reload leaves them as they are).</summary>
    [RelayCommand]
    private void UseExample(Example example) =>
        UseExampleFiles(example, example.AllFiles.Select(file => new AddedFile(file.Name, ExampleCatalog.Read(file.Path), null)).ToList());

    /// <summary>Replaces the file list with an example's files, then selects its script and closes the page.</summary>
    public void UseExampleFiles(Example example, IReadOnlyList<AddedFile> files)
    {
        Files.Clear();
        AddFiles(files, example.With.Where(file => file.IsLocal).Select(file => file.Name).ToList());
        SelectedScript = example.Name;
        IsExamplesOpen = false;
        ErrorText = "";
        StatusText = $"Example {example.Name} is ready: press Run.";
    }
}
