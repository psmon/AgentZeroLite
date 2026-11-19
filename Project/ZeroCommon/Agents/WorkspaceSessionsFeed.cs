namespace Agent.Common.Agents;

/// <summary>One running terminal as a web plugin sees it (<c>sessions.list</c>).</summary>
/// <param name="Subagents">Sub-agents its Claude, Codex or Hermes session is running or just ran (<see cref="ClaudeSubagentReader"/>, <see cref="CodexSubagentReader"/>, <see cref="HermesSubagentReader"/>); null for other agents.</param>
/// <param name="Harness">The garden harness its Claude session works in and the experts on duty in the main conversation (<see cref="ClaudeHarnessDutyReader"/>); null for other agents and other folders.</param>
public sealed record WorkspaceSessionView(
    int Group, int Tab, string Title, string Agent, string State, bool Focused, bool NeedsAttention,
    IReadOnlyList<SubagentView>? Subagents = null, HarnessView? Harness = null);

/// <summary>One workspace with its running terminals, in sidebar order.</summary>
public sealed record WorkspaceView(
    int Group, string Name, string Path, bool Active, IReadOnlyList<WorkspaceSessionView> Sessions);

/// <summary>
/// The workspaces and their running terminals, for web plugins (<c>sessions.list</c> /
/// <c>sessions.focus</c> in <see cref="Browser.ZeroBridgeDispatcher"/>). A GUI host publishes a
/// fresh snapshot each time its ACTIVE SESSIONS pass runs and registers how to go to a terminal;
/// a host that never publishes answers <c>supported: false</c>, so a plugin can say so instead of
/// showing an empty stage. Snapshots are immutable and swapped whole, so the plugin's bridge
/// thread can read while the UI thread publishes.
/// </summary>
public static class WorkspaceSessionsFeed
{
    private static volatile Snapshot? _current;
    private static Func<int, int, bool>? _focus;

    public sealed record Snapshot(DateTimeOffset UpdatedAt, IReadOnlyList<WorkspaceView> Workspaces);

    /// <summary>Null until a host has published (WPF does not yet).</summary>
    public static Snapshot? Current => _current;

    public static void Publish(IReadOnlyList<WorkspaceView> workspaces)
        => _current = new Snapshot(DateTimeOffset.Now, workspaces);

    /// <summary>The host's "go to this terminal" (group, tab) → whether it went.</summary>
    public static void SetFocusHandler(Func<int, int, bool>? focus) => _focus = focus;

    public static bool Focus(int group, int tab) => _focus?.Invoke(group, tab) ?? false;

    /// <summary>Back to "no host has published" — tests only (the feed is process-wide).</summary>
    internal static void Reset()
    {
        _current = null;
        _focus = null;
    }

    /// <summary>
    /// Which agent a tab runs, from its title alone — the last resort of <see cref="AgentKindDetector"/>,
    /// which hosts use (a renamed tab or a profile like claude-sam is not named after its tool).
    /// </summary>
    public static string AgentOf(string title) => AgentKindDetector.FromTitle(title);

    /// <summary>The wire name of a detected state; "running" when nothing was detected.</summary>
    public static string StateOf(TerminalAgentState? s) => s is null
        ? "running"
        : s.Activity switch
        {
            AgentActivity.Blocked => "blocked",
            AgentActivity.Working => "working",
            AgentActivity.Done when !s.Seen => "done",
            AgentActivity.Done => "done-seen",
            AgentActivity.Idle => "idle",
            _ => "running",
        };
}
