using System.Collections.Generic;
using System.Linq;

namespace Agent.Common.Agents;

/// <summary>Per-tab agent state + whether the user has looked at it since it settled.</summary>
public sealed record AgentTabState(string TabId, AgentActivity Activity, bool Seen);

/// <summary>
/// Pure aggregation of per-tab agent states up to a tab/workspace summary
/// (herdr-adoption H2). Drives the "which of my agents needs me" workflow:
/// a blocked agent colors its whole workspace blocked; a finished-but-unseen
/// agent (done + !seen) stays flagged for attention until focused.
/// </summary>
public static class AgentStateRollup
{
    /// <summary>
    /// Rolls children up to a single state. Priority (most-urgent first):
    /// Blocked → Working → unseen-Done → Idle → seen-Done/Unknown.
    /// </summary>
    public static AgentActivity Rollup(IEnumerable<AgentTabState> children)
    {
        var list = children as IReadOnlyList<AgentTabState> ?? children.ToList();
        if (list.Count == 0) return AgentActivity.Unknown;

        if (list.Any(c => c.Activity == AgentActivity.Blocked)) return AgentActivity.Blocked;
        if (list.Any(c => c.Activity == AgentActivity.Working)) return AgentActivity.Working;
        if (list.Any(c => c.Activity == AgentActivity.Done && !c.Seen)) return AgentActivity.Done;
        if (list.Any(c => c.Activity == AgentActivity.Idle)) return AgentActivity.Idle;
        if (list.Any(c => c.Activity == AgentActivity.Done)) return AgentActivity.Done; // all seen
        return AgentActivity.Unknown;
    }

    /// <summary>
    /// True when at least one child needs the user: blocked, or finished but not
    /// yet seen. (A seen 'done' or a working agent does NOT demand attention.)
    /// </summary>
    public static bool NeedsAttention(IEnumerable<AgentTabState> children)
        => children.Any(c => c.Activity == AgentActivity.Blocked
                          || (c.Activity == AgentActivity.Done && !c.Seen));

    /// <summary>Counts children needing attention (for a badge).</summary>
    public static int AttentionCount(IEnumerable<AgentTabState> children)
        => children.Count(c => c.Activity == AgentActivity.Blocked
                            || (c.Activity == AgentActivity.Done && !c.Seen));
}
