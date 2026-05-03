namespace Agent.Common.Data.Entities;

/// <summary>
/// One row per LLM turn observed in a CLI transcript file.
/// Vendor=anthropic rows come from %USERPROFILE%\.claude\projects\**\*.jsonl
/// (assistant lines with message.usage). Vendor=openai rows come from
/// %USERPROFILE%\.codex\sessions\**\rollout-*.jsonl (event_msg payload
/// type=token_count). Dedupe key: RawRequestId for Claude, (SourceFile,
/// SourceLine) for Codex (Codex has no per-turn id).
/// </summary>
public class TokenUsageRecord
{
    public long Id { get; set; }

    public string Vendor { get; set; } = "";          // "anthropic" | "openai"
    public string AccountKey { get; set; } = "";      // organizationUuid (Claude) / plan_type:cli_version (Codex)
    public string SessionId { get; set; } = "";
    public string Cwd { get; set; } = "";
    public string GitBranch { get; set; } = "";
    public string Model { get; set; } = "";

    public DateTime RecordedAt { get; set; }

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheCreateTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long ReasoningTokens { get; set; }

    public string RawRequestId { get; set; } = "";   // Anthropic requestId — empty for Codex
    public string SourceFile { get; set; } = "";
    public long SourceLine { get; set; }
}
