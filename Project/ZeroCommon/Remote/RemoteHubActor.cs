// ───────────────────────────────────────────────────────────
// RemoteHubActor — remote-connection registry + concurrency cap
//
// 역할:
//   1. 웹 세션키당 RemoteSessionActor 자식 생성/추적/회수
//   2. 최대 동시 커넥션 수 강제 (설정값, 기본 3)
//
// 경로: /user/stage/remote
//   자식: /user/stage/remote/session-{key}
//
// WorkspaceActor 의 per-id child 패턴을 그대로 따른다.
// ───────────────────────────────────────────────────────────

using Akka.Actor;
using Akka.Event;
using Agent.Common.Actors;

namespace Agent.Common.Remote;

public sealed class RemoteHubActor : ReceiveActor
{
    private readonly ILoggingAdapter _log = Context.GetLogger();

    // Logical session key → child actor. Key stays raw; only the actor-name segment is sanitized.
    private readonly Dictionary<string, IActorRef> _sessions = new();
    private int _maxConnections;

    public RemoteHubActor(int maxConnections)
    {
        _maxConnections = Math.Max(1, maxConnections);

        Receive<Ping>(_ => Sender.Tell(new Pong("Remote", Self.Path.ToString(),
            $"Sessions={_sessions.Count}/{_maxConnections}")));

        Receive<SetMaxConnections>(msg =>
        {
            _maxConnections = Math.Max(1, msg.Max);
            _log.Info("Remote max connections set to {0}", _maxConnections);
        });

        Receive<CreateRemoteSession>(HandleCreateSession);
        Receive<CloseRemoteSession>(HandleCloseSession);

        Receive<QueryRemoteStatus>(_ =>
            Sender.Tell(new RemoteStatusReply(_sessions.Count, _maxConnections, _sessions.Keys.ToArray())));

        // A child that stops itself (terminal gone, error) must be forgotten so the cap frees up.
        Receive<Terminated>(t =>
        {
            var key = _sessions.FirstOrDefault(kv => kv.Value.Equals(t.ActorRef)).Key;
            if (key is not null && _sessions.Remove(key))
                _log.Info("Remote session terminated: {0} (now {1}/{2})", key, _sessions.Count, _maxConnections);
        });
    }

    private void HandleCreateSession(CreateRemoteSession msg)
    {
        // Idempotent: a reconnect reusing the same key replaces the old actor.
        if (_sessions.TryGetValue(msg.SessionKey, out var existing))
        {
            Context.Stop(existing);
            _sessions.Remove(msg.SessionKey);
        }

        if (_sessions.Count >= _maxConnections)
        {
            _log.Warning("Remote session rejected (cap {0} reached): {1}", _maxConnections, msg.SessionKey);
            Sender.Tell(new RemoteSessionRejected($"connection limit reached ({_maxConnections})"));
            return;
        }

        var child = Context.ActorOf(
            Props.Create(() => new RemoteSessionActor(msg.SessionKey, msg.Session, msg.SendToWeb)),
            $"session-{ActorNameSanitizer.Safe(msg.SessionKey)}");
        Context.Watch(child);
        _sessions[msg.SessionKey] = child;

        _log.Info("Remote session created: {0} ({1}/{2})", msg.SessionKey, _sessions.Count, _maxConnections);
        Sender.Tell(new RemoteSessionCreated(child));
    }

    private void HandleCloseSession(CloseRemoteSession msg)
    {
        if (_sessions.Remove(msg.SessionKey, out var actor))
        {
            Context.Stop(actor);
            _log.Info("Remote session closed: {0} ({1}/{2})", msg.SessionKey, _sessions.Count, _maxConnections);
        }
    }

    protected override SupervisorStrategy SupervisorStrategy()
        => new OneForOneStrategy(
            maxNrOfRetries: 3,
            withinTimeRange: TimeSpan.FromMinutes(1),
            localOnlyDecider: ex => ex switch
            {
                ObjectDisposedException => Directive.Stop,
                _ => Directive.Restart,
            });
}
