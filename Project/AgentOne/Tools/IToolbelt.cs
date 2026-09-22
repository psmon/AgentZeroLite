using AgentOne.Agent;

namespace AgentOne.Tools;

/// <param name="Ok">False for a refusal or failure — the text still goes back to the model so it can correct course.</param>
public readonly record struct ToolResult(bool Ok, string Text)
{
    public static ToolResult Success(string text) => new(true, text);
    public static ToolResult Failure(string text) => new(false, text);
}

/// <summary>The agent's entire side-effect surface. Everything a run can touch goes through here.</summary>
public interface IToolbelt
{
    /// <summary>What this toolbelt is bound to — shown in --verbose and in the system prompt.</summary>
    string Scope { get; }

    Task<ToolResult> InvokeAsync(ToolCall call, CancellationToken ct);
}
