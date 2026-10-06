using Akka.Actor;

namespace AgentOne.Processes;

/// <summary>
/// What a started process is for. A <see cref="Task"/> is expected to end on
/// its own and its output is the answer; a <see cref="Service"/> (a dev server,
/// a watcher) is expected to keep running, so "ready" is what is waited for,
/// never the end.
/// </summary>
public enum ProcessKind { Task, Service }

/// <summary>
/// The lifecycle one <see cref="ProcessActor"/> walks: Running, Ready (a
/// service said it is listening), then exactly one of Exited (ended on its
/// own), Killed (stopped by us), Failed (never started).
/// </summary>
public enum ProcessState { Running, Ready, Exited, Killed, Failed }

/// <summary>What to wait for in <see cref="AwaitProcess"/>.</summary>
public enum ProcessAwait { Ended, ReadyOrEnded }

public sealed record ProcessSpec(string Command, string WorkingDirectory, ProcessKind Kind);

/// <param name="Output">stdout as kept by the buffer: the head, and the tail once it overflowed.</param>
/// <param name="PipeHeld">
/// The shell exited but something it started kept the output pipe open. That
/// is the shape that once hung a turn for good (a Flask reloader child outlived
/// its parent); it is stopped with the rest of the job and reported.
/// </param>
public sealed record ProcessSnapshot(
    string Id,
    string Command,
    ProcessKind Kind,
    ProcessState State,
    int? Pid,
    DateTimeOffset StartedAt,
    TimeSpan Elapsed,
    int? ExitCode,
    string Output,
    string Errors,
    string? Url,
    string LastLine,
    bool PipeHeld)
{
    public bool Ended => State is ProcessState.Exited or ProcessState.Killed or ProcessState.Failed;
    public bool ReadyOrEnded => Ended || State == ProcessState.Ready;

    /// <summary>One line for a list: id, kind, state, where, how long, what.</summary>
    public string Line()
    {
        var where = Url is null ? "" : $" · {Url}";
        var code = ExitCode is { } c && Ended ? $" (exit {c})" : "";
        var pid = Pid is { } p ? $" · pid {p}" : "";
        return $"{Id} · {Kind.ToString().ToLowerInvariant()} · {State.ToString().ToLowerInvariant()}{code}{where}{pid} · {Clock(Elapsed)} · {Command}";
    }

    public static string Clock(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{t.TotalSeconds:0}s" : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{(int)t.TotalHours}h {t.Minutes}m";
}

// ------------------------------------------------------------------ requests

/// <summary>Start one process. Replied with <see cref="ProcessStarted"/> — also when the launch failed (state Failed).</summary>
public sealed record StartProcess(ProcessSpec Spec);

public sealed record ProcessStarted(ProcessSnapshot Snapshot);

/// <summary>One process by id (replied with <see cref="ProcessSnapshot"/>), or every one (<see cref="ProcessList"/>).</summary>
public sealed record QueryProcess(string? Id);

public sealed record ProcessList(IReadOnlyList<ProcessSnapshot> Items);

/// <summary>Replied once, with the snapshot, when the condition holds — at once if it already does.</summary>
public sealed record AwaitProcess(string Id, ProcessAwait Until);

/// <summary>Kill the process and everything it started. Replied with the final snapshot.</summary>
public sealed record StopProcess(string Id);

public sealed record ProcessUnknown(string Id, IReadOnlyList<string> Known);

// ----------------------------------------------------------------- observers

/// <summary>Every lifecycle change of every process goes to <paramref name="Observer"/> from now on.</summary>
public sealed record SubscribeProcesses(IActorRef Observer);

/// <summary>A process became ready or ended. Told to the supervisor, which relays it to its subscribers.</summary>
public sealed record ProcessChanged(ProcessSnapshot Snapshot);
