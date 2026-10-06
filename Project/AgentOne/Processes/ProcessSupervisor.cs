using System.Collections.Concurrent;
using Akka.Actor;
using AgentOne.Actors;

namespace AgentOne.Processes;

/// <summary>
/// The turn's handle on <see cref="ProcessSupervisorActor"/>, for code that is
/// not an actor (the shell toolbelt runs on the turn's pool thread).
///
/// Waiting is both halves of the monitoring pattern at once:
/// <see cref="WaitAsync"/> completes the moment the observer hears the
/// process became ready or ended (notification), and otherwise returns after
/// <c>within</c> with a fresh snapshot asked of the actor (the parent checking
/// again). The caller loops on it, reports progress between check-ins, and
/// decides what to do when it has waited long enough — the process never
/// decides for the turn, and the turn never blocks on the process.
/// </summary>
public sealed class ProcessSupervisor : IDisposable
{
    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(10);

    private readonly IActorRef _supervisor;
    private readonly IActorRef _observer;
    private readonly ActorSystem? _owned;
    private readonly ConcurrentDictionary<string, Tracker> _trackers = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    /// <summary>A process became ready or ended. Raised on the observer actor's thread.</summary>
    public event Action<ProcessSnapshot>? Changed;

    private ProcessSupervisor(IActorRefFactory factory, ActorSystem? owned)
    {
        _owned = owned;
        _supervisor = factory.ActorOf(ProcessSupervisorActor.Props(), "procs");
        _observer = factory.ActorOf(Props.Create(() => new ProcessObserver(OnChanged)), "procs-observer");
        _supervisor.Tell(new SubscribeProcesses(_observer));
    }

    /// <summary>The supervisor as a child of the calling actor — the loop's <c>procs</c>.</summary>
    public static ProcessSupervisor Under(IActorContext context) => new(context, owned: null);

    /// <summary>A supervisor on its own actor system, for a session that runs without the actor pair.</summary>
    public static ProcessSupervisor Standalone()
    {
        var system = AgentActorSystem.Create();
        return new ProcessSupervisor(system, system);
    }

    public async Task<ProcessSnapshot> StartAsync(ProcessSpec spec, CancellationToken ct)
    {
        var reply = await _supervisor.Ask<ProcessStarted>(new StartProcess(spec), AskTimeout, ct);
        Track(reply.Snapshot);
        return reply.Snapshot;
    }

    /// <summary>
    /// Returns as soon as <paramref name="until"/> holds — or after
    /// <paramref name="within"/>, with the process's current state, whichever
    /// comes first. Cancelling <paramref name="ct"/> leaves the process alone.
    /// </summary>
    public async Task<ProcessSnapshot> WaitAsync(string id, ProcessAwait until, TimeSpan within, CancellationToken ct)
    {
        var tracker = _trackers.GetOrAdd(id, _ => new Tracker());
        var signal = until == ProcessAwait.Ended ? tracker.Ended.Task : tracker.ReadyOrEnded.Task;

        if (!signal.IsCompleted)
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await Task.WhenAny(signal, Task.Delay(within, timer.Token));
            timer.Cancel();
            ct.ThrowIfCancellationRequested();
        }

        if (signal.IsCompletedSuccessfully) return signal.Result;
        return await QueryAsync(id, ct) ?? tracker.Latest!;
    }

    public async Task<ProcessSnapshot?> QueryAsync(string id, CancellationToken ct = default)
    {
        var reply = await _supervisor.Ask<object>(new QueryProcess(id), AskTimeout, ct);
        if (reply is not ProcessSnapshot snap) return null;
        Track(snap);
        return snap;
    }

    public async Task<IReadOnlyList<ProcessSnapshot>> ListAsync(CancellationToken ct = default)
    {
        var reply = await _supervisor.Ask<ProcessList>(new QueryProcess(null), AskTimeout, ct);
        return reply.Items;
    }

    /// <summary>Stops the process and all it started. Null for an id this session never handed out (or forgot).</summary>
    public async Task<ProcessSnapshot?> StopAsync(string id, CancellationToken ct = default)
    {
        var reply = await _supervisor.Ask<object>(new StopProcess(id), AskTimeout, ct);
        if (reply is not ProcessSnapshot snap) return null;
        Track(snap);
        return snap;
    }

    private void OnChanged(ProcessSnapshot snap)
    {
        Track(snap);
        Changed?.Invoke(snap);
    }

    private void Track(ProcessSnapshot snap)
    {
        var tracker = _trackers.GetOrAdd(snap.Id, _ => new Tracker());
        tracker.Latest = snap;
        if (snap.ReadyOrEnded) tracker.ReadyOrEnded.TrySetResult(snap);
        if (snap.Ended) tracker.Ended.TrySetResult(snap);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        // Stopping the supervisor stops every ProcessActor, and each kills what it owns.
        _supervisor.Tell(PoisonPill.Instance);
        _observer.Tell(PoisonPill.Instance);
        if (_owned is null) return;
        try { _owned.Terminate().Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
    }

    private sealed class Tracker
    {
        public volatile ProcessSnapshot? Latest;
        public readonly TaskCompletionSource<ProcessSnapshot> ReadyOrEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<ProcessSnapshot> Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>Subscribed to the supervisor; hands each lifecycle change to a callback.</summary>
internal sealed class ProcessObserver : ReceiveActor
{
    public ProcessObserver(Action<ProcessSnapshot> onChanged)
    {
        Receive<ProcessChanged>(m =>
        {
            try { onChanged(m.Snapshot); }
            catch (Exception) { /* an observer's bug must not take the supervisor's relay down */ }
        });
    }
}
