using System;
using System.Collections.Generic;
using System.Linq;
using Agent.Common.Services;

namespace Agent.Common.Agents;

/// <summary>
/// Which agent conversations one terminal tab ran — kept on the tab's row
/// (<see cref="Data.Entities.CliTab.AgentSessions"/>) so that, when the tab starts again, the
/// resume question can name <em>this tab's</em> last session. The folder's session list is
/// shared by every agent tab opened in that workspace; with several of them, the newest entry
/// is often another tab's conversation.
///
/// <para>The ids come from the agents' own live status (Claude's <c>sessions/&lt;pid&gt;.json</c>,
/// Codex's rollout), which the tracker already matches to a tab through its process tree. A few
/// are kept, newest first, because the newest is not always resumable: a tab opened and left
/// without a prompt reports a session the agent never wrote, and the agents' pickers leave those
/// out — then the one before it is still this tab's.</para>
///
/// <para>Stored as <c>agent:id|agent:id</c>. <c>|</c> is not a safe id character
/// (<see cref="AgentResumeCatalog.IsSafeId"/>), and the agent is split at the first <c>:</c>.</para>
/// </summary>
public static class TabSessionMemory
{
    /// <summary>How many conversations a tab remembers.</summary>
    public const int Keep = 3;

    /// <summary>The remembered conversations, newest first; malformed or unsafe entries are skipped.</summary>
    public static IReadOnlyList<(string Agent, string Id)> Parse(string? stored)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(stored)) return result;
        foreach (var part in stored.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = part.IndexOf(':');
            if (colon <= 0 || colon == part.Length - 1) continue;
            var agent = part[..colon].ToLowerInvariant();
            var id = part[(colon + 1)..];
            if (!AgentResumeCatalog.IsSafeId(id)) continue;
            result.Add((agent, id));
        }
        return result;
    }

    /// <summary>
    /// <paramref name="stored"/> with <paramref name="session"/> moved to the front, or null when
    /// it already is the newest entry (nothing to save) or the id is unsafe.
    /// </summary>
    public static string? Push(string? stored, AgentSessionRef session)
    {
        var agent = (session.Agent ?? "").Trim().ToLowerInvariant();
        if (agent.Length == 0 || agent.Contains(':') || agent.Contains('|')) return null;
        if (!AgentResumeCatalog.IsSafeId(session.SessionId)) return null;

        var entries = Parse(stored).ToList();
        if (entries.Count > 0 && Same(entries[0], agent, session.SessionId)) return null;
        entries.RemoveAll(e => Same(e, agent, session.SessionId));
        entries.Insert(0, (agent, session.SessionId));
        return string.Join("|", entries.Take(Keep).Select(e => $"{e.Agent}:{e.Id}"));
    }

    /// <summary>Whether <paramref name="stored"/> names any conversation of <paramref name="tool"/>.</summary>
    public static bool Has(string? stored, AgentCliTool tool) =>
        Parse(stored).Any(e => e.Agent == tool.Command.ToLowerInvariant());

    /// <summary>
    /// The newest of this tab's <paramref name="tool"/> conversations that is in
    /// <paramref name="sessions"/> (the list the agent's store offers for the folder and profile
    /// the tab runs), or null. An id that is not in the list — another profile's, a session left
    /// without a prompt, one deleted since — is passed over: resuming it would fail.
    /// </summary>
    public static AgentSessionInfo? Pick(string? stored, AgentCliTool tool, IReadOnlyList<AgentSessionInfo> sessions)
    {
        var agent = tool.Command.ToLowerInvariant();
        foreach (var (a, id) in Parse(stored))
        {
            if (a != agent) continue;
            var hit = sessions.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return null;
    }

    private static bool Same((string Agent, string Id) e, string agent, string id) =>
        e.Agent == agent && string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A tab that keeps its own <see cref="TabSessionMemory"/>. A host whose tabs do not implement it
/// (the WPF reference host) keeps the stored value through a save instead of erasing it — see
/// <see cref="Module.CliWorkspacePersistence.SaveCliGroups"/>.
/// </summary>
public interface IAgentSessionMemory
{
    /// <summary>The stored form, <c>agent:id|agent:id</c>, or null.</summary>
    string? AgentSessions { get; }
}
