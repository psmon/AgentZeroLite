using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Akka.Event;
using Agent.Common.Orchestration;

namespace Agent.Common.Actors;

/// <summary>
/// Supervised multi-agent orchestration coordinator (mission W6, orca-adoption).
/// Owns a run's task DAG, dispatches ready tasks to a worker router, and advances
/// as <see cref="WorkerDone"/> signals arrive — the actor embodiment of orca's
/// Run/Task/Dispatch model (skill-guides/orchestration.md). The DAG reasoning
/// itself is the pure, headlessly-tested <see cref="OrchestrationDag"/>.
///
/// The coordinator is deliberately transport-agnostic: it emits
/// <see cref="DispatchTaskToWorker"/> to a <c>workerRouter</c> actor supplied at
/// construction (in production this maps task→terminal/agent; in tests it's a
/// probe), so the coordination logic is verifiable in isolation.
/// </summary>
public sealed class CoordinatorActor : ReceiveActor
{
    private readonly IActorRef _workerRouter;
    private readonly ILoggingAdapter _log = Context.GetLogger();

    private readonly Dictionary<string, OrchestrationTaskSpec> _tasks = new();
    private readonly HashSet<string> _completed = new();
    private readonly HashSet<string> _failed = new();
    private readonly HashSet<string> _inFlight = new();
    private readonly Dictionary<string, DateTime> _lastHeartbeat = new();

    private IActorRef? _requester;
    private string _runName = "";
    private bool _finished;

    public CoordinatorActor(IActorRef workerRouter)
    {
        _workerRouter = workerRouter;

        Receive<StartOrchestrationRun>(HandleStart);
        Receive<WorkerDone>(HandleWorkerDone);
        Receive<WorkerHeartbeat>(msg => _lastHeartbeat[msg.TaskKey] = DateTime.UtcNow);
        Receive<AskCoordinator>(HandleAsk);
        Receive<Escalation>(HandleEscalation);
        Receive<QueryRunStatus>(_ => Sender.Tell(BuildStatus()));
        Receive<Ping>(_ => Sender.Tell(new Pong("Coordinator", Self.Path.ToString(),
            $"tasks={_tasks.Count}, done={_completed.Count}, failed={_failed.Count}, inFlight={_inFlight.Count}")));
    }

    private void HandleStart(StartOrchestrationRun msg)
    {
        _requester = Sender;
        _runName = msg.Name;
        _tasks.Clear(); _completed.Clear(); _failed.Clear(); _inFlight.Clear(); _finished = false;
        foreach (var t in msg.Tasks)
            _tasks[t.TaskKey] = t;

        var nodes = Nodes();
        if (OrchestrationDag.HasCycle(nodes, out var unknown))
        {
            _log.Warning("[Coordinator] run '{0}' has a dependency cycle or unknown deps ({1}) — aborting",
                _runName, string.Join(",", unknown));
            Finish(success: false);
            return;
        }

        _log.Info("[Coordinator] run '{0}' started with {1} task(s)", _runName, _tasks.Count);
        DispatchReadyAndMaybeComplete();
    }

    private void HandleWorkerDone(WorkerDone msg)
    {
        if (!_tasks.ContainsKey(msg.TaskKey))
        {
            _log.Warning("[Coordinator] WorkerDone for unknown task '{0}'", msg.TaskKey);
            return;
        }
        _inFlight.Remove(msg.TaskKey);
        if (msg.Success) _completed.Add(msg.TaskKey);
        else _failed.Add(msg.TaskKey);

        _log.Info("[Coordinator] task '{0}' {1}", msg.TaskKey, msg.Success ? "done" : "FAILED");
        DispatchReadyAndMaybeComplete();
    }

    private void HandleAsk(AskCoordinator msg)
    {
        // Minimal decision gate: default to "proceed". Real policies (approve,
        // reroute, abort) can hook in here without changing the protocol.
        Sender.Tell(new AskCoordinatorReply(msg.TaskKey, "proceed"));
    }

    private void HandleEscalation(Escalation msg)
    {
        _log.Warning("[Coordinator] escalation on '{0}': {1}", msg.TaskKey, msg.Reason);
        if (_tasks.ContainsKey(msg.TaskKey))
        {
            _inFlight.Remove(msg.TaskKey);
            _failed.Add(msg.TaskKey);
            DispatchReadyAndMaybeComplete();
        }
    }

    private void DispatchReadyAndMaybeComplete()
    {
        if (_finished) return;

        var nodes = Nodes();
        // Exclude in-flight AND already-failed tasks from dispatch: a failed
        // task must not be re-dispatched, and (not being "completed") it also
        // keeps its dependents blocked — which is the intended semantics.
        var excluded = Excluded();
        var ready = OrchestrationDag.ReadyTasks(nodes, _completed, excluded);
        foreach (var key in ready)
        {
            _inFlight.Add(key);
            _workerRouter.Tell(new DispatchTaskToWorker(key, _tasks[key].Prompt), Self);
            _log.Info("[Coordinator] dispatched task '{0}'", key);
        }

        // The run is over when nothing is running and nothing new can start.
        if (_inFlight.Count == 0)
        {
            var stillReady = OrchestrationDag.ReadyTasks(nodes, _completed, Excluded());
            if (stillReady.Count == 0)
            {
                bool success = _failed.Count == 0 && _completed.Count == _tasks.Count;
                Finish(success);
            }
        }
    }

    private HashSet<string> Excluded()
    {
        var set = new HashSet<string>(_inFlight);
        set.UnionWith(_failed);
        return set;
    }

    private void Finish(bool success)
    {
        if (_finished) return;
        _finished = true;
        _log.Info("[Coordinator] run '{0}' complete: success={1} done={2} failed={3}",
            _runName, success, _completed.Count, _failed.Count);
        _requester?.Tell(new OrchestrationRunCompleted(success, _completed.Count, _failed.Count));
    }

    private RunStatusReply BuildStatus()
        => new(_tasks.Count, _completed.Count, _failed.Count, _inFlight.Count,
            _finished || OrchestrationDag.AllComplete(Nodes(), _completed));

    private List<OrchestrationDag.Node> Nodes()
        => _tasks.Values.Select(t => new OrchestrationDag.Node(t.TaskKey, t.DependsOn)).ToList();
}
