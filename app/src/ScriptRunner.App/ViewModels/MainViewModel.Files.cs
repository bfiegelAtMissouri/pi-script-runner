using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptRunner.App.Services;

namespace ScriptRunner.App.ViewModels;

/// <summary>A file being added: its name, its contents, and how to read it again (null when it cannot be).</summary>
public sealed record AddedFile(string Name, byte[] Data, Func<Task<byte[]>>? Reader);

/// <summary>Reload: reading the added files again, so edits made in another editor are what runs.</summary>
public sealed partial class MainViewModel
{
    /// <summary>When true, every Run first reloads the files that changed since they were added.</summary>
    [ObservableProperty]
    public partial bool ReloadBeforeRun { get; set; } = true;

    partial void OnReloadBeforeRunChanged(bool value) => settings.SetBool("reloadBeforeRun", value);

    [RelayCommand]
    private async Task ReloadFilesAsync()
    {
        ErrorText = "";
        IReadOnlyList<string>? changed = await ReloadChangedFilesAsync();
        if (changed is null)
        {
            return;
        }
        int reloadable = Files.Count(file => file.Reader is not null);
        StatusText = 0 < changed.Count
            ? "Reloaded " + string.Join(", ", changed)
            : 0 < reloadable ? "No changes: the files are the same as when they were added or last reloaded." :
              "There are no files to reload.";
    }

    /// <summary>
    /// Reads every file again from where it was added and keeps the new contents of those that changed. Returns the
    /// names of the files that changed, or null (with ErrorText set) when a file could not be read; the files that
    /// could be read are still updated.
    /// </summary>
    private async Task<IReadOnlyList<string>?> ReloadChangedFilesAsync()
    {
        var changed = new List<string>();
        var problems = new List<string>();
        foreach (FileEntry file in Files.ToList())
        {
            if (file.Reader is null)
            {
                continue;
            }
            try
            {
                byte[] data = await file.Reader();
                if (!data.AsSpan().SequenceEqual(file.Data))
                {
                    file.Data = data;
                    file.Note = "updated " + DateTime.Now.ToString("t");
                    changed.Add(file.Name);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                              InvalidOperationException or NotSupportedException or ArgumentException)
            {
                DiagnosticLog.Write($"Reload of {file.Name} failed: {error.GetType().Name}: {error.Message}");
                file.Note = "could not be reloaded";
                problems.Add(file.Name);
            }
        }
        if (0 < problems.Count)
        {
            ErrorText = $"Could not read {string.Join(", ", problems)} again. If it was moved, renamed, or deleted, " +
                        "remove it from the list and add it again. Nothing was run.";
            return null;
        }
        return changed;
    }
}
