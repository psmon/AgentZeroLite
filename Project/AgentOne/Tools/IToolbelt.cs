using AgentOne.Agent;

namespace AgentOne.Tools;

/// <param name="Ok">False for a refusal or failure — the text still goes back to the model so it can correct course.</param>
public readonly record struct ToolResult(bool Ok, string Text)
{
    public static ToolResult Success(string text) => new(true, text);
    public static ToolResult Failure(string text) => new(false, text);

    /// <summary>
    /// A call turned back before it ran and before anyone was asked — read the script first, the file is
    /// not there. Asking for it again once the cause is fixed is the expected next step, not repetition.
    /// </summary>
    public static ToolResult Held(string text) => new(false, text) { NotRun = true };

    /// <summary>True when the call was held back unrun, so the repeat guard must not count it.</summary>
    public bool NotRun { get; init; }
}

/// <summary>The agent's entire side-effect surface. Everything a run can touch goes through here.</summary>
public interface IToolbelt
{
    /// <summary>What this toolbelt is bound to — shown in --verbose and in the system prompt.</summary>
    string Scope { get; }

    Task<ToolResult> InvokeAsync(ToolCall call, CancellationToken ct);
}
