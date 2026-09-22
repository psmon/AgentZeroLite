using System.Diagnostics;
using AgentOne.Llm;
using AgentOne.Llm.Decision;
using AgentOne.Services;
using AgentOne.Tools;

namespace AgentOne.Agent;

/// <summary>What smart mode decided at one of its two checkpoints, for the renderers.</summary>
/// <param name="Kind">"route" or "escalation".</param>
/// <param name="Verdict">One line for a person: "→ web", "keeping the draft", "escalating to …".</param>
public sealed record SmartNote(string Kind, Decision Decision, string Verdict);

/// <summary>
/// One conversation, from the first prompt to /exit — everything a chat does
/// that is not drawing. The plain REPL, the full-screen window and `run` are
/// all thin renderers over this, so a turn routes, runs, judges its draft and
/// escalates identically whichever one is in front of it. Two copies of that
/// logic would drift within a week.
/// </summary>
public sealed class ChatSession : IDisposable
{
    private readonly IChatProvider _provider;
    private readonly IChatProvider? _reasoning;
    private readonly CompositeToolbelt _toolbelt;
    private readonly AgentLoop _loop;
    private readonly IDecisionEngine _engine;
    private readonly bool _smartAvailable;
    private readonly SmartRouter _router;
    private readonly SessionStore? _log;
    private readonly SessionState _state;
    private readonly AgentConfig _config;

    /// <summary>What the agent is doing right now, for a status line.</summary>
    public event Action<string>? ActivityStarted;

    /// <summary>A tool step finished — or the reasoning hand-off, under the tool name "reasoning".</summary>
    public event Action<AgentStep>? StepCompleted;

    /// <summary>A fragment of the answer, as the model writes it.</summary>
    public event Action<string>? AnswerDelta;

    /// <summary>Smart mode decided something: the route, or whether to escalate.</summary>
    public event Action<SmartNote>? Decided;

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
        _provider = provider;
        _reasoning = reasoning;
        _engine = engine;
        _smartAvailable = smartAvailable;

        _toolbelt = new CompositeToolbelt(
            (ToolCatalog.FilesFamily, new LocalFileToolbelt(root)),
            (ToolCatalog.WebFamily, new WebToolbelt(TimeSpan.FromSeconds(config.WebTimeoutSeconds))));

        _loop = new AgentLoop(_provider, _toolbelt, config.MaxSteps) { Streaming = streaming };
        _loop.Reset();
        _loop.ActivityStarted += what => ActivityStarted?.Invoke(what);
        _loop.StepCompleted += step => { _log?.Step(step); StepCompleted?.Invoke(step); };
        _loop.AnswerDelta += fragment => AnswerDelta?.Invoke(fragment);

        _router = new SmartRouter(_engine, config.JevConfidenceFloor, config.Model,
            reasoning is null ? null : config.ReasoningModel);
        _router.ActivityStarted += what => ActivityStarted?.Invoke(what);

        _log = config.SaveSessions ? SessionStore.Create(logKind) : null;

        // Smart starts where the config left it, but only with a key to make it
        // work. Starting "on" with no key would fail on the first turn.
        _state = new SessionState(config.SmartMode && _smartAvailable);
    }

    public string ProviderName => _provider.Name;
    public string Model => _config.Model;
    public string? ReasoningModel => _reasoning is null ? null : _config.ReasoningModel;
    public string ToolScope => _toolbelt.Scope;
    public string? LogPath => _log?.Path;
    public bool SmartAvailable => _smartAvailable;
    public double ConfidenceFloor => _config.JevConfidenceFloor;

    public bool Smart => _state.Read().Smart;

    /// <summary>Flips the mode. False, with a reason, when it cannot.</summary>
    public bool TryToggleSmart(out string message)
    {
        if (!_smartAvailable)
        {
            message = "no TypeSafe key — set one on the Smart step of `agent-one tui`";
            return false;
        }

        message = _state.ToggleSmart()
            ? "smart mode on — route first, then judge the draft" +
              (ReasoningModel is { } strong ? $", escalating to {strong} when it needs more" : " (no reasoning model: never escalates)")
            : "basic mode — straight to the tool loop";
        return true;
    }

    public void Reset()
    {
        _loop.Reset();
        _state.Reset();
    }

    /// <summary>
    /// Handles one line of input as a turn. Returns null only for an empty line.
    ///
    /// In smart mode, and for anything longer than a greeting, the turn is:
    /// route (which tool family, if any) → the loop, restricted to that family
    /// → the engine judges the draft against the gathered material → if it
    /// needs more, the stronger model reasons over the same material and the
    /// everyday model writes the final answer from that.
    /// </summary>
    public async Task<AgentRun?> SubmitAsync(string line, CancellationToken ct)
    {
        line = line.Trim();
        if (line.Length == 0) return null;

        var snapshot = _state.Read();
        _log?.Prompt(line, snapshot.Smart);
        _state.CountTurn();

        var smart = snapshot.Smart && SmartRouter.Applies(line);
        var prompt = line;
        IReadOnlySet<string>? families = null;

        if (smart)
        {
            var route = await _router.RouteAsync(line, Digest(), ct);
            var verdict = route.Steers ? $"→ {route.Route!.Value.ToString().ToLowerInvariant()}"
                : route.Decision.Ok ? $"unsure ({route.Decision.Choice}), all tools stay available"
                : $"unavailable ({route.Decision.Message})";
            _log?.Decision("route", route.Decision, verdict);
            Decided?.Invoke(new SmartNote("route", route.Decision, verdict));

            if (route.Steers)
            {
                prompt = route.Guidance(line);
                families = route.Families;
            }
        }

        var before = _loop.Messages.Count;
        var run = await _loop.RunAsync(prompt, ct, families);

        if (smart && run.Succeeded && _reasoning is not null)
            run = await MaybeEscalateAsync(line, before, run, ct);

        _log?.Result(run);
        return run;
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
    /// and reasoning hand-off so far by what it was, and each earlier question.
    /// Handed to the router so a follow-up is not sent to fetch what is already
    /// in context.
    /// </summary>
    internal string Digest()
    {
        var lines = new List<string>();

        foreach (var message in _loop.Messages)
        {
            if (message.Role != "user") continue;

            if (message.Content.StartsWith("[tool:", StringComparison.Ordinal)
                || message.Content.StartsWith($"[{ReasoningSubtask.Tag}:", StringComparison.Ordinal))
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
        (_engine as IDisposable)?.Dispose();
        _toolbelt.Dispose();
        (_reasoning as IDisposable)?.Dispose();
        (_provider as IDisposable)?.Dispose();
    }
}
