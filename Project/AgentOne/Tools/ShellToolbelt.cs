using System.Diagnostics;
using System.Text;
using AgentOne.Agent;
using AgentOne.Processes;

namespace AgentOne.Tools;

/// <param name="Allowed">False means the command is not run at all.</param>
/// <param name="Reason">Why, in a sentence the model can act on.</param>
public readonly record struct GateVerdict(bool Allowed, string Reason)
{
    public static GateVerdict Allow(string reason = "allowed") => new(true, reason);
    public static GateVerdict Deny(string reason) => new(false, reason);
}

/// <summary>
/// The verbs that run something: <c>run_command</c> through the platform's
/// own shell — PowerShell on Windows, bash (or sh) elsewhere, as detected by
/// <see cref="ShellInfo"/> — with the workspace root as its working directory,
/// and <c>process_status</c> / <c>process_stop</c> for what is still running.
/// Nothing runs without the <see cref="Gate"/> saying so; the belt itself
/// never decides that.
///
/// The belt never owns a process. Each command is handed to a
/// <see cref="ProcessActor"/> under the session's <see cref="ProcessSupervisor"/>,
/// and the belt watches it as a parent would: it is told the moment the
/// process ends (or a service is ready), checks in every <see cref="CheckIn"/>
/// otherwise and reports what it sees, and when a one-shot command overruns
/// the timeout it asks — wait more, move it to the background, or stop it —
/// instead of killing and hoping. A command that looks like a server is asked
/// about before it starts (background, smoke run, or not at all).
///
/// Measured (2026-10-06): the old belt read the output with ReadToEnd and,
/// after a timeout kill, awaited that read with no bound. A Flask reloader
/// child survived the kill holding the pipe, and the turn waited on it for
/// good — 44 minutes with no event until a person noticed.
/// </summary>
public sealed class ShellToolbelt(string root, TimeSpan timeout) : IToolbelt, IDisposable
{
    public const int MaxOutputChars = 24_000;

