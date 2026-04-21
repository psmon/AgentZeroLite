// ───────────────────────────────────────────────────────────
// StageActor — 최상위 감독자 + 메시지 브로커
//
// 역할:
//   1. Workspace/Bot 자식 액터 생명주기 관리
//   2. 전체 스테이지 상태 집계 (QueryStageStatus)
//   3. 감독 전략 (자식 장애 격리)
//
// 자식:
//   /user/stage/bot          — AgentBotActor (0~1개)
//   /user/stage/ws-{name}    — WorkspaceActor (N개)
// ───────────────────────────────────────────────────────────

using Akka.Actor;
using Akka.Event;
using Agent.Common;

namespace Agent.Common.Actors;

public sealed class StageActor : ReceiveActor
{
    private readonly ILoggingAdapter _log = Context.GetLogger();

    private readonly Dictionary<string, IActorRef> _workspaces = new();
    private IActorRef? _botActor;

    private string? _activeWorkspace;
    private string? _activeTerminalId;

    protected override void PreRestart(Exception reason, object message)
    {
        AppLogger.Log($"[Stage#{GetHashCode()}] PreRestart: reason={reason.GetType().Name}: {reason.Message}, msg={message?.GetType().Name}");
        base.PreRestart(reason, message);
    }

    protected override void PostStop()
    {
        AppLogger.Log($"[Stage#{GetHashCode()}] PostStop (workspaces were {_workspaces.Count})");
        base.PostStop();
    }

    public StageActor()
    {
        AppLogger.Log($"[Stage] ctor instance=#{GetHashCode()}");

        Receive<Ping>(_ => Sender.Tell(new Pong("Stage", Self.Path.ToString(),
            $"Workspaces={_workspaces.Count}, Bot={(_botActor is not null ? "On" : "Off")}")));

        Receive<RegisterWorkspace>(HandleRegisterWorkspace);
        Receive<UnregisterWorkspace>(HandleUnregisterWorkspace);

        Receive<RegisterBot>(HandleRegisterBot);
        Receive<UnregisterBot>(HandleUnregisterBot);

        ReceiveAsync<QueryStageStatus>(HandleQueryStageStatusAsync);
        Receive<QueryActiveTerminal>(HandleQueryActiveTerminal);

        Receive<SendToTerminal>(HandleSendToTerminal);
        Receive<SendControlToTerminal>(HandleSendControlToTerminal);

        Receive<CreateTerminalInWorkspace>(msg =>
        {
            if (_workspaces.TryGetValue(msg.WorkspaceName, out var ws))
                ws.Tell(new CreateTerminal(msg.TerminalId, msg.SessionId));
            else
                _log.Warning("CreateTerminalInWorkspace: workspace not found: {0}", msg.WorkspaceName);
        });

        Receive<DestroyTerminalInWorkspace>(msg =>
        {
            if (_workspaces.TryGetValue(msg.WorkspaceName, out var ws))
                ws.Tell(new DestroyTerminal(msg.TerminalId));
        });

        Receive<RenameTerminalInWorkspace>(msg =>
        {
            if (_workspaces.TryGetValue(msg.WorkspaceName, out var ws))
                ws.Tell(new RenameTerminal(msg.OldTerminalId, msg.NewTerminalId));
        });

        Receive<UpdateTerminalHwnd>(msg =>
        {
            if (_workspaces.TryGetValue(msg.WorkspaceName, out var ws))
                ws.Forward(msg);
        });

        Receive<SetActiveTerminal>(msg =>
        {
            _activeWorkspace = msg.WorkspaceName;
            _activeTerminalId = msg.TerminalId;
        });

        Receive<BindSessionInWorkspace>(msg =>
        {
            if (_workspaces.TryGetValue(msg.WorkspaceName, out var ws))
                ws.Forward(msg);
            else
                _log.Warning("BindSessionInWorkspace: workspace not found: {0}", msg.WorkspaceName);
        });

        Receive<CreateBot>(_ =>
        {
            if (_botActor is not null) { Sender.Tell(new BotCreated(_botActor)); return; }
            _botActor = Context.ActorOf(Props.Create(() => new AgentBotActor(Self)), "bot");
            Sender.Tell(new BotCreated(_botActor));
            _log.Info("Bot created via CreateBot: {0}", _botActor.Path);
        });

        Receive<TerminalRegistered>(msg =>
            _log.Info("Terminal registered: {0}/{1}", msg.WorkspaceName, msg.TerminalId));
        Receive<TerminalUnregistered>(msg =>
            _log.Info("Terminal unregistered: {0}/{1}", msg.WorkspaceName, msg.TerminalId));
    }

