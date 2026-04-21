namespace Agent.Common.Data.Entities;

public class ClipboardEntry
{
    public int Id { get; set; }
    public string Content { get; set; } = "";
    public string Source { get; set; } = "";      // e.g. "AbsPath", "RelPath", "FileName", "CodeCopy"
    public DateTime CopiedAt { get; set; } = DateTime.UtcNow;
}
