// ───────────────────────────────────────────────────────────
// AgentLoopActor — AIMODE 추론 FSM (the agent itself)
//
// 역할:
//   1. IAgentLoop 인스턴스 1개를 lazy로 생성/소유 (LocalAgentLoop 의 LLamaContext
//      또는 ExternalAgentLoop 의 messages[] history 보관)
//   2. StartAgentLoop 수신 → Idle → Thinking → (Generating) → Acting → Done
//   3. 각 phase change를 AgentLoopProgress 로 부모(AgentBotActor)에게 푸시
//   4. CancelAgentLoop / ResetAgentLoopMemory 수신 시 깨끗하게 idle/dispose
//
// 경로: /user/stage/bot/loop
//
// 분리 이유:
//   - 이전: AgentBotWindow.OnAiSendAsync가 _aiLoop를 직접 소유하고 UI 스레드에서
//     RunAsync를 await — UI/추론 결합으로 진행 상황 가시성 0, 외부 신호 수신 불가
//   - 이후: 액터로 분리 → 진행 상황을 매 phase마다 부모로 푸시(UI 콜백 경로),
//     KV cache는 액터 라이프사이클과 묶임, 추후 TerminalDoneSignal 등
//     외부 신호를 액터 주소로 Tell할 수 있음
// ───────────────────────────────────────────────────────────

using Akka.Actor;
using Akka.Event;
using Agent.Common.Llm;
using Agent.Common.Llm.Tools;

namespace Agent.Common.Actors;

public sealed class AgentLoopActor : ReceiveActor
{
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private readonly AgentLoopBindings _bindings;

    private IAgentLoop? _loop;
    private CancellationTokenSource? _cts;
    private int _round;
    private long _runStartedAtTicks;

    // Stamped on every run and carried by its PipeTo'd completion. A run that
    // was abandoned mid-flight (ResetAgentLoopMemory) bumps this, so the task
    // that is still finishing cannot end the NEXT run with the previous run's
    // reason. Exactly one AgentLoopResult per StartAgentLoop depends on it.
    private int _generation;

    public AgentLoopActor(AgentLoopBindings bindings)
    {
        _bindings = bindings;
        BecomeIdle();
    }

    private void TellParent(object msg) => Context.Parent.Tell(msg);

    private static long Now() => DateTimeOffset.UtcNow.Ticks;

    private long ElapsedMsSinceStart()
        => (Now() - _runStartedAtTicks) / TimeSpan.TicksPerMillisecond;

    /// <summary>
    /// True when a piped-back run result belongs to a run we already ended —
    /// abandoned by ResetAgentLoopMemory. Letting one through would end the
    /// current run with the previous run's reason, and leave the current run's
    /// own result to arrive in Idle, where it is dropped.
    /// </summary>
    private bool IsStale(int generation)
    {
        if (generation == _generation) return false;
        _log.Info("[AgentLoop] Stale result from gen {0} ignored (current {1})", generation, _generation);
        return true;
    }

