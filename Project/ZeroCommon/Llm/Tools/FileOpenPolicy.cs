namespace Agent.Common.Llm.Tools;

/// <summary>
/// Which files <c>open_file</c> may hand to the shell (M0032). Opening a file with its
/// default program is <c>ShellExecute</c>, and ShellExecute runs whatever the extension is
/// associated with — for <c>.exe</c>, <c>.lnk</c>, <c>.ps1</c> and friends that is
/// "run this". So this is an <b>allow-list</b> of media, image and document types, not a
/// deny-list of dangerous ones: a type nobody thought of is refused, not run.
/// </summary>
public static class FileOpenPolicy
{
    public const string KindMedia = "media";
    public const string KindImage = "image";
    public const string KindDocument = "document";

    private static readonly HashSet<string> Media = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".aiff",
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".wmv", ".mpg", ".mpeg",
    };

    private static readonly HashSet<string> Image = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".heic", ".avif",
    };

    private static readonly HashSet<string> Document = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".txt", ".md", ".markdown", ".csv", ".json", ".xml", ".log", ".rtf",
        ".docx", ".xlsx", ".pptx", ".doc", ".xls", ".ppt", ".hwp", ".epub",
    };

    /// <summary>
    /// True when the file may be opened, with <paramref name="kind"/> set to one of the
    /// <c>Kind*</c> constants. Decided on the file name alone so it can run before any disk
    /// access and be tested without one.
    /// </summary>
    public static bool TryClassify(string? path, out string kind, out string error)
    {
        kind = "";
        error = "";
        var name = Path.GetFileName((path ?? "").Trim());
        var ext = Path.GetExtension(name);
        if (string.IsNullOrEmpty(ext) || ext == ".")
        {
            error = "files without an extension cannot be opened";
            return false;
        }
        if (Media.Contains(ext)) kind = KindMedia;
        else if (Image.Contains(ext)) kind = KindImage;
        else if (Document.Contains(ext)) kind = KindDocument;
        else
        {
            error = $"'{ext}' is not an openable file type (media, image and document files only)";
            return false;
        }
        return true;
    }
}
