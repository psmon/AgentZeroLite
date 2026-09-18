using Akka.Actor;
using Akka.Event;
using Agent.Common.Actors;

namespace Agent.Common.Wearable.Actors;

/// <summary>
/// The watch's agent as an actor subtree (M0032). Mirrors the main app's split — there
/// <c>AgentBotActor</c> is the UI gateway and <see cref="AgentLoopActor"/> is the agent;
/// here <c>ChatActor</c> is the device gateway and this actor supervises one
/// <see cref="AgentLoopActor"/> per conversation plus the two tool actors every loop shares:
///
/// <code>
/// /user/agent                 WearableAgentActor   — sessions, routing, progress
///     /files                  FileToolActor        — allow-listed folders + open_file
///     /web                    WebToolActor         — GUI browser bridge / headless fetch
///     /session-&lt;key&gt;         AgentLoopActor       — one IAgentLoop, Idle→…→Done FSM (reused as-is)
/// </code>
///
/// <para>Why the extra layer: <see cref="AgentLoopActor"/> ignores a <c>StartAgentLoop</c>
/// while it is running — right for a chat window with one send button, wrong for a watch
/// whose rule is "newest question wins". This actor cancels the running loop and <i>queues</i>
/// the new question until the cancelled run reports back, so the watch is never left
/// waiting on a request that was silently dropped.</para>
///
/// <para>Everything here is WPF/WinRT-free so <c>ZeroCommon.Tests</c> can drive it with
/// TestKit; the wearable host only composes it.</para>
/// </summary>
public sealed class WearableAgentActor : ReceiveActor
{
    // ── Messages ──────────────────────────────────────────────────────────────

    /// <summary>One question from the device. Replies go to the sender as <see cref="Progress"/>* then one <see cref="Answer"/>.</summary>
    public sealed record Ask(string Session, int RequestId, string Prompt, string? ReplyLanguage);

    /// <summary>A phase change of the run answering <see cref="Ask"/> (Thinking / Acting / Generating…).</summary>
    public sealed record Progress(string Session, int RequestId, AgentLoopPhase Phase, string Text, int Round);

    /// <summary>The run's outcome. <see cref="Success"/> false carries the reason; a cancelled run reports here too.</summary>
    public sealed record Answer(string Session, int RequestId, bool Success, string Text, int Turns, long ElapsedMs, string? FailureReason);

    /// <summary>Forget a conversation's history (the device's "new conversation" button).</summary>
    public sealed record ResetSession(string Session);

    /// <summary>Stop whatever is running for a session and drop anything queued behind it.</summary>
    public sealed record CancelSession(string Session);

    /// <summary>Stop and forget every session whose name starts with <paramref name="Prefix"/> (a device went away).</summary>
    public sealed record ForgetSessions(string Prefix);

    // ── State ─────────────────────────────────────────────────────────────────

    private sealed class Session
    {
        public required IActorRef Loop;
        public (Ask Ask, IActorRef ReplyTo)? Current;
        public (Ask Ask, IActorRef ReplyTo)? Queued;
    }

    private readonly ILoggingAdapter _log = Context.GetLogger();
    private readonly Func<IActorRef, IActorRef, AgentLoopBindings> _bindingsFactory;
    private readonly Props _filesProps;
    private readonly Props _webProps;
    private readonly IAsyncDisposable? _owned;

    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<IActorRef, string> _sessionByLoop = new();

    private AgentLoopBindings? _bindings;

    public IActorRef? Files { get; private set; }
    public IActorRef? Web { get; private set; }

    /// <param name="bindingsFactory">
    /// Builds the loop bindings once the tool actors exist — the toolbelt inside them is
    /// an adapter over <c>files</c> and <c>web</c>.
    /// </param>
    /// <param name="owned">
    /// Something the bindings close over that outlives the loops — the loaded GGUF for
    /// the on-device brain. Disposed when this actor stops.
    /// </param>
    public WearableAgentActor(Func<IActorRef, IActorRef, AgentLoopBindings> bindingsFactory,
        Props filesProps, Props webProps, IAsyncDisposable? owned = null)
    {
        _bindingsFactory = bindingsFactory;
        _filesProps = filesProps;
        _webProps = webProps;
        _owned = owned;

        Receive<Ask>(OnAsk);
        Receive<AgentLoopProgress>(OnProgress);
        Receive<AgentLoopResult>(OnResult);
        Receive<ResetSession>(OnReset);
        Receive<CancelSession>(OnCancel);
        Receive<ForgetSessions>(OnForget);
        Receive<Ping>(_ => Sender.Tell(new Pong("WearableAgent", Self.Path.ToString(),
            $"sessions={_sessions.Count} running={_sessions.Values.Count(s => s.Current is not null)}")));
    }

