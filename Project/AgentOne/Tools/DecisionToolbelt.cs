using AgentOne.Agent;
using AgentOne.Llm.Decision;

namespace AgentOne.Tools;

/// <summary>
/// The `decide` verb: the model puts a judgment call to the decision engine
/// (Jev) and gets back a choice, a confidence and the whole distribution.
///
/// Smart mode asks Jev questions the code chose; this is the other direction —
/// questions the model chooses, mid-turn, when it is torn between options it
/// already has. It neither reads nor changes anything, so it is not a guarded
/// family and the route does not rule it out (<see cref="ToolCatalog.IsRouteExempt"/>).
/// </summary>
/// <param name="engine">Null when there is no TypeSafe key — the verb then says so instead of failing silently.</param>
/// <param name="floor">The confidence below which an answer is reported as a lean, not a verdict.</param>
public sealed class DecisionToolbelt(IDecisionEngine? engine, double floor) : IToolbelt
{
    /// <summary>
    /// The person's words for the running turn, put in front of the model's
    /// context so the engine judges against what was asked — the model's own
    /// summary of it is exactly what a second opinion should not rely on.
    /// </summary>
    public Func<string?>? Request { get; init; }

    public string Scope => engine is null ? "unavailable (no TypeSafe key)" : $"{engine.Name}, floor {floor:0.00}";

    public async Task<ToolResult> InvokeAsync(ToolCall call, CancellationToken ct)
    {
        if (!string.Equals(call.Tool, "decide", StringComparison.OrdinalIgnoreCase))
            return ToolResult.Failure($"unknown tool '{call.Tool}'");

        if (engine is null)
            return ToolResult.Failure(
                "decide is unavailable: no TypeSafe key is set (`agent-one auth set --jev`). " +
                "Make the call yourself, and do not try decide again this turn.");

        var question = call.Arg("question").Trim();
        if (question.Length == 0)
            return ToolResult.Failure("decide needs 'question' — what is being decided, as a question");

        if (!DecisionInput.TryParseOptions(call.Arg("options"), out var options, out var error))
            return ToolResult.Failure("decide: " + error);

        var state = DecisionInput.BuildState(Request?.Invoke(), call.Arg("context"), question);
        var decision = await engine.ChooseAsync(state, question, options, ct);
        var result = DecisionInput.Shape(decision, options, question, floor);

        return result.Ok
            ? ToolResult.Success(DecisionInput.Describe(result))
            : ToolResult.Failure(
                $"the decision engine could not decide: {result.Message}. " +
                "Make the call yourself and say it was not checked.");
    }
}
