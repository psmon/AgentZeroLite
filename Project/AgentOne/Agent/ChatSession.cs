using System.Diagnostics;
using System.Text;
using AgentOne.Graph;
using AgentOne.Llm;
using AgentOne.Llm.Decision;
using AgentOne.Services;
using AgentOne.Tools;

namespace AgentOne.Agent;

/// <summary>What smart mode decided at one of its checkpoints, for the renderers.</summary>
/// <param name="Kind">"route", "scope", "safety" or "escalation".</param>
/// <param name="Verdict">One line for a person: "→ web", "keeping the draft", "escalating to …".</param>
public sealed record SmartNote(string Kind, Decision Decision, string Verdict);

/// <summary>A command the gate would not run on its own; a person decides.</summary>
/// <param name="Reason">Why it was not run unasked — what the risk check or the engine said.</param>
public sealed record ApprovalRequest(string Command, string Reason, string WorkingDirectory);

/// <summary>A choice the strong model's design hinges on; a person picks before anything is built.</summary>
/// <param name="Recommended">Index of the option the design recommends.</param>
public sealed record ChoiceRequest(string Question, IReadOnlyList<string> Options, int Recommended)
{
    /// <summary>What an answer means: a number picks, an empty line takes the recommendation, anything else is the choice itself.</summary>
    public string Resolve(string answer)
    {
        answer = answer.Trim();
        if (answer.Length == 0) return Options[Recommended];
        if (int.TryParse(answer, out var n)) return n >= 1 && n <= Options.Count ? Options[n - 1] : Options[Recommended];
        return answer;
    }
}

/// <summary>Everything the status view shows, taken at one moment.</summary>
public sealed record SessionStats(
    bool Smart,
    string? LogPath,
    string Root,
    string Model,
    string? ReasoningModel,
    string Shell,
    string? Title,
    int ContextMessages,
    int ContextChars,
    int EstimatedTokens,
    int MemoryChars,
    string MemoryPath,
    SessionCounters Counters,
    IReadOnlyList<string> ReadGrants,
    GraphStats? Graph = null)
{
    /// <summary>The status block, one fact per line.</summary>
    public IReadOnlyList<string> Describe()
    {
        var c = Counters;
        var graph = Graph is { } g
            ? $"graph     {g.Knowledge} items · {g.Paths} paths · {g.Turns} turns · helped {g.Helped} times"
            : "graph     off (Kùzu library not next to the binary)";
        var lines = new List<string>
        {
            $"task      {Title ?? "(not named yet)"}",
            $"session   {(LogPath ?? "(not saved)")}",
            $"mode      {(Smart ? "smart" : "basic")} · turns {c.Turns}",
            $"context   {ContextMessages} messages · {ContextChars:N0} chars · ~{EstimatedTokens:N0} tokens (estimate)",
            $"memory    {MemoryChars:N0} / {WorkspaceStore.MemoryCapChars:N0} chars · {MemoryPath}",
            graph,
            $"models    {Model} · reasoning {ReasoningModel ?? "none"} · shell {Shell}",
            $"jev       {c.JevCalls} calls · {c.JevMs:N0} ms · escalations {c.Escalations} · designs {c.Designs}",
            $"tools     {c.ToolCalls} calls · commands approved {c.ApprovalsGranted}/{c.ApprovalsAsked} asked",
            $"writes    {Root} only"
        };
        lines.Add(ReadGrants.Count == 0
            ? "reads     the workspace"
            : "reads     the workspace + " + string.Join(", ", ReadGrants) + " (read-only, this session)");
        return lines;
    }
}

/// <summary>
/// One conversation, from the first prompt to /exit — everything a chat does
/// that is not drawing. The plain REPL, the full-screen window and `run` are
/// all thin renderers over this, so a turn routes, runs, judges its draft and
/// escalates identically whichever one is in front of it. Three copies of that
/// logic would drift within a week.
///
/// A session belongs to a workspace (<see cref="WorkspaceStore"/>): it opens
/// with the workspace's memory of earlier sessions, writes what each turn did
/// back into that memory, logs under the workspace, and can resume any of the
/// workspace's saved sessions.
/// </summary>
public sealed class ChatSession : IDisposable
{
    private readonly IChatProvider _provider;
    private readonly IChatProvider? _reasoning;
    private readonly LocalFileToolbelt _files;
    private readonly ShellToolbelt _shell;
    private readonly CompositeToolbelt _toolbelt;
    private readonly AgentLoop _loop;
    private readonly CountingEngine _engine;
    private readonly bool _smartAvailable;
    private readonly SmartRouter _router;
    private readonly SessionState _state;
    private readonly AgentConfig _config;
    private readonly string _root;
    private readonly string _logKind;
    private readonly CancellationTokenSource _background = new();
    private readonly GraphMemory? _graph;
    private SessionStore? _log;

