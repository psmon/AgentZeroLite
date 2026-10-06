using System.Diagnostics;
using System.Text;
using Akka.Actor;
using AgentOne.Tools;

namespace AgentOne.Processes;

/// <summary>
/// Owns ONE operating-system process — the shell running one command — from
/// launch to its last byte of output. Nothing outside this actor touches the
/// <see cref="Process"/>: the turn asks it questions instead (status, wait,
/// stop), so no caller can be left awaiting a pipe that never closes.
///
/// Output is read in chunks on the pool and told back through the mailbox,
/// never with ReadToEnd. The end of a process is its exit, not the EOF of its
/// pipes: after the exit the pipes get <see cref="DrainGrace"/> to finish, and
/// if something the shell left behind still holds them, that something is
/// stopped with the rest of the job and the snapshot says so
/// (<see cref="ProcessSnapshot.PipeHeld"/>).
///
/// Lifecycle changes (ready, ended) go to the parent as
/// <see cref="ProcessChanged"/>; anyone may ask for a snapshot at any time.
/// </summary>
public sealed class ProcessActor : ReceiveActor, IWithTimers
{
    public ITimerScheduler Timers { get; set; } = null!;

    /// <summary>How long the pipes get after the exit before whoever holds them is stopped.</summary>
    public static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(1);

    public const int HeadChars = ShellToolbelt.MaxOutputChars;
    public const int TailChars = 8_000;

    private sealed record Chunk(bool Error, string Text);
    private sealed record StreamClosed(bool Error);
    private sealed record ExitedInternal;
    private sealed record DrainExpired;

    private readonly string _id;
    private readonly ProcessSpec _spec;
    private readonly OutputBuffer _out = new(HeadChars, TailChars);
    private readonly OutputBuffer _err = new(HeadChars / 2, TailChars / 2);
    private readonly StringBuilder _recent = new();
    private readonly List<IActorRef> _readyWaiters = [];
    private readonly List<IActorRef> _endWaiters = [];
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;

    private Process? _process;
    private JobObject? _job;
    private ProcessState _state = ProcessState.Running;
    private DateTimeOffset? _endedAt;
    private int? _exitCode;
    private int? _pid;
    private string? _url;
    private bool _killed, _exited, _outClosed, _errClosed, _pipeHeld;

    public ProcessActor(string id, ProcessSpec spec)
    {
        _id = id;
        _spec = spec;

        Receive<StartProcess>(_ => Sender.Tell(new ProcessStarted(Snapshot())));
        Receive<QueryProcess>(_ => Sender.Tell(Snapshot()));
        Receive<AwaitProcess>(m =>
        {
            var snap = Snapshot();
            if (m.Until == ProcessAwait.Ended ? snap.Ended : snap.ReadyOrEnded) Sender.Tell(snap);
            else (m.Until == ProcessAwait.Ended ? _endWaiters : _readyWaiters).Add(Sender);
        });
        Receive<StopProcess>(_ =>
        {
            if (Ended) { Sender.Tell(Snapshot()); return; }
            _endWaiters.Add(Sender);
            _killed = true;
            Kill();
            // The exit arrives as ExitedInternal; the drain grace bounds the rest.
        });

        Receive<Chunk>(OnChunk);
        Receive<StreamClosed>(m =>
        {
            if (m.Error) _errClosed = true; else _outClosed = true;
            if (_exited && _outClosed && _errClosed) Finish();
        });
        Receive<ExitedInternal>(_ =>
        {
            if (Ended) return;
            _exited = true;
            try { _exitCode = _process!.ExitCode; } catch (InvalidOperationException) { _exitCode = null; }
            if (_outClosed && _errClosed) Finish();
            else Timers.StartSingleTimer("drain", new DrainExpired(), DrainGrace);
        });
        Receive<DrainExpired>(_ =>
        {
            if (Ended) return;
            // The shell is gone and its pipe is not: a child it left behind holds it.
            _pipeHeld = true;
            Kill();
            Finish();
        });
    }

