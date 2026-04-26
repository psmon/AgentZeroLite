// ───────────────────────────────────────────────────────────
// AgentReactorActor + AgentBotActor wiring tests (TestKit, headless)
//
// Scope: message protocol + lifecycle + delegate wiring.
//        NOT covering the inner LLM tool loop (those live tests gate on a
//        model file and are kept in AgentToolLoopTests / NemotronProbeTests).
//
// Trick: ReactorBindings.ToolLoopFactory returns null in these tests so the
//        actor hits the early-exit path that emits a ReactorResult with
//        FailureReason="Backend not ready". That exercises message routing
//        without needing LLamaSharp + a 4 GB model file in CI.
// ───────────────────────────────────────────────────────────

using Akka.Actor;
using Akka.TestKit.Xunit2;
using Agent.Common.Llm.Tools;

namespace ZeroCommon.Tests;

public sealed class AgentReactorActorTests : TestKit
{
    private static ReactorBindings BindingsWithNoLlm() => new(
        HostFactory: () => new StubHost(),
        OptionsFactory: () => new AgentToolLoopOptions(),
        ToolLoopFactory: (_, _) => null);

    [Fact]
    public void StartReactor_with_no_backend_emits_ReactorResult_failure_to_parent()
    {
        var probe = CreateTestProbe("parent-probe");
        // Use the probe as parent so we can assert on what TellParent sends.
        // ActorOf with a parent context isn't directly exposable, so instead
        // we ChildActorOf-style: spawn the reactor under the probe.
        var reactor = probe.ChildActorOf(
            Props.Create(() => new AgentReactorActor(BindingsWithNoLlm())));

        reactor.Tell(new StartReactor("hi"), TestActor);

        var result = probe.ExpectMsg<ReactorResult>(TimeSpan.FromSeconds(2));
        Assert.False(result.Success);
        Assert.Equal(0, result.TurnCount);
        Assert.Equal("Backend not ready", result.FailureReason);
        Assert.Contains("AI Mode", result.FinalMessage);
    }

    [Fact]
    public void Ping_in_Idle_replies_Pong_with_path()
    {
        var reactor = Sys.ActorOf(
            Props.Create(() => new AgentReactorActor(BindingsWithNoLlm())),
            "reactor-ping");

        reactor.Tell(new Ping(), TestActor);

        var pong = ExpectMsg<Pong>(TimeSpan.FromSeconds(2));
        Assert.Equal("Reactor", pong.ActorName);
        Assert.Contains("reactor-ping", pong.ActorPath);
        Assert.Contains("Idle", pong.Status);
    }

    [Fact]
    public void Cancel_and_Reset_in_Idle_are_safe_noops()
    {
        var reactor = Sys.ActorOf(
            Props.Create(() => new AgentReactorActor(BindingsWithNoLlm())),
            "reactor-noop");

        // Should not throw, should not produce messages.
        reactor.Tell(new CancelReactor(), TestActor);
        reactor.Tell(new ResetReactorSession(), TestActor);
        ExpectNoMsg(TimeSpan.FromMilliseconds(200));

        // And still responds to Ping → still Idle.
        reactor.Tell(new Ping(), TestActor);
        var pong = ExpectMsg<Pong>(TimeSpan.FromSeconds(2));
        Assert.Contains("Idle", pong.Status);
    }

    private sealed class StubHost : IAgentToolHost
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
/// AgentBotActor wiring for the reactor: SetReactorCallbacks remembers the
/// delegates, ReactorBindings is required before StartReactor, ReactorProgress
/// + ReactorResult bubble through to delegates, ResetReactorSession kills the
/// child + clears introductions.
/// </summary>
public sealed class AgentBotActorReactorWiringTests : TestKit
{
    private static ReactorBindings BindingsNoLlm() => new(
        HostFactory: () => new StubHost(),
        OptionsFactory: () => new AgentToolLoopOptions(),
        ToolLoopFactory: (_, _) => null);

    private (IActorRef bot, IActorRef stage) NewBot()
    {
        var stage = CreateTestProbe("stage").Ref;
        var bot = Sys.ActorOf(Props.Create(() => new AgentBotActor(stage)), "bot");
        return (bot, stage);
    }

