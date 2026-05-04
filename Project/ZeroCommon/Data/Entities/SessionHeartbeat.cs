namespace Agent.Common.Data.Entities;

/// <summary>
/// One row per (AccountKey, SessionId) pair observed via the AgentZero
/// statusLine wrapper. Heartbeat semantics — every wrapper tick UPSERTs
/// <see cref="LastSeenUtc"/> and bumps <see cref="TickCount"/>; the row
/// is created on first sight and updated forever.
///
/// Distinct from <see cref="TokenRemainingObservation"/> (state-change log
/// of rate-limit %s). Heartbeats answer "which Claude Code sessions were
/// active recently"; rate-limit observations answer "what's the current
/// account-wide quota"; the two have separate write cadences and dedupe
/// rules so they live in separate tables.
///
/// Wrapper v3 writes per-session snapshot files at
/// %LOCALAPPDATA%\AgentZeroLite\cc-hud-snapshots\{accountKey}\{sessionId}.json
/// — flat-file v2 layout (one file per account, last-write-wins) is
/// deprecated because concurrent sessions for the same account would
/// overwrite each other's writes.
///
/// Indices:
///   UNIQUE (AccountKey, SessionId) — upsert lookup
///   (LastSeenUtc DESC)             — "active in last N min" panel hot path
/// </summary>
public class SessionHeartbeat
{
    public long Id { get; set; }

    public string AccountKey { get; set; } = "";   // CLAUDE_CONFIG_DIR sibling: "claude" / "claude-pel3" / ...
    public string SessionId { get; set; } = "";    // Claude Code session_id (UUID)

    public string Cwd { get; set; } = "";          // workspace.current_dir or top-level cwd
    public string ProjectDir { get; set; } = "";   // workspace.project_dir (may equal Cwd)
    public string Model { get; set; } = "";        // model.display_name observed at LastSeenUtc

    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public long TickCount { get; set; }
}
