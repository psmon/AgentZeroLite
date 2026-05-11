// ───────────────────────────────────────────────────────────
// AgentLoopActor + AgentBotActor wiring tests (TestKit, headless)
//
// Scope: message protocol + lifecycle + delegate wiring.
//        NOT covering the inner LLM agent loop (those live tests gate on a
//        model file and are kept in AgentLoopTests / NemotronProbeTests).
//
// Trick: AgentLoopBindings.AgentLoopFactory returns null in these tests so
//        the actor hits the early-exit path that emits an AgentLoopResult
//        with FailureReason="Backend not ready". That exercises message
//        routing without needing LLamaSharp + a 4 GB model file in CI.
// ───────────────────────────────────────────────────────────

using Akka.Actor;
using Akka.TestKit.Xunit2;
using Agent.Common.Llm.Tools;

namespace ZeroCommon.Tests;

public sealed class AgentLoopActorTests : TestKit
{
    private static AgentLoopBindings BindingsWithNoLlm() => new(
        ToolbeltFactory: () => new StubHost(),
        OptionsFactory: () => new AgentLoopOptions(),
        AgentLoopFactory: (_, _) => null);

    [Fact]
    public void StartAgentLoop_with_no_backend_emits_AgentLoopResult_failure_to_parent()
    {
        var probe = CreateTestProbe("parent-probe");
        // Use the probe as parent so we can assert on what TellParent sends.
        // ActorOf with a parent context isn't directly exposable, so instead
        // we ChildActorOf-style: spawn the agent loop under the probe.
        var loop = probe.ChildActorOf(
            Props.Create(() => new AgentLoopActor(BindingsWithNoLlm())));

        loop.Tell(new StartAgentLoop("hi"), TestActor);

        var result = probe.ExpectMsg<AgentLoopResult>(TimeSpan.FromSeconds(2));
        Assert.False(result.Success);
        Assert.Equal(0, result.TurnCount);
        Assert.Equal("Backend not ready", result.FailureReason);
        Assert.Contains("AI Mode", result.FinalMessage);
    }

    [Fact]
    public void Ping_in_Idle_replies_Pong_with_path()
    {
        var loop = Sys.ActorOf(
            Props.Create(() => new AgentLoopActor(BindingsWithNoLlm())),
            "loop-ping");

        loop.Tell(new Ping(), TestActor);

        var pong = ExpectMsg<Pong>(TimeSpan.FromSeconds(2));
        Assert.Equal("AgentLoop", pong.ActorName);
        Assert.Contains("loop-ping", pong.ActorPath);
        Assert.Contains("Idle", pong.Status);
    }

    [Fact]
    public void Cancel_and_Reset_in_Idle_are_safe_noops()
    {
        var loop = Sys.ActorOf(
            Props.Create(() => new AgentLoopActor(BindingsWithNoLlm())),
            "loop-noop");

        // Should not throw, should not produce messages.
        loop.Tell(new CancelAgentLoop(), TestActor);
        loop.Tell(new ResetAgentLoopMemory(), TestActor);
        ExpectNoMsg(TimeSpan.FromMilliseconds(200));

        // And still responds to Ping → still Idle.
        loop.Tell(new Ping(), TestActor);
        var pong = ExpectMsg<Pong>(TimeSpan.FromSeconds(2));
        Assert.Contains("Idle", pong.Status);
    }

    private sealed class StubHost : IAgentToolbelt
    {
        public Task<string> ListTerminalsAsync(CancellationToken ct = default)
            => Task.FromResult("[]");
        public Task<string> ReadTerminalAsync(int g, int t, int n, CancellationToken ct = default)
            => Task.FromResult("");
        public Task<bool> SendToTerminalAsync(int g, int t, string text, CancellationToken ct = default)
            => Task.FromResult(true);
        public Task<bool> SendKeyAsync(int g, int t, string key, CancellationToken ct = default)
            => Task.FromResult(true);
    }
}

/// <summary>
/// AgentBotActor wiring for the agent loop: SetAgentLoopCallbacks remembers
/// the delegates, AgentLoopBindings is required before StartAgentLoop,
/// AgentLoopProgress + AgentLoopResult bubble through to delegates,
/// ResetAgentLoopMemory kills the child + clears introductions.
/// </summary>
public sealed class AgentBotActorAgentLoopWiringTests : TestKit
{
    private static AgentLoopBindings BindingsNoLlm() => new(
        ToolbeltFactory: () => new StubHost(),
        OptionsFactory: () => new AgentLoopOptions(),
        AgentLoopFactory: (_, _) => null);