    private void BecomeIdle()
    {
        Become(() =>
        {
            Receive<StartAgentLoop>(msg =>
            {
                _runStartedAtTicks = Now();
                _round = 0;
                _cts = new CancellationTokenSource();

                // Lazy-create the loop on first turn or after ResetAgentLoopMemory.
                // The loop is reused across follow-up sends — Local backend
                // keeps KV cache + system prompt primed; External backend keeps
                // the messages[] history primed (same effect, different impl).
                if (_loop is null)
                {
                    var host = _bindings.ToolbeltFactory();
                    var baseOpts = _bindings.OptionsFactory();
                    // Inject actor-mailbox callbacks so the loop's thread-pool
                    // task can post progress events back through Self.Tell —
                    // every state mutation stays single-threaded.
                    //
                    // `self` is captured HERE, inside the handler: the callbacks run on a
                    // thread-pool thread, where `Self` (= Context.Self) throws "There is
                    // no active ActorContext" — and the loops swallow callback exceptions,
                    // so Acting progress silently never arrived (found by M0032's
                    // wearable tests; the AgentBot cards had the same gap).
                    var self = Self;
                    var wired = baseOpts with
                    {
                        OnTurnCompleted = turn => self.Tell(new TurnCompletedInternal(turn)),
                        OnGenerationProgress = (phase, tokens) =>
                            self.Tell(new GenerationProgressInternal(phase, tokens)),
                    };

                    var loop = _bindings.AgentLoopFactory(wired, host);
                    if (loop is null)
                    {
                        TellParent(new AgentLoopResult(
                            Success: false,
                            FinalMessage: "AI Mode backend not ready. Open Settings → LLM and configure (Local: Load model, External: pick provider/model/key).",
                            TurnCount: 0,
                            ElapsedMs: 0,
                            FailureReason: "Backend not ready"));
                        return;
                    }
                    _loop = loop;
                    _log.Info("[AgentLoop] Created (impl={0})", loop.GetType().Name);
                }

                TellParent(new AgentLoopProgress(AgentLoopPhase.Thinking,
                    "thinking…", _round));

                // Drive the loop on the thread pool, PipeTo back into our
                // mailbox so completion + failure both land here as messages.
                var loopRef = _loop;
                var ctsRef = _cts;
                var userRequest = msg.UserRequest;
                var gen = ++_generation;
                Task.Run(async () =>
                {
                    try
                    {
                        var run = await loopRef.RunAsync(userRequest, ctsRef.Token);
                        return (object)new RunCompletedInternal(gen, run);
                    }
                    catch (OperationCanceledException)
                    {
                        return new RunFailedInternal(gen, "Cancelled by user");
                    }
                    catch (Exception ex)
                    {
                        return new RunFailedInternal(gen, ex.Message);
                    }
                }).PipeTo(Self);

                BecomeRunning();
            });

            Receive<CancelAgentLoop>(_ =>
            {
                _log.Info("[AgentLoop] Cancel received in Idle (no-op)");
            });

            Receive<ResetAgentLoopMemory>(_ => DisposeLoopAndIdle("reset (idle)"));

            // A run abandoned by ResetAgentLoopMemory already reported its
            // result before we came back here; its task finishes afterwards
            // anyway. Swallow it explicitly rather than letting it reach the
            // dead-letter queue, where it reads like a lost message.
            Receive<RunCompletedInternal>(msg =>
                _log.Info("[AgentLoop] Stale completion from gen {0} (current {1}) discarded in Idle",
                    msg.Generation, _generation));

            Receive<RunFailedInternal>(msg =>
                _log.Info("[AgentLoop] Stale failure from gen {0} (current {1}) discarded in Idle: {2}",
                    msg.Generation, _generation, msg.Error));

            Receive<Ping>(_ => Sender.Tell(new Pong("AgentLoop", Self.Path.ToString(),
                $"Idle, loopActive={_loop is not null}")));
        });
    }

