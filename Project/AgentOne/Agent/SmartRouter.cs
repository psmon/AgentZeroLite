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

/// <param name="Step">Which PDSA step the request is, or null when the engine failed.</param>
/// <param name="Confident">True when the choice cleared the floor — the bar for opening a new cycle.</param>
public sealed record PdsaStepDecision(string? Step, Decision Decision, bool Confident)
{
    /// <summary>The step the cycle should record, falling back to Do — work with no stated step is work being done.</summary>
    public string Kind => Step ?? SmartRouter.DoStep;
}

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

    public const string ResumeOption = "resume";
    public const string StopOption = "stop";
    public const string RefineOption = "refine";

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
            "Something has to be MADE OR CHANGED ON DISK, or read from disk. Building anything the user will run or " +
            "keep counts — a program, a game, a web page, a script, a config, a document — and it counts even when the " +
            "folder is empty and there is no project yet, because the files are the deliverable. Also: reading the " +
            "project's files, editing them, running a build, tests or a command. The agent should use the file and " +
            "command tools."),
        new(AnswerDirectly,
            "The user wants to KNOW something, not to have something built: an explanation, a comparison, a definition, " +
            "advice, a translation, or reasoning over material already in the conversation. No file is to be created or " +
            "changed. Do NOT choose this because the model could write the code in its reply — if the user asked for a " +
            "thing to be made, they want it on disk, and this option cannot write anything.")
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

    // ------------------------------------------------------ knowledge graph

    public const string SaveKnowledge = "save";
    public const string SkipKnowledge = "skip";
    public const string ConsultGraph = "consult_graph";
    public const string SkipGraph = "skip_graph";
    public const string ByKeywords = "by_keywords";
    public const string ByPaths = "by_paths";
    public const string RecentFirst = "recent";
    public const string MostHelpful = "most_helpful";

    public const string PauseQuestion =
        "The person paused the agent mid-task and then typed this line. What do they mean by it?";

    public static readonly DecisionOption[] PauseOptions =
    [
        new(ResumeOption, "Go on exactly as before: an empty line, 'continue', 'go on', 'ok', 'resume', or a remark that changes nothing."),
        new(StopOption, "Abandon the task: 'stop', 'cancel', 'never mind', 'forget it', or a new request unrelated to the task."),
        new(RefineOption, "Go on, but with this instruction taken into account: a correction, a constraint, a detail, a change of direction within the same task.")
    ];

    public const string WorthSavingQuestion =
        "Did this turn produce knowledge a future session in this project would be glad to have — something not obvious " +
        "from the files themselves, and not a passing detail?";

    public const string GuidelineOption = "guideline";
    public const string KnowledgeOption = "knowledge";

    public const string GuidelineQuestion =
        "This is one section of a Markdown document in a software project. Is it a GUIDELINE — an instruction for whoever " +
        "works here — or KNOWLEDGE — a description of how things are?";

    public static readonly DecisionOption[] GuidelineOptions =
    [
        new(GuidelineOption,
            "A guideline: tells the reader what to do or not do when working in this project — a rule, a convention, a " +
            "required procedure or order of steps, something to always or never do, a checklist to follow."),
        new(KnowledgeOption,
            "Knowledge: describes the project as it is — what exists and where, how it works, why it was built that way, " +
            "what was measured or decided, reference tables, history, examples.")
    ];

    /// <summary>`knowledge init`: is this document section a rule to follow, or a description of how things are?</summary>
    public Task<Decision> GuidelineOrKnowledgeAsync(string docPath, string heading, string text, CancellationToken ct)
    {
        var state = "Document: " + docPath + "\nSection: " + heading + "\n\n" + Clip(text, 1500);
        return engine.ChooseAsync(state, GuidelineQuestion, GuidelineOptions, ct);
    }

    public const string GraphHelpsQuestion =
        "Would looking up what this project's knowledge graph already knows help answer this request, before the agent " +
        "starts searching files or the web?";

    public const string GraphStrategyQuestion =
        "Which way of querying the knowledge graph would most likely surface what this request needs?";

    public static readonly DecisionOption[] WorthSavingOptions =
    [
        new(SaveKnowledge,
            "Yes: a fact about how this project is built or laid out, a decision and its reason, an error and its fix, " +
            "a command that works, a constraint discovered. Worth keeping and finding again."),
        new(SkipKnowledge,
            "No: a greeting, a question answered from general knowledge, a routine read with nothing learned, a failed " +
            "turn that taught nothing, or something the files already say plainly.")
    ];

    public static readonly DecisionOption[] GraphHelpsOptions =
    [
        new(ConsultGraph,
            "Yes: the request touches this project — its files, its build, its history, decisions made here — and the " +
            "graph's stored knowledge (listed) overlaps with it. Reading it first avoids scanning files again."),
        new(SkipGraph,
            "No: the request is about something the graph clearly does not cover, or needs no project knowledge at all.")
    ];

    public static readonly DecisionOption[] GraphStrategyOptions =
    [
        new(ByKeywords, "Match the request's words against the stored knowledge's titles and text."),
        new(ByPaths, "Follow the files the request names or implies: knowledge attached to those paths."),
        new(RecentFirst, "The newest knowledge: the request continues what was done most recently."),
        new(MostHelpful, "The knowledge that has helped the most turns before: the request is a recurring kind.")
    ];

    /// <summary>After a turn: keep what it taught? Follows the choice; a stored nothing costs little, a lost fact costs a search.</summary>
    public async Task<Decision> WorthSavingAsync(string request, string did, string outcome, CancellationToken ct)
    {
        ActivityStarted?.Invoke("judging whether this turn taught anything worth keeping");
        var state = "Request:\n" + request + "\n\nWhat the agent did:\n" + did + "\n\nOutcome:\n" + Clip(outcome, 1500);
        return await engine.ChooseAsync(state, WorthSavingQuestion, WorthSavingOptions, ct);
    }

    /// <summary>
    /// Whether a "worth saving?" verdict keeps the turn. Save follows the
    /// choice; skip has to clear the floor. The costs are lopsided: an item
    /// kept by mistake is three lines that ranking sinks when nothing ever
    /// uses them, while a fact forgotten is another scan of the files next
    /// session. Measured: a turn that wrote a run script and a README — the
    /// option text's own example of "a command that works" — came back
    /// "skip" at 0.16. That is the engine saying it cannot tell, and when it
    /// cannot tell, keeping is the cheap mistake.
    /// </summary>
    public bool KeepsKnowledge(Decision verdict) =>
        verdict.Ok && (verdict.Choice == SaveKnowledge || verdict.Confidence < confidenceFloor);

    /// <summary>A line typed during a pause: resume, stop, or refine. Follows the choice — three options rarely clear a floor.</summary>
    public async Task<Decision> PauseVerdictAsync(string request, string progress, string line, CancellationToken ct)
    {
        ActivityStarted?.Invoke("reading what the pause line means");
        var state = "Task in progress:\n" + request + "\n\nDone so far:\n" + Clip(progress, 1500) + "\n\nTyped while paused:\n" + line;
        return await engine.ChooseAsync(state, PauseQuestion, PauseOptions, ct);
    }

    /// <summary>Before a turn: is the graph worth a look? Follows the choice.</summary>
    public async Task<Decision> GraphHelpsAsync(string request, string graphSummary, CancellationToken ct)
    {
        ActivityStarted?.Invoke("judging whether the knowledge graph can help");
        var state = "Request:\n" + request + "\n\nWhat the project's knowledge graph holds:\n" + graphSummary;
        return await engine.ChooseAsync(state, GraphHelpsQuestion, GraphHelpsOptions, ct);
    }

    /// <summary>Which query to run — the engine picks the Cypher, in effect, from four fixed ones.</summary>
    public async Task<Decision> GraphStrategyAsync(string request, string graphSummary, CancellationToken ct)
    {
        ActivityStarted?.Invoke("choosing how to query the knowledge graph");
        var state = "Request:\n" + request + "\n\nWhat the project's knowledge graph holds:\n" + graphSummary;
        return await engine.ChooseAsync(state, GraphStrategyQuestion, GraphStrategyOptions, ct);
    }

    // -------------------------------------------------------------- pdsa

    public const string PlanStep = "plan";
    public const string DoStep = "do";
    public const string StudyStep = "study";
    public const string ActStep = "act";

    public const string MetVerdict = "met";
    public const string PartialVerdict = "partial";
    public const string UnmetVerdict = "unmet";

    public const string PdsaStepQuestion =
        "Deming's improvement cycle runs Plan → Do → Study → Act. Which of those four steps does this request ask for?";

    public const string StudyVerdictQuestion =
        "Measured against what the plan said would happen, what actually happened? Judge the outcome, not the effort.";

    public static readonly DecisionOption[] PdsaStepOptions =
    [
        new(PlanStep,
            "Plan: decide what to build or change and how, and what result would count as success. The request asks for a " +
            "design, an approach, a structure, a scope — or starts a new piece of work whose shape is not settled yet."),
        new(DoStep,
            "Do: carry the plan out. The request asks to build, write, edit, install, run or fix something — the work itself, " +
            "on a course already chosen."),
        new(StudyStep,
            "Study: find out what actually happened and why. The request asks to test, verify, measure, check the result, " +
            "read the errors, compare against what was expected, or explain why something behaved as it did."),
        new(ActStep,
            "Act: settle what was learned. The request asks to adopt the change, clean it up, document it, commit or release " +
            "it, write down the rule that came out of it — or to decide what the next cycle should tackle.")
    ];

    public static readonly DecisionOption[] StudyVerdictOptions =
    [
        new(MetVerdict, "What the plan predicted is what happened: it works, the checks pass, the result is there."),
        new(PartialVerdict, "Some of it holds and some does not: it runs but something is missing, wrong, or unverified."),
        new(UnmetVerdict, "The prediction did not hold: it fails, it was not built, or the result contradicts what was expected.")
    ];

    /// <summary>
    /// Which step of the cycle this request is. Follows the choice inside a
    /// running cycle — a mislabelled phase costs a row, not an action — but the
    /// caller applies the floor when the answer would <em>open</em> a cycle,
    /// because that steers several turns after it.
    /// </summary>
    /// <param name="cycle">What the open cycle is and how far it has got, or a line saying there is none.</param>
    public async Task<PdsaStepDecision> PdsaStepAsync(string request, string cycle, CancellationToken ct)
    {
        ActivityStarted?.Invoke("placing this request in the improvement cycle");
        var state = "Improvement cycle in progress:\n" + cycle + "\n\nRequest:\n" + request;
        var decision = await engine.ChooseAsync(state, PdsaStepQuestion, PdsaStepOptions, ct);

        var step = decision.Ok && Array.Exists(PdsaStepOptions, o => o.Name == decision.Choice) ? decision.Choice : null;
        return new PdsaStepDecision(step, decision, step is not null && decision.Confidence >= confidenceFloor);
    }

    /// <summary>
    /// Study's verdict: what the plan predicted against what the turn actually
    /// produced. Three options rarely clear a floor, so this follows the
    /// choice; a failed engine leaves the cycle unjudged rather than guessing "met".
    /// </summary>
    public async Task<Decision> StudyVerdictAsync(string expected, string request, string outcome, CancellationToken ct)
    {
        ActivityStarted?.Invoke("judging the result against the plan");
        var state = "What the plan said would happen:\n" + (expected.Length == 0 ? "(the cycle opened without a stated expectation)" : Clip(expected, 2000))
                    + "\n\nWhat was just asked:\n" + request
                    + "\n\nWhat actually happened:\n" + Clip(outcome, 3000);
        return await engine.ChooseAsync(state, StudyVerdictQuestion, StudyVerdictOptions, ct);
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
