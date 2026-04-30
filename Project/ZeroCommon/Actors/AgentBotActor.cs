// ───────────────────────────────────────────────────────────
// AgentBotActor — 사용자 제어 컨트롤러 (1개 인스턴스)
//
// 역할:
//   1. 2가지 모드 전환 (CHT/KEY) via Become()
//   2. UI 콜백 게이트웨이 (Sender → UI 표시)
//
// 경로: /user/stage/bot
// ───────────────────────────────────────────────────────────

using Akka.Actor;
using Akka.Event;

namespace Agent.Common.Actors;

public sealed class AgentBotActor : ReceiveActor
{
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private readonly IActorRef _stage;

    private BotMode _currentMode = BotMode.Chat;
    private Action<string, BotResponseType>? _uiCallback;

    // AIMODE first-contact tracking. Per the Akka model, state belongs in the
    // actor that owns the concern — AgentBotActor is the singleton that
    // represents "the agent talking to terminals", so it tracks who has been
    // greeted. WorkspaceTerminalToolHost asks via IntroduceTerminalIfFirst
    // before sending; the reply tells it whether to prepend a self-intro.
    private readonly HashSet<(int g, int t)> _introducedTerminals = new();

    // ── AIMODE Reactor child + UI delegate gateway ──
    // The actual inference loop now lives in AgentReactorActor at
    // /user/stage/bot/reactor. Bot is the gateway: it owns the registered
    // UI callbacks (which marshal to the dispatcher) and forwards
    // user-initiated messages (Start/Cancel/Reset) to the child.
    private IActorRef? _reactor;
    private ReactorBindings? _reactorBindings;
    private Action<ReactorProgress>? _reactorOnProgress;
    private Action<ReactorResult>? _reactorOnResult;

    // ── Peer-signal routing state ──
    // Per-peer (string name, e.g. "Claude") tracking. The string IS the
    // contract: when peer calls `bot-chat ... --from Claude` the
    // PeerName field is "Claude" and lookups here key on that.
    //   - _activeConversations: peer is in an active relay session.
    //   - _handshakeStates    : NotConnected / HandshakeSent / Connected.
    // When a TerminalSentToBot arrives for an ACTIVE peer, Bot
    // synthesises a continuation StartReactor with the incoming text as
    // the "user prompt". For INACTIVE peers the signal is logged and
    // dropped — peer signals from terminals the user never asked us to
    // talk to are noise, not an invitation.
    private readonly HashSet<string> _activeConversations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HandshakeState> _handshakeStates = new(StringComparer.OrdinalIgnoreCase);

    public AgentBotActor(IActorRef stage)
    {
        _stage = stage;
        _stage.Tell(new RegisterBot());
        BecomeChat();
    }

