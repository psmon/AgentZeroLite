namespace Agent.Common.Data.Entities;

/// <summary>
/// Per-file ingestion bookmark for the token usage collector. JSONL files
/// from Claude Code / Codex CLI are append-only, so persisting a byte offset
/// is enough to resume incrementally on every poll cycle without re-reading
/// the whole file.
/// </summary>
public class TokenSourceCheckpoint
{
    public long Id { get; set; }
    public string SourceFile { get; set; } = "";   // absolute path, also the natural key
    public string Vendor { get; set; } = "";       // "anthropic" | "openai"
    public long ByteOffset { get; set; }
    public long LineCount { get; set; }            // total lines parsed so far (Codex dedupe)
    public DateTime UpdatedAt { get; set; }

    // Pinned account for this file. Captured at first ingest and never
    // re-resolved — protects against account-switch races where a user
    // does `claude logout` / `claude /login` between collector ticks.
    // Within a single Claude Code session (= one JSONL file) the account
    // is invariant, so pinning per file is correct.
    public string AccountKey { get; set; } = "";
}
