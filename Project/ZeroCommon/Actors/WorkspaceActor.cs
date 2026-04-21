// ───────────────────────────────────────────────────────────
// WorkspaceActor — 워크스페이스별 터미널 그룹 관리자
//
// 역할:
//   1. 자식 TerminalActor 생성/종료 관리
//   2. 터미널 생명주기 추적 (생성/종료 시 Stage에 알림)
//   3. 워크스페이스 내 터미널 목록 조회
//
// 경로: /user/stage/ws-{workspaceName}
// ───────────────────────────────────────────────────────────

using System.IO;
using Akka.Actor;
using Akka.Event;

namespace Agent.Common.Actors;

public sealed class WorkspaceActor : ReceiveActor
{
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private readonly string _workspaceName;
    private readonly string _directoryPath;

    // 터미널 ID → IActorRef 매핑
    private readonly Dictionary<string, IActorRef> _terminals = new();

    public WorkspaceActor(string workspaceName, string directoryPath)
    {
        _workspaceName = workspaceName;
        _directoryPath = directoryPath;

        // ── HealthCheck ──
        Receive<Ping>(_ => Sender.Tell(new Pong("Workspace", Self.Path.ToString(),
            $"Name={_workspaceName}, Terminals={_terminals.Count}")));

        // ── 터미널 생명주기 ──
        Receive<CreateTerminal>(HandleCreateTerminal);
        Receive<DestroyTerminal>(HandleDestroyTerminal);
        Receive<RenameTerminal>(HandleRenameTerminal);

        // HWND 업데이트 → TerminalActor로 전달
        Receive<UpdateTerminalHwnd>(msg =>
        {
            if (_terminals.TryGetValue(msg.TerminalId, out var term))
                term.Tell(msg);
        });

        // ── 터미널 목록 조회 ──
        ReceiveAsync<QueryTerminals>(HandleQueryTerminalsAsync);

        // ── 세션 바인딩 — TerminalId로 자식 터미널 찾아서 Forward ──
        Receive<BindSessionInWorkspace>(msg =>
        {
            if (_terminals.TryGetValue(msg.TerminalId, out var term))
                term.Tell(new BindSession(msg.Session));
            else
                _log.Warning("BindSession: terminal not found: {0} in workspace {1}",
                    msg.TerminalId, _workspaceName);
        });

        // ── SendToTerminal — TerminalId로 자식 터미널 찾아서 WriteToTerminal Forward ──
        Receive<SendToTerminal>(msg =>
        {
            if (_terminals.TryGetValue(msg.TerminalId, out var term))
                term.Tell(new WriteToTerminal(msg.Text));
            else
                _log.Warning("SendToTerminal: terminal '{0}' not found in workspace '{1}'. Available: [{2}]",
                    msg.TerminalId, _workspaceName, string.Join(", ", _terminals.Keys));
        });

        // ── SendControlToTerminal — TerminalId로 자식 터미널 찾아서 Forward ──
        Receive<SendControlToTerminal>(msg =>
        {
            if (_terminals.TryGetValue(msg.TerminalId, out var term))
                term.Tell(new SendTerminalControl(msg.ControlKey));
            else
                _log.Warning("SendControlToTerminal: terminal '{0}' not found in workspace '{1}'",
                    msg.TerminalId, _workspaceName);
        });
    }

    // ── 감독 전략: 터미널 장애 시 예외별 분기 ──
    protected override SupervisorStrategy SupervisorStrategy()
    {
        return new OneForOneStrategy(
            maxNrOfRetries: 3,
            withinTimeRange: TimeSpan.FromMinutes(1),
            localOnlyDecider: ex => ex switch
            {
                ObjectDisposedException => Directive.Stop,  // 파이프 해제 — 재시작 불가
                IOException => Directive.Stop,               // I/O 실패
                _ => Directive.Restart
            });
    }

    // ═══════ Handlers ═══════

    private void HandleCreateTerminal(CreateTerminal msg)
    {
        if (_terminals.ContainsKey(msg.TerminalId))
        {
            _log.Warning("Terminal already exists: {0}", msg.TerminalId);
            return;
        }

        var child = Context.ActorOf(
            Props.Create(() => new TerminalActor(msg.TerminalId, msg.SessionId, _workspaceName)),
            $"term-{ActorNameSanitizer.Safe(msg.TerminalId)}");

        _terminals[msg.TerminalId] = child;

        // Stage에 등록 알림
        Context.Parent.Tell(new TerminalRegistered(_workspaceName, msg.TerminalId));

        _log.Info("Terminal created: {0} (session: {1})", msg.TerminalId, msg.SessionId);
    }

    private void HandleDestroyTerminal(DestroyTerminal msg)
    {
        if (_terminals.Remove(msg.TerminalId, out var actor))
        {
            Context.Stop(actor);
            Context.Parent.Tell(new TerminalUnregistered(_workspaceName, msg.TerminalId));
            _log.Info("Terminal destroyed: {0}", msg.TerminalId);
        }
    }

    private void HandleRenameTerminal(RenameTerminal msg)
    {
        if (_terminals.Remove(msg.OldTerminalId, out var actor))
        {
            _terminals[msg.NewTerminalId] = actor;
            // TerminalActor 내부 _terminalId도 동기화
            actor.Tell(msg);
            _log.Info("Terminal renamed: {0} → {1}", msg.OldTerminalId, msg.NewTerminalId);
        }
        else
        {
            _log.Warning("RenameTerminal: '{0}' not found in workspace '{1}'",
                msg.OldTerminalId, _workspaceName);
        }
    }

    private async Task HandleQueryTerminalsAsync(QueryTerminals msg)
    {
        var sender = Sender;
        var terminals = new List<TerminalInfo>();

        foreach (var (terminalId, termRef) in _terminals)
        {
            try
            {
                var status = await termRef.Ask<TerminalStatusResponse>(
                    new QueryTerminalStatus(), TimeSpan.FromSeconds(2));
                terminals.Add(new TerminalInfo(status.TerminalId, status.SessionId));
            }
            catch
            {
                terminals.Add(new TerminalInfo(terminalId, ""));
            }
        }

        sender.Tell(new TerminalsResponse(terminals));
    }
}