    /// <summary>What the agent is doing right now, for a status line.</summary>
    public event Action<string>? ActivityStarted;

    /// <summary>A tool step finished — or a hand-off, under the tool name "reasoning" or "design".</summary>
    public event Action<AgentStep>? StepCompleted;

    /// <summary>A fragment of the answer, as the model writes it.</summary>
    public event Action<string>? AnswerDelta;

    /// <summary>Smart mode decided something: the route, the scope, a command's safety, or whether to escalate.</summary>
    public event Action<SmartNote>? Decided;

    /// <summary>Something the person should see once: a read grant, an approval outcome.</summary>
    public event Action<string>? Noted;

    /// <summary>The session's task got a (new) name. Raised off the turn, when the model has named it.</summary>
    public event Action<string>? TitleChanged;

    /// <summary>The strong model's design came back: its first lines, for a person following along.</summary>
    public event Action<IReadOnlyList<string>>? DesignMade;

    /// <summary>The turn taught something and it was kept in the knowledge graph. Raised off the turn.</summary>
    public event Action<IReadOnlyList<Distilled>>? Learned;

    /// <summary>
    /// Who picks when a design hinges on a choice. The REPL reads a line, the
    /// window parks the turn and takes the next line typed, `run` takes the
    /// recommendation. Null takes the recommendation too.
    /// </summary>
    public Func<ChoiceRequest, CancellationToken, Task<string>>? Chooser { get; set; }

    /// <summary>
    /// Who answers when a command needs approval. The REPL reads a line, the
    /// window parks the turn and takes the next one typed, `run` says no
    /// unless it was started with --yes. Null means every such command is refused.
    /// </summary>
    public Func<ApprovalRequest, CancellationToken, Task<bool>>? Approver { get; set; }

    /// <param name="logKind">The session file's suffix: "chat" or "run".</param>
    public ChatSession(AgentConfig config, string root, bool streaming, string logKind = "chat")
        : this(config, root, streaming,
            ChatProviderFactory.Create(config),
            new JevClient(config),
            config.HasReasoningModel ? ChatProviderFactory.Create(config.ForReasoning()) : null,
            logKind)
    {
    }

    private ChatSession(AgentConfig config, string root, bool streaming,
        IChatProvider provider, JevClient jev, IChatProvider? reasoning, string logKind)
        : this(config, root, streaming, provider, jev, jev.HasKey, reasoning, logKind)
    {
    }

    /// <summary>Test seam: a session over scripted providers and engine, without the network.</summary>
    internal ChatSession(AgentConfig config, string root, bool streaming,
        IChatProvider provider, IDecisionEngine engine, bool smartAvailable,
        IChatProvider? reasoning = null, string logKind = "chat")
    {
        _config = config;
        _root = Path.GetFullPath(root);
        _provider = provider;
        _reasoning = reasoning;
        _smartAvailable = smartAvailable;
        _logKind = logKind;

        Workspace = new WorkspaceStore(_root).Ensure();

        // The knowledge graph needs Kùzu's library next to the binary; without
        // it the agent runs as before and the status block says so.
        try { _graph = GraphMemory.Open(Workspace); }
        catch (InvalidOperationException) { _graph = null; }

        // Smart starts where the config left it, but only with a key to make it
        // work. Starting "on" with no key would fail on the first turn.
        _state = new SessionState(config.SmartMode && _smartAvailable);
        _engine = new CountingEngine(engine, _state);

        _files = new LocalFileToolbelt(_root);
        _shell = new ShellToolbelt(_root, TimeSpan.FromSeconds(config.CommandTimeoutSeconds)) { Gate = GateAsync };

        _toolbelt = new CompositeToolbelt(
            (ToolCatalog.FilesFamily, _files),
            (ToolCatalog.EditFamily, _files),
            (ToolCatalog.WebFamily, new WebToolbelt(TimeSpan.FromSeconds(config.WebTimeoutSeconds))),
            (ToolCatalog.ExecFamily, _shell));

        _loop = new AgentLoop(_provider, _toolbelt, config.MaxSteps) { Streaming = streaming };
        _loop.Reset(Workspace.MemoryForPrompt());
        _loop.ActivityStarted += what => ActivityStarted?.Invoke(what);
        _loop.StepCompleted += step =>
        {
            if (ToolCatalog.FamilyOf(step.Tool) is { } family && family != ToolCatalog.LoopFamily) _state.CountToolCall();
            _log?.Step(step);
            StepCompleted?.Invoke(step);
        };
        _loop.AnswerDelta += fragment => AnswerDelta?.Invoke(fragment);

        _router = new SmartRouter(_engine, config.JevConfidenceFloor, config.Model,
            reasoning is null ? null : config.ReasoningModel);
        _router.ActivityStarted += what => ActivityStarted?.Invoke(what);

        _log = config.SaveSessions ? SessionStore.Create(logKind, Workspace.SessionsDir) : null;
    }