    /// <summary>How often a running command is checked on and reported.</summary>
    public TimeSpan CheckIn { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long a service is given to say it is up before the step returns anyway.</summary>
    public TimeSpan ReadyWait { get; set; } = TimeSpan.FromSeconds(20);

    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>
    /// Decides whether a command may run. Null means nothing may: a belt with
    /// no gate refuses everything, which is the safe way to be misconfigured.
    /// </summary>
    public Func<string, CancellationToken, Task<GateVerdict>>? Gate { get; set; }

    /// <summary>
    /// Who picks how a long-running command runs, and what happens to one that
    /// overruns. Returns the index of the option picked. Null takes the
    /// recommendation — the unattended answer.
    /// </summary>
    public Func<ChoiceRequest, CancellationToken, Task<int>>? Choose { get; set; }

    /// <summary>A check-in on a running command, for a status line.</summary>
    public event Action<string>? Activity;

    /// <summary>A background process became ready or ended.</summary>
    public event Action<ProcessSnapshot>? ProcessChanged;

    private ProcessSupervisor? _processes;
    private bool _ownsProcesses;
    private readonly object _gate = new();
    private readonly HashSet<string> _detached = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Only what outlives its step is news: a service, or a one-shot left in
    /// the background. A command that ended inside its step is in the step's result.
    /// </summary>
    private void OnChanged(ProcessSnapshot snap)
    {
        bool detached;
        lock (_gate) detached = _detached.Contains(snap.Id);
        if (snap.Kind == ProcessKind.Service || detached) ProcessChanged?.Invoke(snap);
    }

    private void Detach(string id)
    {
        lock (_gate) _detached.Add(id);
    }

    public string Scope => $"{ShellName} in the workspace root";

    /// <summary>The shell this machine gets, named with its version — see <see cref="ShellInfo"/>.</summary>
    public static string ShellName => ShellInfo.Current.Describe;

    /// <summary>
    /// Runs processes under <paramref name="processes"/> (the loop actor's
    /// <c>procs</c> child) instead of a supervisor of the belt's own. Must
    /// come before the first command.
    /// </summary>
    public void UseProcesses(ProcessSupervisor processes)
    {
        lock (_gate)
        {
            if (_processes is not null) throw new InvalidOperationException("this belt already has a process supervisor");
            _processes = processes;
            processes.Changed += OnChanged;
        }
    }

    /// <summary>The supervisor in use — made on first need when none was given.</summary>
    public ProcessSupervisor Processes
    {
        get
        {
            lock (_gate)
            {
                if (_processes is null)
                {
                    _processes = ProcessSupervisor.Standalone();
                    _ownsProcesses = true;
                    _processes.Changed += OnChanged;
                }
                return _processes;
            }
        }
    }

    /// <summary>Whether this session has started anything yet (status views ask before creating a supervisor).</summary>
    public bool HasProcesses
    {
        get { lock (_gate) return _processes is not null; }
    }

    public async Task<ToolResult> InvokeAsync(ToolCall call, CancellationToken ct)
    {
        switch (call.Tool.ToLowerInvariant())
        {
            case "process_status": return await StatusAsync(call.Arg("id").Trim(), ct);
            case "process_stop": return await StopAsync(call.Arg("id").Trim(), ct);
            case "run_command": break;
            default: return ToolResult.Failure($"unknown tool '{call.Tool}'");
        }

        var command = call.Arg("command").Trim();
        if (command.Length == 0) return ToolResult.Failure("run_command needs a 'command' argument");

        if (Gate is null) return ToolResult.Failure("not run: no approval gate is configured for commands in this session");

        var verdict = await Gate(command, ct);
        if (!verdict.Allowed) return ToolResult.Failure($"not run: {verdict.Reason}");

        var service = IsTrue(call.Arg("background")) || CommandLifetime.LooksLongRunning(command, Root);
        return service ? await RunServiceAsync(command, ct) : await RunTaskAsync(command, ct);
    }

    // ------------------------------------------------------------ one-shot

    internal const string WaitOption = "wait — give it another timeout";
    internal const string BackgroundOption = "background — leave it running and carry on (process_status / process_stop)";
    internal const string StopOption = "stop — kill it and carry on";

    private async Task<ToolResult> RunTaskAsync(string command, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var snap = await Processes.StartAsync(new ProcessSpec(command, Root, ProcessKind.Task), ct);
        if (snap.State == ProcessState.Failed) return ToolResult.Failure(snap.Errors);

        var deadline = timeout;
        try
        {
            while (true)
            {
                var within = Min(CheckIn, deadline - sw.Elapsed);
                snap = await Processes.WaitAsync(snap.Id, ProcessAwait.Ended, within, ct);
                if (snap.Ended) return Finished(snap, sw);

                if (sw.Elapsed < deadline)
                {
                    Activity?.Invoke($"running {Short(command)} · {ProcessSnapshot.Clock(sw.Elapsed)}" +
                                     (snap.LastLine.Length > 0 ? $" · {snap.LastLine}" : ""));
                    continue;
                }

                // Overran: the person decides, not a kill on a timer.
                var pick = await PickAsync(new ChoiceRequest(
                    $"`{Short(command)}` is still running after {ProcessSnapshot.Clock(sw.Elapsed)}" +
                    (snap.LastLine.Length > 0 ? $" — last output: {snap.LastLine}" : " — no output yet"),
                    [WaitOption, BackgroundOption, StopOption], Recommended: 2), ct);

                if (pick == 0) { deadline = sw.Elapsed + timeout; continue; }
                if (pick == 1) { Detach(snap.Id); return MovedToBackground(snap); }

                var stopped = await Processes.StopAsync(snap.Id, CancellationToken.None) ?? snap;
                return ToolResult.Failure(
                    $"killed after {sw.Elapsed.TotalSeconds:0}s (commandTimeoutSeconds) — output so far:\n{Clip(Combined(stopped))}");
            }
        }
        catch (OperationCanceledException)
        {
            // The turn was stopped: a one-shot command goes with it.
            await Processes.StopAsync(snap.Id, CancellationToken.None);
            throw;
        }
    }

    private static ToolResult Finished(ProcessSnapshot snap, Stopwatch sw)
    {
        var sb = new StringBuilder();
        sb.Append("exit code ").Append(snap.ExitCode?.ToString() ?? "?").Append(" · ").Append(sw.ElapsedMilliseconds).Append(" ms");
        if (snap.Output.Length > 0) sb.Append('\n').Append(Clip(snap.Output));
        if (snap.Errors.Length > 0) sb.Append("\n[stderr]\n").Append(Clip(snap.Errors));
        if (snap.PipeHeld)
            sb.Append("\n[note] the command ended but something it started kept running and holding its output; that was stopped. " +
                      "Start servers and watchers with \"background\":\"true\".");

        // A non-zero exit is a failed step for the loop's bookkeeping, but the
        // text still goes back to the model: the error output is what it needs.
        return snap.ExitCode == 0 && snap.State == ProcessState.Exited
            ? ToolResult.Success(sb.ToString())
            : ToolResult.Failure(sb.ToString());
    }

    private static ToolResult MovedToBackground(ProcessSnapshot snap) =>
        ToolResult.Success(
            $"still running — left in the background as {snap.Id}{Pid(snap)}. " +
            $"Check it with process_status {{\"id\":\"{snap.Id}\"}}, stop it with process_stop. Do not report it as finished.\n" +
            $"output so far:\n{Tail(snap)}");

    // ------------------------------------------------------------- service

    internal const string ServiceBackgroundOption = "background — start it, wait until it is up, keep it running";
    internal const string ServiceSmokeOption = "smoke — start it, check it comes up, then stop it";
    internal const string ServiceSkipOption = "skip — do not start it; give the user the command";

    private async Task<ToolResult> RunServiceAsync(string command, CancellationToken ct)
    {
        var pick = await PickAsync(new ChoiceRequest(
            $"`{Short(command)}` keeps running (a server or watcher) — how should it run?",
            [ServiceBackgroundOption, ServiceSmokeOption, ServiceSkipOption], Recommended: 0), ct);

        if (pick == 2)
            return ToolResult.Failure(
                "not run: the user chose not to start this long-running command here. Give them the command to run themselves; do not retry it.");

        var sw = Stopwatch.StartNew();
        var snap = await Processes.StartAsync(new ProcessSpec(command, Root, ProcessKind.Service), ct);
        if (snap.State == ProcessState.Failed) return ToolResult.Failure(snap.Errors);

        try
        {
            while (!snap.ReadyOrEnded && sw.Elapsed < ReadyWait)
            {
                snap = await Processes.WaitAsync(snap.Id, ProcessAwait.ReadyOrEnded, Min(CheckIn, ReadyWait - sw.Elapsed), ct);
                if (!snap.ReadyOrEnded)
                    Activity?.Invoke($"starting {Short(command)} · {ProcessSnapshot.Clock(sw.Elapsed)}" +
                                     (snap.LastLine.Length > 0 ? $" · {snap.LastLine}" : ""));
            }
        }
        catch (OperationCanceledException)
        {
            await Processes.StopAsync(snap.Id, CancellationToken.None);
            throw;
        }

        if (snap.Ended)
            return ToolResult.Failure(
                $"{snap.Id} ended before it came up — exit code {snap.ExitCode?.ToString() ?? "?"}\n{Clip(Combined(snap))}");

        var state = snap.State == ProcessState.Ready
            ? "ready" + (snap.Url is null ? "" : $" at {snap.Url}")
            : $"no sign of being up after {ProcessSnapshot.Clock(sw.Elapsed)}, still running";

        if (pick == 1)
        {
            var stopped = await Processes.StopAsync(snap.Id, CancellationToken.None) ?? snap;
            var text = $"smoke run of {snap.Id}: {state}; stopped after {ProcessSnapshot.Clock(stopped.Elapsed)}\noutput:\n{Clip(Combined(stopped))}";
            return snap.State == ProcessState.Ready ? ToolResult.Success(text) : ToolResult.Failure(text);
        }

        return ToolResult.Success(
            $"started in the background as {snap.Id}{Pid(snap)} — {state}. It keeps running after this step: " +
            $"check it with process_status {{\"id\":\"{snap.Id}\"}}, stop it with process_stop.\noutput so far:\n{Tail(snap)}");
    }

    // ------------------------------------------------------ status and stop

    private async Task<ToolResult> StatusAsync(string id, CancellationToken ct)
    {
        if (!HasProcesses) return ToolResult.Success("no processes started in this session");

        if (id.Length == 0)
        {
            var all = await Processes.ListAsync(ct);
            return ToolResult.Success(all.Count == 0
                ? "no processes started in this session"
                : string.Join("\n", all.Select(s => s.Line())));
        }

        var snap = await Processes.QueryAsync(id, ct);
        return snap is null
            ? ToolResult.Failure(await UnknownAsync(id, ct))
            : ToolResult.Success($"{snap.Line()}\noutput (newest):\n{Tail(snap)}");
    }

    private async Task<ToolResult> StopAsync(string id, CancellationToken ct)
    {
        if (id.Length == 0) return ToolResult.Failure("process_stop needs an 'id' (see process_status)");
        if (!HasProcesses) return ToolResult.Failure("no processes started in this session");

        var snap = await Processes.StopAsync(id, ct);
        return snap is null
            ? ToolResult.Failure(await UnknownAsync(id, ct))
            : ToolResult.Success($"{snap.Line()}\noutput (newest):\n{Tail(snap)}");
    }

    private async Task<string> UnknownAsync(string id, CancellationToken ct)
    {
        var all = await Processes.ListAsync(ct);
        return $"no process '{id}'. " + (all.Count == 0 ? "Nothing was started." : "Known: " + string.Join(", ", all.Select(s => s.Id)));
    }

    // -------------------------------------------------------------- helpers

    private async Task<int> PickAsync(ChoiceRequest ask, CancellationToken ct) =>
        Choose is null ? ask.Recommended : Math.Clamp(await Choose(ask, ct), 0, ask.Options.Count - 1);

    private static bool IsTrue(string value) =>
        value.Trim().ToLowerInvariant() is "true" or "yes" or "1";

    private static TimeSpan Min(TimeSpan a, TimeSpan b) =>
        a < b ? (a > TimeSpan.Zero ? a : TimeSpan.FromMilliseconds(1)) : (b > TimeSpan.Zero ? b : TimeSpan.FromMilliseconds(1));

    private static string Pid(ProcessSnapshot snap) => snap.Pid is { } pid ? $" (pid {pid})" : "";

    private static string Short(string command) => command.Length <= 60 ? command : command[..57] + "…";

    private static string Combined(ProcessSnapshot snap) =>
        snap.Errors.Length == 0 ? snap.Output : $"{snap.Output}\n[stderr]\n{snap.Errors}".TrimStart('\n');

    private static string Tail(ProcessSnapshot snap)
    {
        var text = Combined(snap);
        return text.Length <= 4_000 ? text : "…\n" + text[^4_000..];
    }

    private static string Clip(string text) =>
        text.Length <= MaxOutputChars ? text : text[..MaxOutputChars] + $"\n… truncated ({text.Length} chars total)";

    public void Dispose()
    {
        ProcessSupervisor? owned;
        lock (_gate) owned = _ownsProcesses ? _processes : null;
        owned?.Dispose();
    }
}
