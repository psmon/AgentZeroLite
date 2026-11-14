using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AgentOne.Graph;
using AgentOne.Llm;
using AgentOne.Llm.Decision;
using AgentOne.Services;
using AgentOne.Processes;
using AgentOne.Tools;
using AgentOne.Tools.Browser;
using AgentOne.Tools.Cli;

namespace AgentOne.Agent;

/// <summary>What smart mode decided at one of its checkpoints, for the renderers.</summary>
/// <param name="Kind">"route", "scope", "safety", "escalation", "graph", "knowledge", "pdsa" or "pdsa-study".</param>
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
    GraphStats? Graph = null,
    string? Pdsa = null)
{
    /// <summary>The status block, one fact per line.</summary>
    public IReadOnlyList<string> Describe()
    {
        var c = Counters;
        var graph = Graph is { } g
            ? $"graph     {g.Knowledge} items · {g.Paths} paths · {g.Turns} turns · helped {g.Helped} times"
            : "graph     off (Kùzu library not next to the binary)";
        var pdsa = Pdsa is { Length: > 0 } p ? "pdsa      " + p
            : Graph is null ? "pdsa      off (needs the knowledge graph)"
            : "pdsa      no improvement cycle yet — a plan starts one";
        var lines = new List<string>
        {
            $"task      {Title ?? "(not named yet)"}",
            $"session   {(LogPath ?? "(not saved)")}",
            $"mode      {(Smart ? "smart" : "basic")} · turns {c.Turns}",
            $"context   {ContextMessages} messages · {ContextChars:N0} chars · ~{EstimatedTokens:N0} tokens (estimate)",
            $"memory    {MemoryChars:N0} / {WorkspaceStore.MemoryCapChars:N0} chars · {MemoryPath}",
            graph,
            pdsa,
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
public sealed partial class ChatSession : IAgentSession
{
    private readonly IChatProvider _provider;
    private readonly IChatProvider? _reasoning;
    private readonly LocalFileToolbelt _files;
    private readonly ShellToolbelt _shell;
    private readonly BrowserToolbelt _browser;
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

    /// <summary>Set inside the after-turn work; flows to what it awaits and never back to the caller.</summary>
    private static readonly AsyncLocal<bool> AfterTurnScope = new();

    public bool RaisingAfterTurn => AfterTurnScope.Value;
    private readonly GraphMemory? _graph;
    private readonly PdsaMemory? _pdsa;
    private SessionStore? _log;

    /// <summary>The running turn's own cancellation — Stop, from a pause, cancels this.</summary>
    private CancellationTokenSource? _turnCts;
    private string _turnRequest = "";
    private readonly List<string> _turnProgress = [];

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

    /// <summary>An improvement cycle reached Act and was wired to its knowledge. Raised off the turn, after the distillation.</summary>
    public event Action<PdsaClosing>? CycleClosed;

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
        // A hard per-call ceiling (config.maxCallSeconds > 0) so no single model
        // call can hang the loop: past it the call ends as a stall the loop below
        // already recovers from. 0 leaves the provider untouched.
        _provider = WatchdogChatProvider.Wrap(provider, config.MaxCallSeconds)!;
        _reasoning = WatchdogChatProvider.Wrap(reasoning, config.MaxCallSeconds);
        _smartAvailable = smartAvailable;
        _logKind = logKind;

        Workspace = new WorkspaceStore(_root).Ensure();

        // The knowledge graph needs Kùzu's library next to the binary; without
        // it the agent runs as before and the status block says so.
        try { _graph = GraphMemory.Open(Workspace); }
        catch (InvalidOperationException) { _graph = null; }

        // The improvement cycle lives in the same graph as the knowledge it
        // produces — that is what lets a closed cycle be wired to it.
        _pdsa = _graph is null ? null : new PdsaMemory(_graph.Graph);

        // Smart starts where the config left it, but only with a key to make it
        // work. Starting "on" with no key would fail on the first turn.
        _state = new SessionState(config.SmartMode && _smartAvailable);
        _engine = new CountingEngine(engine, _state);

        _files = new LocalFileToolbelt(_root);
        _shell = new ShellToolbelt(_root, TimeSpan.FromSeconds(config.CommandTimeoutSeconds)) { Gate = GateAsync, Choose = ChooseIndexAsync };
        _shell.Activity += what => ActivityStarted?.Invoke(what);
        _shell.ProcessChanged += OnProcessChanged;

        // The browser runs playwright-cli in the workspace's own agent-one
        // folder, so its snapshot files never land in the person's project.
        _browser = new BrowserToolbelt(
            new PlaywrightCliDriver(Path.Combine(Workspace.Dir, "browser"), TimeSpan.FromSeconds(Math.Max(config.WebTimeoutSeconds, 30))),
            config.BrowserCdpUrl)
        {
            Gate = BrowserGateAsync
        };

        _toolbelt = new CompositeToolbelt(
            (ToolCatalog.FilesFamily, _files),
            (ToolCatalog.EditFamily, _files),
            (ToolCatalog.WebFamily, new WebToolbelt(TimeSpan.FromSeconds(config.WebTimeoutSeconds))),
            (ToolCatalog.ExecFamily, _shell),
            (ToolCatalog.BrowserFamily, _browser),
            (ToolCatalog.GuideFamily, new KnownCliToolbelt()),
            // Counted like smart mode's own questions, and judged against the person's words.
            (ToolCatalog.DecideFamily, new DecisionToolbelt(_smartAvailable ? _engine : null, config.JevConfidenceFloor)
            {
                Request = () => _loop?.Request
            }));

        _loop = new AgentLoop(_provider, _toolbelt, config.MaxSteps) { Streaming = streaming, Root = _root };
        _loop.OnStall = async (stall, retries, ct) =>
            await AskAfterStallAsync(_config.Model, stall, retries, [StopTurnOption], ct) == 0;
        _loop.Guide = WorkspaceGuide.Load(_root);
        _loop.Reset(Workspace.MemoryForPrompt());
        _loop.ActivityStarted += what => ActivityStarted?.Invoke(what);
        _loop.StepCompleted += step =>
        {
            if (step.Tool is not ("final" or "unwrapped")) _turnProgress.Add($"{step.Tool}{(step.Ok ? "" : " (failed)")}");
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
        // Read again: the owner may have edited it since the session started.
        _loop.Guide = WorkspaceGuide.Load(_root);
        _loop.Reset(Workspace.MemoryForPrompt());
        _unfinished = false;
        _lastRequest = null;
        _workFolder = null;
        _state.Reset();
        _files.ClearGrants();
        _browser.ClearGrants();
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
        var steps = new List<SessionEntry>();
        string? design = null;

        // A turn with no successful result — stopped, failed, or cut off with no
        // result at all — used to be dropped: its request and everything it did
        // were gone from the resumed context. Measured (2026-11-06): a long MV
        // build lost its connection after writing the lore file; a resume would
        // have known neither the request nor the file.
        void RestoreUnfinished(string why)
        {
            if (pending is null) return;
            _loop.Restore(pending, UnfinishedNote(why, steps, design));
            _unfinished = true;
        }

        foreach (var entry in entries)
        {
            switch (entry.Kind)
            {
                case "prompt":
                    RestoreUnfinished("it was cut off before it ended");
                    pending = entry.Text;
                    if (SmartRouter.Applies(entry.Text)) _lastRequest = entry.Text;
                    steps.Clear();
                    design = null;
                    _unfinished = false;
                    _state.CountTurn();
                    break;
                case "step" when entry.Tool == ReasoningSubtask.DesignTag + "-text":
                    design = entry.Text;
                    break;
                case "step":
                    steps.Add(entry);
                    if (entry.Tool == "write_file" && entry.Ok == true) NoteWorkFolder(entry.Text);
                    break;
                case "result":
                    if (pending is not null && entry.Ok == true) { _loop.Restore(pending, entry.Text); _unfinished = false; }
                    else RestoreUnfinished($"it stopped ({entry.Tool}): {WorkspaceStore.FirstLine(entry.Text, 160)}");
                    pending = null;
                    break;
                case "title":
                    Title = entry.Text;
                    break;
            }
        }
        RestoreUnfinished("it was cut off before it ended");

        _log = SessionStore.Open(path);
        _log.Resumed();
        if (Title is { } title) TitleChanged?.Invoke(title);
        return entries;
    }

    /// <summary>Steps of an unfinished turn shown on resume, newest kept.</summary>
    internal const int UnfinishedSteps = 25;

    /// <summary>
    /// What a resumed conversation is told about a turn that did not finish:
    /// why, what its tools did (the files written are the progress), and the
    /// head of its design — so "재개" / "continue" carries on from there.
    /// </summary>
    internal static string UnfinishedNote(string why, IReadOnlyList<SessionEntry> steps, string? design)
    {
        var sb = new StringBuilder($"(This turn did not finish — {why}. What it had done before that:");
        // "learned" is the turn before, distilled after it ended; not this turn's work.
        var done = steps.Where(s => s.Tool is not (null or "final" or "unwrapped" or "reasoning-text" or "(unparsed)" or "learned")).ToList();
        if (done.Count == 0) sb.Append("\n- nothing yet");
        foreach (var s in done.Skip(Math.Max(0, done.Count - UnfinishedSteps)))
            sb.Append("\n- ").Append(s.Tool).Append(s.Ok == false ? " (failed)" : "").Append(": ").Append(WorkspaceStore.FirstLine(s.Text, 160));
        if (design is { Length: > 0 })
            sb.Append("\n\nThe design it was following (head):\n").Append(Clip(design, 2_000));
        sb.Append("\n\nWhen the user says to resume or continue, check what is already on disk and carry on from the first " +
                  "step that is not done — do not start over.)");
        return sb.ToString();
    }

    /// <summary>
    /// The last turn ended without finishing (stopped, failed, or cut off). A
    /// short "재개" / "continue" right after one is a build resumed, and gets
    /// the build's repeat allowance.
    /// </summary>
    private bool _unfinished;

    /// <summary>The last request long enough to stand on its own — what "진행해" / "go on" refers to.</summary>
    private string? _lastRequest;

    /// <summary>The collection folder the session's latest writes went into (songs/2026-11-07-x), or null.</summary>
    private string? _workFolder;

    /// <summary>Remembers where a written path's work lives; a step detail "path=a/b/c.md content=…" or a bare path.</summary>
    private void NoteWorkFolder(string detail)
    {
        var path = detail.StartsWith("path=", StringComparison.Ordinal) ? detail[5..] : detail;
        var cut = path.IndexOf(" content=", StringComparison.Ordinal);
        if (cut >= 0) path = path[..cut];
        cut = path.IndexOf(" ->", StringComparison.Ordinal);
        if (cut >= 0) path = path[..cut];
        if (WorkspacePrecedent.WorkFolderOf(_root, path.Trim()) is { } folder) _workFolder = folder;
    }

    /// <summary>
    /// With no write to go by, the work folder the turn looked at most: the
    /// collection folder of each path a successful file step named (a folder path
    /// counts as itself), the most named one winning, the earliest on a tie.
    /// </summary>
    private void NoteWorkFolderFromLooks(AgentRun run)
    {
        if (WorkFolderLookedAt(_root, run.Steps) is { } folder) _workFolder = folder;
    }

    internal static string? WorkFolderLookedAt(string root, IEnumerable<AgentStep> steps)
    {
        var counts = new Dictionary<string, (int Count, int First)>(StringComparer.OrdinalIgnoreCase);
        var order = 0;
        foreach (var step in steps)
        {
            if (!step.Ok || step.Tool is not ("list_files" or "read_file" or "find_files" or "grep" or "write_file")) continue;
            var path = PathArgument(step.Detail);
            if (path is null || path is "." or "") continue;
            var probe = Directory.Exists(Path.Combine(root, path)) ? path.TrimEnd('/', '\\') + "/_" : path;
            if (WorkspacePrecedent.WorkFolderOf(root, probe) is not { } folder) continue;
            counts[folder] = counts.TryGetValue(folder, out var c) ? (c.Count + 1, c.First) : (1, order++);
        }
        return counts.Count == 0 ? null
            : counts.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Value.First).First().Key;
    }

    /// <summary>The path= argument of a step detail ("pattern=*.mp3 path=songs/x -> …"), or null.</summary>
    private static string? PathArgument(string detail)
    {
        var at = detail.StartsWith("path=", StringComparison.Ordinal) ? 0 : detail.IndexOf(" path=", StringComparison.Ordinal) + 1;
        if (at < 0 || (at == 0 && !detail.StartsWith("path=", StringComparison.Ordinal))) return null;
        var path = detail[(at + 5)..];
        foreach (var end in new[] { " content=", " ->", " pattern=", " text=" })
        {
            var cut = path.IndexOf(end, StringComparison.Ordinal);
            if (cut >= 0) path = path[..cut];
        }
        return path.Trim().Trim('"');
    }

    /// <summary>The precedent material for the current work folder, or empty.</summary>
    internal string Precedent() => WorkspacePrecedent.Material(_root, _workFolder);

    private string PrecedentSuffix() => Precedent() is { Length: > 0 } p ? "\n" + p : "";

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
            _graph is null ? null : SafeStats(),
            SafePdsaLine());
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

        // Commands on the knowledge graph: the session holds the graph's one
        // handle, so every face — window, REPL, `ask` — reaches it through here.
        if (IsKnowledgeCommand(line)) return await KnowledgeCommandAsync(line, ct);

        // The turn runs on its own token, linked to the caller's, so a Stop
        // typed during a pause ends this turn and nothing else.
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _turnCts = turn;
        _turnRequest = line;
        _turnProgress.Clear();
        try
        {
            return await SubmitCoreAsync(line, turn.Token);
        }
        finally
        {
            _turnCts = null;
            _loop.Pause.Clear();
        }
    }

    // ------------------------------------------------------ knowledge graph commands

    public static bool IsKnowledgeCommand(string line) =>
        line is "/knowledge" or "/cypher" ||
        line.StartsWith("/knowledge ", StringComparison.Ordinal) || line.StartsWith("/cypher ", StringComparison.Ordinal);

    /// <summary>
    /// /knowledge [status] · /knowledge init|update [folder] · /knowledge rebuild [folder]
    /// · /knowledge guidelines [n|file] · /cypher &lt;query&gt;. Not a turn: nothing goes to
    /// the model, the conversation and the session log are untouched.
    /// </summary>
    private async Task<AgentRun> KnowledgeCommandAsync(string line, CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        AgentRun Done(string text, bool ok = true) =>
            new(ok ? StopReason.Final : StopReason.ProviderError, text, [], clock.Elapsed);

        if (_graph is null)
            return Done("the knowledge graph is off here — Kùzu's library (kuzu_shared.dll / libkuzu.so / libkuzu.dylib) is not next to the binary", ok: false);
        var graph = _graph.Graph;

        if (line.StartsWith("/cypher", StringComparison.Ordinal))
        {
            var cypher = line.Length > 7 ? line[7..].Trim() : "";
            if (cypher.Length == 0) return Done("usage: /cypher <query>   e.g. /cypher MATCH (d:Doc)-[:GUIDES]->(k:Knowledge) RETURN d.path, k.title LIMIT 10", ok: false);
            try { return Done(KnowledgeText.Table(graph.QueryTable(cypher))); }
            catch (InvalidOperationException ex) { return Done(ex.Message, ok: false); }
        }

        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
        var sub = words.Count > 0 ? words[0].ToLowerInvariant() : "status";
        var argument = words.Count > 1 ? string.Join(' ', words.Skip(1)) : null;

        switch (sub)
        {
            case "status":
                return Done(string.Join("\n", KnowledgeText.Status(graph, Workspace.Root)));

            case "init" or "update" or "rebuild":
                if (argument is not null && !Directory.Exists(Path.Combine(_root, argument)))
                    return Done($"no such folder under the workspace: {argument}", ok: false);
                var scanner = new KnowledgeScanner(graph, _root, _smartAvailable ? _router : null);
                var report = await scanner.ScanAsync(sub == "rebuild", argument, what => ActivityStarted?.Invoke(what), ct);
                return Done(string.Join("\n", [$"knowledge {sub} · {Workspace.Root}", .. report.Describe(), "", .. KnowledgeText.Status(graph, Workspace.Root)]));

            case "guidelines":
                var limit = int.TryParse(argument, out var n) ? n : 20;
                var fragment = argument is not null && !int.TryParse(argument, out _) ? argument : null;
                return Done(KnowledgeText.Guidelines(graph.Guidelines(limit, fragment)));

            default:
                return Done(KnowledgeText.Usage, ok: false);
        }
    }

    // ------------------------------------------------------------- pause

    /// <summary>True while the turn is held between steps.</summary>
    public bool Paused => _loop.Pause.IsPaused;

    /// <summary>
    /// Holds the turn at its next step — the request in flight finishes first;
    /// nothing can interrupt a model mid-sentence. Harmless when nothing runs:
    /// the next turn would pause at its first step, so it is cleared then.
    /// </summary>
    public void Pause()
    {
        if (_turnCts is null) return;
        _loop.Pause.Pause();
        Noted?.Invoke("⏸ pausing at the next step — type to go on, 'stop' to abandon, or say what to change");
    }

    /// <summary>
    /// The line typed during a pause, read for what it means — resume, stop,
    /// or refine — by the decision engine when there is one, by a short word
    /// list otherwise; then applied. Refining puts the line in front of the
    /// model as the next thing it reads.
    /// </summary>
    public async Task<PauseOutcome> ResumeAsync(string line, CancellationToken ct)
    {
        line = line.Trim();
        if (_turnCts is null || !_loop.Pause.IsPaused)
            return new PauseOutcome(PauseVerdict.Resume, null, "nothing is paused");

        Decision? decision = null;
        PauseVerdict verdict;
        if (line.Length == 0 || !_smartAvailable)
        {
            verdict = JudgePauseLine(line);
        }
        else
        {
            decision = await _router.PauseVerdictAsync(_turnRequest, string.Join("; ", _turnProgress), line, ct);
            verdict = decision.Ok ? decision.Choice switch
            {
                SmartRouter.StopOption => PauseVerdict.Stop,
                SmartRouter.RefineOption => PauseVerdict.Refine,
                _ => PauseVerdict.Resume
            } : JudgePauseLine(line);
        }

        string message;
        switch (verdict)
        {
            case PauseVerdict.Stop:
                message = "stopped — the turn is abandoned";
                _turnCts.Cancel();
                _loop.Pause.Resume();
                break;
            case PauseVerdict.Refine:
                message = "refining: " + WorkspaceStore.FirstLine(line, 120);
                _loop.Pause.Resume(line);
                break;
            default:
                message = "resuming";
                _loop.Pause.Resume();
                break;
        }

        _log?.Decision("pause", decision ?? new Decision(true, verdict.ToString().ToLowerInvariant(), 1.0, new Dictionary<string, double>(), "rule", 0), message);
        Decided?.Invoke(new SmartNote("pause", decision ?? new Decision(true, verdict.ToString().ToLowerInvariant(), 1.0, new Dictionary<string, double>(), "rule", 0), message));
        return new PauseOutcome(verdict, decision, message);
    }

    /// <summary>The plain rule, for no engine or an empty line: a few words mean go on or stop; anything else is a refinement.</summary>
    internal static PauseVerdict JudgePauseLine(string line)
    {
        var l = line.Trim().ToLowerInvariant().TrimEnd('.', '!');
        if (l.Length == 0 || l is "continue" or "go on" or "go" or "ok" or "resume" or "y" or "yes" or "계속" or "재개" or "진행" or "고" or "ㅇㅋ")
            return PauseVerdict.Resume;
        if (l is "stop" or "cancel" or "abort" or "quit" or "never mind" or "forget it" or "n" or "no" or "중단" or "그만" or "취소" or "멈춰" or "스톱")
            return PauseVerdict.Stop;
        return PauseVerdict.Refine;
    }

    private async Task<AgentRun?> SubmitCoreAsync(string line, CancellationToken ct)
    {
        GrantNamedFolders(line);

        var snapshot = _state.Read();
        _log?.Prompt(line, snapshot.Smart);
        var turnNumber = _state.CountTurn();
        var turnId = $"{_log?.Id ?? "session"}-{turnNumber}";

        var smart = snapshot.Smart && SmartRouter.Applies(line);

        // "3번 수행" means item 3 of the last answer. Measured (2026-09-30): the
        // router saw only the words, routed answer_directly, and the model read
        // "3번 수행 이제 필요없음" as "don't" — the file stayed. The item goes
        // into the request both the router and the model see.
        var asked = line;
        if (ResolveReference(line) is { } reference)
        {
            asked = line + "\n\n" + reference;
            _log?.Step(new AgentStep(0, "reference", reference, true));
        }

        // "생성 진행해", "재개", "go on": too short to route, and it means the
        // last real request. Measured (2026-11-07): after "종말의 그날 곡 다비드로
        // 생성 진행해줘", the reply "생성 진행해" was taken as "generate the MV's
        // image assets" — a prompt document for Midjourney was written and the
        // music was never generated. The model is told what it continues; the
        // after-turn checks judge the answer against that request, not two words.
        var subject = line;
        if (!SmartRouter.Applies(line) && _lastRequest is { } earlier && ResolveReference(line) is null)
        {
            asked += $"\n\n[context] This short reply continues the earlier request: \"{WorkspaceStore.FirstLine(earlier, 300)}\"";
            subject = earlier + "\n(the user, continuing: " + line + ")";
        }
        if (SmartRouter.Applies(line)) _lastRequest = line;
        _loop.Request = line;

        var prompt = asked;
        IReadOnlySet<string>? families = null;
        var designed = false;
        var stopped = false;
        string? designText = null;
        PdsaTurn? pdsa = null;
        ProbeResult? probe = null;
        var lookup = false;

        // The task is (re)named from the request, in the background, as the turn
        // starts — so the header says "게시판 API 개발" seconds in, not minutes
        // later when a long build ends. A greeting is not a task.
        if (SmartRouter.Applies(line)) _ = RetitleAsync(line);

        if (smart)
        {
            var route = await _router.RouteAsync(asked, Digest(), ct);
            var verdict = route.Steers ? $"→ {RouteName(route.Route!.Value)}"
                : route.Decision.Ok ? $"unsure ({route.Decision.Choice}), all tools stay available"
                : $"unavailable ({route.Decision.Message})";
            _log?.Decision("route", route.Decision, verdict);
            Decided?.Invoke(new SmartNote("route", route.Decision, verdict));

            if (route.Steers)
            {
                prompt = route.Guidance(asked);
                families = route.Families;
            }

            // A web lookup is answered from the pages it reads (see MaybeEscalateAsync).
            lookup = route.Steers && route.Route == Route.Web;

            // Work in the workspace, or work whose shape is unclear, may be big
            // enough to design first. A confident web lookup or plain answer
            // never is — but an unsure route keeps every tool, so it is sized too:
            // "make a board API" once routed answer_directly at 0.58 and got no
            // design for exactly the request that needed one.
            var mayBuild = !route.Steers || route.Route == Route.Files;

            // What the request names may already be in the workspace. Looked for
            // before anything is designed or built, and handed to both models.
            if (mayBuild && ProbesWorkspace && SmartRouter.Applies(line))
            {
                probe = await ProbeAsync(line, ct);
                if (probe.Found) prompt = prompt + Environment.NewLine + Environment.NewLine + probe.Material();
                if (Precedent() is { Length: > 0 } precedent) prompt = prompt + Environment.NewLine + Environment.NewLine + precedent;
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

            if (_reasoning is not null && mayBuild)
            {
                var (design, stop) = await MaybeDesignAsync(line, probe, ct);
                if (stop)
                {
                    // The person chose to end the turn at the stalled design.
                    // Cancelling the turn's own token lets the loop return
                    // Cancelled on its first step, so the turn still ends the
                    // usual way — logged, remembered — with nothing built.
                    _turnCts?.Cancel();
                    stopped = true;
                }
                if (design is { } plan)
                {
                    prompt = prompt + Environment.NewLine + Environment.NewLine + plan;
                    designText = plan;
                    designed = true;
                }
            }

            // Planning is the door into the improvement cycle: a design just
            // made is a Plan, and otherwise the engine has to be sure this
            // request is one before several turns get drawn into a cycle.
            if (!stopped) pdsa = await PdsaBeforeAsync(line, designed, ct);
        }

        var before = _loop.Messages.Count;
        AgentRun run;
        // "재개" after a build that did not finish is that build, resumed.
        _loop.Building = designed || (_unfinished && !SmartRouter.Applies(line));
        try { run = await _loop.RunAsync(prompt, ct, families); }
        finally { _loop.Building = false; }
        _unfinished = !run.Succeeded;
        // The checks below hand the precedent to the redo, and a fresh session has
        // written nothing yet. Measured (2026-11-07, /new then "review the takes"):
        // the turn listed songs/2026-11-07-x twice, the redo plan came without the
        // sibling song folders and their download log, and the model never found
        // where the takes were.
        if (_workFolder is null) NoteWorkFolderFromLooks(run);

        // Before anything builds on the answer, hold its report against what the
        // tools actually did. A turn that had to be sent back is not escalated:
        // measured, the strong model polished an unbacked "tests performed"
        // into a completion report — it sees the draft, not the disk.
        // The checks judge the answer, not the request: "재개" is too short to
        // route, but a turn it started that used tools is real work and is held
        // to its report like any other.
        var checks = smart || (snapshot.Smart && ToolSteps(run) > 0);
        var checkedClaims = false;
        if (checks && run.Succeeded && ChecksClaims)
            (run, checkedClaims) = await CheckClaimsAsync(subject, run, ct, probe);

        // An answer that hands the work back goes back once: with the files that
        // hold what it asked for, when the workspace has them and the turn did
        // not look; or, for a turn that worked with tools and changed nothing,
        // with the order to go ahead. Measured (2026-11-07): "재개" on "generate
        // it with deevid" read the right guides, then laid out two versions of
        // the three fields and asked which one — nothing written, nothing run.
        var handedBack = false;
        var unread = probe is { Found: true } && !LookedAt(run, probe);
        // Not only "changed nothing": measured, a turn wrote a prompt document and
        // then told the user it was their turn to generate. Nor does writing and
        // running settle it: measured (2026-11-07), a review turn wrote two reports,
        // ran ffprobe over no files, and ended "please upload the takes to out/" —
        // the takes were on the generator's site and the workspace had the script
        // that fetches them. Any turn that worked with tools is asked; the question
        // is one fixed-option call.
        var workedButUnfinished = ToolSteps(run) > 0;
        if (checks && run.Succeeded && ChecksClaims && run.Unverified is null && (unread || workedButUnfinished))
            (run, handedBack) = await CheckHandBackAsync(subject, run, unread ? probe! : null, ct);

        // A designed build already had the strong model's thinking; judging its
        // report against the same model again would be a second slow pass for nothing.
        // Nor is a turn that acted — wrote, ran, clicked — escalated: its answer is
        // a report, right or wrong by the tools' record (which the claim check
        // holds), and the strong model cannot redo the actions. Measured
        // (2026-11-07): a deevid generation report went to the strong model at 0.74
        // and came back 11 minutes later saying the same thing.
        var acted = run.Steps.Any(s => s.Ok && (s.Tool is "write_file" or "run_command"
                                                || (ToolCatalog.FamilyOf(s.Tool) == ToolCatalog.BrowserFamily
                                                    && ToolCatalog.RepeatOf(s.Tool) == RepeatRule.Free)));
        if (smart && run.Succeeded && _reasoning is not null && !designed && !checkedClaims && !handedBack && !acted && run.Unverified is null)
            run = await MaybeEscalateAsync(line, before, run, ct, lookup);

        // A turn that ran out of budget mid-work must not end on "[stopped: MaxSteps]"
        // and nothing else: the person needs to know what got done and what is next.
        if (!run.Succeeded && run.Reason is StopReason.MaxSteps or StopReason.Repeat && ToolSteps(run) > 0)
            run = await WrapUpAsync(run, ct);

        foreach (var step in run.Steps)
            if (step.Ok && step.Tool == "write_file") NoteWorkFolder(step.Detail);
        _log?.Result(run);
        Remember(line, run);

        if (pdsa is not null) await PdsaAfterAsync(pdsa, turnId, line, run, designText, ct);

        // Learning runs off the turn, and a cycle must not be closed before
        // its last lesson is stored — so the close rides behind the learning.
        // An unverified turn teaches nothing: its report is exactly what must
        // not become a fact the next turn is handed as [graph memory].
        var learns = _graph is not null && UsesGraph && _smartAvailable && _provider.Name != "echo" && run.Unverified is null;
        if (run.Unverified is not null && _graph is not null && UsesGraph)
            Noted?.Invoke("knowledge: not learned from this turn — its report is unverified");
        var closing = pdsa is { Closes: true } ? pdsa.Cycle : (long?)null;
        if (learns || closing is not null) _ = LearnThenCloseAsync(turnId, line, run, learns, closing);

        return run;
    }

    // ---------------------------------------------------------------- pdsa

    /// <summary>Which phase of which cycle this turn is, or null when PDSA has nothing to say about it.</summary>
    private async Task<PdsaTurn?> PdsaBeforeAsync(string request, bool designed, CancellationToken ct)
    {
        if (_pdsa is null || !UsesPdsa) return null;

        PdsaTurn? turn;
        try
        {
            turn = await _pdsa.BeforeAsync(_router, request, designed, ct);
        }
        catch (InvalidOperationException ex)
        {
            Noted?.Invoke("improvement cycle unavailable: " + ex.Message);
            return null;
        }

        if (turn is null) return null;

        var verdict = turn.Opened
            ? $"cycle #{turn.Cycle} opened at plan" + (turn.Reinforces > 0 ? $", reinforcing #{turn.Reinforces}" : "")
            : $"cycle #{turn.Cycle} · {turn.Step}";
        _log?.Decision("pdsa", turn.Decision, verdict);
        Decided?.Invoke(new SmartNote("pdsa", turn.Decision, verdict));
        return turn;
    }

    /// <summary>Writes the turn as its phase; a Study turn also gets the engine's verdict against the plan.</summary>
    private async Task PdsaAfterAsync(PdsaTurn turn, string turnId, string request, AgentRun run, string? design, CancellationToken ct)
    {
        var note = _turnProgress.Count > 0 ? string.Join(", ", _turnProgress) : "(no tools)";
        var outcome = run.Succeeded ? run.Text : $"stopped ({run.Reason}): {run.Text}";

        try
        {
            var record = await _pdsa!.RecordAsync(_router, turn, turnId, request, note, outcome, design, ct);
            if (record.Verdict is not { } judged) return;

            var verdict = judged.Ok
                ? $"cycle #{turn.Cycle}: the plan was {judged.Choice}"
                : $"cycle #{turn.Cycle}: unjudged ({judged.Message})";
            _log?.Decision("pdsa-study", judged, verdict);
            Decided?.Invoke(new SmartNote("pdsa-study", judged, verdict));
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            // The record is bookkeeping; a turn that answered is not a failure
            // because its phase could not be written.
            Noted?.Invoke("improvement cycle: phase not recorded — " + ex.Message);
        }
    }

    /// <summary>Off the turn: distil what it taught, then — for an Act turn — close the cycle onto that knowledge.</summary>
    private async Task LearnThenCloseAsync(string turnId, string request, AgentRun run, bool learn, long? closing)
    {
        AfterTurnScope.Value = true;
        if (learn) await LearnAsync(turnId, request, run);
        if (closing is not { } cycle || _pdsa is null) return;

        try
        {
            var closed = _pdsa.Close(cycle);
            var line = $"cycle #{cycle} closed ({closed.Verdict}) — taught {closed.Taught.Count}, built on {closed.BuiltOn.Count}";
            _log?.Step(new AgentStep(0, "pdsa", line, true));
            Noted?.Invoke("  ↳ " + line);
            CycleClosed?.Invoke(closed);
        }
        catch (InvalidOperationException ex)
        {
            Noted?.Invoke("improvement cycle: not closed — " + ex.Message);
        }
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

    private string? SafePdsaLine()
    {
        if (_pdsa is null) return null;
        try { return _pdsa.StatusLine(); }
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
            var outcome = await _graph!.LearnAsync(_router, _provider, turnId, request, run, _background.Token);

            // The verdict is shown either way: a graph that stays empty has to
            // be explainable — "skip" three turns running is a fact, not a bug.
            var verdict = outcome.KeptUnsure ? $"kept {outcome.Items.Count} item(s) — the engine leaned to skip but was not sure"
                : outcome.Saved ? $"kept {outcome.Items.Count} item(s)"
                : !outcome.Verdict.Ok ? $"unavailable ({outcome.Verdict.Message})"
                : outcome.Verdict.Choice == SmartRouter.SaveKnowledge ? "worth keeping, but nothing distilled"
                : "nothing worth keeping";
            _log?.Decision("knowledge", outcome.Verdict, verdict);
            Decided?.Invoke(new SmartNote("knowledge", outcome.Verdict, verdict));

            if (!outcome.Saved) return;
            foreach (var item in outcome.Items)
            {
                _log?.Step(new AgentStep(0, "learned", $"({item.Kind}) {item.Title} — {item.Text}", true));
                Noted?.Invoke($"  ↳ learned ({item.Kind}) {item.Title}");
            }
            Learned?.Invoke(outcome.Items);
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
            ct, SmartRouter.FamiliesFor(Route.Answer), familiesAreFinal: true);

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
            ? Clip(MemoryText(run.Text), MemoryOutcomeChars)
            : $"stopped ({run.Reason}): {Clip(run.Text.Replace('\n', ' '), 200)}";

        // memory.md opens every later session's prompt. A report that did not
        // hold is kept as a warning, not as what happened.
        if (run.Unverified is { } unverified)
            outcome = $"UNVERIFIED — {unverified}. The answer said: {Clip(run.Text.Replace("\r", "").Replace('\n', ' '), 200)}";

        var sb = new StringBuilder();
        sb.Append("## ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append(" · ")
          .AppendLine(Title ?? WorkspaceStore.FirstLine(request, 60));
        sb.Append("- asked: ").AppendLine(WorkspaceStore.FirstLine(request, 200));
        sb.Append("- did: ").AppendLine(did.Count == 0 ? "(no tools)" : string.Join("; ", did));
        sb.Append("- outcome: ").Append(outcome);

        Workspace.Remember(sb.ToString());
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    /// <summary>How much of an answer a memory.md entry keeps, after the markup is gone.</summary>
    internal const int MemoryOutcomeChars = 600;

    /// <summary>
    /// An answer as one line of substance: markdown bold, headings, table rules
    /// and cell bars dropped, lines joined with " / ". Measured (2026-11-06): a
    /// channel's video list was kept as "### 📜 역사/세계사 주제 영상 | 제목 | 표시된
    /// 등록일 | | :--- | :--- | | 고대 이집트 피라미드…" — 300 characters, one title,
    /// the next session's memory blind to the other six.
    /// </summary>
    internal static string MemoryText(string answer)
    {
        var lines = new List<string>();
        foreach (var raw in answer.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || TableRule().IsMatch(line) || line is "---" or "***") continue;
            // A bar escaped inside a cell ("고흐의 편지 \| 영어 회화") is text, not a cell edge.
            line = line.TrimStart('#', ' ').Replace("**", "").Replace("__", "").Replace("\\|", "\u0001");
            if (line.StartsWith('|')) line = string.Join(" · ", line.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            line = line.Replace('\u0001', '|');
            if (line.Length > 0) lines.Add(line);
        }
        return string.Join(" / ", lines);
    }

    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$")]
    private static partial Regex TableRule();

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

    /// <summary>Off for tests that script the engine: the step question and the Study verdict would consume its answers.</summary>
    internal bool UsesPdsa { get; set; } = true;

    /// <summary>Off for tests that script the engine: the claim question after a turn would consume its answers.</summary>
    internal bool ChecksClaims { get; set; } = true;

    /// <summary>Off for a test that wants the turn's prompt exactly as the route made it.</summary>
    internal bool ProbesWorkspace { get; set; } = true;

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
    private async Task<(string? Design, bool Stop)> MaybeDesignAsync(string request, ProbeResult? probe, CancellationToken ct)
    {
        var scope = await _router.ScopeAsync(request, Digest(), ct);
        var strong = _config.ReasoningModel;
        var verdict = scope.NeedsDesign ? $"large — {strong} designs first"
            : scope.Decision is { Ok: true, Choice: SmartRouter.NeedsDesign } ? "large but unsure — going ahead without a design"
            : scope.Decision.Ok ? "small — going ahead"
            : $"unavailable ({scope.Decision.Message}), going ahead";
        _log?.Decision("scope", scope.Decision, verdict);
        Decided?.Invoke(new SmartNote("scope", scope.Decision, verdict));

        if (!scope.NeedsDesign) return (null, false);

        ActivityStarted?.Invoke($"designing with {strong}");
        var clock = Stopwatch.StartNew();

        string design;
        for (var retries = 0; ; retries++)
        {
            try
            {
                design = await ReasoningSubtask.DesignAsync(_reasoning!, _config.Model, Shell, request, DesignContext(probe), ct,
                    received => ActivityStarted?.Invoke($"designing with {strong} · {received:N0} chars so far"),
                    Tools.ShellInfo.Current.Hints);
                break;
            }
            catch (ChatProviderStalledException ex)
            {
                // A stall says nothing about the request, so the design is not
                // given up on quietly: the person decides — the same request
                // again, go on without it, or end the turn here.
                var pick = await AskAfterStallAsync(strong, ex, retries, [BuildWithoutDesignOption, StopTurnOption], ct);
                var stalled = new AgentStep(0, ReasoningSubtask.DesignTag,
                    $"{strong} stalled: {ex.Message} — " + (pick == 0 ? "retrying" : pick == 1 ? "building without a design" : "turn stopped"),
                    false, clock.ElapsedMilliseconds);
                _log?.Step(stalled);
                StepCompleted?.Invoke(stalled);
                if (pick == 1) return (null, false);
                if (pick == 2) return (null, true);

                ActivityStarted?.Invoke($"designing with {strong} · retry {retries + 1}");
                clock.Restart();
            }
            catch (ChatProviderException ex)
            {
                var failed = new AgentStep(0, ReasoningSubtask.DesignTag, $"{strong} failed: {ex.Message} — building without a design", false, clock.ElapsedMilliseconds);
                _log?.Step(failed);
                StepCompleted?.Invoke(failed);
                return (null, false);
            }
        }

        if (design.Length == 0) return (null, false);

        _state.CountDesign();
        var step = new AgentStep(0, ReasoningSubtask.DesignTag, $"{strong} · {design.Length} chars", true, clock.ElapsedMilliseconds);
        _log?.Step(step);
        StepCompleted?.Invoke(step);
        LogText(ReasoningSubtask.DesignTag, design);

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

        return (ReasoningSubtask.DesignFeedBack(strong, body, chosen), false);
    }

    // ------------------------------------------------------------ claims

    /// <summary>
    /// Holds the answer's report against the turn's tool calls. Asked only when
    /// something is missing from the record — no successful write, or no
    /// successful command — since a turn that did both has nothing the engine
    /// could catch that the loop's file check would not. The engine's verdict
    /// is taken only where the record agrees: "unbacked change" with a
    /// successful write_file in hand is not acted on, and the same for runs.
    /// On a contradiction the model gets one correction pass with every tool;
    /// if the record still does not back the report, the answer carries a
    /// warning and the run is marked <see cref="AgentRun.Unverified"/>.
    /// </summary>
    /// <returns>The run to use, and whether a correction pass was needed.</returns>
    private async Task<(AgentRun Run, bool Corrected)> CheckClaimsAsync(string request, AgentRun run, CancellationToken ct,
        ProbeResult? probe = null)
    {
        var what = await UnbackedClaimAsync(request, run, ct);
        if (what is null) return (run, false);

        var (claim, tool) = what.Value;
        var sentBack = new AgentStep(0, "unbacked", $"the answer reports {claim}, but no {tool} succeeded this turn — sent back once", false);
        _log?.Step(sentBack);
        StepCompleted?.Invoke(sentBack);
        ActivityStarted?.Invoke("checking the report against what the tools did");

        // A review the user asked for is work to do, not a confession to make.
        // Measured (2026-11-07): given "do it now, or say plainly what was not
        // done", the model chose the confession — "I did not listen to them" —
        // and the takes stayed unexamined.
        var review = claim.StartsWith("a judgement", StringComparison.Ordinal);
        var order = review
            ? "[check] Your answer judges files or media that no tool examined, so the judgement has no basis. The user " +
              "asked for the review: do it now. Get the files (download them if they exist only online — the workspace " +
              "may already have a script for that), then examine them with tools — ffprobe / ffmpeg, a speech recognizer, " +
              "or the measuring scripts earlier work in this workspace used — and recommend from what you measured. Do " +
              "not answer by explaining what you could not do." + PrecedentSuffix()
            : $"[check] Your answer says {claim}, but no {tool} call succeeded in this turn — so it did not happen. " +
              "Either do it now with the tools, or answer again and say plainly, in the user's language, what has NOT " +
              "been done. If it was done in an earlier turn, look with a tool (read_file, list_files) before saying so. " +
              "Never report a change or a result that a tool did not produce.";
        if (review && await PlanRedoAsync(request, "it judged files or media that no tool examined", probe, ct) is { } plan)
            order += "\n\n" + plan;
        AgentRun fix;
        _loop.Building = review;
        try { fix = await _loop.RunAsync(order, ct); }
        finally { _loop.Building = false; }

        var merged = fix with { Steps = [.. run.Steps, sentBack, .. fix.Steps], Elapsed = run.Elapsed + fix.Elapsed };
        if (!fix.Succeeded || fix.Unverified is not null) return (merged, true);

        var still = await UnbackedClaimAsync(request, merged, ct);
        if (still is null) return (merged, true);

        var note = $"unverified: the answer reports {still.Value.Claim}, but no {still.Value.Tool} succeeded in this turn";
        var flagged = new AgentStep(0, "unverified", note, false);
        _log?.Step(flagged);
        StepCompleted?.Invoke(flagged);
        return (merged with { Steps = [.. merged.Steps, flagged], Text = merged.Text + "\n\n⚠ " + note, Unverified = $"reports {still.Value.Claim} with no {still.Value.Tool}" }, true);
    }

    /// <summary>The share an unbacked option needs to stand in for an unsure "backed".</summary>
    internal const double UnbackedRunnerUpShare = 0.25;

    /// <summary>
    /// "backed" below the floor is not a clearance: when an unbacked option holds a
    /// real share, it stands. Measured (2026-11-07): a turn read the report an
    /// earlier unmeasured turn had written and repeated its ranking of six takes;
    /// the engine said "backed" at 0.31 and the answer went out as a review.
    /// </summary>
    internal static Decision UnsureBackedYields(Decision judged, double floor)
    {
        if (!judged.Ok || judged.Choice != SmartRouter.ClaimsBacked || judged.Confidence >= floor) return judged;
        var runnerUp = judged.Ranked.FirstOrDefault(p => p.Key != SmartRouter.ClaimsBacked);
        return runnerUp.Key is { } key && runnerUp.Value >= UnbackedRunnerUpShare
            ? judged with { Choice = key, Confidence = runnerUp.Value }
            : judged;
    }

    /// <summary>The command of a run_command step detail ("command=… -> 733 chars").</summary>
    internal static string CommandOf(string detail)
    {
        var text = detail.StartsWith("command=", StringComparison.Ordinal) ? detail[8..] : detail;
        var cut = text.LastIndexOf(" -> ", StringComparison.Ordinal);
        return cut >= 0 ? text[..cut] : text;
    }

    private static readonly HashSet<string> ListingVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "get-childitem", "gci", "ls", "dir", "test-path", "get-item", "gi", "resolve-path", "get-location", "pwd",
        "cd", "set-location", "select-object", "select", "where-object", "where", "?", "format-table", "ft",
        "format-list", "fl", "sort-object", "sort", "measure-object", "measure", "out-string", "write-output",
        "echo", "write-host", "find", "tree", "out-host", "group-object", "foreach-object", "%", "stat", "wc", "head", "tail",
        // control words: the programs inside their blocks are parts of their own
        "foreach", "for", "if", "else", "elseif", "in",
    };

    /// <summary>
    /// True when every part of a command only lists or filters files — nothing
    /// in it opens, plays, measures or transcribes one. Parts are split at
    /// pipes, semicolons, &amp;&amp; and newlines; a part's verb is its first word
    /// after an assignment. ForEach-Object passes only when its block holds no
    /// other program (the block's words are parts of their own).
    /// </summary>
    internal static bool ListsOnly(string command)
    {
        var parts = System.Text.RegularExpressions.Regex.Split(command, @"\|\||&&|[|;\n{}()]");
        var any = false;
        foreach (var raw in parts)
        {
            var part = raw.Trim();
            var assign = System.Text.RegularExpressions.Regex.Match(part, @"^\$[\w:]+\s*=\s*");
            if (assign.Success) part = part[assign.Length..].Trim();
            part = part.TrimStart('&', '.', ' ').Trim();
            if (part.Length == 0 || part.StartsWith('$') || part.StartsWith('[') || part.StartsWith('-') || part.StartsWith('\'') || part.StartsWith('"')) continue;
            var verb = part.Split(' ', 2)[0];
            if (verb.StartsWith('-')) continue;
            any = true;
            if (!ListingVerbs.Contains(verb)) return false;
        }
        return any;
    }

    /// <summary>What the answer claims that the record does not show, or null when nothing is missing or the engine cannot say.</summary>
    private async Task<(string Claim, string Tool)?> UnbackedClaimAsync(string request, AgentRun run, CancellationToken ct)
    {
        var wrote = run.Steps.Any(s => s.Ok && s.Tool == "write_file");
        // An action in the browser backs a report of one, and of what it made
        // (a download, a generated track) — like a command does.
        var ran = run.Steps.Any(s => s.Ok && (s.Tool == "run_command"
                                              || (ToolCatalog.FamilyOf(s.Tool) == ToolCatalog.BrowserFamily
                                                  && ToolCatalog.RepeatOf(s.Tool) == RepeatRule.Free)));
        // Listing files examines nothing: measured (2026-11-07), a turn ran
        // Get-ChildItem for *.mp3, found none, ranked six takes by their lengths —
        // and the engine's "unbacked_review" (0.97) was ignored because a command
        // had succeeded.
        var examined = run.Steps.Any(s => s.Ok && (s.Tool == "run_command" ? !ListsOnly(CommandOf(s.Detail))
                                                   : ToolCatalog.FamilyOf(s.Tool) == ToolCatalog.BrowserFamily
                                                     && ToolCatalog.RepeatOf(s.Tool) == RepeatRule.Free));
        if (wrote && examined) return null;

        var did = string.Join("\n", run.Steps
            .Where(s => s.Tool is not ("final" or "unwrapped" or "(unparsed)"))
            .Select(s => $"- {s.Tool} ({(s.Ok ? "ok" : "failed")}): {WorkspaceStore.FirstLine(s.Detail, 120)}"));

        var judged = UnsureBackedYields(await _router.ClaimAsync(request, did, run.Text, ct), _config.JevConfidenceFloor);
        // A command makes files too — a download, a build, a generator script.
        // Measured (2026-11-05, music-harness): dv-fetch.cjs saved four takes,
        // the answer said so, and "files created with no write_file" sent a
        // finished turn back. A change is unbacked only when nothing that could
        // have made it succeeded; the loop's own check still holds every file
        // the answer names against the disk.
        (string, string)? contradiction = judged is { Ok: true, Choice: SmartRouter.UnbackedChange } && !wrote && !ran ? ("files created or changed", "write_file or run_command")
            : judged is { Ok: true, Choice: SmartRouter.UnbackedRun } && !ran ? ("commands, builds or tests run", "run_command")
            : judged is { Ok: true, Choice: SmartRouter.UnbackedReview } && !examined
                ? ("a judgement of files or media it never examined", "run_command or browser action that examined them")
            : null;

        var verdict = contradiction is { } c ? $"reports {c.Item1} with no {c.Item2}"
            : judged.Ok ? "the report matches the tool calls"
            : $"unavailable ({judged.Message})";
        _log?.Decision("claims", judged, verdict);
        Decided?.Invoke(new SmartNote("claims", judged, verdict));
        return contradiction;
    }

    // ---------------------------------------------------- the workspace

    /// <summary>The request's words searched for in the workspace, logged as a step; never fatal.</summary>
    private async Task<ProbeResult> ProbeAsync(string request, CancellationToken ct)
    {
        ActivityStarted?.Invoke("looking for the request's words in the workspace");
        var clock = Stopwatch.StartNew();
        ProbeResult probe;
        try { probe = await WorkspaceProbe.SearchAsync(_root, request, ct); }
        catch (OperationCanceledException) { return ProbeResult.Empty; }

        if (probe.Terms.Count == 0) return probe;
        var detail = probe.Found
            ? $"{probe.Hits.Count} file(s) mention {string.Join(", ", probe.Terms.Take(5))}: {string.Join(", ", probe.Hits.Take(4).Select(h => h.Path))}"
            : $"no file mentions {string.Join(", ", probe.Terms.Take(5))} ({probe.FilesScanned} scanned)";
        var step = new AgentStep(0, "workspace", detail, true, clock.ElapsedMilliseconds);
        _log?.Step(step);
        StepCompleted?.Invoke(step);
        return probe;
    }

    /// <summary>
    /// What the strong model is told before it designs: the conversation so far,
    /// the workspace's own instructions, and the files that mention the request.
    /// It used to get the conversation alone — and designed for a project it had
    /// never seen.
    /// </summary>
    internal string DesignContext(ProbeResult? probe)
    {
        var parts = new List<string>();
        var digest = Digest();
        if (digest.Length > 0) parts.Add(digest);
        var earlier = EarlierAnswers(_loop.Messages.Count);
        if (earlier.Length > 0) parts.Add(earlier);
        if (WorkspaceGuide.Load(_root, WorkspaceGuide.DesignChars) is { } guide)
            parts.Add($"The workspace's own instructions for agents ({string.Join(", ", guide.Files)}) — follow them:\n" + guide.Text);
        if (probe is { Found: true }) parts.Add(probe.Material());
        if (Precedent() is { Length: > 0 } precedent) parts.Add(precedent);
        return string.Join("\n\n", parts);
    }

    /// <summary>Whether the turn already looked: a search of its own, or one of the probe's files read.</summary>
    internal static bool LookedAt(AgentRun run, ProbeResult probe) =>
        run.Steps.Any(s => s.Ok && (s.Tool is "grep" or "find_files"
                                    || (s.Tool == "read_file" && probe.Hits.Any(h => s.Detail.Contains(h.Path, StringComparison.OrdinalIgnoreCase)))));

    /// <summary>
    /// An answer that hands the work back — "put the files in assets/in and tell
    /// me" — while the workspace holds what the request names, goes back once
    /// with those files. Measured (2026-11-06, music-harness): that answer was
    /// the whole turn, and the claim check passed it at 0.97, rightly — nothing
    /// in it was false. What was wrong is that nothing had been looked for.
    /// Asked when the probe found files the turn read none of (<paramref name="unread"/>), or when the
    /// turn worked with tools and changed nothing (null).
    /// </summary>
    private async Task<(AgentRun Run, bool SentBack)> CheckHandBackAsync(string request, AgentRun run, ProbeResult? unread, CancellationToken ct)
    {
        var judged = await _router.HandBackAsync(request, run.Text, ct);
        var handsBack = judged is { Ok: true, Choice: SmartRouter.HandsBack };
        var verdict = !handsBack ? (judged.Ok ? "the answer carries the work" : $"unavailable ({judged.Message})")
            : unread is not null ? "hands the work back without looking — sent back with the workspace's files"
            : "hands the work back — sent back to go ahead";
        _log?.Decision("hand-back", judged, verdict);
        Decided?.Invoke(new SmartNote("hand-back", judged, verdict));
        if (!handsBack) return (run, false);

        var sentBack = new AgentStep(0, "handed-back", unread is not null
            ? $"the answer asks the user for what {unread.Hits.Count} workspace file(s) may hold — sent back once"
            : "the answer hands the work or a choice back to the user — sent back once to go ahead", false);
        _log?.Step(sentBack);
        StepCompleted?.Invoke(sentBack);
        ActivityStarted?.Invoke(unread is not null ? "looking in the workspace before asking the user" : "going ahead instead of asking");

        var order = unread is not null
            ? "[check] Your answer hands the work back to the user, but you have not looked at what this workspace " +
              "already holds. These files mention the request:\n" +
              string.Join("\n", unread.Hits.Select(h => $"- {h.Path}:{h.Line}: {h.Text}")) + "\n" +
              "Read the relevant ones with read_file (and the documents they point to) and carry on with the work from " +
              "what is there. Ask the user only for what is truly not in the workspace — and then say which files you " +
              "looked at."
            : "[check] Your answer stops and hands the work, or a choice between options you laid out, back to the user. " +
              "They asked you to do it. Go ahead with your tools: where you offered options, take the default or the one " +
              "you would recommend (the workspace's instructions decide when they say); write the files and run what the " +
              "work needs. The gate asks the user by itself before anything that needs their approval runs. Ask the user " +
              "only what truly only they can answer, and say why." + PrecedentSuffix();
        if (await PlanRedoAsync(request, unread is not null
                ? "it asked the user for what the workspace already holds"
                : "it handed the work, or a choice, back to the user instead of doing it", unread, ct) is { } plan)
            order += "\n\n" + plan;

        // Sent back to do the work, it is a build: the same repeat allowance.
        AgentRun fix;
        _loop.Building = true;
        try { fix = await _loop.RunAsync(order, ct); }
        finally { _loop.Building = false; }

        var merged = fix with { Steps = [.. run.Steps, sentBack, .. fix.Steps], Elapsed = run.Elapsed + fix.Elapsed };
        return (merged, true);
    }

    /// <summary>
    /// When a check rejects the everyday model's turn, the strong model designs
    /// the redo: one completion over the workspace's instructions, the files the
    /// probe found and the conversation's answers — what to read and which of the
    /// workspace's scripts to run, step by step. Measured (2026-11-07): sent back
    /// twice with "examine the takes now", the everyday model listed folders and
    /// rewrote the MV plan; it could not find the procedure on its own, and the
    /// strong model, given the same material before an earlier build, had. Null
    /// when there is no strong model or it fails — the redo then goes on without.
    /// </summary>
    private async Task<string?> PlanRedoAsync(string request, string why, ProbeResult? probe, CancellationToken ct)
    {
        if (_reasoning is null || !_smartAvailable) return null;
        var strong = _config.ReasoningModel;
        ActivityStarted?.Invoke($"planning the redo with {strong}");
        var clock = Stopwatch.StartNew();

        string design;
        try
        {
            design = await ReasoningSubtask.DesignAsync(_reasoning, _config.Model, Shell,
                request + $"\n\n(The assistant's first attempt at this was rejected: {why}. Design how to actually do it " +
                "in this workspace: which of its documents to read first and which of its scripts or tools to run, step by step.)",
                DesignContext(probe), ct,
                received => ActivityStarted?.Invoke($"planning the redo with {strong} · {received:N0} chars so far"),
                Tools.ShellInfo.Current.Hints);
        }
        catch (ChatProviderException ex)
        {
            var failed = new AgentStep(0, ReasoningSubtask.DesignTag, $"{strong} failed to plan the redo: {ex.Message}", false, clock.ElapsedMilliseconds);
            _log?.Step(failed);
            StepCompleted?.Invoke(failed);
            return null;
        }
        if (design.Length == 0) return null;

        _state.CountDesign();
        var step = new AgentStep(0, ReasoningSubtask.DesignTag, $"{strong} · redo plan · {design.Length} chars", true, clock.ElapsedMilliseconds);
        _log?.Step(step);
        StepCompleted?.Invoke(step);
        LogText(ReasoningSubtask.DesignTag, design);

        // A redo does not stop for a choice: the recommendation is taken.
        var decision = ReasoningSubtask.ExtractDecision(design, out var body);
        DesignMade?.Invoke(ReasoningSubtask.Summary(body));
        return ReasoningSubtask.DesignFeedBack(strong, body, decision?.Options[decision.Recommended]);
    }

    // ------------------------------------------------------------ stalls

    internal const string RetryOption = "retry — send the same request again";
    internal const string StopTurnOption = "stop the turn";
    internal const string BuildWithoutDesignOption = "go on without a design";
    internal const string KeepDraftOption = "keep the draft";

    /// <summary>
    /// Retries recommended before the recommendation turns to giving up. It
    /// bounds only the unattended case — `run`, or a background session nobody
    /// is attached to, takes the recommendation — so a dead endpoint cannot
    /// keep a turn retrying forever; a person may still pick retry every time.
    /// </summary>
    internal const int UnattendedStallRetries = 2;

    /// <summary>Dropped connections retried without asking, per step.</summary>
    internal const int DroppedAutoRetries = 2;

    /// <summary>The pause before each automatic retry of a dropped connection; a test sets it to zero.</summary>
    internal Func<int, TimeSpan> DroppedRetryDelay { get; set; } = retry => TimeSpan.FromSeconds(retry == 0 ? 5 : 20);

    /// <summary>
    /// A model went quiet. Puts retry and the caller's alternatives to the
    /// person through <see cref="Chooser"/> and returns the index picked:
    /// 0 is retry, 1.. are <paramref name="alternatives"/> in order. Nothing
    /// before the stalled call is thrown away — a retry sends the same messages.
    /// </summary>
    private async Task<int> AskAfterStallAsync(string model, ChatProviderStalledException stall, int retries,
        IReadOnlyList<string> alternatives, CancellationToken ct)
    {
        // A dropped connection is the network, not the request: retried on its
        // own, after a pause, before anyone is asked. The person who left a long
        // build running comes back to a finished turn, not to a question.
        if ((stall.Dropped || stall.Degenerate) && retries < DroppedAutoRetries)
        {
            var wait = stall.Degenerate ? TimeSpan.Zero : DroppedRetryDelay(retries);
            Noted?.Invoke($"{model}: {(stall.Degenerate ? "cut off a runaway reply" : "connection dropped")} — " +
                          $"retrying{(wait > TimeSpan.Zero ? $" in {wait.TotalSeconds:0}s" : "")} ({retries + 1}/{DroppedAutoRetries})");
            await Task.Delay(wait, ct);
            return 0;
        }

        string[] options = [RetryOption, .. alternatives];
        var recommended = retries < UnattendedStallRetries ? 0 : options.Length - 1;
        var ask = new ChoiceRequest(
            $"{model} stalled — {stall.Message}" + (retries > 0 ? $" (retried {retries}x)" : ""),
            options, recommended);

        var picked = Chooser is null ? options[recommended] : ask.Resolve(await Chooser(ask, ct));
        var index = Array.IndexOf(options, picked);
        if (index < 0) index = recommended;   // a free-text answer: take the recommendation

        Noted?.Invoke($"{model} stalled: {options[index]}");
        return index;
    }

    /// <summary>Requests and drafts under these lengths, with no tool material, are never escalated.</summary>
    internal const int ShortRequestChars = 60, ShortDraftChars = 200;

    /// <summary>The share of a draft's words found in the material above which a web turn's draft is a report of it.</summary>
    internal const double LookupShare = 0.6;

    /// <summary>The confidence a web turn's escalation must reach (others follow the engine's choice alone).</summary>
    internal const double LookupEscalationConfidence = 0.95;

    /// <summary>
    /// How much of the draft is the material's own words: distinct words (two
    /// letters for Hangul, three otherwise) found anywhere in it. A table of
    /// titles copied off a page scores high; an argument built on two search
    /// snippets does not.
    /// </summary>
    internal static double ShareFromMaterial(string draft, string material)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = new StringBuilder();
        foreach (var c in draft + " ")
        {
            if (char.IsLetterOrDigit(c)) { current.Append(c); continue; }
            if (current.Length > 0)
            {
                var word = current.ToString();
                var hangul = word.Any(ch => ch is >= '가' and <= '힣');
                if (word.Length >= (hangul ? 2 : 3) && !word.All(char.IsDigit)) words.Add(word);
                current.Clear();
            }
        }
        if (words.Count == 0) return 0;
        return (double)words.Count(w => material.Contains(w, StringComparison.OrdinalIgnoreCase)) / words.Count;
    }

    internal static bool IsShortExchange(string request, string material, string draft) =>
        material.Length == 0 && request.Trim().Length < ShortRequestChars && draft.Trim().Length < ShortDraftChars;

    private async Task<AgentRun> MaybeEscalateAsync(string request, int messagesBefore, AgentRun draft, CancellationToken ct, bool lookup = false)
    {
        var material = ToolResultsSince(messagesBefore);

        var judged = await _router.EscalateAsync(request, material, draft.Text, ct);

        // Escalation normally follows the engine's choice alone (see
        // SmartRouter.EscalateAsync). A short reply to a short remark, with
        // nothing gathered, has to clear the floor as well. Measured
        // (2026-09-30): "3번 수행 이제 필요없음" → a two-line acknowledgement
        // went to the strong model at 0.14 and cost 31 s to come back the same.
        // Not skipped outright: "17×23?" is short too, and arithmetic is
        // exactly what the second look is for.
        var shortExchange = IsShortExchange(request, material, draft.Text);
        if (judged.Escalate && shortExchange && judged.Decision.Confidence < _config.JevConfidenceFloor)
            judged = judged with { Escalate = false };

        // A web lookup is right or wrong by what was read, and the strong model
        // reads the same pages. Measured (2026-11-06), twice: eleven video titles
        // sorted into history/science, escalated at 0.94 and at 0.86, the same
        // table back 169 s and 206 s later. What a lookup lacks is more reading,
        // which the everyday model's tools do. So a web turn escalates only when
        // the engine is all but certain, and never when the draft is mostly the
        // pages' own words. (A share test alone missed the second run: its table
        // came with a paragraph of conclusions.)
        var webLookup = lookup && material.Length > 0
                        && (ShareFromMaterial(draft.Text, material) >= LookupShare || judged.Decision.Confidence < LookupEscalationConfidence);
        if (judged.Escalate && webLookup)
            judged = judged with { Escalate = false };

        var strong = _config.ReasoningModel;
        var verdict = judged.Escalate ? $"escalating to {strong}"
            : judged.Decision is { Ok: true, Choice: SmartRouter.EscalateOption } && webLookup ? "keeping the draft — a web lookup is answered from the pages read"
            : judged.Decision is { Ok: true, Choice: SmartRouter.EscalateOption } ? $"keeping the draft — a short exchange, and the engine was unsure"
            : judged.Decision.Ok ? "keeping the draft"
            : $"unavailable ({judged.Decision.Message}), keeping the draft";
        _log?.Decision("escalation", judged.Decision, verdict);
        Decided?.Invoke(new SmartNote("escalation", judged.Decision, verdict));

        if (!judged.Escalate) return draft;

        _state.CountEscalation();
        ActivityStarted?.Invoke($"reasoning with {strong}");
        var clock = Stopwatch.StartNew();

        string reasoning;
        for (var retries = 0; ; retries++)
        {
            try
            {
                reasoning = await ReasoningSubtask.RunAsync(_reasoning!, _config.Model, request, material, draft.Text, ct,
                    received => ActivityStarted?.Invoke($"reasoning with {strong} · {received:N0} chars so far"),
                    EarlierAnswers(messagesBefore));
                break;
            }
            catch (ChatProviderStalledException ex)
            {
                var again = await AskAfterStallAsync(strong, ex, retries, [KeepDraftOption], ct) == 0;
                var stalled = new AgentStep(0, ReasoningSubtask.Tag,
                    $"{strong} stalled: {ex.Message} — " + (again ? "retrying" : "keeping the draft"), false, clock.ElapsedMilliseconds);
                _log?.Step(stalled);
                StepCompleted?.Invoke(stalled);
                if (!again) return draft;

                ActivityStarted?.Invoke($"reasoning with {strong} · retry {retries + 1}");
                clock.Restart();
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
        }

        var step = new AgentStep(0, ReasoningSubtask.Tag, $"{strong} · {reasoning.Length} chars", true, clock.ElapsedMilliseconds);
        _log?.Step(step);
        StepCompleted?.Invoke(step);
        LogText(ReasoningSubtask.Tag, reasoning);

        // Back to the everyday model, with no tools: it has everything it needs.
        var finalRun = await _loop.RunAsync(ReasoningSubtask.FeedBack(strong, reasoning), ct,
            SmartRouter.FamiliesFor(Route.Answer), familiesAreFinal: true);

        // The final write-up is the last upgrade step, not a dependency either.
        // If it could not complete — a stall the loop's recovery retried past and
        // then gave up on, or a provider error — the draft still stands, now
        // informed by the reasoning that was logged above. The turn ends with an
        // answer instead of an error, and never hangs on this last call.
        if (!finalRun.Succeeded && draft.Succeeded)
        {
            var kept = new AgentStep(0, ReasoningSubtask.Tag,
                $"final write-up did not complete ({finalRun.Reason}) — keeping the reasoned draft", false, clock.ElapsedMilliseconds);
            _log?.Step(kept);
            StepCompleted?.Invoke(kept);
            return draft;
        }

        // The write-up is the turn's answer, but the turn's work is the draft's:
        // returned alone, memory.md recorded "did: (no tools)" for a turn that had
        // opened the page it reported on.
        return finalRun with { Steps = [.. draft.Steps, step, .. finalRun.Steps], Elapsed = draft.Elapsed + clock.Elapsed + finalRun.Elapsed };
    }

    /// <summary>
    /// Writes a hand-off's whole text to the session file, and nowhere else.
    ///
    /// Measured (2026-09-24, the tetris session): a 17,788-character answer
    /// from the reasoning model was logged as "qwen3.8-27b · 17788 chars" and
    /// kept only inside the everyday model's context. The turn ended with two
    /// paragraphs of summary, an empty folder, and the work recoverable from
    /// nowhere. The step stays a one-liner for the screen; the file keeps it all.
    /// </summary>
    private void LogText(string tag, string text)
    {
        if (text.Length == 0) return;
        _log?.Step(new AgentStep(0, tag + "-text", text, true));
    }

    // ------------------------------------------------------------- the gate

    /// <summary>
    /// Whether a command may run. The static risk check comes first and cannot
    /// be overruled; then the decision engine, when there is one, judges the
    /// grey area and a confident "safe" runs unasked; everything else — unsafe,
    /// unsure, no engine, no key — is put to a person.
    /// </summary>
    /// <summary>
    /// The header comments of every script inside the root that the command
    /// runs (a token ending in .js/.cjs/.mjs/.ts/.py/.ps1/.sh that is a file
    /// there), for the safety question: a script's first lines say what it
    /// does, which its path alone does not.
    /// </summary>
    internal static string ScriptNotes(string command, string root)
    {
        var notes = new StringBuilder();
        foreach (var raw in command.Split([' ', '\t', '"', '\''], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim(';', '&', '|', '(', ')');
            if (!ScriptExtension().IsMatch(token)) continue;
            string full;
            try { full = Path.GetFullPath(Path.IsPathRooted(token) ? token : Path.Combine(root, token)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (!full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) continue;

            string[] lines;
            try { lines = File.ReadLines(full).Take(20).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            var comments = lines.Select(l => l.Trim())
                .TakeWhile(l => l.Length == 0 || l.StartsWith("//") || l.StartsWith('#') || l.StartsWith("<#") || l.StartsWith("\"\"\"") || l.StartsWith('*') || l.StartsWith("/*"))
                .Where(l => l.Length > 0 && !l.StartsWith("#!"))
                .ToList();
            if (comments.Count == 0) continue;
            notes.Append("- ").Append(token).Append(":\n  ").AppendLine(Clip(string.Join("\n  ", comments), 1_200));
        }
        return notes.ToString().TrimEnd();
    }

    [GeneratedRegex(@"\.(c|m)?js$|\.ts$|\.py$|\.ps1$|\.sh$", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptExtension();

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".m4a", ".ogg", ".mp4", ".mov", ".webm", ".mkv", ".png", ".jpg", ".jpeg", ".webp",
    };

    private static readonly HashSet<string> OutputFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--out", "-o", "--output", "-outfile", "-destination", ">", ">>", "out-file", "set-content", "--save", "--dest",
    };

    /// <summary>
    /// Media files a command reads that are not on disk — read relative to the root
    /// or to a folder the command moves into (Push-Location, cd). A path after an
    /// output flag is written, not read, and so is the last of several media paths
    /// (ffmpeg -i in.mp3 out.wav, Copy-Item a b). Measured (2026-11-07): with the
    /// download still failing, the model asked to transcribe "out/C2_v2.mp3" — a
    /// name it made up from its own ranking.
    /// </summary>
    internal static List<string> MissingMediaInputs(string command, string root)
    {
        var tokens = command.Split([' ', '\t', '\n', '"', '\'', ';', '(', ')', ','], StringSplitOptions.RemoveEmptyEntries);
        var bases = new List<string> { root };
        for (var i = 0; i + 1 < tokens.Length; i++)
            if (tokens[i] is "cd" || tokens[i].Equals("Push-Location", StringComparison.OrdinalIgnoreCase)
                                  || tokens[i].Equals("Set-Location", StringComparison.OrdinalIgnoreCase) || tokens[i] is "pushd")
                try { bases.Add(Path.GetFullPath(Path.Combine(root, tokens[i + 1]))); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }

        var media = new List<(string Token, bool Written)>();
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i].Trim('&', '|');
            if (token.IndexOfAny(['*', '?', '$', '{', '<']) >= 0 || !MediaExtensions.Contains(Path.GetExtension(token))) continue;
            var written = i > 0 && (OutputFlags.Contains(tokens[i - 1]) || tokens[i - 1].EndsWith('>'));
            media.Add((token, written));
        }
        if (media.Count >= 2) media[^1] = (media[^1].Token, true);

        var missing = new List<string>();
        foreach (var (token, written) in media)
        {
            if (written) continue;
            var found = false;
            foreach (var b in bases)
            {
                try { found = File.Exists(Path.IsPathRooted(token) ? token : Path.Combine(b, token)); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { found = true; }
                if (found) break;
            }
            if (!found) missing.Add(token);
        }
        return missing;
    }

    private async Task<GateVerdict> GateAsync(string command, CancellationToken ct)
    {
        // A command over files that do not exist is not put to a person: it can only
        // fail, and asking spends their attention on a guess.
        if (MissingMediaInputs(command, _root) is { Count: > 0 } absent)
            return GateVerdict.Deny(
                $"it reads {string.Join(", ", absent)}, which is not on disk (nobody was asked). Find the real files " +
                "first with list_files / find_files, or run the step that produces them, then run it on those.");

        var risk = CommandRisk.Inspect(command);
        string reason;

        if (risk.Dangerous)
        {
            reason = risk.Reason;
        }
        else if (_smartAvailable)
        {
            var safety = await _router.SafetyAsync(command, _root, Shell, ct, ScriptNotes(command, _root));
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

    /// <summary>
    /// Whether the agent may act (click, type, press) on a site in the browser.
    /// Always a person, once per site per session: the browser may be their own,
    /// logged in, where a click can post, buy or delete — the decision engine
    /// cannot see the page to judge that. Reading pages needs no approval.
    /// </summary>
    private Task<GateVerdict> BrowserGateAsync(string what, string site, CancellationToken ct) =>
        what.Contains(" AGAIN ", StringComparison.Ordinal)
            ? AskAsync($"browser: {what} on {site} (this click only)", "repeating an action can repeat what it started", ct)
            : AskAsync($"browser: {what} on {site} (and further actions on {site} this session)",
                       "acting in a browser can change things on the site", ct);

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

    // ---------------------------------------------------------- processes

    /// <summary>
    /// Runs this session's commands under <paramref name="processes"/> — the
    /// loop actor's <c>procs</c> child — instead of a supervisor of its own.
    /// Called once, before the first turn.
    /// </summary>
    public void UseProcesses(ProcessSupervisor processes) => _shell.UseProcesses(processes);

    /// <summary>Every process this session started and still remembers, newest last. Empty when none was started.</summary>
    public async Task<IReadOnlyList<ProcessSnapshot>> ProcessesAsync(CancellationToken ct = default) =>
        _shell.HasProcesses ? await _shell.Processes.ListAsync(ct) : [];

    /// <summary>
    /// A choice about a running command (how to run a server, what to do with
    /// one that overran), put to the person through <see cref="Chooser"/>.
    /// Returns the index picked; no chooser, or a free-text answer, takes the
    /// recommendation.
    /// </summary>
    private async Task<int> ChooseIndexAsync(ChoiceRequest ask, CancellationToken ct)
    {
        var picked = Chooser is null ? ask.Options[ask.Recommended] : ask.Resolve(await Chooser(ask, ct));
        var index = ask.Options.ToList().IndexOf(picked);
        if (index < 0) index = ask.Recommended;

        var choice = ask.Options[index];
        var cut = choice.IndexOf(" — ", StringComparison.Ordinal);
        _log?.Step(new AgentStep(0, "process-choice", $"{ask.Question} → {choice}", true));
        Noted?.Invoke($"process: {(cut > 0 ? choice[..cut] : choice)}");
        return index;
    }

    /// <summary>A background process came up or ended — the person hears it whenever it happens, mid-turn or not.</summary>
    private void OnProcessChanged(ProcessSnapshot snap)
    {
        var what = snap.State switch
        {
            ProcessState.Ready => "is up" + (snap.Url is null ? "" : $" at {snap.Url}"),
            ProcessState.Killed => "was stopped",
            _ => $"ended (exit {snap.ExitCode?.ToString() ?? "?"})" + (snap.LastLine.Length > 0 ? $" · last: {snap.LastLine}" : ""),
        };
        var text = $"process {snap.Id} {what} — {snap.Command}";
        _log?.Step(new AgentStep(0, "process", text, snap.State != ProcessState.Exited || snap.ExitCode == 0));
        Noted?.Invoke(text);
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

        // The last answer, with its numbered list: a follow-up like "3번 해줘"
        // or "go with 2" means nothing without it, and the router used to see
        // only the questions.
        if (LastAnswer() is { Length: > 0 } answer)
        {
            var head = answer.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            var answerLines = new List<string> { "- your last answer: " + Clip(head, 100) };
            answerLines.AddRange(NumberedItems(answer).Take(9).Select(item => $"    {item.Number}. {Clip(item.Text, 100)}"));
            digest = string.Join('\n', answerLines) + (digest.Length > 0 ? "\n" + digest : "");
        }

        if (Title is { } title) digest = $"- the session's task: {title}\n" + digest;
        return digest;
    }

    // ------------------------------------------------------------ references

    /// <summary>The text of the model's last answer in this conversation, or null.</summary>
    internal string? LastAnswer()
    {
        for (var i = _loop.Messages.Count - 1; i >= 0; i--)
        {
            var message = _loop.Messages[i];
            if (message.Role != "assistant") continue;
            if (!ToolCall.TryParse(message.Content, out var call, out _)) return message.Content.Trim();   // prose taken as the answer
            if (call.IsFinal) return call.Arg("text");
        }
        return null;
    }

    /// <summary>How much of each earlier answer the strong model is shown, and how many answers.</summary>
    internal const int EarlierAnswerChars = 2_500, EarlierAnswerCount = 2;

    /// <summary>
    /// The conversation's answers before <paramref name="beforeIndex"/>, newest
    /// first, as material for the strong model: "the analysis this request
    /// builds on". Measured (2026-11-06, music-harness): one turn listed a
    /// channel's videos by topic — 「종말의 그날 1」 and 「2」 among them — and
    /// the next asked to make a music video from those two. The everyday model
    /// had that answer in its conversation; the design pass got the Digest — the
    /// answer's first line and its numbered items, and a table has none — and
    /// designed "join two local video files (recommended)" from nothing.
    /// </summary>
    internal string EarlierAnswers(int beforeIndex)
    {
        var answers = new List<string>();
        for (var i = Math.Min(beforeIndex, _loop.Messages.Count) - 1; i >= 0 && answers.Count < EarlierAnswerCount; i--)
        {
            var message = _loop.Messages[i];
            if (message.Role != "assistant") continue;
            var text = !ToolCall.TryParse(message.Content, out var call, out _) ? message.Content.Trim()
                : call.IsFinal ? call.Arg("text") : null;
            if (text is { Length: > 0 }) answers.Add(Clip(text.Trim(), EarlierAnswerChars));
        }
        if (answers.Count == 0) return "";

        var sb = new StringBuilder("Earlier answers in this conversation, newest first — the request may build on them " +
                                   "(\"those two\", \"from that analysis\"); take names, titles and facts from here rather than assuming:");
        for (var n = 0; n < answers.Count; n++)
            sb.Append("\n--- answer ").Append(n == 0 ? "just before this request" : $"{n + 1} turns back").Append(" ---\n").Append(answers[n]);
        return sb.ToString();
    }

    /// <summary>The lines of a numbered list ("1. …", "2) …"), in order.</summary>
    internal static IReadOnlyList<(int Number, string Text)> NumberedItems(string text) =>
        NumberedLine().Matches(text)
            .Select(m => (int.Parse(m.Groups[1].Value), m.Groups[2].Value.Trim().Trim('*').Trim()))
            .ToList();

    /// <summary>
    /// When the request points at an item of the last answer by number — "3번",
    /// "#3", "option 3", "step 3" — the item itself, as a note for the router
    /// and the model. Null when there is no such reference or no such item.
    /// </summary>
    internal string? ResolveReference(string request)
    {
        var match = NumberReference().Match(request);
        if (!match.Success) return null;
        var number = int.Parse(match.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value);

        if (LastAnswer() is not { } answer) return null;
        var item = NumberedItems(answer).FirstOrDefault(i => i.Number == number);
        if (item.Text is null) return null;

        return $"[reference] \"{match.Value.Trim()}\" is item {number} of your previous answer: \"{Clip(item.Text, 300)}\". " +
               "Decide from the user's words whether they want it done now. If that is not clear — above all when it " +
               "would delete, overwrite or undo something — do not guess either way: ask one short question in \"final\".";
    }

    [GeneratedRegex(@"^\s*(\d{1,2})[.)]\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex NumberedLine();

    [GeneratedRegex(@"(?<![\d.])(\d{1,2})\s*번|#\s?(\d{1,2})\b|\b(?:number|no\.|option|item|step|choice)\s*(\d{1,2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex NumberReference();

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
