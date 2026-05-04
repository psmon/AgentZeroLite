namespace Agent.Common.Data.Entities;

/// <summary>
/// One row per observed change in Claude Code's per-model rate-limit telemetry.
/// Sourced from the AgentZero statusLine wrapper's per-tick snapshot files at
/// %LOCALAPPDATA%\AgentZeroLite\cc-hud-snapshots\{accountKey}.json.
///
/// The wrapper writes the latest stdin payload on every Claude Code statusline
/// tick (~300 ms). The collector reads these files every 30 s and INSERTs only
/// when the (5h%, 7d%, resetsAt) tuple differs from the latest existing row for
/// the same (Vendor, AccountKey, Model) — so a single row per state-change.
///
/// Index (AccountKey, Model, ObservedAtUtc DESC) backs the latest-per-pair
/// lookup the widget uses.
/// </summary>
public class TokenRemainingObservation
{
    public long Id { get; set; }

    public string Vendor { get; set; } = "claude";   // currently always "claude"
    public string AccountKey { get; set; } = "";     // CLAUDE_CONFIG_DIR sibling: "claude" / "claude-pel3" / ...
    public string Model { get; set; } = "";          // e.g. "Opus 4.7 (1M context)" — verbatim from stdin display_name

    public int FiveHourPercent { get; set; }
    public DateTime? FiveHourResetsAtUtc { get; set; }

    public int SevenDayPercent { get; set; }
    public DateTime? SevenDayResetsAtUtc { get; set; }

    public DateTime ObservedAtUtc { get; set; }
}
