using System.Text.Json.Nodes;

namespace Agent.Common.Llm.Tools;

/// <summary>
/// Side-effect surface that a tool-using LLM (Gemma 4 / Nemotron Nano) acts
/// against. The on-device model emits structured tool calls; the host executes
/// them. Real implementation talks to the workspace + terminal actor topology;
/// the test implementation (<see cref="MockAgentToolHost"/>) records calls and
/// returns scripted results so the LLM behavior can be verified deterministically.
/// </summary>
public interface IAgentToolHost
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
}

/// <summary>
/// Parsed tool-call result from one model turn.
/// </summary>
public sealed record ToolCall(string Tool, JsonObject Args);

/// <summary>
/// One full request → tool-call → result cycle inside a session.
/// </summary>
public sealed record ToolTurn(ToolCall Call, string ToolResult);

/// <summary>
/// Outcome of running an <see cref="AgentToolLoop"/> session.
/// </summary>
public sealed record AgentToolSession(
    IReadOnlyList<ToolTurn> Turns,
    string FinalMessage,
    bool TerminatedCleanly,
    string? FailureReason)
{
    public int TurnCount => Turns.Count;
}