    public WorkspaceStore Workspace { get; }
    public string Root => _root;
    public string ProviderName => _provider.Name;
    public string Model => _config.Model;
    public string? ReasoningModel => _reasoning is null ? null : _config.ReasoningModel;
    public string ToolScope => _toolbelt.Scope;
    public string Shell => ShellToolbelt.ShellName;
    public string? LogPath => _log?.Path;
    public bool SmartAvailable => _smartAvailable;
    public double ConfidenceFloor => _config.JevConfidenceFloor;

    /// <summary>The task this session is on, as the model named it. Null until the first turn is named.</summary>
    public string? Title { get; private set; }

    public bool Smart => _state.Read().Smart;

    /// <summary>Flips the mode. False, with a reason, when it cannot.</summary>
    public bool TryToggleSmart(out string message)
    {
        if (!_smartAvailable)
        {
            message = "no TypeSafe key — set one on the Smart step of `agent-one setup`";
            return false;
        }

        message = _state.ToggleSmart()
            ? "smart mode on — route first, then judge the draft" +
              (ReasoningModel is { } strong ? $", escalating to {strong} when it needs more" : " (no reasoning model: never escalates)")
            : "basic mode — straight to the tool loop";
        return true;
    }

    /// <summary>Forgets the conversation, the counters, the grants and the title; keeps the mode, the log and the memory.</summary>
    public void Reset()
    {
        _loop.Reset(Workspace.MemoryForPrompt());
        _state.Reset();
        _files.ClearGrants();
        Title = null;
    }

    /// <summary>A fresh session: everything Reset forgets, plus a new log file.</summary>
    public void NewSession()
    {
        Reset();
        if (_config.SaveSessions) _log = SessionStore.Create(_logKind, Workspace.SessionsDir);
    }

    /// <summary>This workspace's saved sessions, newest first.</summary>
    public IReadOnlyList<SessionSummary> ListSessions() => Workspace.ListSessions();

    /// <summary>
    /// Picks a saved session back up: the model's context is rebuilt from its
    /// questions and answers (the tool traffic between them stays in the file;
    /// the memory says what was done), its title is restored, and the same
    /// file keeps being appended to. Returns the transcript, for a renderer to
    /// replay on screen.
    /// </summary>
    public IReadOnlyList<SessionEntry> Resume(string path)
    {
        var entries = WorkspaceStore.ReadEntries(path);

        Reset();

        string? pending = null;
        foreach (var entry in entries)
        {
            switch (entry.Kind)
            {
                case "prompt":
                    pending = entry.Text;
                    _state.CountTurn();
                    break;
                case "result":
                    if (pending is not null && entry.Ok == true) _loop.Restore(pending, entry.Text);
                    pending = null;
                    break;
                case "title":
                    Title = entry.Text;
                    break;
            }
        }

        _log = SessionStore.Open(path);
        _log.Resumed();
        if (Title is { } title) TitleChanged?.Invoke(title);
        return entries;
    }

