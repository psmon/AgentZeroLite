using Agent.Common.Data.Entities;
using Agent.Common.Platform;
using Agent.Common.Services;

namespace Agent.Common.Agents;

/// <summary>
/// Which agent a terminal tab is running — "claude", "codex", "agentone", "netclaw", "hermes" or "shell" —
/// for web plugins (<c>sessions.list</c>: Agent Stage draws Claude ✳, Codex ◆ …).
///
/// <para>It used to be the tab's title alone: a tab renamed "agent개선", a custom profile such as
/// <c>claude-sam</c> (a PowerShell function that sets <c>CLAUDE_CONFIG_DIR</c> and runs claude), or a
/// plain PowerShell tab where claude was typed by hand all showed a shell. Now the evidence goes,
/// strongest first:</para>
/// <list type="number">
/// <item>the conversation the agent reported itself (Claude's status file, Hermes' lease);</item>
/// <item>the processes below the tab's shell — <c>claude.exe</c>, <c>codex.exe</c>, <c>agent-one</c>, <c>netclaw</c>, <c>hermes.exe</c>;</item>
/// <item>the tab's CLI definition — the built-in rows, and a profile command named after the tool
/// (<c>claude-sam</c>, <c>codex-work</c>);</item>
/// <item>the title, as before.</item>
/// </list>
/// Not thread-safe; the host calls it on the thread that runs its ACTIVE SESSIONS pass.
/// </summary>
public sealed class AgentKindDetector
{
    private readonly Func<ProcessTable> _snapshot;
    private readonly Func<long> _now;
    private ProcessTable? _table;
    private long _tableAt = long.MinValue;

    /// <summary>How old the process table may be (ps is a process launch on macOS/Linux).</summary>
    public long TableTtlMs { get; init; } = 3_000;

    public AgentKindDetector(Func<ProcessTable>? snapshot = null, Func<long>? nowMs = null)
    {
        _snapshot = snapshot ?? ProcessTable.Snapshot;
        _now = nowMs ?? (() => Environment.TickCount64);
    }

    /// <param name="title">The tab's title.</param>
    /// <param name="reported">The conversation the agent reported (from the tracker), if any.</param>
    /// <param name="shellPid">The PTY's child (the shell), when the session knows it.</param>
    /// <param name="definition">The CLI definition the tab was opened with.</param>
    public string KindOf(string title, AgentSessionRef? reported, int? shellPid, CliDefinition? definition)
    {
        if (reported is { Agent.Length: > 0 }) return reported.Agent;
        if (shellPid is { } pid && FromProcesses(pid) is { } running) return running;
        if (FromDefinition(definition) is { } defined) return defined;
        return FromTitle(title);
    }

    /// <summary>The nearest agent process below the shell, by executable name.</summary>
    internal string? FromProcesses(int shellPid)
    {
        if (_table is null || _now() - _tableAt >= TableTtlMs)
        {
            _table = _snapshot();
            _tableAt = _now();
        }
        foreach (var pid in _table.Descendants(shellPid))   // breadth first: the nearest wins
        {
            var name = _table.NameOf(pid);
            if (name.Length == 0) continue;
            foreach (var tool in AgentCliTools.All)
                if (string.Equals(name, tool.Command, StringComparison.OrdinalIgnoreCase))
                    return Wire(tool);
        }
        return null;
    }

    /// <summary>A built-in row (<see cref="AgentCliTools.Match(CliDefinition?)"/>), or a profile command named after a tool.</summary>
    public static string? FromDefinition(CliDefinition? definition)
    {
        if (definition is null) return null;
        if (AgentCliTools.Match(definition) is { } tool) return Wire(tool);

        // "claude-sam", "codex-work": the multi-account wrappers people define (a shell function or
        // script that sets the profile and runs the tool). The rule lives in AgentProfileCommand,
        // which the resume picker uses too.
        return AgentProfileCommand.Find(definition) is { } wrapper ? Wire(wrapper.Tool) : null;
    }

    /// <summary>The old rule: a title that starts with a tool's name ("Codex", "Claude-2").</summary>
    public static string FromTitle(string title)
    {
        foreach (var tool in AgentCliTools.All)
            if (title.StartsWith(tool.Name, StringComparison.OrdinalIgnoreCase))
                return Wire(tool);
        return "shell";
    }

    private static string Wire(AgentCliTool tool) => tool.Name.ToLowerInvariant();
}