    private void CommonHandlers()
    {
        Receive<Ping>(_ => Sender.Tell(new Pong("Bot", Self.Path.ToString(),
            $"Mode={_currentMode} introduced={_introducedTerminals.Count}")));

        Receive<SetBotUiCallback>(msg =>
        {
            _uiCallback = msg.Callback;
            _log.Info("Bot UI callback registered");
        });

        Receive<SwitchBotMode>(msg =>
        {
            _currentMode = msg.TargetMode;
            switch (msg.TargetMode)
            {
                case BotMode.Chat: BecomeChat(); break;
                case BotMode.Key:  BecomeKey();  break;
            }
            _log.Info("Bot mode switched to: {0}", msg.TargetMode);
        });

        Receive<QueryTerminalStatus>(_ =>
            _stage.Forward(new QueryStageStatus()));

        // AIMODE introduction state — atomic mark-and-tell.
        Receive<IntroduceTerminalIfFirst>(msg =>
        {
            var key = (msg.GroupIndex, msg.TabIndex);
            var first = _introducedTerminals.Add(key);
            if (first)
                _log.Info("AIMODE first contact with terminal [{0}:{1}]",
                    msg.GroupIndex, msg.TabIndex);
            Sender.Tell(new IntroduceTerminalReply(WasFirstContact: first));
        });

        Receive<ResetIntroductions>(_ =>
        {
            var n = _introducedTerminals.Count;
            _introducedTerminals.Clear();
            _log.Info("AIMODE introductions cleared (was tracking {0})", n);
        });

        // ── Reactor wiring ──
        Receive<SetReactorCallbacks>(msg =>
        {
            _reactorOnProgress = msg.OnProgress;
            _reactorOnResult = msg.OnResult;
            _log.Info("[Bot] Reactor callbacks registered");
        });

        Receive<ReactorBindings>(msg =>
        {
            _reactorBindings = msg;
            _log.Info("[Bot] Reactor bindings registered");
        });

        Receive<StartReactor>(msg =>
        {
            if (_reactorBindings is null)
            {
                _log.Warning("[Bot] StartReactor before ReactorBindings — dropping");
                _reactorOnResult?.Invoke(new ReactorResult(false,
                    "AI Mode not wired (host bindings missing).", 0, 0,
                    FailureReason: "no bindings"));
                return;
            }
            EnsureReactorChild();
            _reactor!.Tell(msg);
        });

        Receive<CancelReactor>(_ => _reactor?.Tell(new CancelReactor()));

        Receive<ResetReactorSession>(_ =>
        {
            // Tell the reactor to drop its loop + ask it to stop so the next
            // EnsureReactorChild rebuilds. Also clear introductions so the
            // peer terminals get re-introduced in the new session.
            if (_reactor is not null)
            {
                _reactor.Tell(new ResetReactorSession());
                Context.Stop(_reactor);
                _reactor = null;
            }
            var nIntro = _introducedTerminals.Count;
            _introducedTerminals.Clear();
            var nConv = _activeConversations.Count;
            _activeConversations.Clear();
            var nHand = _handshakeStates.Count;
            _handshakeStates.Clear();
            _log.Info("[Bot] Reactor session reset (intros={0}, conversations={1}, handshakes={2})",
                nIntro, nConv, nHand);
        });

        // ── Peer-signal routing ──
        Receive<MarkConversationActive>(msg =>
        {
            if (_activeConversations.Add(msg.PeerName))
                _log.Info("[Bot] Conversation ACTIVE for peer=\"{0}\" (total active={1})",
                    msg.PeerName, _activeConversations.Count);
        });

        Receive<ClearConversationActive>(msg =>
        {
            if (_activeConversations.Remove(msg.PeerName))
                _log.Info("[Bot] Conversation CLEARED for peer=\"{0}\" (remaining active={1})",
                    msg.PeerName, _activeConversations.Count);
        });

        Receive<QueryActiveConversations>(_ =>
        {
            Sender.Tell(new ActiveConversationsReply(_activeConversations));
        });

        Receive<MarkHandshakeSent>(msg =>
        {
            _handshakeStates[msg.PeerName] = HandshakeState.HandshakeSent;
            _log.Info("[Bot] Handshake SENT to peer=\"{0}\" (awaiting bot-chat callback)", msg.PeerName);
        });

        Receive<QueryHandshakeState>(msg =>
        {
            var state = _handshakeStates.TryGetValue(msg.PeerName, out var s)
                ? s : HandshakeState.NotConnected;
            Sender.Tell(new HandshakeStateReply(msg.PeerName, state));
        });

        Receive<TerminalSentToBot>(msg =>
        {
            // Any incoming bot-chat from a peer marks them as Connected —
            // they proved the reverse channel works.
            var prevState = _handshakeStates.TryGetValue(msg.PeerName, out var s)
                ? s : HandshakeState.NotConnected;
            if (prevState != HandshakeState.Connected)
            {
                _handshakeStates[msg.PeerName] = HandshakeState.Connected;
                _log.Info("[Bot] Handshake CONNECTED for peer=\"{0}\" (was {1})",
                    msg.PeerName, prevState);
            }

            if (!_activeConversations.Contains(msg.PeerName))
            {
                // Inactive — peer signals are not invitations to start a
                // conversation. Just log so it's debuggable.
                _log.Info("[Bot] Peer signal from INACTIVE peer=\"{0}\" dropped: \"{1}\"",
                    msg.PeerName,
                    msg.Text.Length > 80 ? msg.Text.Substring(0, 80) + "..." : msg.Text);
                return;
            }
            // Active — wake the reactor with this incoming as the user
            // prompt context. Continuation: KV cache from previous cycles
            // gives the model the prior exchange history; the prompt
            // already teaches "one cycle per run" so this becomes one
            // short reaction.
            _log.Info("[Bot] Peer signal from ACTIVE peer=\"{0}\" → continuation StartReactor",
                msg.PeerName);
            if (_reactorBindings is null)
            {
                _log.Warning("[Bot] Cannot route peer signal — bindings not registered");
                return;
            }
            EnsureReactorChild();
            // Frame the synthetic prompt so the model knows this is a
            // peer-signal continuation, not a fresh user request.
            var synth = $"[peer signal from {msg.PeerName} via bot-chat] {msg.Text}";
            _reactor!.Tell(new StartReactor(synth));
        });

        // Progress + Result messages bubble UP from the child to here.
        Receive<ReactorProgress>(msg =>
        {
            try { _reactorOnProgress?.Invoke(msg); }
            catch (Exception ex) { _log.Warning("[Bot] OnProgress callback threw: {0}", ex.Message); }
        });

        Receive<ReactorResult>(msg =>
        {
            try { _reactorOnResult?.Invoke(msg); }
            catch (Exception ex) { _log.Warning("[Bot] OnResult callback threw: {0}", ex.Message); }
        });
    }

    private void EnsureReactorChild()
    {
        if (_reactor is not null) return;
        _reactor = Context.ActorOf(
            Props.Create(() => new AgentReactorActor(_reactorBindings!)),
            "reactor");
        _log.Info("[Bot] Reactor child created at {0}", _reactor.Path);
    }

    private void BecomeChat()
    {
        Become(() =>
        {
            CommonHandlers();
            Receive<UserInput>(msg =>
                _log.Info("[CHT] User input → Terminal: {0}", msg.Text));
        });
    }

    private void BecomeKey()
    {
        Become(() =>
        {
            CommonHandlers();
            Receive<UserInput>(msg =>
                _log.Info("[KEY] Key input → Terminal: {0}", msg.Text));
        });
    }

    protected override void PostStop()
    {
        _stage.Tell(new UnregisterBot());
        base.PostStop();
    }
}
