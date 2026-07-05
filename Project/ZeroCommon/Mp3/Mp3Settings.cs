namespace Agent.Common.Mp3;

/// <summary>
/// Agent Band local-MP3 settings (M0029). One scan root at a time — the
/// WebDev bridge maps it to the <c>https://mp3.local/</c> virtual host so
/// the plugin's &lt;audio&gt; element can stream files with native seeking.
/// </summary>
public class Mp3Settings
{
    /// <summary>Absolute path of the folder scanned for *.mp3 (recursive). Empty = not set.</summary>
    public string ScanFolder { get; set; } = "";
}