    private (IActorRef bot, IActorRef stage) NewBot()
    {
        var stage = CreateTestProbe("stage").Ref;
        var bot = Sys.ActorOf(Props.Create(() => new AgentBotActor(stage)), "bot");
        return (bot, stage);
    }

    [Fact]
    public void StartAgentLoop_without_bindings_dispatches_friendly_failure_via_callback()
    {
        var (bot, _) = NewBot();
        var resultProbe = CreateTestProbe("result");
        // We emulate the dispatcher delegate by Tell-ing into a probe.
        bot.Tell(new SetAgentLoopCallbacks(
            OnProgress: _ => { },
            OnResult: r => resultProbe.Ref.Tell(r)));

        bot.Tell(new StartAgentLoop("hi"));

        var result = resultProbe.ExpectMsg<AgentLoopResult>(TimeSpan.FromSeconds(2));
        Assert.False(result.Success);
        Assert.Equal("no bindings", result.FailureReason);
    }

    [Fact]
    public void StartAgentLoop_with_bindings_creates_child_and_routes_failure_back()
    {
        var (bot, _) = NewBot();
        var resultProbe = CreateTestProbe("result");
        bot.Tell(new SetAgentLoopCallbacks(
            OnProgress: _ => { },
            OnResult: r => resultProbe.Ref.Tell(r)));
        bot.Tell(BindingsNoLlm());

        bot.Tell(new StartAgentLoop("anything"));

        // The child agent loop short-circuits when the factory returns null and
        // bubbles the failure result back through the bot → our probe.
        var result = resultProbe.ExpectMsg<AgentLoopResult>(TimeSpan.FromSeconds(2));
        Assert.False(result.Success);
        Assert.Equal("Backend not ready", result.FailureReason);
    }

    [Fact]
    public void ResetAgentLoopMemory_clears_introductions_and_kills_child()
    {
        var (bot, _) = NewBot();
        bot.Tell(new SetAgentLoopCallbacks(_ => { }, _ => { }));
        bot.Tell(BindingsNoLlm());
        // Build child via a Start that we don't await.
        bot.Tell(new StartAgentLoop("trigger child create"));
        // Mark some terminals introduced first.
        bot.Tell(new IntroduceTerminalIfFirst(0, 0), TestActor);
        ExpectMsg<IntroduceTerminalReply>();
        bot.Tell(new IntroduceTerminalIfFirst(1, 2), TestActor);
        ExpectMsg<IntroduceTerminalReply>();

        // Reset.
        bot.Tell(new ResetAgentLoopMemory());
        // Give the bot mailbox a beat to process the reset before next assertion.
        ExpectNoMsg(TimeSpan.FromMilliseconds(150));

        // After reset, IntroduceTerminalIfFirst(0, 0) should report first contact again.
        bot.Tell(new IntroduceTerminalIfFirst(0, 0), TestActor);
        var reply = ExpectMsg<IntroduceTerminalReply>();
        Assert.True(reply.WasFirstContact);
    }

    [Fact]
    public void Bot_Ping_works_after_agent_loop_wiring()
    {
        var (bot, _) = NewBot();
        bot.Tell(new SetAgentLoopCallbacks(_ => { }, _ => { }));
        bot.Tell(BindingsNoLlm());

        bot.Tell(new Ping(), TestActor);
        var pong = ExpectMsg<Pong>(TimeSpan.FromSeconds(2));
        Assert.Equal("Bot", pong.ActorName);
    }

    // ── Peer-signal protocol tests ──

    [Fact]
    public void MarkConversationActive_then_Query_returns_peer_names()
    {
        var (bot, _) = NewBot();
        bot.Tell(new MarkConversationActive("Claude"));
        bot.Tell(new MarkConversationActive("Codex"));

        bot.Tell(new QueryActiveConversations(), TestActor);
        var reply = ExpectMsg<ActiveConversationsReply>(TimeSpan.FromSeconds(2));
        Assert.Equal(2, reply.Active.Count);
        Assert.Contains("Claude", reply.Active);
        Assert.Contains("Codex", reply.Active);
    }

