// ───────────────────────────────────────────────────────────
// AgentReactorActor — AIMODE 추론 FSM
//
// 역할:
//   1. AgentToolLoop 인스턴스 1개를 lazy로 생성/소유 (LLamaContext 보관)
//   2. StartReactor 수신 → Idle → Thinking → (Generating) → Acting → Done
//   3. 각 phase change를 ReactorProgress로 부모(AgentBotActor)에게 푸시
//   4. CancelReactor / ResetReactorSession 수신 시 깨끗하게 idle/dispose
//
// 경로: /user/stage/bot/reactor
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

public sealed class AgentReactorActor : ReceiveActor
{
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private readonly ReactorBindings _bindings;

    private AgentToolLoop? _loop;
    private CancellationTokenSource? _cts;
    private int _round;
    private long _runStartedAtTicks;

    public AgentReactorActor(ReactorBindings bindings)
    {
        _bindings = bindings;
        BecomeIdle();
    }

    private void TellParent(object msg) => Context.Parent.Tell(msg);

    private static long Now() => DateTimeOffset.UtcNow.Ticks;

    private long ElapsedMsSinceStart()
        => (Now() - _runStartedAtTicks) / TimeSpan.TicksPerMillisecond;

    private void BecomeIdle()
    {
        Become(() =>
        {
            Receive<StartReactor>(msg =>
            {
                _runStartedAtTicks = Now();
                _round = 0;
                _cts = new CancellationTokenSource();

                // Lazy-create the loop on first turn or after ResetReactorSession.
                // The loop holds a fresh LLamaContext + InteractiveExecutor and
                // outlives one StartReactor — re-used across follow-up sends so
                // KV cache + system prompt stay primed.
                if (_loop is null)
                {
                    var llm = _bindings.LlmAccessor();
                    if (llm is null)
                    {
                        TellParent(new ReactorResult(
                            Success: false,
                            FinalMessage: "AI Mode requires a loaded LLM (LlamaSharpLocalLlm). Open Settings → AI Mode and click Load.",
                            TurnCount: 0,
                            ElapsedMs: 0,
                            FailureReason: "LLM not loaded"));
                        return;
                    }

                    var host = _bindings.HostFactory();
                    var template = _bindings.TemplateFactory();
                    var baseOpts = _bindings.OptionsFactory();
                    // We override the loop's two streaming callbacks so we can
                    // pump them through Self.Tell — that keeps every state
                    // mutation single-threaded under the actor's mailbox even
                    // though the loop's task runs on the thread pool.
                    var wired = baseOpts with
                    {
                        OnTurnCompleted = turn => Self.Tell(new TurnCompletedInternal(turn)),
                        OnGenerationProgress = (phase, tokens) =>
                            Self.Tell(new GenerationProgressInternal(phase, tokens)),
                    };
                    _loop = new AgentToolLoop(llm, host, wired, template);
                    _log.Info("[Reactor] Loop created (template={0})", template.FamilyId);
                }

                TellParent(new ReactorProgress(ReactorPhase.Thinking,
                    "thinking…", _round));

                // Drive the loop on the thread pool, PipeTo back into our
                // mailbox so completion + failure both land here as messages.
                var loopRef = _loop;
                var ctsRef = _cts;
                var userRequest = msg.UserRequest;
                Task.Run(async () =>
                {
                    try
                    {
                        var session = await loopRef.RunAsync(userRequest, ctsRef.Token);
                        return (object)new RunCompletedInternal(session);
                    }
                    catch (OperationCanceledException)
                    {
                        return new RunFailedInternal("Cancelled by user");
                    }
                    catch (Exception ex)
                    {
                        return new RunFailedInternal(ex.Message);
                    }
                }).PipeTo(Self);

                BecomeRunning();
            });

            Receive<CancelReactor>(_ =>
            {
                _log.Info("[Reactor] Cancel received in Idle (no-op)");
            });

            Receive<ResetReactorSession>(_ => DisposeLoopAndIdle("reset (idle)"));

            Receive<Ping>(_ => Sender.Tell(new Pong("Reactor", Self.Path.ToString(),
                $"Idle, loopActive={_loop is not null}")));
        });
    }

    private void BecomeRunning()
    {
        Become(() =>
        {
            Receive<GenerationProgressInternal>(msg =>
            {
                TellParent(new ReactorProgress(
                    ReactorPhase.Generating,
                    msg.Phase,
                    _round) { Tokens = msg.Tokens });
            });

            Receive<TurnCompletedInternal>(msg =>
            {
                _round++;
                var info = new ReactorToolCallInfo(
                    Tool: msg.Turn.Call.Tool,
                    ArgsJson: msg.Turn.Call.Args.ToJsonString(),
                    Result: msg.Turn.ToolResult);
                TellParent(new ReactorProgress(
                    ReactorPhase.Acting,
                    msg.Turn.Call.Tool,
                    _round) { ToolCall = info });
            });

            Receive<RunCompletedInternal>(msg =>
            {
                var elapsed = ElapsedMsSinceStart();
                if (msg.Session.TerminatedCleanly)
                {
                    TellParent(new ReactorProgress(ReactorPhase.Done,
                        msg.Session.FinalMessage, _round));
                    TellParent(new ReactorResult(
                        Success: true,
                        FinalMessage: msg.Session.FinalMessage,
                        TurnCount: msg.Session.TurnCount,
                        ElapsedMs: elapsed));
                }
                else
                {
                    TellParent(new ReactorProgress(ReactorPhase.Error,
                        msg.Session.FailureReason ?? msg.Session.FinalMessage, _round));
                    TellParent(new ReactorResult(
                        Success: false,
                        FinalMessage: msg.Session.FinalMessage,
                        TurnCount: msg.Session.TurnCount,
                        ElapsedMs: elapsed,
                        FailureReason: msg.Session.FailureReason));
                }
                BecomeIdle();
            });

            Receive<RunFailedInternal>(msg =>
            {
                var elapsed = ElapsedMsSinceStart();
                TellParent(new ReactorProgress(ReactorPhase.Error, msg.Error, _round));
                TellParent(new ReactorResult(
                    Success: false,
                    FinalMessage: $"⚠ {msg.Error}",
                    TurnCount: _round,
                    ElapsedMs: elapsed,
                    FailureReason: msg.Error));
                BecomeIdle();
            });

            Receive<CancelReactor>(_ =>
            {
                _log.Info("[Reactor] Cancel received in Running");
                try { _cts?.Cancel(); } catch { }
                // The PipeTo'd Run task will complete with RunFailedInternal
                // and tip us back to Idle naturally — don't BecomeIdle here.
            });

            Receive<ResetReactorSession>(_ =>
            {
                // User asked for a fresh session mid-run: cancel current and
                // dispose the loop. Next StartReactor recreates from scratch.
                try { _cts?.Cancel(); } catch { }
                DisposeLoopAndIdle("reset (running)");
            });

            Receive<StartReactor>(_ =>
            {
                // Defensive: a second StartReactor while running is a UI bug.
                // Tell parent + ignore so we don't double-run on one context.
                _log.Warning("[Reactor] StartReactor received while running — ignored");
                TellParent(new ReactorProgress(ReactorPhase.Generating,
                    "(busy, request ignored)", _round));
            });

            Receive<Ping>(_ => Sender.Tell(new Pong("Reactor", Self.Path.ToString(),
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
            _log.Info("[Reactor] Loop disposed ({0})", reason);
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
    private sealed record RunCompletedInternal(AgentToolSession Session);
    private sealed record RunFailedInternal(string Error);
}
