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

    // ── AIMODE AgentLoop child + UI delegate gateway ──
    // The actual inference loop now lives in AgentLoopActor at
    // /user/stage/bot/loop. Bot is the gateway: it owns the registered
    // UI callbacks (which marshal to the dispatcher) and forwards
    // user-initiated messages (Start/Cancel/Reset) to the child.
    private IActorRef? _agentLoop;
    private AgentLoopBindings? _agentLoopBindings;
    private Action<AgentLoopProgress>? _agentLoopOnProgress;
    private Action<AgentLoopResult>? _agentLoopOnResult;

    // ── Peer-signal routing state ──
    // Per-peer (string name, e.g. "Claude") tracking. The string IS the
    // contract: when peer calls `bot-chat ... --from Claude` the
    // PeerName field is "Claude" and lookups here key on that.
    //   - _activeConversations: peer is in an active relay session.
    //   - _handshakeStates    : NotConnected / HandshakeSent / Connected.
    // When a TerminalSentToBot arrives for an ACTIVE peer, Bot
    // synthesises a continuation StartAgentLoop with the incoming text as
    // the "user prompt". For INACTIVE peers the signal is logged and
    // dropped — peer signals from terminals the user never asked us to
    // talk to are noise, not an invitation.
    private readonly HashSet<string> _activeConversations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HandshakeState> _handshakeStates = new(StringComparer.OrdinalIgnoreCase);

    // ── Delegation mode (M0017 — voice mode for visually impaired users) ──
    // When true, every incoming StartAgentLoop has its UserRequest rewritten
    // to force Mode 2 (terminal relay) targeting a "Claude" terminal. The
    // user's actual phrase is appended verbatim so the LLM has the payload
    // it needs to forward. Toggling off restores normal Mode 1/2 mixed
    // behaviour; the toggle is paired with ResetAgentLoopMemory on the UI
    // side so the new framing applies to a fresh KV cache.
    private bool _delegationMode;

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

        // M0017 후속 #1: pre-mark a terminal as already introduced. Used by
        // delegation mode entry to suppress WorkspaceTerminalToolHost's
        // auto-prepended first-contact handshake header for the Claude tab.
        Receive<MarkTerminalIntroduced>(msg =>
        {
            var key = (msg.GroupIndex, msg.TabIndex);
            if (_introducedTerminals.Add(key))
                _log.Info("AIMODE terminal pre-marked introduced [{0}:{1}] (delegation suppress)",
                    msg.GroupIndex, msg.TabIndex);
        });

        // ── AgentLoop wiring ──
        Receive<SetAgentLoopCallbacks>(msg =>
        {
            _agentLoopOnProgress = msg.OnProgress;
            _agentLoopOnResult = msg.OnResult;
            _log.Info("[Bot] AgentLoop callbacks registered");
        });

        Receive<AgentLoopBindings>(msg =>
        {
            _agentLoopBindings = msg;
            _log.Info("[Bot] AgentLoop bindings registered");
        });

        Receive<StartAgentLoop>(msg =>
        {
            if (_agentLoopBindings is null)
            {
                _log.Warning("[Bot] StartAgentLoop before AgentLoopBindings — dropping");
                _agentLoopOnResult?.Invoke(new AgentLoopResult(false,
                    "AI Mode not wired (host bindings missing).", 0, 0,
                    FailureReason: "no bindings"));
                return;
            }
            EnsureAgentLoopChild();

            // Delegation mode (M0017): rewrap the user request with a strong
            // Mode 2 directive so the LLM ALWAYS relays to a Claude terminal
            // rather than falling back to Mode 1 direct answer. The rewrite
            // is one-way — the LLM sees only the wrapped prompt; the original
            // text is preserved inside the wrap so the agent can forward it
            // verbatim via send_to_terminal.
            var outgoing = _delegationMode
                ? new StartAgentLoop(WrapForDelegation(msg.UserRequest))
                : msg;
            _agentLoop!.Tell(outgoing);
        });

        Receive<CancelAgentLoop>(_ => _agentLoop?.Tell(new CancelAgentLoop()));

        // ── Delegation mode toggle (M0017) ──
        Receive<SetDelegationMode>(msg =>
        {
            if (_delegationMode == msg.Enabled) return;
            _delegationMode = msg.Enabled;
            _log.Info("[Bot] Delegation mode → {0}", _delegationMode ? "ON" : "OFF");
        });

        Receive<QueryDelegationMode>(_ =>
            Sender.Tell(new DelegationModeReply(_delegationMode)));

        Receive<ResetAgentLoopMemory>(_ =>
        {
            // Tell the agent loop to drop itself + ask it to stop so the next
            // EnsureAgentLoopChild rebuilds. Also clear introductions so the
            // peer terminals get re-introduced in the new session.
            if (_agentLoop is not null)
            {
                _agentLoop.Tell(new ResetAgentLoopMemory());
                Context.Stop(_agentLoop);
                _agentLoop = null;
            }
            var nIntro = _introducedTerminals.Count;
            _introducedTerminals.Clear();
            var nConv = _activeConversations.Count;
            _activeConversations.Clear();
            var nHand = _handshakeStates.Count;
            _handshakeStates.Clear();
            _log.Info("[Bot] AgentLoop session reset (intros={0}, conversations={1}, handshakes={2})",
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
            Sender.Tell(new ActiveConversationsReply(_activeConversations.ToList()));
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

            // M0017 후속 #5 + #10 — speak-everything shortcut for delegation mode.
            //
            // The handshake (BuildDelegationHandshakeMessage) teaches Claude
            // to reply via `bot-chat "DONE(text)" --from <peer>` so we have a
            // canonical envelope. BUT the IPC layer (MainWindow.HandleBotChat)
            // pre-unwraps the DONE(...) for its UI display path and forwards
            // ONLY the inner text to this actor — so the wrapper-matching
            // shortcut from 후속 #5 stopped firing in production (operator-
            // observed: "ChatBot UI 창에서는 DONE 찍히는데 음성 출력 안됨").
            //
            // 후속 #10 fix: accept BOTH shapes — try the wrapper, fall back
            // to raw inner text. Either way we route through OnResult so the
            // UI's AddBotMessage → SpeakBotMessageIfEnabled path speaks it.
            // The agent loop is bypassed entirely (followup #9 already did
            // this on the send side; this closes the receive side).
            //
            // Handshake-style acks (ready / handshake-ok / ok / ack) are
            // still filtered — voice user doesn't need to hear "ready".
            // Empty payloads after filtering also fall through to the legacy
            // continuation path so the model can still react if Claude sent
            // something genuinely empty / malformed.
            if (_delegationMode)
            {
                var spoken = (TryExtractDoneContent(msg.Text) ?? msg.Text ?? string.Empty).Trim();
                if (spoken.Length > 0)
                {
                    if (IsHandshakeAck(spoken))
                    {
                        _log.Info("[Bot] Delegation reply ignored — handshake ack: \"{0}\"", spoken);
                        return;
                    }
                    _log.Info("[Bot] Delegation reply → speak ({0} chars)", spoken.Length);
                    try
                    {
                        _agentLoopOnResult?.Invoke(new AgentLoopResult(
                            Success: true,
                            FinalMessage: spoken,
                            TurnCount: 0,
                            ElapsedMs: 0));
                    }
                    catch (Exception ex)
                    {
                        _log.Warning("[Bot] Delegation reply callback threw: {0}", ex.Message);
                    }
                    return;
                }
                // Empty payload — fall through to legacy continuation. Unusual
                // case; lets the agent loop emit a sensible failure message.
            }

            // Active — wake the agent loop with this incoming as the user
            // prompt context. Continuation: KV cache from previous cycles
            // gives the model the prior exchange history; the prompt
            // already teaches "one cycle per run" so this becomes one
            // short reaction.
            _log.Info("[Bot] Peer signal from ACTIVE peer=\"{0}\" → continuation StartAgentLoop",
                msg.PeerName);
            if (_agentLoopBindings is null)
            {
                _log.Warning("[Bot] Cannot route peer signal — bindings not registered");
                return;
            }
            EnsureAgentLoopChild();
            // Frame the synthetic prompt so the model knows this is a
            // peer-signal continuation, not a fresh user request.
            var synth = $"[peer signal from {msg.PeerName} via bot-chat] {msg.Text}";
            _agentLoop!.Tell(new StartAgentLoop(synth));
        });

        // Progress + Result messages bubble UP from the child to here.
        Receive<AgentLoopProgress>(msg =>
        {
            try { _agentLoopOnProgress?.Invoke(msg); }
            catch (Exception ex) { _log.Warning("[Bot] OnProgress callback threw: {0}", ex.Message); }
        });

        Receive<AgentLoopResult>(msg =>
        {
            try { _agentLoopOnResult?.Invoke(msg); }
            catch (Exception ex) { _log.Warning("[Bot] OnResult callback threw: {0}", ex.Message); }
        });
    }

    private void EnsureAgentLoopChild()
    {
        if (_agentLoop is not null) return;
        _agentLoop = Context.ActorOf(
            Props.Create(() => new AgentLoopActor(_agentLoopBindings!)),
            "loop");
        _log.Info("[Bot] AgentLoop child created at {0}", _agentLoop.Path);
    }

    // M0017 후속 #5 — DONE(...) message parsing for the delegation TTS shortcut.
    //
    // The reverse channel contract (taught to Claude in the delegation
    // handshake — see AgentBotWindow.Voice.cs::BuildDelegationHandshakeMessage)
    // wraps every reply in DONE(...) so we have a deterministic envelope to
    // unwrap. Body may contain any chars including parens; we use the last ')'
    // as the close so legitimate nested parens inside content survive.
    // Multi-line content is fine — TTS reads through newlines.

    /// <summary>
    /// Return the inner payload of a <c>DONE(...)</c> wrapper, or null when
    /// the input doesn't match the envelope. Match is case-insensitive on
    /// the "DONE" prefix; surrounding whitespace is trimmed.
    /// </summary>
    internal static string? TryExtractDoneContent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("DONE(", StringComparison.OrdinalIgnoreCase))
            return null;
        var open = trimmed.IndexOf('(');
        if (open < 0) return null;
        var lastClose = trimmed.LastIndexOf(')');
        if (lastClose <= open + 1) return null;
        var inner = trimmed.Substring(open + 1, lastClose - open - 1).Trim();
        return inner.Length > 0 ? inner : null;
    }

    /// <summary>
    /// True when the DONE content is a handshake-protocol acknowledgement
    /// rather than user-facing reply text. We skip TTS for these — the
    /// operator doesn't need to hear "ready" / "handshake-ok" out loud.
    /// </summary>
    internal static bool IsHandshakeAck(string inner)
    {
        if (string.IsNullOrWhiteSpace(inner)) return false;
        var t = inner.Trim();
        return string.Equals(t, "ready",         StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "handshake-ok",  StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "ack",           StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "ok",            StringComparison.OrdinalIgnoreCase);
    }

    // M0017 — produce the system-directive wrapper for delegation mode.
    // The block in front of the user phrase forces Mode 2 by spelling out
    // the tool sequence verbatim. Even small local models (Gemma 4) reliably
    // follow this when paired with the existing AgentToolGrammar GBNF —
    // they can't emit anything but one of the 5 known tool calls per turn,
    // so the constraints push them into send_to_terminal → wait → read →
    // done. Reading the Claude terminal's "thinking" indicator is handled
    // by the same retry loop documented in AgentToolGrammar.SystemPrompt.
    internal static string WrapForDelegation(string original)
    {
        // Tight directive — every line earns its space. The verbose version
        // shipped in v1 of M0017 was 600+ chars and contributed to External
        // backends (Webnori/Gemma 4e4b) emitting malformed envelopes on later
        // turns because the prompt+history budget started crowding out the
        // tool-call JSON. M0017 후속 #2 trims to ~340 chars.
        return
            "[DELEGATION MODE]\n" +
            "사용자 음성 요청을 Claude 터미널로 위임. 직접 답하지 말 것.\n" +
            "절차: list_terminals → title 에 Claude 포함된 첫 탭 → send_to_terminal(text=원문) → wait(10) → read_terminal(last_n=4000).\n" +
            "응답 판정: buffer 가 thinking 지표(✻ ✶ Crafting Working …)만 또는 내 발신 echo 만이면 응답 미완 — wait(5)+read 최대 4회.\n" +
            "done: Claude 새 응답 라인을 한국어 1~2문장 요약. Claude 탭이 없으면 done(\"Claude 탭을 먼저 열어 주세요\"). 4회 후에도 응답 없으면 done(\"Claude 응답이 지연됩니다\").\n" +
            "금지: Mode 1 직답, 동일 텍스트 2회 send_to_terminal, read 없이 send 2연속, echo 만 보고 done.\n\n" +
            $"사용자 요청 원문: {original}";
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
