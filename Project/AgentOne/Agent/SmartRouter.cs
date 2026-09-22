using AgentOne.Llm.Decision;
using AgentOne.Tools;

namespace AgentOne.Agent;

/// <summary>Which resource a request needs first — the decision engine's first question.</summary>
public enum Route
{
    /// <summary>Current or external information: search the web, read pages.</summary>
    Web,
    /// <summary>The user's own files: list, find, grep, read.</summary>
    Files,
    /// <summary>No lookup at all: answer from knowledge and the conversation.</summary>
    Answer
}

/// <param name="Route">The route chosen, or null when the engine failed or named nothing offered.</param>
/// <param name="Confident">True when the choice cleared the floor and steers the loop.</param>
public sealed record RouteDecision(Route? Route, Decision Decision, bool Confident)
{
    public bool Steers => Confident && Route is not null;

    /// <summary>The tool families the loop may use this turn; null means all of them.</summary>
    public IReadOnlySet<string>? Families => Steers ? SmartRouter.FamiliesFor(Route!.Value) : null;

    /// <summary>The request with the route stated under it, as a user line — a steer, not a system rule.</summary>
    public string Guidance(string request) =>
        Steers ? request + Environment.NewLine + Environment.NewLine + SmartRouter.GuidanceFor(Route!.Value) : request;
}

/// <param name="Escalate">True when the stronger model should take the request.</param>
public sealed record EscalationDecision(bool Escalate, Decision Decision);

/// <summary>
/// Smart mode's two questions to the decision engine, both with fixed options
/// so they cost one engine call (≈0.3 s) and no planning LLM call (12–15 s,
/// measured, for options that then did not separate).
///
/// 1. <b>Route</b>, before the loop: does this need the web, the workspace, or
///    nothing? The chosen family is the only one the loop may use that turn.
/// 2. <b>Escalation</b>, after the everyday model has drafted an answer with
///    whatever the tools found: is the draft good enough, or does the problem
///    need the stronger, slower model? The engine is always told which model
///    drafted and which one is on offer, and sees the gathered material — a
///    judgement about the draft without the material would be a guess.
/// </summary>
public sealed class SmartRouter(IDecisionEngine engine, double confidenceFloor, string basicModel, string? reasoningModel)
{
    /// <summary>Below this many characters a request is a greeting or a nudge, not something to route.</summary>
    public const int MinRequestChars = 10;

    public const string SearchWeb = "search_web";
    public const string ReadWorkspace = "read_workspace";
    public const string AnswerDirectly = "answer_directly";

    public const string KeepDraft = "keep_draft";
    public const string EscalateOption = "escalate";

    public const string RouteQuestion =
        "Which resource does answering this request need first? Choose the one that fits best.";

    public const string EscalationQuestion =
        "Is the draft answer good enough to give the user as it is, or does this request need the stronger model?";

    public static readonly DecisionOption[] RouteOptions =
    [
        new(SearchWeb,
            "The answer depends on current or external information — news, documentation on the web, " +
            "prices, weather, anything not in the user's own files. The agent should search the web and read pages before answering."),
        new(ReadWorkspace,
            "The answer depends on the files in the working directory — code, configuration, documents the user has locally. " +
            "The agent should list, find, grep or read those files before answering."),
        new(AnswerDirectly,
            "No lookup is needed: general knowledge, reasoning, writing, translation, or material already in the conversation. " +
            "The model should answer at once, without tools.")
    ];

    public static readonly DecisionOption[] EscalationOptions =
    [
        new(KeepDraft,
            "The draft answers the request correctly and completely. The everyday model's answer is good enough to give as it is."),
        new(EscalateOption,
            "The request needs deeper reasoning than the draft shows — multi-step analysis, careful trade-offs, tricky logic or " +
            "arithmetic, design judgement — or the draft is shallow, hedging, or likely wrong. " +
            "Hand the request and the gathered material to the stronger model.")
    ];

