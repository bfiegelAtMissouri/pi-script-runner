using System.Text;
using Avalonia.Media.Imaging;
using ScriptRunner.Core;

namespace ScriptRunner.App.ViewModels;

/// <summary>
/// A file a script showed with show(): the name it used, the file's bytes (for saving), and how it is displayed:
/// as a picture, as the first lines of a text file, or as a card with only its name and size.
/// </summary>
public sealed class ShownFile : IDisposable
{
    /// <summary>Files the app tries to display as pictures.</summary>
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp",
    };

    /// <summary>Files whose first lines are shown as a preview.</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".tsv", ".txt", ".log", ".json", ".py", ".md", ".xml", ".yaml", ".yml", ".ini", ".cfg",
    };

    /// <summary>Most lines and characters of a text file shown in its card.</summary>
    public const int PreviewLines = 20;
    public const int PreviewCharacters = 2000;

    private ShownFile(string name, byte[] data, Bitmap? bitmap, string? preview, string note)
    {
        Name = name;
        Data = data;
        Bitmap = bitmap;
        Preview = preview;
        Note = note;
    }

    public string Name { get; }

    public byte[] Data { get; }

    /// <summary>The picture, or null when the file is not displayed as one.</summary>
    public Bitmap? Bitmap { get; }

    /// <summary>The first lines of a text file, or null.</summary>
    public string? Preview { get; }

    /// <summary>A line about the file for its card, for example "2.4 KB, no preview".</summary>
    public string Note { get; }

    /// <summary>The name without any folder, for saving (for example "photo.jpg" for "/tmp/photo.jpg").</summary>
    public string FileName => Path.GetFileName(Name.Replace('\\', '/'));

    /// <summary>True when the file is displayed as a picture.</summary>
    public bool IsImage => Bitmap is not null;

    /// <summary>True when the extension says the file is a picture the app can usually display.</summary>
    public static bool LooksLikeImage(string name) => ImageExtensions.Contains(Path.GetExtension(name));

    /// <summary>Decides how to display a file from its name and contents.</summary>
    public static ShownFile Create(string name, byte[] data)
    {
        string size = FileSizes.Describe(data.LongLength);
        string extension = Path.GetExtension(name);
        if (ImageExtensions.Contains(extension))
        {
            try
            {
                return new ShownFile(name, data, new Bitmap(new MemoryStream(data)), null, size);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException
                                          or NotSupportedException)
            {
                return new ShownFile(name, data, null, null, size + ", could not be displayed as an image");
            }
        }
        if (TextExtensions.Contains(extension) && !data.Contains((byte)0))
        {
            return new ShownFile(name, data, null, PreviewOf(data), size);
        }
        return new ShownFile(name, data, null, null, size + ", no preview");
    }

    /// <summary>The first lines of a text file, marked when there is more.</summary>
    private static string PreviewOf(byte[] data)
    {
        string text = new UTF8Encoding(false, false).GetString(data).Replace("\r\n", "\n");
        string[] lines = text.Split('\n');
        string preview = string.Join("\n", lines.Take(PreviewLines));
        bool more = PreviewLines < lines.Length && !(PreviewLines + 1 == lines.Length && 0 == lines[^1].Length);
        if (PreviewCharacters < preview.Length)
        {
            preview = preview[..PreviewCharacters];
            more = true;
        }
        return more ? preview.TrimEnd('\n') + "\n…" : preview.TrimEnd('\n');
    }

    public void Dispose() => Bitmap?.Dispose();
}