    /// <summary>The status view's numbers, taken now.</summary>
    public SessionStats Stats()
    {
        var snapshot = _state.Read();
        var chars = 0;
        foreach (var message in _loop.Messages) chars += message.Content.Length;

        return new SessionStats(
            snapshot.Smart, LogPath, _root, Model, ReasoningModel, Shell, Title,
            _loop.Messages.Count, chars, Tokens.Estimate(_loop.Messages),
            Workspace.MemoryChars, Workspace.MemoryPath,
            snapshot.Counters, snapshot.ReadGrants,
            _graph is null ? null : SafeStats());
    }

    /// <summary>
    /// Handles one line of input as a turn. Returns null only for an empty line.
    ///
    /// In smart mode, and for anything longer than a greeting, the turn is:
    /// route (which tool family, if any) → for workspace work, scope (design it
    /// first?) → the loop, restricted to that family → the engine judges the
    /// draft against the gathered material → if it needs more, the stronger
    /// model reasons over the same material and the everyday model writes the
    /// final answer from that. A folder named by its absolute path becomes
    /// readable for the session before any of it starts; what the turn did is
    /// written to the workspace memory after, and the task is (re)named off
    /// the turn.
    /// </summary>
    public async Task<AgentRun?> SubmitAsync(string line, CancellationToken ct)
    {
        line = line.Trim();
        if (line.Length == 0) return null;

        GrantNamedFolders(line);

        var snapshot = _state.Read();
        _log?.Prompt(line, snapshot.Smart);
        var turnNumber = _state.CountTurn();
        var turnId = $"{_log?.Id ?? "session"}-{turnNumber}";

        var smart = snapshot.Smart && SmartRouter.Applies(line);
        var prompt = line;
        IReadOnlySet<string>? families = null;
        var designed = false;

        // The task is (re)named from the request, in the background, as the turn
        // starts — so the header says "게시판 API 개발" seconds in, not minutes
        // later when a long build ends. A greeting is not a task.
        if (SmartRouter.Applies(line)) _ = RetitleAsync(line);

        if (smart)
        {
            var route = await _router.RouteAsync(line, Digest(), ct);
            var verdict = route.Steers ? $"→ {RouteName(route.Route!.Value)}"
                : route.Decision.Ok ? $"unsure ({route.Decision.Choice}), all tools stay available"
                : $"unavailable ({route.Decision.Message})";
            _log?.Decision("route", route.Decision, verdict);
            Decided?.Invoke(new SmartNote("route", route.Decision, verdict));

            if (route.Steers)
            {
                prompt = route.Guidance(line);
                families = route.Families;
            }

            // Before any file is scanned: does the graph already know? The
            // engine says whether to look and which query to run; what comes
            // back is material for the model, and the graph remembers it helped.
            if (_graph is not null && UsesGraph && route.Route != Route.Web && GraphHasKnowledge())
            {
                _graph.Graph.RememberTurn(turnId, line, "(in progress)");
                var consult = await ConsultGraphAsync(turnId, line, ct);
                if (consult is { Items.Count: > 0 })
                    prompt = prompt + Environment.NewLine + Environment.NewLine + consult.Material();
            }

            // Work in the workspace, or work whose shape is unclear, may be big
            // enough to design first. A confident web lookup or plain answer
            // never is — but an unsure route keeps every tool, so it is sized too:
            // "make a board API" once routed answer_directly at 0.58 and got no
            // design for exactly the request that needed one.
            var mayBuild = !route.Steers || route.Route == Route.Files;
            if (_reasoning is not null && mayBuild)
            {
                var design = await MaybeDesignAsync(line, ct);
                if (design is { } plan)
                {
                    prompt = prompt + Environment.NewLine + Environment.NewLine + plan;
                    designed = true;
                }
            }
        }

        var before = _loop.Messages.Count;
        var run = await _loop.RunAsync(prompt, ct, families);

        // A designed build already had the strong model's thinking; judging its
        // report against the same model again would be a second slow pass for nothing.
        if (smart && run.Succeeded && _reasoning is not null && !designed)
            run = await MaybeEscalateAsync(line, before, run, ct);

        // A turn that ran out of budget mid-work must not end on "[stopped: MaxSteps]"
        // and nothing else: the person needs to know what got done and what is next.
        if (!run.Succeeded && run.Reason is StopReason.MaxSteps or StopReason.Repeat && ToolSteps(run) > 0)
            run = await WrapUpAsync(run, ct);

        _log?.Result(run);
        Remember(line, run);
        if (_graph is not null && UsesGraph && _smartAvailable && _provider.Name != "echo") _ = LearnAsync(turnId, line, run);
        return run;
    }

