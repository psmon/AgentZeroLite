using AgentOne.Llm;
using AgentOne.Llm.Decision;

namespace AgentOne.Agent;

/// <param name="Options">What the planner proposed. Empty when planning did not produce a plan.</param>
/// <param name="Decision">Null when there was nothing to decide.</param>
/// <param name="Confident">True when the decision cleared the floor and can be acted on unasked.</param>
public sealed record SmartPlan(
    IReadOnlyList<DecisionOption> Options,
    Decision? Decision,
    bool Confident)
{
    public static readonly SmartPlan None = new([], null, false);

    /// <summary>True when the decision was that a person has to settle this.</summary>
    public bool NeedsReview =>
        Decision is { Ok: true } d && d.Choice == SmartTurn.ReviewOption;

    /// <summary>True when there is an approach worth handing to the loop.</summary>
    public bool HasChoice => Decision is { Ok: true };

    public DecisionOption? Chosen =>
        Decision is { Ok: true } d && Options.FirstOrDefault(o => o.Name == d.Choice) is { Name.Length: > 0 } hit
            ? hit
            : null;

    /// <summary>
    /// The line handed to the loop as the chosen approach. A user message, not a
    /// system rule: it steers the turn without pretending to be policy.
    /// </summary>
    public string Guidance(string request) =>
        Chosen is { } option ? GuidanceFor(request, option) : request;

    /// <summary>
    /// The same line for an approach chosen by a person rather than by the
    /// engine — which is why it takes the option instead of looking it up: after
    /// an override, the decision's own choice is no longer the one being taken.
    /// </summary>
    public static string GuidanceFor(string request, DecisionOption option) =>
        request + Environment.NewLine + Environment.NewLine +
        $"[plan] Take this approach: {option.Name} — {option.Description}";
}

/// <summary>
/// Smart mode's one turn: plan, decide, and report how sure the decision was.
///
/// It owns no policy about what to do when the decision is weak — that belongs
/// to the caller, because `run` cannot ask anybody and `chat` can.
/// </summary>
public sealed class SmartTurn(IChatProvider provider, IDecisionEngine engine, double confidenceFloor)
{
    /// <summary>The option that means "a person has to decide this".</summary>
    public const string ReviewOption = "needs_review";

    /// <summary>
    /// Worded once, here, rather than left to the planner. The model would
    /// sometimes forget it and would phrase it differently every time, and a
    /// criterion that keeps changing is a criterion the decision engine cannot
    /// judge consistently.
    /// </summary>
    public const string ReviewDescription =
        "None of the other approaches should be taken without a person deciding first: " +
        "the request is risky, irreversible, ambiguous about what is wanted, or needs approval.";

    /// <summary>Raised as each stage begins, for the progress line.</summary>
    public event Action<string>? ActivityStarted;

    public async Task<SmartPlan> PrepareAsync(string request, string toolScope, CancellationToken ct)
    {
        ActivityStarted?.Invoke("planning the approach");

        var planned = await new Planner(provider).PlanAsync(request, toolScope, ct);

        // Nothing planned at all: run as the basic mode would.
        if (planned.Count == 0) return new SmartPlan(planned, null, Confident: false);

        // "Ask a person" is always on the ballot, and always last. Adding it in
        // code rather than asking the planner for it means it is always there
        // and always says the same thing.
        var options = planned
            .Where(o => o.Name != ReviewOption)
            .Append(new DecisionOption(ReviewOption, ReviewDescription))
            .ToList();

        // One real approach plus the review option is still a decision worth
        // making — it is exactly the "should a person look at this?" question.
        ActivityStarted?.Invoke($"deciding between {options.Count} approaches");

        var decision = await engine.ChooseAsync(request, Planner.DecisionQuestion, options, ct);

        // Confident means "act on this unasked". Choosing review is the opposite
        // of that however sure the engine is, so it never counts as confident.
        var confident = decision.Ok
                        && decision.Confidence >= confidenceFloor
                        && decision.Choice != ReviewOption;

        return new SmartPlan(options, decision, confident);
    }
}
