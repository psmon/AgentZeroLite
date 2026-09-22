using AgentOne.Llm.Decision;
using AgentOne.Tools;

namespace AgentOne.Agent;

/// <summary>Which resource a request needs first — the decision engine's first question.</summary>
public enum Route
{
    /// <summary>Current or external information: search the web, read pages.</summary>
    Web,
    /// <summary>The workspace: read, create, edit or run things in it.</summary>
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

/// <param name="NeedsDesign">True when the stronger model should design before the everyday model builds.</param>
public sealed record ScopeDecision(bool NeedsDesign, Decision Decision);

/// <param name="Safe">True when the command may run without asking anyone.</param>
public sealed record SafetyDecision(bool Safe, Decision Decision);

/// <summary>
/// Smart mode's questions to the decision engine, all with fixed options so
/// each costs one engine call (≈0.3 s) and no planning LLM call (12–15 s,
/// measured, for options that then did not separate).
///
/// 1. <b>Route</b>, before the loop: web, the workspace, or nothing? The
///    chosen family is the only one the loop may use that turn.
/// 2. <b>Scope</b>, for workspace work: a small task, or something large
///    enough that the stronger model should design it first?
/// 3. <b>Safety</b>, before any command runs: fine unattended, or ask?
/// 4. <b>Escalation</b>, after the everyday model has drafted an answer with
///    whatever the tools found: good enough, or hand it to the stronger model?
///
/// The engine is always told which model is answering and which one is on
/// offer — "is this draft good enough?" is a different question for a 4B
/// model than for a 27B one.
/// </summary>
public sealed class SmartRouter(IDecisionEngine engine, double confidenceFloor, string basicModel, string? reasoningModel)
{
    /// <summary>Below this many characters a request is a greeting or a nudge, not something to route.</summary>
    public const int MinRequestChars = 10;

    public const string SearchWeb = "search_web";
    public const string WorkInWorkspace = "work_in_workspace";
    public const string AnswerDirectly = "answer_directly";

    public const string KeepDraft = "keep_draft";
    public const string EscalateOption = "escalate";

    public const string SmallTask = "small_task";
    public const string NeedsDesign = "needs_design";

    public const string SafeOption = "safe";
    public const string UnsafeOption = "unsafe";

    public const string SameTask = "same_task";
    public const string NewTask = "new_task";

    public const string RouteQuestion =
        "Which resource does answering this request need first? Choose the one that fits best.";

    public const string EscalationQuestion =
        "Is the draft answer good enough to give the user as it is, or does this request need the stronger model?";

    public const string ScopeQuestion =
        "Is this a small task the everyday model can simply do, or work large enough that it should be designed first?";

    public const string SafetyQuestion =
        "Is this command safe to run unattended inside the project folder, or should a person approve it first?";

    public const string TaskSwitchQuestion =
        "Does the new request continue the task the session is on, or start a different one?";

