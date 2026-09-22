using System.Diagnostics;
using Akka.Actor;
using AgentOne.Agent;
using AgentOne.Llm;

namespace AgentOne.Actors;

/// <summary>
/// THE agent: owns one <see cref="ChatSession"/> — agent-one's loop, the way
/// AgentZero's <c>AgentLoopActor</c> owns one <c>IAgentLoop</c> — and drives a
/// turn from Idle through Thinking / Acting / Generating to Done or Error.
///
/// Two behaviours only, Idle and Running; the phase is what gets reported.
/// The turn itself runs on the pool (<c>Task.Run</c> + <c>PipeTo</c>), never on
/// the actor's thread, and everything the session raises while it runs comes
/// back through the mailbox as a private internal message before it is told
/// to the parent — so the parent sees one ordered stream, from one thread.
///
/// Cancel is <c>_cts.Cancel()</c> and nothing else: the in-flight task ends as
/// a failed run and that, not the cancel, tips the actor back to Idle. Exactly
/// one <see cref="AgentLoopResult"/> per <see cref="StartAgentLoop"/>, whatever
/// happened — the invariant the bot's one-at-a-time rule stands on.
/// </summary>
public sealed class AgentLoopActor : ReceiveActor
{
    private readonly AgentLoopBindings _bindings;
    private readonly IActorRef _parent;
    private readonly IActorRef _self;

    private ChatSession? _session;
    private string? _failure;
    private CancellationTokenSource? _cts;
    private int _round;
    private int _pauseId;
    private readonly Dictionary<int, TaskCompletionSource<string>> _pauses = new();
    private readonly Stopwatch _clock = new();

    // Mailbox-only messages. They stay private on purpose: a renderer never
    // sees them, and promoting one would let it bypass the ordering above.
    private sealed record ProgressInternal(AgentLoopPhase Phase, string Text, AgentStep? Step);
    private sealed record NoticeInternal(AgentLoopNotice Notice);
    private sealed record PauseInternal(PersonNeeded Request, TaskCompletionSource<string> Answer);
    private sealed record RunCompletedInternal(AgentRun? Run);
    private sealed record RunFailedInternal(string Reason, bool Cancelled);

    public AgentLoopActor(AgentLoopBindings bindings)
    {
        _bindings = bindings;
        // Captured here, on the actor's thread. Context.Self / Context.Parent
        // throw when read from the pool thread the turn runs on.
        _parent = Context.Parent;
        _self = Self;
        BecomeIdle();
    }

    // ------------------------------------------------------------------ idle

    private void BecomeIdle()
    {
        Become(() =>
        {
            Common();
            Receive<StartAgentLoop>(Start);
            Receive<CancelAgentLoop>(_ => { });
        });
    }

    private void Start(StartAgentLoop msg)
    {
        if (!EnsureSession())
        {
            _parent.Tell(new AgentLoopResult(false, "", 0, 0, _failure));
            return;
        }

        _round = 0;
        _cts = new CancellationTokenSource();
        _clock.Restart();
        _parent.Tell(new AgentLoopProgress(AgentLoopPhase.Thinking, "thinking", 0));

        var session = _session!;
        var token = _cts.Token;
        var request = msg.UserRequest;

        Task.Run(async () =>
        {
            try
            {
                var run = await session.SubmitAsync(request, token);
                return (object)new RunCompletedInternal(run);
            }
            catch (OperationCanceledException)
            {
                return new RunFailedInternal(AgentLoopResult.Cancelled, Cancelled: true);
            }
            catch (Exception ex)
            {
                return new RunFailedInternal(ex.Message, Cancelled: false);
            }
        }).PipeTo(_self);

        BecomeRunning();
    }

    // --------------------------------------------------------------- running

    private void BecomeRunning()
    {
        Become(() =>
        {
            Common();

            Receive<ProgressInternal>(p =>
            {
                if (p.Phase == AgentLoopPhase.Acting) _round++;
                _parent.Tell(new AgentLoopProgress(p.Phase, p.Text, _round) { Step = p.Step });
            });

            Receive<RunCompletedInternal>(m =>
            {
                var run = m.Run;
                var elapsed = _clock.ElapsedMilliseconds;
                if (run is null)
                {
                    // An empty line: nothing ran, nothing to say.
                    _parent.Tell(new AgentLoopResult(true, "", 0, elapsed));
                }
                else if (run.Succeeded)
                {
                    _parent.Tell(new AgentLoopProgress(AgentLoopPhase.Done, run.Text, _round));
                    _parent.Tell(new AgentLoopResult(true, run.Text, run.Steps.Count, elapsed) { Run = run });
                }
                else
                {
                    // The loop itself turns a cancelled token into a run that
                    // says so; the result names it the one way, whichever path.
                    var reason = run.Reason == StopReason.Cancelled ? AgentLoopResult.Cancelled : run.Reason.ToString();
                    _parent.Tell(new AgentLoopProgress(AgentLoopPhase.Error, reason, _round));
                    _parent.Tell(new AgentLoopResult(false, run.Text, run.Steps.Count, elapsed, reason) { Run = run });
                }
                FinishTurn();
            });

            Receive<RunFailedInternal>(m =>
            {
                _parent.Tell(new AgentLoopProgress(AgentLoopPhase.Error, m.Reason, _round));
                _parent.Tell(new AgentLoopResult(false, "", 0, _clock.ElapsedMilliseconds, m.Reason));
                FinishTurn();
            });

            // Cancel only. The task notices, fails, and RunFailedInternal ends
            // the turn — so a cancelled turn still produces its one result.
            Receive<CancelAgentLoop>(_ => _cts?.Cancel());

            Receive<StartAgentLoop>(_ =>
                _parent.Tell(new AgentLoopResult(false, "", 0, 0, "a turn is already running")));
        });
    }