    protected override SupervisorStrategy SupervisorStrategy()
    {
        return new OneForOneStrategy(
            maxNrOfRetries: 5,
            withinTimeRange: TimeSpan.FromMinutes(1),
            localOnlyDecider: ex => ex switch
            {
                ArgumentException => Directive.Resume,
                NullReferenceException => Directive.Restart,
                _ => Directive.Escalate
            });
    }

    private void HandleRegisterWorkspace(RegisterWorkspace msg)
    {
        if (_workspaces.ContainsKey(msg.WorkspaceName))
        {
            _log.Warning("Workspace already registered: {0}", msg.WorkspaceName);
            AppLogger.Log($"[Stage] RegisterWorkspace SKIPPED (already exists): {msg.WorkspaceName} (total={_workspaces.Count})");
            return;
        }

        try
        {
            var child = Context.ActorOf(
                Props.Create(() => new WorkspaceActor(msg.WorkspaceName, msg.DirectoryPath)),
                $"ws-{ActorNameSanitizer.Safe(msg.WorkspaceName)}");

            _workspaces[msg.WorkspaceName] = child;
            _log.Info("Workspace registered: {0} → {1}", msg.WorkspaceName, msg.DirectoryPath);
            AppLogger.Log($"[Stage#{GetHashCode()}] Workspace registered: {msg.WorkspaceName} → {msg.DirectoryPath} (total={_workspaces.Count})");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Stage] RegisterWorkspace FAILED: {msg.WorkspaceName} — {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private void HandleUnregisterWorkspace(UnregisterWorkspace msg)
    {
        if (_workspaces.Remove(msg.WorkspaceName, out var actor))
        {
            Context.Stop(actor);
            _log.Info("Workspace unregistered: {0}", msg.WorkspaceName);
        }
    }

    private void HandleRegisterBot(RegisterBot msg)
    {
        _botActor = Sender;
        _log.Info("Bot registered: {0}", Sender.Path);
    }

    private void HandleUnregisterBot(UnregisterBot msg)
    {
        _botActor = null;
        _log.Info("Bot unregistered");
    }

    private async Task HandleQueryStageStatusAsync(QueryStageStatus msg)
    {
        var sender = Sender;
        var workspaces = new List<WorkspaceInfo>();

        AppLogger.Log($"[Stage#{GetHashCode()}] QueryStageStatus: _workspaces.Count={_workspaces.Count}, keys=[{string.Join(",", _workspaces.Keys)}]");

        foreach (var (name, wsRef) in _workspaces)
        {
            try
            {
                var resp = await wsRef.Ask<TerminalsResponse>(
                    new QueryTerminals(), TimeSpan.FromSeconds(3));
                workspaces.Add(new WorkspaceInfo(name, "", resp.Terminals));
            }
            catch
            {
                workspaces.Add(new WorkspaceInfo(name, "", []));
            }
        }

        sender.Tell(new StageStatusResponse(workspaces, _botActor is not null));
    }

    private void HandleQueryActiveTerminal(QueryActiveTerminal msg)
    {
        Sender.Tell(new ActiveTerminalResponse(_activeWorkspace, _activeTerminalId));
    }

    private void HandleSendToTerminal(SendToTerminal msg)
    {
        if (_workspaces.TryGetValue(msg.WorkspaceName, out var workspace))
            workspace.Forward(msg);
        else
            _log.Warning("SendToTerminal: workspace not found: {0}", msg.WorkspaceName);
    }

    private void HandleSendControlToTerminal(SendControlToTerminal msg)
    {
        if (_workspaces.TryGetValue(msg.WorkspaceName, out var workspace))
            workspace.Forward(msg);
        else
            _log.Warning("SendControlToTerminal: workspace not found: {0}", msg.WorkspaceName);
    }
}