    public static readonly DecisionOption[] RouteOptions =
    [
        new(SearchWeb,
            "The answer depends on current or external information — news, documentation on the web, " +
            "prices, weather, anything not in the user's own files. The agent should search the web and read pages before answering."),
        new(WorkInWorkspace,
            "The request is about the project in the working directory: reading its files, creating or editing files, " +
            "running a build, tests or a command there. The agent should use the file and command tools."),
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

    public static readonly DecisionOption[] ScopeOptions =
    [
        new(SmallTask,
            "The request itself is small and well-defined, whatever state the project is in: run a build, run the tests, " +
            "execute a command, check whether something works, fix one error, change one or two files, answer a question. " +
            "The everyday model does it directly."),
        new(NeedsDesign,
            "The request asks for something new and large: a project or a feature from scratch, several new files, a " +
            "structure to choose, dependencies between steps, a scaffold to create. Doing it without a plan would go wrong. " +
            "Have the stronger model design the file layout and the steps first; then the everyday model implements it.")
    ];

    public static readonly DecisionOption[] TaskSwitchOptions =
    [
        new(SameTask,
            "The new request continues, refines, fixes or asks about the same piece of work the session is already on."),
        new(NewTask,
            "The new request starts a different piece of work — another feature, another problem, another subject.")
    ];

    public static readonly DecisionOption[] SafetyOptions =
    [
        new(SafeOption,
            "Ordinary development work confined to the project folder: building, testing, listing, formatting, " +
            "installing the project's own dependencies, creating or running project files, git status/diff/add/commit."),
        new(UnsafeOption,
            "Could damage or expose something beyond the project: deletes or overwrites outside the folder, changes " +
            "system or user settings, needs elevated rights, sends data or secrets somewhere, is irreversible " +
            "(force-push, history rewrite, wiping data), or is not clearly understood.")
    ];

    /// <summary>Raised as each question is asked, for the progress line.</summary>
    public event Action<string>? ActivityStarted;

    public static bool Applies(string request) => request.Trim().Length >= MinRequestChars;

    public bool CanEscalate => !string.IsNullOrEmpty(reasoningModel);

    public string ModelsLine =>
        $"Everyday model (answering now): {basicModel} — small and fast, weak at multi-step reasoning. " +
        $"Stronger model available: {(CanEscalate ? reasoningModel : "none")} — strong at reasoning, slow.";

    // ------------------------------------------------------------- route

    public async Task<RouteDecision> RouteAsync(string request, string context, CancellationToken ct)
    {
        ActivityStarted?.Invoke("deciding what this needs: web, the workspace, or neither");

        var state = ModelsLine + "\n\nRequest:\n" + request
                    + (context.Length == 0 ? "" : "\n\nAlready in the conversation:\n" + context);

        var decision = await engine.ChooseAsync(state, RouteQuestion, RouteOptions, ct);

        Route? route = decision.Ok
            ? decision.Choice switch
            {
                SearchWeb => Route.Web,
                WorkInWorkspace => Route.Files,
                AnswerDirectly => Route.Answer,
                _ => null
            }
            : null;

        return new RouteDecision(route, decision, route is not null && decision.Confidence >= confidenceFloor);
    }

    // ------------------------------------------------------------- scope

    /// <summary>
    /// The floor applies here, unlike escalation: a design changes what the
    /// turn <em>does</em> — the everyday model is handed a plan and starts
    /// building — so it is a steer, and a weak call must not steer. Measured:
    /// "run the build and see if it works" got needs_design at 0.55 because the
    /// half-built project in the digest looked like large work, and the person
    /// waited on a design they had not asked for.
    /// </summary>
    public async Task<ScopeDecision> ScopeAsync(string request, string context, CancellationToken ct)
    {
        if (!CanEscalate) return new ScopeDecision(false, Decision.Failed("no reasoning model configured"));

        ActivityStarted?.Invoke("judging the size of the work");

        var state = ModelsLine + "\n\nRequest:\n" + request
                    + (context.Length == 0 ? "" : "\n\nWhat is already known about the project:\n" + context)
                    + "\n\nJudge the size of what the request asks for NOW, not the state of the project.";

        var decision = await engine.ChooseAsync(state, ScopeQuestion, ScopeOptions, ct);
        var needsDesign = decision.Ok && decision.Choice == NeedsDesign && decision.Confidence >= confidenceFloor;
        return new ScopeDecision(needsDesign, decision);
    }

    // ------------------------------------------------------------ safety

    /// <summary>
    /// The one question where the floor applies to the <em>permissive</em>
    /// answer: a command runs unasked only when the engine says safe and is
    /// sure of it. Anything else — unsafe, unsure, failed — goes to a person.
    /// </summary>
    public async Task<SafetyDecision> SafetyAsync(string command, string workspaceRoot, string shell, CancellationToken ct)
    {
        ActivityStarted?.Invoke("judging whether the command is safe");

        var state = $"A command-line agent wants to run this {shell} command in the project folder {workspaceRoot}:\n\n"
                    + command
                    + "\n\nThe agent may only change files inside that folder. Nothing outside it should be modified.";

        var decision = await engine.ChooseAsync(state, SafetyQuestion, SafetyOptions, ct);
        var safe = decision.Ok && decision.Choice == SafeOption && decision.Confidence >= confidenceFloor;
        return new SafetyDecision(safe, decision);
    }

    // ------------------------------------------------------- task switch

    /// <summary>
    /// Whether the session's title still fits. Cheap (one engine call) where
    /// re-naming the task with the LLM after every turn is not; the LLM is
    /// only asked for a new name when this says the task changed. Follows
    /// the choice: a stale title is a cosmetic cost, not a wrong action.
    /// </summary>
    public async Task<bool> TaskSwitchedAsync(string currentTitle, string request, CancellationToken ct)
    {
        var state = $"The session's task so far: {currentTitle}" + "\n\nNew request:\n" + request;
        var decision = await engine.ChooseAsync(state, TaskSwitchQuestion, TaskSwitchOptions, ct);
        return decision.Ok && decision.Choice == NewTask;
    }

    // -------------------------------------------------------- escalation

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
        Route.Files => new HashSet<string> { ToolCatalog.FilesFamily, ToolCatalog.EditFamily, ToolCatalog.ExecFamily },
        _ => new HashSet<string>()
    };

    public static string GuidanceFor(Route route) => route switch
    {
        Route.Web =>
            "[route: web] This needs current information from the web. Use web_search, then web_read the most relevant result, " +
            "before answering. Workspace tools are not available on this turn.",
        Route.Files =>
            "[route: workspace] This is about the project in the workspace. Look with list_files, find_files, grep and read_file; " +
            "create or change files with write_file; run builds, tests and commands with run_command. " +
            "Web tools are not available on this turn.",
        _ =>
            "[route: answer] No lookup is needed. Answer directly from what you know and what is already in this conversation; " +
            "no tool is available on this turn, so reply with the final envelope."
    };

    internal static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..max] + $"\n… ({text.Length - max} more characters)";
}