    protected override void PreStart()
    {
        Files = Context.ActorOf(_filesProps, "files");
        Web = Context.ActorOf(_webProps, "web");
        _bindings = _bindingsFactory(Files, Web);
        base.PreStart();
    }

    protected override void PostStop()
    {
        if (_owned is { } owned)
        {
            // Off the actor thread: unloading a model can block briefly on the GPU driver.
            Task.Run(async () =>
            {
                try { await owned.DisposeAsync(); }
                catch { /* process is going down anyway */ }
            });
        }
        base.PostStop();
    }

    // ── Handlers ──────────────────────────────────────────────────────────────

    private void OnAsk(Ask ask)
    {
        var session = GetOrCreate(ask.Session);
        var replyTo = Sender;

        if (session.Current is null)
        {
            Start(session, ask, replyTo);
            return;
        }

        // Newest question wins: cancel the run in flight, hold this one until the loop
        // has actually returned to Idle (AgentLoopActor drops a Start while Running).
        _log.Info("session {0}: #{1} supersedes #{2}", ask.Session, ask.RequestId, session.Current.Value.Ask.RequestId);
        session.Loop.Tell(new CancelAgentLoop());
        if (session.Queued is { } stale)
            stale.ReplyTo.Tell(Superseded(stale.Ask, ask.RequestId), Self);
        session.Queued = (ask, replyTo);
    }

    private void Start(Session session, Ask ask, IActorRef replyTo)
    {
        session.Current = (ask, replyTo);
        session.Loop.Tell(new StartAgentLoop(WearablePromptFrame.Frame(ask.Prompt, ask.ReplyLanguage)), Self);
    }

    private void OnProgress(AgentLoopProgress progress)
    {
        if (!TryOwner(Sender, out var name, out var session) || session.Current is not { } current) return;
        current.ReplyTo.Tell(new Progress(name, current.Ask.RequestId, progress.Phase, progress.Text, progress.Round), Self);
    }

    private void OnResult(AgentLoopResult result)
    {
        if (!TryOwner(Sender, out var name, out var session) || session.Current is not { } current) return;
        session.Current = null;
        current.ReplyTo.Tell(new Answer(name, current.Ask.RequestId, result.Success, result.FinalMessage,
            result.TurnCount, result.ElapsedMs, result.FailureReason), Self);

        if (session.Queued is { } next)
        {
            session.Queued = null;
            Start(session, next.Ask, next.ReplyTo);
        }
    }

    private void OnReset(ResetSession reset)
    {
        if (!_sessions.TryGetValue(reset.Session, out var session)) return;
        session.Queued = null;
        session.Loop.Tell(new ResetAgentLoopMemory(), Self);
    }

    private void OnCancel(CancelSession cancel)
    {
        if (!_sessions.TryGetValue(cancel.Session, out var session)) return;
        if (session.Queued is { } queued)
            queued.ReplyTo.Tell(Superseded(queued.Ask, 0), Self);
        session.Queued = null;
        if (session.Current is not null) session.Loop.Tell(new CancelAgentLoop(), Self);
    }

    private void OnForget(ForgetSessions forget)
    {
        var names = _sessions.Keys.Where(k => k.StartsWith(forget.Prefix, StringComparison.Ordinal)).ToList();
        foreach (var name in names)
        {
            var session = _sessions[name];
            _sessions.Remove(name);
            _sessionByLoop.Remove(session.Loop);
            Context.Stop(session.Loop);
        }
        if (names.Count > 0) _log.Info("forgot {0} session(s) for {1}", names.Count, forget.Prefix);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Session GetOrCreate(string name)
    {
        if (_sessions.TryGetValue(name, out var existing)) return existing;
        var bindings = _bindings ?? throw new InvalidOperationException("bindings are built in PreStart");
        var loop = Context.ActorOf(Props.Create(() => new AgentLoopActor(bindings)), "session-" + ChildName(name));
        var session = new Session { Loop = loop };
        _sessions[name] = session;
        _sessionByLoop[loop] = name;
        _log.Info("session {0} opened", name);
        return session;
    }

    private bool TryOwner(IActorRef loop, out string name, out Session session)
    {
        session = null!;
        return _sessionByLoop.TryGetValue(loop, out name!) && _sessions.TryGetValue(name, out session!);
    }

    private static Answer Superseded(Ask ask, int byRequestId)
        => new(ask.Session, ask.RequestId, false, "", 0, 0,
            byRequestId > 0 ? $"superseded by request #{byRequestId}" : "cancelled");

    /// <summary>Akka child names are a restricted alphabet; a session key is not guaranteed to fit it.</summary>
    private static string ChildName(string session)
    {
        var chars = session.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        var s = new string(chars).Trim('-');
        return s.Length == 0 ? "default" : s;
    }
}
