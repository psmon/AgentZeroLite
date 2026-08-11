using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Agent.Common.Agents;

/// <summary>How to relaunch a given agent CLI into a prior conversation.</summary>
public sealed record ResumeSpec(string Executable, string ArgTemplate)
{
    /// <summary>Renders the full launch command for a session id ({id} placeholder).</summary>
    public string Render(string sessionId) => $"{Executable} {ArgTemplate.Replace("{id}", sessionId)}".Trim();
}

/// <summary>
/// Per-CLI native session-restore command table (herdr-adoption H3). When a
/// hosted agent reports its native conversation id, AgentZero can relaunch the
/// terminal into that conversation after a restart/crash — restoring the
/// *conversation*, not just an empty shell. Pure &amp; data-driven; ported from
/// herdr's documented resume commands.
/// </summary>
public static class AgentResumeCatalog
{
    // Keyed by a lowercase substring of the agent/tab name. Order = specificity.
    private static readonly (string Key, ResumeSpec Spec)[] Table =
    {
        ("claude",       new ResumeSpec("claude", "--resume {id}")),
        ("codex",        new ResumeSpec("codex", "resume {id}")),
        ("cursor",       new ResumeSpec("cursor-agent", "--resume {id}")),
        ("opencode",     new ResumeSpec("opencode", "--session {id}")),
        ("grok",         new ResumeSpec("grok", "--resume {id}")),
        ("copilot",      new ResumeSpec("copilot", "--resume={id}")),
        ("devin",        new ResumeSpec("devin", "--resume {id}")),
        ("droid",        new ResumeSpec("droid", "--resume {id}")),
        ("kimi",         new ResumeSpec("kimi", "--session {id}")),
        ("kilo",         new ResumeSpec("kilo", "--session {id}")),
        ("omp",          new ResumeSpec("omp", "--resume={id}")),
    };

    // Session ids are used to build a launch command line, so restrict them to
    // safe characters (defense against injection via a spoofed id).
    private static readonly Regex SafeId = new(@"^[A-Za-z0-9._\-:/]{1,128}$", RegexOptions.Compiled);

    /// <summary>Finds the resume spec for an agent/tab name, or null.</summary>
    public static ResumeSpec? Lookup(string? agentName)
    {
        var n = (agentName ?? "").ToLowerInvariant();
        foreach (var (key, spec) in Table)
            if (n.Contains(key)) return spec;
        return null;
    }

    /// <summary>
    /// Builds the full resume launch command for an agent + session id, or null
    /// when the agent is unknown or the id is unsafe/empty.
    /// </summary>
    public static string? BuildResumeCommand(string? agentName, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !SafeId.IsMatch(sessionId)) return null;
        return Lookup(agentName)?.Render(sessionId);
    }

    /// <summary>Agent keys that support native session restore.</summary>
    public static IEnumerable<string> SupportedAgents => Table.Select(t => t.Key);
}