    // ------------------------------------------------------- knowledge graph

    private bool GraphHasKnowledge()
    {
        try { return _graph!.HasKnowledge; }
        catch (InvalidOperationException) { return false; }
    }

    private GraphStats? SafeStats()
    {
        try { return _graph!.Stats(); }
        catch (InvalidOperationException) { return null; }
    }

    private async Task<GraphConsult?> ConsultGraphAsync(string turnId, string request, CancellationToken ct)
    {
        GraphConsult? consult;
        try
        {
            consult = await _graph!.ConsultAsync(_router, turnId, request, ct);
        }
        catch (InvalidOperationException ex)
        {
            Noted?.Invoke("graph memory unavailable: " + ex.Message);
            return null;
        }

        var verdict = consult.Consulted
            ? consult.Items.Count > 0 ? $"consulted via {consult.StrategyName} — {consult.Items.Count} item(s)" : $"consulted via {consult.StrategyName} — nothing matched"
            : consult.Helps.Ok ? "not needed" : $"unavailable ({consult.Helps.Message})";
        _log?.Decision("graph", consult.Helps, verdict);
        Decided?.Invoke(new SmartNote("graph", consult.Helps, verdict));

        if (consult.Items.Count > 0)
            foreach (var item in consult.Items)
                Noted?.Invoke($"  ↳ ({item.Kind}) {item.Title}");

        return consult;
    }

    /// <summary>Off the turn: judge, distil, store. Nothing here can fail the turn that is already over.</summary>
    private async Task LearnAsync(string turnId, string request, AgentRun run)
    {
        try
        {
            var items = await _graph!.LearnAsync(_router, _provider, turnId, request, run, _background.Token);
            if (items.Count == 0) return;

            foreach (var item in items)
                _log?.Step(new AgentStep(0, "learned", $"({item.Kind}) {item.Title} — {item.Text}", true));
            Learned?.Invoke(items);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChatProviderException or InvalidOperationException)
        {
            // Memory is a bonus; a failed distillation is not worth a line.
        }
    }

    private static int ToolSteps(AgentRun run) =>
        run.Steps.Count(s => s.Ok && s.Tool is not ("final" or "unwrapped" or "(unparsed)"));

    /// <summary>
    /// One more model call, no tools: what was done this turn, what is left,
    /// what to do next. The stop reason stays on the run — `run` still exits
    /// non-zero — but the text a person reads is a summary, not a guard's name.
    /// </summary>
    private async Task<AgentRun> WrapUpAsync(AgentRun stopped, CancellationToken ct)
    {
        ActivityStarted?.Invoke("summarizing what was done");
        Noted?.Invoke($"stopped early ({stopped.Reason}) — summarizing this turn");

        var summary = await _loop.RunAsync(
            "[wrap-up] This turn's step budget is used up. Do NOT call any tool now. In the user's language, " +
            "say: what was done this turn (files, commands, results), what is left or unverified, and the next " +
            "steps as a numbered list. Reply with the final envelope.",
            ct, SmartRouter.FamiliesFor(Route.Answer));

        return summary.Succeeded
            ? stopped with { Text = summary.Text, Streamed = summary.Streamed }
            : stopped;
    }

    private static string RouteName(Route route) => route switch
    {
        Route.Web => "web",
        Route.Files => "workspace",
        _ => "answer"
    };

    // ------------------------------------------------------------ memory