    private void FinishTurn()
    {
        foreach (var pause in _pauses.Values) pause.TrySetResult("");
        _pauses.Clear();
        _cts?.Dispose();
        _cts = null;
        BecomeIdle();
    }

    // ---------------------------------------------------------- both states

    private void Common()
    {
        Receive<NoticeInternal>(n => _parent.Tell(n.Notice));

        Receive<PauseInternal>(p =>
        {
            _pauses[p.Request.PauseId] = p.Answer;
            _parent.Tell(p.Request);
        });

        Receive<ResolvePause>(r =>
        {
            if (_pauses.Remove(r.PauseId, out var answer)) answer.TrySetResult(r.Answer);
        });

        // The person's pause. Both states: pausing with nothing running is a
        // no-op the session already handles, and resuming judges the line on
        // the pool (the engine may be asked) and answers the asker from there.
        Receive<PauseAgentLoop>(_ =>
        {
            if (!EnsureSession()) { Sender.Tell(new AgentSessionFailed(_failure!)); return; }
            _session!.Pause();
            Sender.Tell(new AgentLoopPaused(_session.Paused));
        });
        Receive<ResumeAgentLoop>(m =>
        {
            if (!EnsureSession()) { Sender.Tell(new AgentSessionFailed(_failure!)); return; }
            var session = _session!;
            var asker = Sender;
            Task.Run(async () =>
            {
                try { return (object)new AgentLoopResumed(await session.ResumeAsync(m.Line, CancellationToken.None)); }
                catch (Exception ex) when (ex is ChatProviderException or InvalidOperationException) { return new AgentSessionFailed(ex.Message); }
            }).PipeTo(asker);
        });

        Receive<QueryAgentInfo>(_ => Sender.Tell(EnsureSession() ? Info() : new AgentSessionFailed(_failure!)));
        Receive<QueryAgentStats>(_ => Reply(s => s.Stats()));
        Receive<QueryAgentSessions>(_ => Reply(s => new AgentSessionList(s.ListSessions())));
        Receive<ToggleSmartMode>(_ => Reply(s =>
        {
            var changed = s.TryToggleSmart(out var message);
            return new SmartModeToggled(changed, message, s.Smart);
        }));
        Receive<ResetAgentLoopMemory>(_ => Reply(s => { s.Reset(); return new AgentSessionReset(s.LogPath); }));
        Receive<NewAgentSession>(_ => Reply(s => { s.NewSession(); return new AgentSessionReset(s.LogPath); }));
        Receive<ResumeAgentSession>(m => Reply(s => new AgentSessionResumed(s.Resume(m.Path), s.Title, s.LogPath)));
    }

    private void Reply(Func<ChatSession, object> answer)
    {
        if (!EnsureSession()) { Sender.Tell(new AgentSessionFailed(_failure!)); return; }
        try { Sender.Tell(answer(_session!)); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            Sender.Tell(new AgentSessionFailed(ex.Message));
        }
    }

    private AgentSessionInfo Info()
    {
        var s = _session!;
        return new AgentSessionInfo(s.Root, s.ProviderName, s.Model, s.ReasoningModel, s.ToolScope, s.Shell,
            s.LogPath, s.SmartAvailable, s.Smart, s.Title, s.Workspace.MemoryChars);
    }

    // ----------------------------------------------------------- the session

    /// <summary>Builds the session on first use and wires every event of it back through the mailbox.</summary>
    private bool EnsureSession()
    {
        if (_session is not null) return true;
        if (_failure is not null) return false;

        ChatSession session;
        try
        {
            session = _bindings.SessionFactory();
        }
        catch (Exception ex) when (ex is ChatProviderException or InvalidOperationException or IOException)
        {
            _failure = ex.Message;
            return false;
        }

        var self = _self;
        session.ActivityStarted += what => self.Tell(new ProgressInternal(AgentLoopPhase.Thinking, what, null));
        session.StepCompleted += step => self.Tell(new ProgressInternal(AgentLoopPhase.Acting, step.Tool, step));
        session.AnswerDelta += fragment => self.Tell(new ProgressInternal(AgentLoopPhase.Generating, fragment, null));
        session.Decided += note => self.Tell(new NoticeInternal(new DecisionNotice(note)));
        session.Noted += text => self.Tell(new NoticeInternal(new NoteNotice(text)));
        session.TitleChanged += title => self.Tell(new NoticeInternal(new TitleNotice(title)));
        session.DesignMade += lines => self.Tell(new NoticeInternal(new DesignNotice(lines)));
        session.Learned += items => self.Tell(new NoticeInternal(new LearnedNotice(items)));

        // The pause-for-a-person, from the pool thread the turn runs on: the
        // question goes through the mailbox, the answer comes back as
        // ResolvePause. A cancelled turn answers every open pause with "".
        session.Approver = async (request, ct) =>
        {
            var answer = await PauseAsync(id => new ApprovalNeeded(id, request), ct);
            return Commands.ChatCommand.IsYes(answer);
        };
        session.Chooser = (request, ct) => PauseAsync(id => new ChoiceNeeded(id, request), ct);

        _session = session;
        return true;
    }

    private Task<string> PauseAsync(Func<int, PersonNeeded> request, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _pauseId);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetResult(""));
        _self.Tell(new PauseInternal(request(id), tcs));
        return tcs.Task;
    }

    protected override void PostStop()
    {
        _cts?.Cancel();
        foreach (var pause in _pauses.Values) pause.TrySetResult("");
        _session?.Dispose();
        _session = null;
        base.PostStop();
    }
}
