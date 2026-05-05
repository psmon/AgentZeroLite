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