    [Fact]
    public void ClearConversationActive_removes_peer()
    {
        var (bot, _) = NewBot();
        bot.Tell(new MarkConversationActive("Claude"));
        bot.Tell(new MarkConversationActive("Codex"));
        bot.Tell(new ClearConversationActive("Claude"));

        bot.Tell(new QueryActiveConversations(), TestActor);
        var reply = ExpectMsg<ActiveConversationsReply>(TimeSpan.FromSeconds(2));
        Assert.Single(reply.Active);
        Assert.Contains("Codex", reply.Active);
    }

    [Fact]
    public void TerminalSentToBot_for_inactive_peer_does_not_trigger_agent_loop()
    {
        var (bot, _) = NewBot();
        var resultProbe = CreateTestProbe("result");
        bot.Tell(new SetAgentLoopCallbacks(_ => { }, r => resultProbe.Ref.Tell(r)));
        bot.Tell(BindingsNoLlm());

        // No MarkConversationActive — peer "Claude" is inactive.
        bot.Tell(new TerminalSentToBot("Claude", "hello bot"));

        // Agent loop should NOT be invoked (no Result message back).
        resultProbe.ExpectNoMsg(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void TerminalSentToBot_for_active_peer_routes_to_agent_loop()
    {
        var (bot, _) = NewBot();
        var resultProbe = CreateTestProbe("result");
        bot.Tell(new SetAgentLoopCallbacks(_ => { }, r => resultProbe.Ref.Tell(r)));
        bot.Tell(BindingsNoLlm());

        bot.Tell(new MarkConversationActive("Claude"));
        bot.Tell(new TerminalSentToBot("Claude", "Claude responded with hi"));

        // Agent loop wakes (factory returns null → friendly failure result), proving routing reached it.
        var result = resultProbe.ExpectMsg<AgentLoopResult>(TimeSpan.FromSeconds(2));
        Assert.False(result.Success);
        Assert.Equal("Backend not ready", result.FailureReason);
    }

    // ── M0017 후속 #5 — DONE shortcut to TTS in delegation mode ──

    [Fact]
    public void TryExtractDoneContent_unwraps_balanced_DONE_envelope()
    {
        Assert.Equal("hello",
            Agent.Common.Actors.AgentBotActor.TryExtractDoneContent("DONE(hello)"));
        Assert.Equal("hi (with parens)",
            Agent.Common.Actors.AgentBotActor.TryExtractDoneContent("DONE(hi (with parens))"));
        // Multi-line content survives.
        Assert.Equal("line one\nline two",
            Agent.Common.Actors.AgentBotActor.TryExtractDoneContent("DONE(line one\nline two)"));
        // Case-insensitive prefix.
        Assert.Equal("ok",
            Agent.Common.Actors.AgentBotActor.TryExtractDoneContent("done(ok)"));
        // Leading whitespace stripped.
        Assert.Equal("trimmed",
            Agent.Common.Actors.AgentBotActor.TryExtractDoneContent("  DONE(trimmed)  "));
    }

    [Fact]
    public void TryExtractDoneContent_returns_null_for_non_envelope_text()
    {
        Assert.Null(Agent.Common.Actors.AgentBotActor.TryExtractDoneContent("just a plain reply"));
        Assert.Null(Agent.Common.Actors.AgentBotActor.TryExtractDoneContent("DONE()"));         // empty body
        Assert.Null(Agent.Common.Actors.AgentBotActor.TryExtractDoneContent("DONE missing-parens"));
        Assert.Null(Agent.Common.Actors.AgentBotActor.TryExtractDoneContent(""));
        Assert.Null(Agent.Common.Actors.AgentBotActor.TryExtractDoneContent(null));
    }

    [Fact]
    public void IsHandshakeAck_filters_protocol_noise_but_keeps_real_content()
    {
        Assert.True(Agent.Common.Actors.AgentBotActor.IsHandshakeAck("ready"));
        Assert.True(Agent.Common.Actors.AgentBotActor.IsHandshakeAck("handshake-ok"));
        Assert.True(Agent.Common.Actors.AgentBotActor.IsHandshakeAck("OK"));
        Assert.True(Agent.Common.Actors.AgentBotActor.IsHandshakeAck("  Ready  "));   // trimmed/case
        Assert.False(Agent.Common.Actors.AgentBotActor.IsHandshakeAck("오늘은 맑음입니다"));
        Assert.False(Agent.Common.Actors.AgentBotActor.IsHandshakeAck("ready to ship"));
        Assert.False(Agent.Common.Actors.AgentBotActor.IsHandshakeAck(""));
    }

    [Fact]
    public void DelegationMode_DONE_message_fires_result_callback_without_agent_loop()
    {
        // Acceptance: in delegation mode, a DONE(...) peer signal short-circuits
        // straight to OnResult (which the UI uses to drive AddBotMessage→TTS),
        // skipping the agent loop entirely. No StartAgentLoop is triggered, so
        // the LLM doesn't burn another 5-10s "summarizing" what Claude already
        // wrapped neatly for us.
        var (bot, _) = NewBot();
        var resultProbe = CreateTestProbe("result");
        bot.Tell(new SetAgentLoopCallbacks(_ => { }, r => resultProbe.Ref.Tell(r)));
        bot.Tell(BindingsNoLlm());
        bot.Tell(new MarkConversationActive("Claude"));
        bot.Tell(new SetDelegationMode(true));

        bot.Tell(new TerminalSentToBot("Claude", "DONE(오늘 서울은 맑음입니다.)"));

        // The OnResult delegate should be invoked with the inner text.
        var result = resultProbe.ExpectMsg<AgentLoopResult>(TimeSpan.FromSeconds(2));
        Assert.True(result.Success);
        Assert.Equal("오늘 서울은 맑음입니다.", result.FinalMessage);
        Assert.Equal(0, result.TurnCount);
    }

    [Fact]
    public void DelegationMode_pre_unwrapped_peer_text_also_fires_result_callback()
    {
        // M0017 후속 #10 regression: MainWindow.HandleBotChat pre-strips
        // the DONE(...) wrapper before forwarding to the actor, so the actor
        // only ever sees raw inner text. The shortcut must accept that shape
        // too — otherwise the production TTS path silently dies (which is
        // exactly what operator hit after 후속 #9).
        var (bot, _) = NewBot();
        var resultProbe = CreateTestProbe("result");
        bot.Tell(new SetAgentLoopCallbacks(_ => { }, r => resultProbe.Ref.Tell(r)));
        bot.Tell(BindingsNoLlm());
        bot.Tell(new MarkConversationActive("Claude"));
        bot.Tell(new SetDelegationMode(true));

        // No DONE(...) wrapper — peer signal text is already the inner content
        // because the IPC layer extracted it for its own UI display path.
        bot.Tell(new TerminalSentToBot("Claude", "오늘 서울은 맑음입니다."));

        var result = resultProbe.ExpectMsg<AgentLoopResult>(TimeSpan.FromSeconds(2));
        Assert.True(result.Success);
        Assert.Equal("오늘 서울은 맑음입니다.", result.FinalMessage);
        Assert.Equal(0, result.TurnCount);
    }

    [Fact]
    public void DelegationMode_pre_unwrapped_handshake_ack_is_silent()
    {
        // Handshake-ack filter must still apply to pre-unwrapped peer text,
        // not just DONE(...) wrapped form. Operator shouldn't hear "ready"
        // out loud after the delegation handshake bootstrap.
        var (bot, _) = NewBot();
        var resultProbe = CreateTestProbe("result");
        bot.Tell(new SetAgentLoopCallbacks(_ => { }, r => resultProbe.Ref.Tell(r)));
        bot.Tell(BindingsNoLlm());
        bot.Tell(new MarkConversationActive("Claude"));
        bot.Tell(new SetDelegationMode(true));

        bot.Tell(new TerminalSentToBot("Claude", "ready"));
        bot.Tell(new TerminalSentToBot("Claude", "handshake-ok"));

        resultProbe.ExpectNoMsg(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void DelegationMode_DONE_handshake_ack_is_silent()
    {
        // Acceptance: DONE(ready) / DONE(handshake-ok) are protocol noise, not
        // user-facing replies — the voice user shouldn't hear "ready". The
        // shortcut path swallows them silently, and no agent loop fires either.
        var (bot, _) = NewBot();
        var resultProbe = CreateTestProbe("result");
        bot.Tell(new SetAgentLoopCallbacks(_ => { }, r => resultProbe.Ref.Tell(r)));
        bot.Tell(BindingsNoLlm());
        bot.Tell(new MarkConversationActive("Claude"));
        bot.Tell(new SetDelegationMode(true));

        bot.Tell(new TerminalSentToBot("Claude", "DONE(ready)"));
        bot.Tell(new TerminalSentToBot("Claude", "DONE(handshake-ok)"));

        resultProbe.ExpectNoMsg(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void DelegationMode_off_DONE_falls_back_to_agent_loop_continuation()
    {
        // Acceptance: without delegation mode the shortcut must NOT fire — the
        // normal Mode 2 behaviour (wake the agent loop with the peer signal
        // as user prompt) still applies. Guards regression: a future operator
        // pattern that depends on continuation cycles for non-delegation
        // chat shouldn't break.
        var (bot, _) = NewBot();
        var resultProbe = CreateTestProbe("result");
        bot.Tell(new SetAgentLoopCallbacks(_ => { }, r => resultProbe.Ref.Tell(r)));
        bot.Tell(BindingsNoLlm());
        bot.Tell(new MarkConversationActive("Claude"));
        // NOTE: SetDelegationMode is intentionally NOT sent.

        bot.Tell(new TerminalSentToBot("Claude", "DONE(any content)"));

        // Agent loop fires; with no bindings it returns the friendly failure.
        var result = resultProbe.ExpectMsg<AgentLoopResult>(TimeSpan.FromSeconds(2));
        Assert.False(result.Success);
        Assert.Equal("Backend not ready", result.FailureReason);
    }

    [Fact]
    public void ResetAgentLoopMemory_also_clears_active_and_handshakes()
    {
        var (bot, _) = NewBot();
        bot.Tell(new SetAgentLoopCallbacks(_ => { }, _ => { }));
        bot.Tell(BindingsNoLlm());
        bot.Tell(new MarkConversationActive("Claude"));
        bot.Tell(new MarkConversationActive("Codex"));
        bot.Tell(new MarkHandshakeSent("Claude"));

        bot.Tell(new ResetAgentLoopMemory());
        ExpectNoMsg(TimeSpan.FromMilliseconds(150));

        bot.Tell(new QueryActiveConversations(), TestActor);
        var convReply = ExpectMsg<ActiveConversationsReply>(TimeSpan.FromSeconds(2));
        Assert.Empty(convReply.Active);

        bot.Tell(new QueryHandshakeState("Claude"), TestActor);
        var hsReply = ExpectMsg<HandshakeStateReply>(TimeSpan.FromSeconds(2));
        Assert.Equal(HandshakeState.NotConnected, hsReply.State);
    }

    // ── Handshake state machine ──

    [Fact]
    public void Handshake_state_starts_as_NotConnected()
    {
        var (bot, _) = NewBot();
        bot.Tell(new QueryHandshakeState("Claude"), TestActor);
        var reply = ExpectMsg<HandshakeStateReply>(TimeSpan.FromSeconds(2));
        Assert.Equal(HandshakeState.NotConnected, reply.State);
        Assert.Equal("Claude", reply.PeerName);
    }

    [Fact]
    public void MarkHandshakeSent_transitions_to_HandshakeSent()
    {
        var (bot, _) = NewBot();
        bot.Tell(new MarkHandshakeSent("Claude"));

        bot.Tell(new QueryHandshakeState("Claude"), TestActor);
        var reply = ExpectMsg<HandshakeStateReply>(TimeSpan.FromSeconds(2));
        Assert.Equal(HandshakeState.HandshakeSent, reply.State);
    }

    [Fact]
    public void TerminalSentToBot_promotes_to_Connected_even_if_inactive()
    {
        var (bot, _) = NewBot();
        bot.Tell(new SetAgentLoopCallbacks(_ => { }, _ => { }));
        bot.Tell(BindingsNoLlm());
        bot.Tell(new MarkHandshakeSent("Claude"));

        // Even though peer isn't in active conversation list, the
        // handshake state should still flip — the peer DID prove the
        // reverse channel works.
        bot.Tell(new TerminalSentToBot("Claude", "DONE(handshake-ok)"));
        ExpectNoMsg(TimeSpan.FromMilliseconds(150));

        bot.Tell(new QueryHandshakeState("Claude"), TestActor);
        var reply = ExpectMsg<HandshakeStateReply>(TimeSpan.FromSeconds(2));
        Assert.Equal(HandshakeState.Connected, reply.State);
    }

    private sealed class StubHost : IAgentToolbelt
    {
        public Task<string> ListTerminalsAsync(CancellationToken ct = default)
            => Task.FromResult("[]");
        public Task<string> ReadTerminalAsync(int g, int t, int n, CancellationToken ct = default)
            => Task.FromResult("");
        public Task<bool> SendToTerminalAsync(int g, int t, string text, CancellationToken ct = default)
            => Task.FromResult(true);
        public Task<bool> SendKeyAsync(int g, int t, string key, CancellationToken ct = default)
            => Task.FromResult(true);
    }
}
