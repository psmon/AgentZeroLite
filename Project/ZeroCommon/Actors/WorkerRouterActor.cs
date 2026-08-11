using System.Collections.Generic;
using Akka.Actor;

namespace Agent.Common.Actors;

/// <summary>
/// Routes <see cref="DispatchTaskToWorker"/> from a <see cref="CoordinatorActor"/>
/// to one of N worker sinks, round-robin (mission W6 activation). Forwards each
/// dispatch so the original sender (the coordinator) stays the reply target —
/// the worker sends <see cref="WorkerDone"/> straight back to the coordinator.
///
/// In production a "sink" is an adapter that launches an agent in a hosted
/// terminal and reports completion; in tests it's a probe. Keeping the router a
/// plain forwarding primitive makes the coordination path verifiable without any
/// real agent.
/// </summary>
public sealed class WorkerRouterActor : ReceiveActor
{
    private readonly IReadOnlyList<IActorRef> _workers;
    private int _next;

    public WorkerRouterActor(IReadOnlyList<IActorRef> workers)
    {
        _workers = workers;

        Receive<DispatchTaskToWorker>(msg =>
        {
            if (_workers.Count == 0) return;
            var worker = _workers[_next % _workers.Count];
            _next++;
            // Forward preserves Sender == coordinator, so WorkerDone routes back.
            worker.Forward(msg);
        });
    }

    public static Props Props(IReadOnlyList<IActorRef> workers)
        => Akka.Actor.Props.Create(() => new WorkerRouterActor(workers));
}
