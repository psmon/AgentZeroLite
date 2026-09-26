using System.Threading.Channels;
using Akka.Actor;
using AgentOne.Agent;
using AgentOne.Llm;
using AgentOne.Services;

namespace AgentOne.Actors;

/// <summary>
/// The renderers' handle on the conversation, over the actors: one
/// <see cref="ActorSystem"/>, one <see cref="AgentBotActor"/> at <c>/user/bot</c>,
/// its <see cref="AgentLoopActor"/> at <c>/user/bot/loop</c>. It presents the
/// same surface as <see cref="ChatSession"/> — events, the two delegates, a
/// turn, the session commands — so a renderer does not know which it holds.
///
/// A turn is a Tell of <see cref="StartAgentLoop"/> and a wait for the one
/// <see cref="AgentLoopResult"/>; everything in between arrives through the
/// bot's callbacks and is re-raised as events. Session commands are Asks.
///
/// The bot's callbacks only enqueue: one pump task raises the events, in
/// order, on its own thread. That is AgentZero's rule — the UI registers
/// delegates that marshal, the actor never runs UI code — learned here the
/// hard way: raised on the bot's thread, the window's first repaint
/// deadlocked against Termina's own loop and the turn never came back.
/// </summary>
public sealed class AgentGateway : IAgentSession
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private readonly ActorSystem _system;
    private readonly IActorRef _bot;
    private readonly CancellationTokenSource _closing = new();
    private AgentSessionInfo _info;
    private TaskCompletionSource<AgentLoopResult>? _turn;
    private bool _disposed;
    private readonly Channel<Action> _events = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _pump;

    public event Action<string>? ActivityStarted;
    public event Action<AgentStep>? StepCompleted;
    public event Action<string>? AnswerDelta;
    public event Action<SmartNote>? Decided;
    public event Action<string>? Noted;
    public event Action<string>? TitleChanged;
    public event Action<IReadOnlyList<string>>? DesignMade;
    public event Action<IReadOnlyList<Distilled>>? Learned;

    public Func<ApprovalRequest, CancellationToken, Task<bool>>? Approver { get; set; }
    public Func<ChoiceRequest, CancellationToken, Task<string>>? Chooser { get; set; }

    private AgentGateway(ActorSystem system, IActorRef bot, AgentSessionInfo info)
    {
        _system = system;
        _bot = bot;
        _info = info;
        Workspace = new WorkspaceStore(info.Root);
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Raises the queued events one at a time; a handler that throws loses only its own event.</summary>
    private async Task PumpAsync()
    {
        await foreach (var raise in _events.Reader.ReadAllAsync())
        {
            try { raise(); }
            catch (Exception ex) { Console.Error.WriteLine("[agent-one] an event handler threw: " + ex.Message); }
        }
    }

    private void Enqueue(Action raise) => _events.Writer.TryWrite(raise);

    /// <summary>The CLI's entry: a real session from the config. Throws ChatProviderException the way the session's constructor would.</summary>
    public static AgentGateway Start(AgentConfig config, string root, bool streaming, string logKind = "chat") =>
        Start(new AgentLoopBindings(() => new ChatSession(config, root, streaming, logKind)));

    /// <summary>Test seam, and the general form: any session the bindings can build.</summary>
    public static AgentGateway Start(AgentLoopBindings bindings)
    {
        var system = AgentActorSystem.Create();
        try
        {
            var bot = system.ActorOf(Props.Create(() => new AgentBotActor(bindings)), "bot");

            // Building the session happens inside the loop actor; a provider
            // that cannot be built comes back as a message, not a dead actor.
            var reply = bot.Ask<object>(QueryAgentInfo.Instance, CommandTimeout).GetAwaiter().GetResult();
            if (reply is AgentSessionFailed failed) throw new ChatProviderException(failed.Message);
            var info = (AgentSessionInfo)reply;

            var gateway = new AgentGateway(system, bot, info);
            bot.Tell(new SetAgentLoopCallbacks(
                p => gateway.Enqueue(() => gateway.OnProgress(p)),
                r => gateway.Enqueue(() => gateway.OnResult(r)),
                n => gateway.Enqueue(() => gateway.OnNotice(n)),
                q => gateway.Enqueue(() => gateway.OnPause(q))));
            return gateway;
        }
        catch
        {
            system.Terminate().GetAwaiter().GetResult();
            system.Dispose();
            throw;
        }
    }

    // ---------------------------------------------------------- the surface

    public WorkspaceStore Workspace { get; }
    public string Root => _info.Root;
    public string ProviderName => _info.ProviderName;
    public string Model => _info.Model;
    public string? ReasoningModel => _info.ReasoningModel;
    public string ToolScope => _info.ToolScope;
    public string Shell => _info.Shell;
    public string? LogPath => _info.LogPath;
    public bool SmartAvailable => _info.SmartAvailable;
    public bool Smart => _info.Smart;
    public string? Title => _info.Title;

    /// <summary>The actor the pipe server or a test can address directly.</summary>
    public IActorRef Bot => _bot;

    public async Task<AgentRun?> SubmitAsync(string line, CancellationToken ct)
    {
        if (line.Trim().Length == 0) return null;

        var turn = new TaskCompletionSource<AgentLoopResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _turn = turn;

        // The person's cancel becomes the loop's: the turn still ends with its
        // one result, marked cancelled, and that is what surfaces here.
        using var cancel = ct.Register(() => _bot.Tell(CancelAgentLoop.Instance));

        var accepted = await _bot.Ask<object>(new StartAgentLoop(line), CommandTimeout);
        if (accepted is TurnRefused refused) throw new InvalidOperationException(refused.Reason);

        var result = await turn.Task;
        _turn = null;

        // A run that ended — cancelled included — comes back as the run, the
        // way the session returns it; only a turn that never produced one throws.
        if (result.Run is not null) return result.Run;
        if (result.FailureReason == AgentLoopResult.Cancelled) throw new OperationCanceledException(ct);
        if (result.FailureReason is not null) throw new InvalidOperationException(result.FailureReason);
        return null;
    }

    public SessionStats Stats() => Command<SessionStats>(QueryAgentStats.Instance);

    public bool Paused { get; private set; }

    public void Pause() => Paused = Command<AgentLoopPaused>(PauseAgentLoop.Instance).Paused;

    public async Task<PauseOutcome> ResumeAsync(string line, CancellationToken ct)
    {
        var reply = await _bot.Ask<object>(new ResumeAgentLoop(line), CommandTimeout);
        if (reply is AgentSessionFailed failed) throw new InvalidOperationException(failed.Message);
        Paused = false;
        return ((AgentLoopResumed)reply).Outcome;
    }

    public bool TryToggleSmart(out string message)
    {
        var reply = Command<SmartModeToggled>(ToggleSmartMode.Instance);
        _info = _info with { Smart = reply.Smart };
        message = reply.Message;
        return reply.Changed;
    }

    public void Reset()
    {
        var reply = Command<AgentSessionReset>(ResetAgentLoopMemory.Instance);
        _info = _info with { LogPath = reply.LogPath, Title = null };
    }

    public void NewSession()
    {
        var reply = Command<AgentSessionReset>(NewAgentSession.Instance);
        _info = _info with { LogPath = reply.LogPath, Title = null };
    }

    public IReadOnlyList<SessionSummary> ListSessions() => Command<AgentSessionList>(QueryAgentSessions.Instance).Sessions;

    public IReadOnlyList<SessionEntry> Resume(string path)
    {
        var reply = Command<AgentSessionResumed>(new ResumeAgentSession(path));
        _info = _info with { LogPath = reply.LogPath, Title = reply.Title };
        return reply.Entries;
    }

    /// <summary>One round trip to the loop, with the loop's failure surfaced as an exception.</summary>
    private T Command<T>(object command)
    {
        var reply = _bot.Ask<object>(command, CommandTimeout).GetAwaiter().GetResult();
        if (reply is AgentSessionFailed failed) throw new InvalidOperationException(failed.Message);
        return (T)reply;
    }

    // -------------------------------------------------------- the callbacks

    private void OnProgress(AgentLoopProgress progress)
    {
        switch (progress.Phase)
        {
            case AgentLoopPhase.Thinking: ActivityStarted?.Invoke(progress.Text); break;
            case AgentLoopPhase.Acting when progress.Step is { } step: StepCompleted?.Invoke(step); break;
            case AgentLoopPhase.Generating: AnswerDelta?.Invoke(progress.Text); break;
            // Done and Error are for actor-level observers; the result carries them here.
        }
    }

    /// <summary>Events are raised one at a time on the pump thread, so one field is enough to say which kind this is.</summary>
    private volatile bool _raisingAfterTurn;

    public bool RaisingAfterTurn => _raisingAfterTurn;

    private void OnNotice(AgentLoopNotice notice)
    {
        _raisingAfterTurn = notice is DecisionNotice { AfterTurn: true } or NoteNotice { AfterTurn: true } or LearnedNotice { AfterTurn: true };
        try { Raise(notice); }
        finally { _raisingAfterTurn = false; }
    }

    private void Raise(AgentLoopNotice notice)
    {
        switch (notice)
        {
            case DecisionNotice d: Decided?.Invoke(d.Note); break;
            case NoteNotice n: Noted?.Invoke(n.Text); break;
            case TitleNotice t:
                _info = _info with { Title = t.Title };
                TitleChanged?.Invoke(t.Title);
                break;
            case DesignNotice d: DesignMade?.Invoke(d.Lines); break;
            case LearnedNotice l: Learned?.Invoke(l.Items); break;
        }
    }

    private void OnResult(AgentLoopResult result)
    {
        Paused = false;
        _turn?.TrySetResult(result);
    }

    /// <summary>The person is asked off the bot's thread; the answer goes back as ResolvePause.</summary>
    private void OnPause(PersonNeeded pause) => _ = AnswerAsync(pause);

    private async Task AnswerAsync(PersonNeeded pause)
    {
        string answer;
        try
        {
            answer = pause switch
            {
                ApprovalNeeded a => await (Approver?.Invoke(a.Request, _closing.Token) ?? Task.FromResult(false)) ? "y" : "n",
                ChoiceNeeded c => await (Chooser?.Invoke(c.Request, _closing.Token) ?? Task.FromResult("")),
                _ => ""
            };
        }
        catch (Exception)
        {
            answer = "";
        }
        _bot.Tell(new ResolvePause(pause.PauseId, answer));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _closing.Cancel();
        _events.Writer.TryComplete();
        _turn?.TrySetResult(new AgentLoopResult(false, "", 0, 0, AgentLoopResult.Cancelled));
        try { _system.Terminate().Wait(TimeSpan.FromSeconds(10)); }
        catch (AggregateException) { /* a system that would not stop in time is left to the process exit */ }
        _system.Dispose();
        _closing.Dispose();
    }
}
