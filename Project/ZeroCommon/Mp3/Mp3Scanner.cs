using System.Text.RegularExpressions;

namespace Agent.Common.Mp3;

/// <summary>Tag + path facts for one scanned MP3 file (pre-classification).</summary>
public sealed record Mp3ScannedFile(
    string FilePath,
    string RelativePath,
    string FileName,
    string FolderName,
    string Title,
    string Artist,
    string Album,
    string TagGenre,
    double DurationSeconds,
    long FileSizeBytes);

/// <summary>
/// Local MP3 folder scanning + ID3 tag reading (M0029). TagLibSharp does the
/// tag parse; everything that can be pure (filename→title cleanup, relative
/// URL path, LLM classification hint) is a static helper so the headless
/// test suite can cover it without fixture MP3 files.
/// </summary>
public static class Mp3Scanner
{
    /// <summary>Recursive *.mp3 enumeration, sorted for stable scan order. Unreadable subdirs are skipped.</summary>
    public static IReadOnlyList<string> EnumerateMp3Files(string root)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        return Directory.EnumerateFiles(root, "*.mp3", opts)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Read one file's tags, falling back to filename-derived title when the
    /// tag is missing/corrupt. Never throws for a bad tag — a broken ID3
    /// header should not kill the whole scan job.
    /// </summary>
    public static Mp3ScannedFile ReadFile(string root, string path)
    {
        var fileName = Path.GetFileName(path);
        var folderName = Path.GetFileName(Path.GetDirectoryName(path) ?? "") ?? "";
        long size = 0;
        try { size = new FileInfo(path).Length; } catch { }

        string title = "", artist = "", album = "", genre = "";
        double durationSec = 0;
        try
        {
            using var tf = TagLib.File.Create(path);
            title  = tf.Tag.Title ?? "";
            artist = tf.Tag.FirstPerformer ?? tf.Tag.FirstAlbumArtist ?? "";
            album  = tf.Tag.Album ?? "";
            genre  = tf.Tag.FirstGenre ?? "";
            durationSec = tf.Properties?.Duration.TotalSeconds ?? 0;
        }
        catch
        {
            // corrupt/absent tag — filename fallback below still applies
        }
        if (string.IsNullOrWhiteSpace(title))
            title = TitleFromFileName(fileName);

        return new Mp3ScannedFile(
            FilePath: path,
            RelativePath: ToRelativePath(root, path),
            FileName: fileName,
            FolderName: folderName,
            Title: title.Trim(),
            Artist: artist.Trim(),
            Album: album.Trim(),
            TagGenre: genre.Trim(),
            DurationSeconds: durationSec,
            FileSizeBytes: size);
    }

    /// <summary>
    /// Human title from a bare filename: strips the extension, leading track
    /// numbers ("01. ", "03 - ", "1-02 "), and turns '_' into spaces.
    /// "01. IU - 좋은 날.mp3" → "IU - 좋은 날".
    /// </summary>
    public static string TitleFromFileName(string fileName)
    {
        var s = Path.GetFileNameWithoutExtension(fileName ?? "");
        // Disc-track prefix first ("1-02 …"), else a plain track number
        // ("01. ", "03 - "). Only one of the two strips runs so a title that
        // itself starts with digits ("99 Problems") isn't eaten twice.
        var stripped = Regex.Replace(s, @"^\s*\d{1,3}\s*-\s*\d{1,3}([.\-_)\]]|\s)+\s*", "");
        if (stripped == s)
            stripped = Regex.Replace(s, @"^\s*\d{1,3}([.\-_)\]]|\s)+\s*", "");
        s = stripped;
        s = s.Replace('_', ' ');
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        return s.Length > 0 ? s : Path.GetFileNameWithoutExtension(fileName ?? "");
    }

    /// <summary>
    /// Path of <paramref name="fullPath"/> relative to <paramref name="root"/>,
    /// '/'-separated (ready to append to the mp3.local virtual host). Returns
    /// "" when the file is not under the root.
    /// </summary>
    public static string ToRelativePath(string root, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(fullPath)) return "";
        string rel;
        try { rel = Path.GetRelativePath(root, fullPath); }
        catch { return ""; }
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return "";
        return rel.Replace('\\', '/');
    }

    /// <summary>
    /// First embedded picture (ID3 APIC — usually the album cover) as raw
    /// bytes + mime, or null when the tag has none / is unreadable.
    /// </summary>
    public static (byte[] Data, string Mime)? ReadCover(string path)
    {
        try
        {
            using var tf = TagLib.File.Create(path);
            var pic = tf.Tag.Pictures?.FirstOrDefault(p => p?.Data?.Count > 0);
            if (pic is null) return null;
            var mime = string.IsNullOrWhiteSpace(pic.MimeType) ? "image/jpeg" : pic.MimeType;
            return (pic.Data.Data, mime);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// One-line description of a track for the LLM classification prompt —
    /// tag fields first (strongest signal), then filename + folder name so a
    /// tagless "OST/포뇨 주제가.mp3" can still classify. Pure; unit-tested.
    /// </summary>
    public static string BuildClassifyHint(Mp3ScannedFile f)
    {
        var parts = new List<string>(6);
        if (!string.IsNullOrWhiteSpace(f.Title))    parts.Add($"제목: {f.Title}");
        if (!string.IsNullOrWhiteSpace(f.Artist))   parts.Add($"아티스트: {f.Artist}");
        if (!string.IsNullOrWhiteSpace(f.Album))    parts.Add($"앨범: {f.Album}");
        if (!string.IsNullOrWhiteSpace(f.TagGenre)) parts.Add($"태그장르: {f.TagGenre}");
        if (!string.IsNullOrWhiteSpace(f.FileName)) parts.Add($"파일명: {f.FileName}");
        if (!string.IsNullOrWhiteSpace(f.FolderName)) parts.Add($"폴더명: {f.FolderName}");
        return string.Join(" / ", parts);
    }
}