    private bool Ended => _state is ProcessState.Exited or ProcessState.Killed or ProcessState.Failed;

    protected override void PreStart()
    {
        var start = ShellLaunch.For(_spec.Command);
        start.WorkingDirectory = _spec.WorkingDirectory;
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = true;          // and closed at once: nothing here can answer a prompt
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        start.CreateNoWindow = true;

        var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            _err.Append($"could not start {ShellToolbelt.ShellName}: {ex.Message}");
            _state = ProcessState.Failed;
            _endedAt = DateTimeOffset.Now;
            return;
        }

        _process = process;
        _job = JobObject.For(process);
        try { _pid = process.Id; } catch (InvalidOperationException) { }
        try { process.StandardInput.Close(); } catch (IOException) { }

        var self = Self;
        _ = Pump(process.StandardOutput, error: false, self);
        _ = Pump(process.StandardError, error: true, self);
        process.WaitForExitAsync().ContinueWith(_ => (object)new ExitedInternal(), TaskScheduler.Default).PipeTo(self);
    }

    protected override void PostStop()
    {
        if (!Ended) Kill();
        _job?.Dispose();          // KILL_ON_JOB_CLOSE: whatever is left goes with the handle
        _job = null;
        _process?.Dispose();
        base.PostStop();
    }

    private static async Task Pump(StreamReader reader, bool error, IActorRef self)
    {
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory())) > 0)
                self.Tell(new Chunk(error, new string(buffer, 0, read)));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        self.Tell(new StreamClosed(error));
    }

    private void OnChunk(Chunk chunk)
    {
        (chunk.Error ? _err : _out).Append(chunk.Text);

        _recent.Append(chunk.Text);
        if (_recent.Length > 2048) _recent.Remove(0, _recent.Length - 1024);

        if (_spec.Kind != ProcessKind.Service || _state != ProcessState.Running) return;
        if (!CommandLifetime.DetectReady(_recent.ToString(), out var url)) return;

        _state = ProcessState.Ready;
        _url = url;
        var snap = Snapshot();
        Reply(_readyWaiters, snap);
        Context.Parent.Tell(new ProcessChanged(snap));
    }

    /// <summary>
    /// The shell and everything below it. The tree is read first, while every
    /// parent id still points somewhere — see <see cref="ProcessTree"/> for the
    /// two kills this replaces and how each one missed.
    /// </summary>
    private void Kill()
    {
        var below = _pid is { } pid ? ProcessTree.Descendants(pid) : [];
        _job?.Terminate();
        try { if (_process is { HasExited: false }) _process.Kill(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        ProcessTree.Kill(below);
    }

    private void Finish()
    {
        if (Ended) return;
        _state = _killed ? ProcessState.Killed : ProcessState.Exited;
        _endedAt = DateTimeOffset.Now;

        // Anything still in the job is a leftover of a command that has ended.
        _job?.Dispose();
        _job = null;

        var snap = Snapshot();
        Reply(_readyWaiters, snap);
        Reply(_endWaiters, snap);
        Context.Parent.Tell(new ProcessChanged(snap));
    }

    private static void Reply(List<IActorRef> waiters, ProcessSnapshot snap)
    {
        foreach (var waiter in waiters) waiter.Tell(snap);
        waiters.Clear();
    }

    private ProcessSnapshot Snapshot()
    {
        var elapsed = (_endedAt ?? DateTimeOffset.Now) - _startedAt;
        return new ProcessSnapshot(_id, _spec.Command, _spec.Kind, _state, _pid, _startedAt, elapsed, _exitCode,
            _out.Text().TrimEnd(), _err.Text().TrimEnd(), _url, LastLine(), _pipeHeld);
    }

    private string LastLine()
    {
        var text = _recent.ToString().TrimEnd();
        var cut = text.LastIndexOf('\n');
        var line = (cut >= 0 ? text[(cut + 1)..] : text).Trim();
        return line.Length <= 120 ? line : line[..117] + "…";
    }
}
