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
