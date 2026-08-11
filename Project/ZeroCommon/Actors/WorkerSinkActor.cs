using System;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Event;
using Agent.Common.Llm.Tools;

namespace Agent.Common.Actors;

/// <summary>
/// Tuning for a <see cref="WorkerSinkActor"/>'s completion detection.
/// </summary>
public sealed record WorkerSinkOptions
{
    /// <summary>Delay between output polls.</summary>
    public TimeSpan PollDelay { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Consecutive unchanged reads that count as "idle / done".</summary>
    public int StableRounds { get; init; } = 2;
    /// <summary>Max polls before giving up and reporting what we have.</summary>
    public int MaxReads { get; init; } = 30;
    /// <summary>Chars of trailing output to read each poll.</summary>
    public int ReadChars { get; init; } = 4000;
}

/// <summary>
/// Bridges an orchestration <see cref="DispatchTaskToWorker"/> to a real hosted
/// terminal agent (mission W6 activation): it sends the task prompt into a
/// terminal via <see cref="IAgentToolbelt"/>, waits for the terminal to go idle
/// (output stops changing = the agent finished), reads the result, and reports
/// <see cref="WorkerDone"/> back to the coordinator.
///
/// Toolbelt-based, so it is headlessly testable with a mock toolbelt; the same
/// actor drives live terminals in the GUI (production toolbelt =
/// <c>WorkspaceTerminalToolHost</c>). One sink = one terminal; concurrent
/// dispatches are stashed and processed sequentially.
/// </summary>
public sealed class WorkerSinkActor : ReceiveActor, IWithStash
{
    public IStash Stash { get; set; } = null!;

    private readonly IAgentToolbelt _toolbelt;
    private readonly int _group;
    private readonly int _tab;
    private readonly WorkerSinkOptions _opts;
    private readonly ILoggingAdapter _log = Context.GetLogger();

    private sealed record TaskFinished(string TaskKey, bool Success, string Result, IActorRef Coordinator);

    public WorkerSinkActor(IAgentToolbelt toolbelt, int group, int tab, WorkerSinkOptions? opts = null)
    {
        _toolbelt = toolbelt;
        _group = group;
        _tab = tab;
        _opts = opts ?? new WorkerSinkOptions();
        Become(Ready);
    }

    // Become(Action) expects the action to register the receives directly.
    private void Ready()
    {
        Receive<DispatchTaskToWorker>(msg =>
        {
            var coordinator = Sender;
            _log.Info("[WorkerSink {0}:{1}] task '{2}' → terminal", _group, _tab, msg.TaskKey);
            RunTask(msg.TaskKey, msg.Prompt, coordinator).PipeTo(Self);
            Become(Busy);
        });
    }

    private void Busy()
    {
        Receive<DispatchTaskToWorker>(_ => Stash.Stash());   // one terminal at a time
        Receive<TaskFinished>(f =>
        {
            f.Coordinator.Tell(new WorkerDone(f.TaskKey, f.Success, f.Result));
            _log.Info("[WorkerSink {0}:{1}] task '{2}' done", _group, _tab, f.TaskKey);
            Stash.UnstashAll();
            Become(Ready);
        });
        Receive<Status.Failure>(fail =>
        {
            _log.Warning("[WorkerSink {0}:{1}] task failed: {2}", _group, _tab, fail.Cause?.Message);
            Stash.UnstashAll();
            Become(Ready);
        });
    }

    private async Task<TaskFinished> RunTask(string taskKey, string prompt, IActorRef coordinator)
    {
        var ct = CancellationToken.None;
        await _toolbelt.SendToTerminalAsync(_group, _tab, prompt, ct).ConfigureAwait(false);

        string last = "";
        int stable = 0;
        for (int i = 0; i < _opts.MaxReads; i++)
        {
            await Task.Delay(_opts.PollDelay, ct).ConfigureAwait(false);
            var read = await _toolbelt.ReadTerminalAsync(_group, _tab, _opts.ReadChars, ct).ConfigureAwait(false);
            if (string.Equals(read, last, StringComparison.Ordinal))
            {
                if (++stable >= _opts.StableRounds) break;
            }
            else
            {
                last = read;
                stable = 0;
            }
        }
        return new TaskFinished(taskKey, true, last, coordinator);
    }
}