    /// <summary>One entry per turn: what was asked, what was done, how it ended.</summary>
    private void Remember(string request, AgentRun run)
    {
        var did = run.Steps
            .Where(s => s.Tool is not ("final" or "unwrapped" or "(unparsed)"))
            .Select(s => $"{s.Tool}{(s.Ok ? "" : "(failed)")}: {WorkspaceStore.FirstLine(s.Detail, 80)}")
            .ToList();

        var outcome = run.Succeeded
            ? Clip(run.Text.Replace("\r", "").Replace('\n', ' '), 300)
            : $"stopped ({run.Reason}): {Clip(run.Text.Replace('\n', ' '), 200)}";

        var sb = new StringBuilder();
        sb.Append("## ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append(" · ")
          .AppendLine(Title ?? WorkspaceStore.FirstLine(request, 60));
        sb.Append("- asked: ").AppendLine(WorkspaceStore.FirstLine(request, 200));
        sb.Append("- did: ").AppendLine(did.Count == 0 ? "(no tools)" : string.Join("; ", did));
        sb.Append("- outcome: ").Append(outcome);

        Workspace.Remember(sb.ToString());
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    // ------------------------------------------------------------- title

    /// <summary>
    /// Names the task from the request, concurrently with the turn, so the
    /// name is on screen while the work is still running. With an engine, its
    /// task-switch question (0.3 s) decides whether the current name still
    /// fits, and the LLM is only asked for a new one when it does not; without
    /// an engine the LLM names the task once and the name stays. Requests
    /// shorter than a sentence ("안녕", "hi") never name anything, and neither
    /// do `run` and the echo provider.
    /// </summary>
    /// <summary>Off for tests that count provider calls; the naming call runs beside the turn and would race them.</summary>
    internal bool NamesTasks { get; set; } = true;

    /// <summary>Off for tests that script the engine: consulting and learning would consume its answers.</summary>
    internal bool UsesGraph { get; set; } = true;

    private async Task RetitleAsync(string request)
    {
        if (!NamesTasks || _logKind != "chat" || _provider.Name == "echo") return;
        var ct = _background.Token;

        try
        {
            if (Title is { } current)
            {
                if (!_smartAvailable) return;
                if (!await _router.TaskSwitchedAsync(current, request, ct)) return;
            }

            var title = await TaskTitler.NameAsync(_provider, request, "(in progress)", Title, ct);
            if (title.Length == 0 || title == Title) return;

            Title = title;
            _log?.Title(title);
            TitleChanged?.Invoke(title);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChatProviderException)
        {
            // A name is a nicety; a failed naming call is not worth a line.
        }
    }

    /// <summary>The scope question, and the design pass when it says so. Null when the everyday model just goes ahead.</summary>
    private async Task<string?> MaybeDesignAsync(string request, CancellationToken ct)
    {
        var scope = await _router.ScopeAsync(request, Digest(), ct);
        var strong = _config.ReasoningModel;
        var verdict = scope.NeedsDesign ? $"large — {strong} designs first"
            : scope.Decision is { Ok: true, Choice: SmartRouter.NeedsDesign } ? "large but unsure — going ahead without a design"
            : scope.Decision.Ok ? "small — going ahead"
            : $"unavailable ({scope.Decision.Message}), going ahead";
        _log?.Decision("scope", scope.Decision, verdict);
        Decided?.Invoke(new SmartNote("scope", scope.Decision, verdict));

        if (!scope.NeedsDesign) return null;

        ActivityStarted?.Invoke($"designing with {strong}");
        var clock = Stopwatch.StartNew();

        string design;
        try
        {
            design = await ReasoningSubtask.DesignAsync(_reasoning!, _config.Model, Shell, request, Digest(), ct,
                received => ActivityStarted?.Invoke($"designing with {strong} · {received:N0} chars so far"));
        }
        catch (ChatProviderException ex)
        {
            var failed = new AgentStep(0, ReasoningSubtask.DesignTag, $"{strong} failed: {ex.Message} — building without a design", false, clock.ElapsedMilliseconds);
            _log?.Step(failed);
            StepCompleted?.Invoke(failed);
            return null;
        }

        if (design.Length == 0) return null;

        _state.CountDesign();
        var step = new AgentStep(0, ReasoningSubtask.DesignTag, $"{strong} · {design.Length} chars", true, clock.ElapsedMilliseconds);
        _log?.Step(step);
        StepCompleted?.Invoke(step);

        // The person follows along: the design's first lines, and — when it
        // hinges on a choice — the choice itself, before anything is built.
        var decision = ReasoningSubtask.ExtractDecision(design, out var body);
        DesignMade?.Invoke(ReasoningSubtask.Summary(body));

        string? chosen = null;
        if (decision is not null)
        {
            var ask = new ChoiceRequest($"the design needs a decision ({strong} recommends {decision.Recommended + 1})", decision.Options, decision.Recommended);
            chosen = Chooser is null
                ? decision.Options[decision.Recommended]
                : ask.Resolve(await Chooser(ask, ct));
            Noted?.Invoke($"decided: {chosen}");
            _log?.Step(new AgentStep(0, "decision", chosen, true));
        }

        return ReasoningSubtask.DesignFeedBack(strong, body, chosen);
    }

    private async Task<AgentRun> MaybeEscalateAsync(string request, int messagesBefore, AgentRun draft, CancellationToken ct)
    {
        var material = ToolResultsSince(messagesBefore);
        var judged = await _router.EscalateAsync(request, material, draft.Text, ct);

        var strong = _config.ReasoningModel;
        var verdict = judged.Escalate ? $"escalating to {strong}"
            : judged.Decision.Ok ? "keeping the draft"
            : $"unavailable ({judged.Decision.Message}), keeping the draft";
        _log?.Decision("escalation", judged.Decision, verdict);
        Decided?.Invoke(new SmartNote("escalation", judged.Decision, verdict));

        if (!judged.Escalate) return draft;

        _state.CountEscalation();
        ActivityStarted?.Invoke($"reasoning with {strong}");
        var clock = Stopwatch.StartNew();

        string reasoning;
        try
        {
            reasoning = await ReasoningSubtask.RunAsync(_reasoning!, _config.Model, request, material, draft.Text, ct,
                received => ActivityStarted?.Invoke($"reasoning with {strong} · {received:N0} chars so far"));
        }
        catch (ChatProviderException ex)
        {
            // The strong model is an upgrade, not a dependency: when it cannot be
            // reached the draft stands, and the step says why.
            var failed = new AgentStep(0, ReasoningSubtask.Tag, $"{strong} failed: {ex.Message} — keeping the draft", false, clock.ElapsedMilliseconds);
            _log?.Step(failed);
            StepCompleted?.Invoke(failed);
            return draft;
        }

        var step = new AgentStep(0, ReasoningSubtask.Tag, $"{strong} · {reasoning.Length} chars", true, clock.ElapsedMilliseconds);
        _log?.Step(step);
        StepCompleted?.Invoke(step);

        // Back to the everyday model, with no tools: it has everything it needs.
        return await _loop.RunAsync(ReasoningSubtask.FeedBack(strong, reasoning), ct, SmartRouter.FamiliesFor(Route.Answer));
    }

    // ------------------------------------------------------------- the gate

    /// <summary>
    /// Whether a command may run. The static risk check comes first and cannot
    /// be overruled; then the decision engine, when there is one, judges the
    /// grey area and a confident "safe" runs unasked; everything else — unsafe,
    /// unsure, no engine, no key — is put to a person.
    /// </summary>
    private async Task<GateVerdict> GateAsync(string command, CancellationToken ct)
    {
        var risk = CommandRisk.Inspect(command);
        string reason;

        if (risk.Dangerous)
        {
            reason = risk.Reason;
        }
        else if (_smartAvailable)
        {
            var safety = await _router.SafetyAsync(command, _root, Shell, ct);
            var verdict = safety.Safe ? "safe, running"
                : safety.Decision.Ok ? $"{safety.Decision.Choice} — asking you"
                : $"unavailable ({safety.Decision.Message}) — asking you";
            _log?.Decision("safety", safety.Decision, verdict);
            Decided?.Invoke(new SmartNote("safety", safety.Decision, verdict));

            if (safety.Safe) return GateVerdict.Allow($"judged safe (confidence {safety.Decision.Confidence:0.00})");

            reason = safety.Decision.Ok
                ? $"the decision engine judged it {safety.Decision.Choice} (confidence {safety.Decision.Confidence:0.00})"
                : "the decision engine could not judge it";
        }
        else
        {
            reason = "no decision engine to judge it";
        }

        return await AskAsync(command, reason, ct);
    }

    private async Task<GateVerdict> AskAsync(string command, string reason, CancellationToken ct)
    {
        if (Approver is null)
        {
            _state.CountApproval(granted: false);
            return GateVerdict.Deny($"nobody here to approve it — {reason}");
        }

        var granted = await Approver(new ApprovalRequest(command, reason, _root), ct);
        _state.CountApproval(granted);
        Noted?.Invoke(granted ? $"approved: {command}" : $"declined: {command}");

        return granted
            ? GateVerdict.Allow("approved by the user")
            : GateVerdict.Deny($"the user declined to run it — {reason}. Do not retry it; explain, or do it another way.");
    }

    // ------------------------------------------------------------ grants

    /// <summary>
    /// A folder the person names by its absolute path becomes readable for the
    /// session — naming it is the approval. Nothing outside the root is ever
    /// written, whatever is named.
    /// </summary>
    private void GrantNamedFolders(string line)
    {
        foreach (var directory in PathGrants.FindOutside(line, _root))
        {
            if (!_state.Grant(directory)) continue;
            _files.GrantRead(directory);
            Noted?.Invoke($"read access granted for this session (read-only): {directory}");
        }
    }

    /// <summary>Every tool result the loop added since <paramref name="fromIndex"/>, in order.</summary>
    private string ToolResultsSince(int fromIndex)
    {
        var parts = new List<string>();
        for (var i = fromIndex; i < _loop.Messages.Count; i++)
        {
            var message = _loop.Messages[i];
            if (message.Role == "user" && message.Content.StartsWith("[tool:", StringComparison.Ordinal))
                parts.Add(message.Content);
        }
        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// What this conversation already holds, in a few lines: every tool result
    /// and hand-off so far by what it was, and each earlier question. Handed to
    /// the router so a follow-up is not sent to fetch what is already in context.
    /// </summary>
    internal string Digest()
    {
        var lines = new List<string>();

        foreach (var message in _loop.Messages)
        {
            if (message.Role != "user") continue;

            if (message.Content.StartsWith("[tool:", StringComparison.Ordinal)
                || message.Content.StartsWith($"[{ReasoningSubtask.Tag}:", StringComparison.Ordinal)
                || message.Content.StartsWith($"[{ReasoningSubtask.DesignTag}:", StringComparison.Ordinal))
            {
                // "[tool:web_read] Title\nhttps://…" — keep the tool and its first line.
                var end = message.Content.IndexOf('\n');
                var head = end < 0 ? message.Content : message.Content[..end];
                lines.Add(head.Length > 120 ? head[..120] + "…" : head);
            }
            else if (!message.Content.StartsWith('['))
            {
                var first = message.Content.Split('\n')[0];
                lines.Add("- asked earlier: " + (first.Length > 80 ? first[..80] + "…" : first));
            }
        }

        // The current request is not in the loop yet, so everything here is
        // genuinely "already there". Newest first, and only the last few.
        lines.Reverse();
        var digest = string.Join('\n', lines.Take(8));

        if (Title is { } title) digest = $"- the session's task: {title}\n" + digest;
        return digest;
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _background.Cancel();
        _background.Dispose();
        _graph?.Dispose();
        _engine.Dispose();
        _toolbelt.Dispose();
        (_reasoning as IDisposable)?.Dispose();
        (_provider as IDisposable)?.Dispose();
    }
}

/// <summary>Counts every question put to the engine, and what it cost, into the session state.</summary>
internal sealed class CountingEngine(IDecisionEngine inner, SessionState state) : IDecisionEngine, IDisposable
{
    public string Name => inner.Name;

    public async Task<Decision> ChooseAsync(
        string stateText, string question, IReadOnlyList<DecisionOption> options, CancellationToken ct)
    {
        var decision = await inner.ChooseAsync(stateText, question, options, ct);
        state.CountJev(decision.ElapsedMs);
        return decision;
    }

    public void Dispose() => (inner as IDisposable)?.Dispose();
}

/// <summary>
/// A token estimate with no tokenizer: ASCII runs about four characters a
/// token, CJK about one and a half. Close enough for a status line, which is
/// the only place it is shown, and honest about being an estimate there.
/// </summary>
public static class Tokens
{
    public static int Estimate(IReadOnlyList<ChatMessage> messages)
    {
        double total = 0;
        foreach (var message in messages) total += Estimate(message.Content);
        return (int)Math.Round(total);
    }

    public static double Estimate(string text)
    {
        int ascii = 0, wide = 0;
        foreach (var c in text)
        {
            if (c < 128) ascii++; else wide++;
        }
        return ascii / 4.0 + wide / 1.5;
    }
}
