using System.Diagnostics;
using System.Text;
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

/// <summary>Everything the status view shows, taken at one moment.</summary>
public sealed record SessionStats(
    bool Smart,
    string? LogPath,
    string Root,
    string Model,
    string? ReasoningModel,
    string Shell,
    int ContextMessages,
    int ContextChars,
    int EstimatedTokens,
    SessionCounters Counters,
    IReadOnlyList<string> ReadGrants)
{
    /// <summary>The status block, one fact per line.</summary>
    public IReadOnlyList<string> Describe()
    {
        var c = Counters;
        var lines = new List<string>
        {
            $"session   {(LogPath ?? "(not saved)")}",
            $"mode      {(Smart ? "smart" : "basic")} · turns {c.Turns}",
            $"context   {ContextMessages} messages · {ContextChars:N0} chars · ~{EstimatedTokens:N0} tokens (estimate)",
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
        _loop.Reset();
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

        _log = config.SaveSessions ? SessionStore.Create(logKind) : null;
    }

    public string Root => _root;
    public string ProviderName => _provider.Name;
    public string Model => _config.Model;
    public string? ReasoningModel => _reasoning is null ? null : _config.ReasoningModel;
    public string ToolScope => _toolbelt.Scope;
    public string Shell => ShellToolbelt.ShellName;
    public string? LogPath => _log?.Path;
    public bool SmartAvailable => _smartAvailable;
    public double ConfidenceFloor => _config.JevConfidenceFloor;

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

    /// <summary>Forgets the conversation, the counters and the read grants; keeps the mode and the log.</summary>
    public void Reset()
    {
        _loop.Reset();
        _state.Reset();
        _files.ClearGrants();
    }

    /// <summary>A fresh session: everything Reset forgets, plus a new log file.</summary>
    public void NewSession()
    {
        Reset();
        if (_config.SaveSessions) _log = SessionStore.Create(_logKind);
    }

    /// <summary>The status view's numbers, taken now.</summary>
    public SessionStats Stats()
    {
        var snapshot = _state.Read();
        var chars = 0;
        foreach (var message in _loop.Messages) chars += message.Content.Length;

        return new SessionStats(
            snapshot.Smart, LogPath, _root, Model, ReasoningModel, Shell,
            _loop.Messages.Count, chars, Tokens.Estimate(_loop.Messages),
            snapshot.Counters, snapshot.ReadGrants);
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
    /// readable for the session before any of it starts.
    /// </summary>
    public async Task<AgentRun?> SubmitAsync(string line, CancellationToken ct)
    {
        line = line.Trim();
        if (line.Length == 0) return null;

        GrantNamedFolders(line);

        var snapshot = _state.Read();
        _log?.Prompt(line, snapshot.Smart);
        _state.CountTurn();

        var smart = snapshot.Smart && SmartRouter.Applies(line);
        var prompt = line;
        IReadOnlySet<string>? families = null;
        var designed = false;

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

            // Work in the workspace, or work whose shape is unclear, may be big
            // enough to design first. Web lookups and plain answers never are.
            if (_reasoning is not null && route.Route is not (Route.Web or Route.Answer))
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

        _log?.Result(run);
        return run;
    }

    private static string RouteName(Route route) => route switch
    {
        Route.Web => "web",
        Route.Files => "workspace",
        _ => "answer"
    };

    /// <summary>The scope question, and the design pass when it says so. Null when the everyday model just goes ahead.</summary>
    private async Task<string?> MaybeDesignAsync(string request, CancellationToken ct)
    {
        var scope = await _router.ScopeAsync(request, Digest(), ct);
        var strong = _config.ReasoningModel;
        var verdict = scope.NeedsDesign ? $"large — {strong} designs first"
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

        return ReasoningSubtask.DesignFeedBack(strong, design);
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
        return string.Join('\n', lines.Take(8));
    }

    public void Dispose()
    {
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
