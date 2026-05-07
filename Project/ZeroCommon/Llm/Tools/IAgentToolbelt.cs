using System.Text.Json.Nodes;

namespace Agent.Common.Llm.Tools;

/// <summary>
/// Side-effect surface that a tool-using LLM (Gemma 4 / Nemotron Nano) acts
/// against — the agent's "toolbelt". The on-device model emits structured
/// tool calls; the toolbelt executes them. Real implementation talks to the
/// workspace + terminal actor topology; the test implementation
/// (<see cref="MockAgentToolbelt"/>) records calls and returns scripted
/// results so the LLM behavior can be verified deterministically.
/// </summary>
public interface IAgentToolbelt
{
    /// <summary>
    /// Returns the catalog of terminal groups and tabs as a compact JSON string,
    /// e.g. <c>{"groups":[{"index":0,"name":"main","tabs":[{"index":0,"title":"Claude","running":true}]}]}</c>.
    /// </summary>
    Task<string> ListTerminalsAsync(CancellationToken ct);

    /// <summary>
    /// Reads up to <paramref name="lastN"/> characters of recent output from
    /// the given terminal. Returns the raw text (may be multi-line).
    /// </summary>
    Task<string> ReadTerminalAsync(int group, int tab, int lastN, CancellationToken ct);

    /// <summary>
    /// Writes <paramref name="text"/> followed by Enter to the given terminal.
    /// Returns true if the terminal exists and accepted the write.
    /// </summary>
    Task<bool> SendToTerminalAsync(int group, int tab, string text, CancellationToken ct);

    /// <summary>
    /// Sends a control-key sequence (one of <c>cr</c>, <c>lf</c>, <c>crlf</c>,
    /// <c>esc</c>, <c>tab</c>, <c>backspace</c>, <c>del</c>, <c>ctrlc</c>,
    /// <c>ctrld</c>, <c>up</c>, <c>down</c>, <c>left</c>, <c>right</c>).
    /// </summary>
    Task<bool> SendKeyAsync(int group, int tab, string key, CancellationToken ct);

    // ====================== OS-control surface (mission M0014) ===============
    // Default implementations return a "not supported" JSON envelope so test
    // doubles (MockAgentToolbelt) and any future host that doesn't carry the
    // Win32 surface keep compiling. The production WPF host implements them.

    /// <summary>List visible top-level windows. Returns JSON envelope.</summary>
    Task<string> OsListWindowsAsync(string? titleFilter, CancellationToken ct)
        => Task.FromResult("{\"ok\":false,\"error\":\"os tools not available in this host\"}");

    /// <summary>Capture a PNG screenshot. <paramref name="hwnd"/> = 0 means full virtual desktop.</summary>
    Task<string> OsScreenshotAsync(long hwnd, bool grayscale, CancellationToken ct)
        => Task.FromResult("{\"ok\":false,\"error\":\"os tools not available in this host\"}");

    /// <summary>Bring a window to the foreground.</summary>
    Task<string> OsActivateAsync(long hwnd, CancellationToken ct)
        => Task.FromResult("{\"ok\":false,\"error\":\"os tools not available in this host\"}");

    /// <summary>UI Automation tree dump for a window.</summary>
    Task<string> OsElementTreeAsync(long hwnd, int maxDepth, string? search, CancellationToken ct)
        => Task.FromResult("{\"ok\":false,\"error\":\"os tools not available in this host\"}");

    /// <summary>Synthesize a mouse click at screen coordinates. Gated by approval.</summary>
    Task<string> OsMouseClickAsync(int x, int y, bool right, bool dbl, CancellationToken ct)
        => Task.FromResult("{\"ok\":false,\"error\":\"os tools not available in this host\"}");

    /// <summary>Synthesize a key press by spec ("ctrl+c", "alt+f4", "f5"). Gated by approval.</summary>
    Task<string> OsKeyPressAsync(string keySpec, CancellationToken ct)
        => Task.FromResult("{\"ok\":false,\"error\":\"os tools not available in this host\"}");
}

/// <summary>
/// Parsed tool-call result from one model turn.
/// </summary>
public sealed record ToolCall(string Tool, JsonObject Args);

/// <summary>
/// One full request → tool-call → result cycle inside an agent loop run.
/// </summary>
public sealed record ToolTurn(ToolCall Call, string ToolResult);

/// <summary>
/// Outcome of one <see cref="IAgentLoop.RunAsync"/> execution — turns,
/// final user-visible message, and termination metadata.
/// </summary>
public sealed record AgentLoopRun(
    IReadOnlyList<ToolTurn> Turns,
    string FinalMessage,
    bool TerminatedCleanly,
    string? FailureReason)
{
    public int TurnCount => Turns.Count;

    /// <summary>
    /// Guard activity over the run — non-zero values mean the loop hit
    /// repeat-blocks or transient-retry budgets. Defaults to <see cref="GuardStats.Empty"/>
    /// so callers from before the guard rollout keep compiling.
    /// </summary>
    public GuardStats GuardStats { get; init; } = GuardStats.Empty;
}