    private void BecomeRunning()
    {
        Become(() =>
        {
            Receive<GenerationProgressInternal>(msg =>
            {
                TellParent(new AgentLoopProgress(
                    AgentLoopPhase.Generating,
                    msg.Phase,
                    _round) { Tokens = msg.Tokens });
            });

            Receive<TurnCompletedInternal>(msg =>
            {
                _round++;
                var info = new AgentLoopToolCallInfo(
                    Tool: msg.Turn.Call.Tool,
                    ArgsJson: msg.Turn.Call.Args.ToJsonString(),
                    Result: msg.Turn.ToolResult);
                TellParent(new AgentLoopProgress(
                    AgentLoopPhase.Acting,
                    msg.Turn.Call.Tool,
                    _round) { ToolCall = info });
            });

            Receive<RunCompletedInternal>(msg =>
            {
                if (IsStale(msg.Generation)) return;

                var elapsed = ElapsedMsSinceStart();
                if (msg.Run.TerminatedCleanly)
                {
                    TellParent(new AgentLoopProgress(AgentLoopPhase.Done,
                        msg.Run.FinalMessage, _round));
                    TellParent(new AgentLoopResult(
                        Success: true,
                        FinalMessage: msg.Run.FinalMessage,
                        TurnCount: msg.Run.TurnCount,
                        ElapsedMs: elapsed));
                }
                else
                {
                    TellParent(new AgentLoopProgress(AgentLoopPhase.Error,
                        msg.Run.FailureReason ?? msg.Run.FinalMessage, _round));
                    TellParent(new AgentLoopResult(
                        Success: false,
                        FinalMessage: msg.Run.FinalMessage,
                        TurnCount: msg.Run.TurnCount,
                        ElapsedMs: elapsed,
                        FailureReason: msg.Run.FailureReason));
                }
                BecomeIdle();
            });

            Receive<RunFailedInternal>(msg =>
            {
                if (IsStale(msg.Generation)) return;

                var elapsed = ElapsedMsSinceStart();
                TellParent(new AgentLoopProgress(AgentLoopPhase.Error, msg.Error, _round));
                TellParent(new AgentLoopResult(
                    Success: false,
                    FinalMessage: $"⚠ {msg.Error}",
                    TurnCount: _round,
                    ElapsedMs: elapsed,
                    FailureReason: msg.Error));
                BecomeIdle();
            });

            Receive<CancelAgentLoop>(_ =>
            {
                _log.Info("[AgentLoop] Cancel received in Running");
                try { _cts?.Cancel(); } catch { }
                // The PipeTo'd Run task will complete with RunFailedInternal
                // and tip us back to Idle naturally — don't BecomeIdle here.
            });

            Receive<ResetAgentLoopMemory>(_ =>
            {
                // User asked for a fresh session mid-run: cancel current and
                // dispose the loop. Next StartAgentLoop recreates from scratch.
                //
                // The in-flight turn still owes the parent its one result. We
                // report it HERE rather than waiting for the cancelled task,
                // because disposing the loop takes us back to Idle immediately
                // and Idle no longer ends turns. Without this the StartAgentLoop
                // that began this run never produced an AgentLoopResult and the
                // parent stayed "busy" for good; bumping the generation then
                // keeps the task's own late result from ending the NEXT run.
                try { _cts?.Cancel(); } catch { }

                var elapsed = ElapsedMsSinceStart();
                TellParent(new AgentLoopProgress(AgentLoopPhase.Error, "reset by user", _round));
                TellParent(new AgentLoopResult(
                    Success: false,
                    FinalMessage: "⚠ Session reset before the turn finished.",
                    TurnCount: _round,
                    ElapsedMs: elapsed,
                    FailureReason: "Reset by user"));

                _generation++;
                DisposeLoopAndIdle("reset (running)");
            });

            Receive<StartAgentLoop>(_ =>
            {
                // Defensive: a second StartAgentLoop while running is a UI bug.
                // Tell parent + ignore so we don't double-run on one context.
                _log.Warning("[AgentLoop] StartAgentLoop received while running — ignored");
                TellParent(new AgentLoopProgress(AgentLoopPhase.Generating,
                    "(busy, request ignored)", _round));
            });

            Receive<Ping>(_ => Sender.Tell(new Pong("AgentLoop", Self.Path.ToString(),
                $"Running round={_round}")));
        });
    }

    private void DisposeLoopAndIdle(string reason)
    {
        var loop = _loop;
        _loop = null;
        if (loop is not null)
        {
            // Fire-and-forget: dispose the loop (which disposes LLamaContext).
            // We don't await — DisposeAsync of LLamaContext can block briefly
            // on Vulkan and we don't want to stall the actor's mailbox.
            Task.Run(async () =>
            {
                try { await loop.DisposeAsync(); }
                catch { /* finalizer is the backstop */ }
            });
            _log.Info("[AgentLoop] Disposed ({0})", reason);
        }
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        BecomeIdle();
    }

    protected override void PostStop()
    {
        DisposeLoopAndIdle("PostStop");
        base.PostStop();
    }

    // ─── Internal mailbox messages (PipeTo / Self.Tell only) ───
    private sealed record GenerationProgressInternal(string Phase, int Tokens);
    private sealed record TurnCompletedInternal(ToolTurn Turn);
    private sealed record RunCompletedInternal(int Generation, AgentLoopRun Run);
    private sealed record RunFailedInternal(int Generation, string Error);
}
