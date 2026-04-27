// ───────────────────────────────────────────────────────────
// TerminalActor — 개별 터미널 세션 래퍼
//
// 역할:
//   1. 터미널 세션(ITerminalSession) 래핑
//   2. 단순 텍스트/키 입출력
//   3. 출력 이벤트를 구독자(Stage)에 발행
//
// 경로: /user/stage/ws-{workspace}/term-{id}
// ───────────────────────────────────────────────────────────

using Akka.Actor;
using Akka.Event;
using Agent.Common.Services;

namespace Agent.Common.Actors;

public sealed class TerminalActor : ReceiveActor
{
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private string _terminalId;
    private readonly string _sessionId;
    private readonly string _workspaceName;

    private ITerminalSession? _session;
    private nint _hwnd;

    public TerminalActor(string terminalId, string sessionId, string workspaceName)
    {
        _terminalId = terminalId;
        _sessionId = sessionId;
        _workspaceName = workspaceName;

        BuildHandlers();
    }

    private void BuildHandlers()
    {
        Receive<Ping>(_ => Sender.Tell(new Pong("Terminal", Self.Path.ToString(),
            $"Id={_terminalId}")));

        Receive<BindSession>(msg =>
        {
            if (ReferenceEquals(_session, msg.Session))
            {
                // PTY-FREEZE-DIAG: same-ref rebind is normal (Loaded fires
                // twice on tab reparenting). Logged for completeness so a
                // log full of these doesn't look like a missing handoff.
                _log.Info("BindSession no-op (same ref) | terminal={0} session={1}/{2}",
                    _terminalId, msg.Session.SessionId, msg.Session.InternalId);
                return;
            }

            // PTY-FREEZE-DIAG: when a tab is restarted, a *new* ConPtyTerminalSession
            // gets bound here while the previous one still exists in MainWindow's
            // `tab.Session` history. If write traffic is racing in flight when this
            // swap happens, it can land on either object. The log emits both
            // identifiers so a freeze trace can match write-side `id=...` lines
            // against the actor's view of which session is active.
            var prevSessionId = _session?.SessionId ?? "(none)";
            var prevInternalId = _session?.InternalId ?? "(none)";
            if (_session is not null)
                _session.OutputReceived -= OnSessionOutput;

            _session = msg.Session;
            _session.OutputReceived += OnSessionOutput;
            _log.Info("BindSession swap | terminal={0} prev={1}/{2} new={3}/{4}",
                _terminalId, prevSessionId, prevInternalId,
                _session.SessionId, _session.InternalId);
        });

        Receive<UnbindSession>(_ =>
        {
            if (_session is not null)
            {
                _session.OutputReceived -= OnSessionOutput;
                _session = null;
                _log.Info("Session unbound: {0}", _terminalId);
            }
        });

        Receive<UpdateTerminalHwnd>(msg =>
        {
            if (_hwnd == msg.Hwnd) return;
            _hwnd = msg.Hwnd;
            _log.Info("Terminal HWND updated: {0} → 0x{1:X}", _terminalId, _hwnd);
        });

        Receive<RenameTerminal>(msg =>
        {
            _log.Info("Terminal ID updated: {0} → {1}", _terminalId, msg.NewTerminalId);
            _terminalId = msg.NewTerminalId;
        });

        Receive<QueryTerminalStatus>(_ =>
        {
            Sender.Tell(new TerminalStatusResponse(
                _terminalId, _sessionId, _session?.IsRunning ?? false));
        });

        Receive<WriteToTerminal>(msg =>
        {
            if (_session is null) { _log.Warning("[CLI] Write: no session bound"); return; }
            _session.WriteAndSubmit(msg.Text);
        });

        Receive<SendTerminalControl>(msg =>
        {
            if (_session is null) return;
            if (Enum.TryParse<TerminalControl>(msg.ControlKey, true, out var ctrl))
                _session.SendControl(ctrl);
        });

        Receive<TerminalOutput>(_ => { /* no-op in Lite */ });
    }

    private void OnSessionOutput(TerminalOutputFrame frame)
    {
        Self.Tell(new TerminalOutput(_terminalId, frame.Text, frame.Timestamp));
    }

    protected override void PreStart()
    {
        _log.Info("TerminalActor started: {0} (session: {1}, workspace: {2})",
            _terminalId, _sessionId, _workspaceName);
        base.PreStart();
    }

    protected override void PostStop()
    {
        if (_session is not null)
        {
            _session.OutputReceived -= OnSessionOutput;
            _session = null;
        }
        _log.Info("TerminalActor stopped: {0}", _terminalId);
        base.PostStop();
    }
}