    /// <summary>Raised as each question is asked, for the progress line.</summary>
    public event Action<string>? ActivityStarted;

    public static bool Applies(string request) => request.Trim().Length >= MinRequestChars;

    public bool CanEscalate => !string.IsNullOrEmpty(reasoningModel);

    /// <summary>
    /// The engine is always told which models are involved. "Is this draft good
    /// enough?" means something different from a 4B model than from a 27B one.
    /// </summary>
    public string ModelsLine =>
        $"Everyday model (answering now): {basicModel} — small and fast, weak at multi-step reasoning. " +
        $"Stronger model available: {(CanEscalate ? reasoningModel : "none")} — strong at reasoning, slow.";

    public async Task<RouteDecision> RouteAsync(string request, string context, CancellationToken ct)
    {
        ActivityStarted?.Invoke("deciding what this needs: web, files, or neither");

        var state = ModelsLine + "\n\nRequest:\n" + request
                    + (context.Length == 0 ? "" : "\n\nAlready in the conversation:\n" + context);

        var decision = await engine.ChooseAsync(state, RouteQuestion, RouteOptions, ct);

        Route? route = decision.Ok
            ? decision.Choice switch
            {
                SearchWeb => Route.Web,
                ReadWorkspace => Route.Files,
                AnswerDirectly => Route.Answer,
                _ => null
            }
            : null;

        return new RouteDecision(route, decision, route is not null && decision.Confidence >= confidenceFloor);
    }

    /// <param name="material">Everything the tools returned this turn, or empty.</param>
    /// <param name="draft">What the everyday model answered.</param>
    public async Task<EscalationDecision> EscalateAsync(string request, string material, string draft, CancellationToken ct)
    {
        // No stronger model, no question: the engine would be asked something
        // nothing can act on.
        if (!CanEscalate) return new EscalationDecision(false, Decision.Failed("no reasoning model configured"));

        ActivityStarted?.Invoke($"judging the draft against {reasoningModel}");

        var state = ModelsLine
                    + "\n\nRequest:\n" + request
                    + "\n\nMaterial the tools gathered:\n" + (material.Length == 0 ? "(none — no tool was used)" : Clip(material, MaterialChars))
                    + $"\n\nDraft answer from {basicModel}:\n" + Clip(draft, DraftChars);

        var decision = await engine.ChooseAsync(state, EscalationQuestion, EscalationOptions, ct);

        // The choice alone decides, not the floor. The floor guards steering,
        // where acting on a weak call sends the loop the wrong way; escalating
        // costs time, not correctness, and a two-option judgement call rarely
        // clears 0.60 — measured: "escalate" at 0.25 for a question the small
        // model plainly got shallow. The engine's choice is already "more
        // likely than not", and that is the right bar for a slower second look.
        var escalate = decision.Ok && decision.Choice == EscalateOption;
        return new EscalationDecision(escalate, decision);
    }

    /// <summary>How much gathered material the engine is shown. Enough to judge, not the whole page.</summary>
    public const int MaterialChars = 6000;
    public const int DraftChars = 4000;

    public static IReadOnlySet<string> FamiliesFor(Route route) => route switch
    {
        Route.Web => new HashSet<string> { ToolCatalog.WebFamily },
        Route.Files => new HashSet<string> { ToolCatalog.FilesFamily },
        _ => new HashSet<string>()
    };

    public static string GuidanceFor(Route route) => route switch
    {
        Route.Web =>
            "[route: web] This needs current information from the web. Use web_search, then web_read the most relevant result, " +
            "before answering. Workspace file tools are not available on this turn.",
        Route.Files =>
            "[route: files] This is about the files in the workspace. Use list_files, find_files, grep and read_file to look, " +
            "then answer from what they contain. Web tools are not available on this turn.",
        _ =>
            "[route: answer] No lookup is needed. Answer directly from what you know and what is already in this conversation; " +
            "no tool is available on this turn, so reply with the final envelope."
    };

    internal static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..max] + $"\n… ({text.Length - max} more characters)";
}
