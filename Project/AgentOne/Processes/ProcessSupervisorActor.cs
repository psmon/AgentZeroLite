using System.Diagnostics;
using Akka.Actor;
using AgentOne.Tools;

namespace AgentOne.Processes;

/// <summary>
/// The registry of every process a session started — the agent's sub-agent
/// for "things that run". In the actor topology it sits under the loop
/// (<c>/user/bot/loop/procs</c>), one <see cref="ProcessActor"/> per command
/// (<c>proc-p1</c>, <c>proc-p2</c>, …), so stopping the loop stops every
/// process it started, and a session that dies takes no server with it.
///
/// It hands out ids, routes queries, waits and stops by id, and relays each
/// child's lifecycle change to its subscribers — the observer half of the
/// pattern; the other half is a caller asking again (<see cref="QueryProcess"/>)
/// whenever it likes. Only the newest <see cref="KeepEnded"/> ended processes
/// are remembered.
/// </summary>
public sealed class ProcessSupervisorActor : ReceiveActor
{
    public const int KeepEnded = 20;

    private readonly Dictionary<string, IActorRef> _children = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _ended = [];
    private readonly HashSet<IActorRef> _subscribers = [];
    private int _next;

    public static Props Props() => Akka.Actor.Props.Create(() => new ProcessSupervisorActor());

    public ProcessSupervisorActor()
    {
        Receive<StartProcess>(m =>
        {
            var id = $"p{++_next}";
            var child = Context.ActorOf(Akka.Actor.Props.Create(() => new ProcessActor(id, m.Spec)), "proc-" + id);
            _children[id] = child;
            child.Forward(m);
        });

        Receive<QueryProcess>(m =>
        {
            if (m.Id is not null) { Route(m.Id, m); return; }

            var asker = Sender;
            var asks = _children.Select(c => c.Value.Ask<ProcessSnapshot>(new QueryProcess(c.Key), TimeSpan.FromSeconds(3))).ToArray();
            Task.WhenAll(asks).ContinueWith(t => t.IsCompletedSuccessfully
                    ? new ProcessList(t.Result.OrderBy(s => s.StartedAt).ToArray())
                    : new ProcessList(asks.Where(a => a.IsCompletedSuccessfully).Select(a => a.Result).ToArray()),
                TaskScheduler.Default).PipeTo(asker);
        });

        Receive<AwaitProcess>(m => Route(m.Id, m));
        Receive<StopProcess>(m => Route(m.Id, m));

        Receive<SubscribeProcesses>(m =>
        {
            if (_subscribers.Add(m.Observer)) Context.Watch(m.Observer);
        });
        Receive<Terminated>(t => _subscribers.Remove(t.ActorRef));

        Receive<ProcessChanged>(m =>
        {
            foreach (var subscriber in _subscribers) subscriber.Tell(m);
            if (m.Snapshot.Ended) Forget(m.Snapshot.Id);
        });
    }

    protected override SupervisorStrategy SupervisorStrategy() =>
        // A process is never restarted behind the caller's back: a crash in its
        // actor ends it, and the caller sees that as an end.
        new OneForOneStrategy(_ => Directive.Stop);

    private void Route(string id, object message)
    {
        if (_children.TryGetValue(id, out var child)) child.Forward(message);
        else Sender.Tell(new ProcessUnknown(id, _children.Keys.ToArray()));
    }

    private void Forget(string id)
    {
        _ended.Remove(id);
        _ended.Add(id);
        while (_ended.Count > KeepEnded)
        {
            var oldest = _ended[0];
            _ended.RemoveAt(0);
            if (_children.Remove(oldest, out var child)) Context.Stop(child);
        }
    }
}

/// <summary>How the platform's shell is started for one command — the one place that knows.</summary>
internal static class ShellLaunch
{
    public static ProcessStartInfo For(string command)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows PowerShell 5 writes in the console code page; ask it for
            // UTF-8 first, or Korean output comes back as question marks.
            var utf8 = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; ";
            var info = new ProcessStartInfo(ShellInfo.Current.Exe);
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-NonInteractive");
            info.ArgumentList.Add("-ExecutionPolicy");
            info.ArgumentList.Add("Bypass");
            info.ArgumentList.Add("-Command");
            info.ArgumentList.Add(utf8 + command);
            return info;
        }

        var posix = new ProcessStartInfo(ShellInfo.Current.Exe);
        posix.ArgumentList.Add("-c");
        posix.ArgumentList.Add(command);
        return posix;
    }
}
