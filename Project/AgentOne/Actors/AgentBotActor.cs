using Akka.Actor;
using Akka.Event;

namespace AgentOne.Actors;

/// <summary>
/// The gateway — AgentZero's <c>AgentBotActor</c> shape: it never runs
/// inference itself. It spawns the loop lazily, holds the renderer's
/// callbacks, keeps the one-turn-at-a-time rule, and passes everything the
/// loop reports out through those callbacks, in order, on this actor's
/// thread. Session commands (status, reset, resume, …) are forwarded to the
/// loop, which owns the session, with the original sender kept so the reply
/// goes straight back.
///
/// A callback that throws is logged and dropped: a renderer's bug must not
/// take the conversation down with it.
/// </summary>
public sealed class AgentBotActor : ReceiveActor
{
    private readonly AgentLoopBindings _bindings;
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private IActorRef? _loop;
    private bool _running;

    private Action<AgentLoopProgress>? _onProgress;
    private Action<AgentLoopResult>? _onResult;
    private Action<AgentLoopNotice>? _onNotice;
    private Action<PersonNeeded>? _onPause;

    public AgentBotActor(AgentLoopBindings bindings)
    {
        _bindings = bindings;

        Receive<SetAgentLoopCallbacks>(msg =>
        {
            _onProgress = msg.OnProgress;
            _onResult = msg.OnResult;
            _onNotice = msg.OnNotice;
            _onPause = msg.OnPause;
        });

        Receive<StartAgentLoop>(msg =>
        {
            if (_running)
            {
                Sender.Tell(new TurnRefused("a turn is already running — wait for it, or cancel it"));
                return;
            }
            _running = true;
            EnsureLoop().Tell(msg);
            Sender.Tell(TurnAccepted.Instance);
        });

        Receive<CancelAgentLoop>(msg => _loop?.Tell(msg));
        Receive<ResolvePause>(msg => _loop?.Tell(msg));
        Receive<IAgentSessionCommand>(msg => EnsureLoop().Forward(msg));

        // From the loop, out to the renderer.
        Receive<AgentLoopProgress>(msg => Invoke(_onProgress, msg));
        Receive<AgentLoopNotice>(msg => Invoke(_onNotice, msg));
        Receive<PersonNeeded>(msg => Invoke(_onPause, msg));
        Receive<AgentLoopResult>(msg =>
        {
            _running = false;
            Invoke(_onResult, msg);
        });
    }

    private IActorRef EnsureLoop() =>
        _loop ??= Context.ActorOf(Props.Create(() => new AgentLoopActor(_bindings)), "loop");

    private void Invoke<T>(Action<T>? callback, T message)
    {
        try { callback?.Invoke(message); }
        catch (Exception ex) { _log.Warning("[bot] a {0} callback threw: {1}", typeof(T).Name, ex.Message); }
    }
}