    [Fact]
    public void StartReactor_without_bindings_dispatches_friendly_failure_via_callback()
    {
        var (bot, _) = NewBot();
        var resultProbe = CreateTestProbe("result");
        // We emulate the dispatcher delegate by Tell-ing into a probe.
        bot.Tell(new SetReactorCallbacks(
            OnProgress: _ => { },
            OnResult: r => resultProbe.Ref.Tell(r)));

        bot.Tell(new StartReactor("hi"));

        var result = resultProbe.ExpectMsg<ReactorResult>(TimeSpan.FromSeconds(2));
        Assert.False(result.Success);
        Assert.Equal("no bindings", result.FailureReason);
    }

    [Fact]
    public void StartReactor_with_bindings_creates_child_and_routes_failure_back()
    {
        var (bot, _) = NewBot();
        var resultProbe = CreateTestProbe("result");
        bot.Tell(new SetReactorCallbacks(
            OnProgress: _ => { },
            OnResult: r => resultProbe.Ref.Tell(r)));
        bot.Tell(BindingsNoLlm());

        bot.Tell(new StartReactor("anything"));

        // The child reactor short-circuits when the factory returns null and
        // bubbles the failure result back through the bot → our probe.
        var result = resultProbe.ExpectMsg<ReactorResult>(TimeSpan.FromSeconds(2));
        Assert.False(result.Success);
        Assert.Equal("Backend not ready", result.FailureReason);
    }

    [Fact]
    public void ResetReactorSession_clears_introductions_and_kills_child()
    {
        var (bot, _) = NewBot();
        bot.Tell(new SetReactorCallbacks(_ => { }, _ => { }));
        bot.Tell(BindingsNoLlm());
        // Build child via a Start that we don't await.
        bot.Tell(new StartReactor("trigger child create"));
        // Mark some terminals introduced first.
        bot.Tell(new IntroduceTerminalIfFirst(0, 0), TestActor);
        ExpectMsg<IntroduceTerminalReply>();
        bot.Tell(new IntroduceTerminalIfFirst(1, 2), TestActor);
        ExpectMsg<IntroduceTerminalReply>();

        // Reset.
        bot.Tell(new ResetReactorSession());
        // Give the bot mailbox a beat to process the reset before next assertion.
        ExpectNoMsg(TimeSpan.FromMilliseconds(150));

        // After reset, IntroduceTerminalIfFirst(0, 0) should report first contact again.
        bot.Tell(new IntroduceTerminalIfFirst(0, 0), TestActor);
        var reply = ExpectMsg<IntroduceTerminalReply>();
        Assert.True(reply.WasFirstContact);
    }

    [Fact]
    public void Bot_Ping_works_after_reactor_wiring()
    {
        var (bot, _) = NewBot();
        bot.Tell(new SetReactorCallbacks(_ => { }, _ => { }));
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
    public void TerminalSentToBot_for_inactive_peer_does_not_trigger_reactor()
    {
        var (bot, _) = NewBot();
        var resultProbe = CreateTestProbe("result");
        bot.Tell(new SetReactorCallbacks(_ => { }, r => resultProbe.Ref.Tell(r)));
        bot.Tell(BindingsNoLlm());

        // No MarkConversationActive — peer "Claude" is inactive.
        bot.Tell(new TerminalSentToBot("Claude", "hello bot"));

        // Reactor should NOT be invoked (no Result message back).
        resultProbe.ExpectNoMsg(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void TerminalSentToBot_for_active_peer_routes_to_reactor()
    {
        var (bot, _) = NewBot();
        var resultProbe = CreateTestProbe("result");
        bot.Tell(new SetReactorCallbacks(_ => { }, r => resultProbe.Ref.Tell(r)));
        bot.Tell(BindingsNoLlm());

        bot.Tell(new MarkConversationActive("Claude"));
        bot.Tell(new TerminalSentToBot("Claude", "Claude responded with hi"));

        // Reactor wakes (factory returns null → friendly failure result), proving routing reached it.
        var result = resultProbe.ExpectMsg<ReactorResult>(TimeSpan.FromSeconds(2));
        Assert.False(result.Success);
        Assert.Equal("Backend not ready", result.FailureReason);
    }

    [Fact]
    public void ResetReactorSession_also_clears_active_and_handshakes()
    {
        var (bot, _) = NewBot();
        bot.Tell(new SetReactorCallbacks(_ => { }, _ => { }));
        bot.Tell(BindingsNoLlm());
        bot.Tell(new MarkConversationActive("Claude"));
        bot.Tell(new MarkConversationActive("Codex"));
        bot.Tell(new MarkHandshakeSent("Claude"));

        bot.Tell(new ResetReactorSession());
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
        bot.Tell(new SetReactorCallbacks(_ => { }, _ => { }));
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

    private sealed class StubHost : IAgentToolHost
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
