using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AgentOne.Services;

namespace AgentOne.Agent;

/// <summary>
/// A <see cref="ChatSession"/> behind a local named pipe. A turn belongs to
/// the <b>server</b>, not to the connection that asked for it: clients attach
/// to the running turn, get its events as they happen and its one result at
/// the end, and may leave at any time without taking the turn down — a long
/// request outlives a caller's timeout, and `agent-one session wait` picks it
/// up again. Connections are served concurrently, so `status` answers while a
/// turn runs (what it is doing, for how long, how many steps) instead of
/// queueing behind it.
///
/// Ops: ask (optionally <c>detach</c>: accepted at once, runs on its own),
/// wait (attach to the running turn, or get the last result), status, stop,
/// and answer (the reply to an ask/choose raised mid-turn). The first client
/// still attached to a turn is the one asked back for approvals and design
/// choices; with nobody attached, a command is refused (unless the turn was
/// started with yes) and a choice takes the recommendation.
/// </summary>
public sealed class SessionServer
{
    private static readonly UTF8Encoding NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IAgentSession _session;
    private readonly string _pipe;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly List<Task> _handlers = [];

    private Turn? _turn;
    private PipeEvent? _last;

    public SessionServer(IAgentSession session, string pipeName)
    {
        _session = session;
        _pipe = pipeName;

        // Subscribed once for the server's life; each event goes to whoever is
        // attached to the running turn. An event with no turn running — a
        // knowledge note the previous turn's background work raised late — is
        // dropped rather than shown under the next, unrelated request.
        _session.ActivityStarted += what => Broadcast(t => t.Activity = what, new PipeEvent { Event = "activity", Text = what });
        _session.StepCompleted += step => Broadcast(t => { if (step.Tool is not ("final" or "unwrapped")) t.Steps++; },
            new PipeEvent { Event = "step", Tool = step.Tool, Ok = step.Ok, Text = step.Detail, ElapsedMs = step.ElapsedMs });
        _session.AnswerDelta += fragment => Broadcast(t => t.Streamed += fragment.Length, new PipeEvent { Event = "delta", Text = fragment });
        _session.Noted += note => Broadcast(null, new PipeEvent { Event = "note", Text = note });
        _session.Decided += note => Broadcast(null, new PipeEvent { Event = "decided", Kind = note.Kind, Text = note.Verdict, Confidence = note.Decision.Confidence, Ok = note.Decision.Ok });
        _session.TitleChanged += title => Broadcast(null, new PipeEvent { Event = "title", Text = title });
        _session.DesignMade += lines => Broadcast(null, new PipeEvent { Event = "design", Options = [.. lines] });

        _session.Approver = ApproveAsync;
        _session.Chooser = ChooseAsync;
    }

    public string PipeName => _pipe;

    /// <summary>Set once a client has asked the session to stop; the accept loop ends after that.</summary>
    public bool StopRequested => _stop.IsCancellationRequested;

    /// <summary>Signalled once the first listener is up — for tests and `start`.</summary>
    public TaskCompletionSource Listening { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>True while a turn runs.</summary>
    public bool Busy { get { lock (_gate) return _turn is not null; } }

    /// <summary>Accepts clients, each on its own task, until one says stop or the token is cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);

        while (!linked.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(_pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            Listening.TrySetResult();

            try
            {
                await pipe.WaitForConnectionAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }

            var handler = Task.Run(async () =>
            {
                await using (pipe)
                {
                    try { await ServeAsync(pipe, linked.Token); }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or ObjectDisposedException)
                    {
                        // A client that went away takes nothing down with it — the turn runs on.
                    }
                }
            }, CancellationToken.None);

            lock (_gate)
            {
                _handlers.RemoveAll(h => h.IsCompleted);
                _handlers.Add(handler);
            }
        }

        // Stop: end the running turn, give the attached clients their result, then leave.
        Turn? running;
        Task[] handlers;
        lock (_gate) { running = _turn; handlers = [.. _handlers]; }
        if (running is not null)
        {
            try { running.Cancel.Cancel(); } catch (ObjectDisposedException) { /* just ended */ }
            await Task.WhenAny(running.Done.Task, Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None));
        }
        await Task.WhenAny(Task.WhenAll(handlers), Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None));
    }

    // ------------------------------------------------------------ requests

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var client = new Client(
            new StreamReader(pipe, NoBom, false, 4096, leaveOpen: true),
            new StreamWriter(pipe, NoBom, 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" });

        var first = await client.Reader.ReadLineAsync(ct);
        if (first is null) return;
        var request = JsonSerializer.Deserialize(first, AgentOneWireJson.Default.PipeRequest);
        if (request is null) { await client.SendAsync(new PipeEvent { Event = "error", Text = "unreadable request" }); return; }

        switch (request.Op)
        {
            case "status":
                await client.SendAsync(new PipeEvent { Event = "result", Ok = true, Kind = "Status", Text = StatusText() });
                return;

            case "stop":
                await client.SendAsync(new PipeEvent { Event = "result", Ok = true, Kind = "Stopped", Text = "background session stopped" });
                _stop.Cancel();
                return;

            case "wait":
                await WaitAsync(client, ct);
                return;

            case "ask":
                await AskAsync(client, request, ct);
                return;

            default:
                await client.SendAsync(new PipeEvent { Event = "error", Text = $"unknown op '{request.Op}'" });
                return;
        }
    }

    private async Task AskAsync(Client client, PipeRequest request, CancellationToken ct)
    {
        var text = request.Text.Trim();

        if (text == "/status")
        {
            await client.SendAsync(new PipeEvent { Event = "result", Ok = true, Kind = "Status", Text = StatusText() });
            return;
        }

        Turn? turn = null, running;
        var slash = text is "/new" or "/reset";
        lock (_gate)
        {
            running = _turn;
            if (running is null && !slash)
            {
                turn = new Turn(text, request.Yes, CancellationTokenSource.CreateLinkedTokenSource(_stop.Token));
                if (!request.Detach) turn.Clients.Add(client);
                _turn = turn;
            }
        }

        if (running is not null)
        {
            await client.SendAsync(new PipeEvent
            {
                Event = "error", Kind = "Busy",
                Text = $"busy — still working on \"{Clip(running.Text, 60)}\" ({Seconds(running.Clock.Elapsed)}). " +
                       "`agent-one session wait` for its result, `agent-one session status` for its progress"
            });
            return;
        }

        if (slash)
        {
            if (text == "/new") _session.NewSession(); else _session.Reset();
            lock (_gate) _last = null;
            await client.SendAsync(new PipeEvent { Event = "result", Ok = true, Kind = "Reset", Text = text == "/new" ? $"new session: {_session.LogPath ?? "not saved"}" : "conversation cleared" });
            return;
        }

        _ = Task.Run(() => RunTurnAsync(turn!), CancellationToken.None);

        if (request.Detach)
        {
            await client.SendAsync(new PipeEvent
            {
                Event = "result", Ok = true, Kind = "Accepted", Request = text,
                Text = "accepted — running in the background session. `agent-one session wait` for the result, `agent-one session status` for progress"
            });
            return;
        }

        await FollowAsync(client, turn!, ct);
    }

    /// <summary>Attach to the running turn; with none running, the last result (or say there is none).</summary>
    private async Task WaitAsync(Client client, CancellationToken ct)
    {
        Turn? turn;
        PipeEvent? last;
        PipeEvent? attached = null;
        lock (_gate)
        {
            turn = _turn;
            last = _last;
            if (turn is not null)
            {
                // The snapshot goes out before the client joins, so it is the first line it reads.
                attached = new PipeEvent
                {
                    Event = "attached", Text = turn.Text, ElapsedMs = (long)turn.Clock.Elapsed.TotalMilliseconds,
                    Steps = turn.Steps, Kind = turn.Activity, Streamed = turn.Streamed
                };
            }
        }

        if (turn is null)
        {
            await client.SendAsync(last ?? new PipeEvent { Event = "error", Kind = "Idle", Text = "nothing has run in this session yet" });
            return;
        }

        await client.SendAsync(attached!);
        // A turn that ended in between has its result waiting in Done already.
        lock (_gate) { if (ReferenceEquals(_turn, turn)) turn.Clients.Add(client); }
        await FollowAsync(client, turn, ct);
    }

    /// <summary>Wait for the turn's result and hand it to this client. Leaving early is allowed.</summary>
    private static async Task FollowAsync(Client client, Turn turn, CancellationToken ct)
    {
        PipeEvent result;
        try { result = await turn.Done.Task.WaitAsync(ct); }
        catch (OperationCanceledException) { result = await turn.Done.Task; }
        await client.SendAsync(result);
    }

    private async Task RunTurnAsync(Turn turn)
    {
        PipeEvent result;
        try
        {
            var run = await _session.SubmitAsync(turn.Text, turn.Cancel.Token);
            result = run is null
                ? new PipeEvent { Event = "result", Ok = true, Kind = "Empty", Text = "" }
                : new PipeEvent
                {
                    Event = "result",
                    Ok = run.Succeeded,
                    Kind = run.Reason.ToString(),
                    Text = run.Text,
                    Streamed = run.Streamed.Length > 0 && run.Text.StartsWith(run.Streamed, StringComparison.Ordinal) ? run.Streamed.Length : 0,
                    ElapsedMs = (long)turn.Clock.Elapsed.TotalMilliseconds,
                    Steps = turn.Steps,
                    Turn = SafeTurns(),
                    Request = turn.Text
                };
        }
        catch (OperationCanceledException)
        {
            result = new PipeEvent { Event = "result", Ok = false, Kind = "Cancelled", Text = "the turn was cancelled", ElapsedMs = (long)turn.Clock.Elapsed.TotalMilliseconds, Request = turn.Text };
        }
        catch (Exception ex)
        {
            result = new PipeEvent { Event = "error", Text = ex.Message, Request = turn.Text };
        }

        lock (_gate)
        {
            if (result.Event == "result") _last = result;
            if (ReferenceEquals(_turn, turn)) _turn = null;
        }
        turn.Cancel.Dispose();
        turn.Done.TrySetResult(result);
    }

    // ------------------------------------------------------------- events

    private void Broadcast(Action<Turn>? track, PipeEvent e)
    {
        Client[] clients;
        lock (_gate)
        {
            if (_turn is not { } turn) return;
            track?.Invoke(turn);
            clients = [.. turn.Clients];
        }

        foreach (var client in clients)
        {
            // A client that is gone is forgotten; the turn does not notice.
            if (!client.TrySend(e)) Forget(client);
        }
    }

    private void Forget(Client client)
    {
        lock (_gate) _turn?.Clients.Remove(client);
    }

    private async Task<bool> ApproveAsync(ApprovalRequest approval, CancellationToken token)
    {
        var turn = Current();
        if (turn is null) return false;
        if (turn.Yes)
        {
            Broadcast(null, new PipeEvent { Event = "note", Text = $"running (yes): {approval.Command}" });
            return true;
        }

        var answer = await AskAttachedAsync(turn, new PipeEvent { Event = "ask", Text = approval.Command, Reason = approval.Reason }, token);
        if (answer is null)
        {
            Broadcast(null, new PipeEvent { Event = "note", Text = $"not run — nobody attached to approve it (ask with --yes): {approval.Command}" });
            return false;
        }
        return Commands.ChatCommand.IsYes(answer);
    }

    private async Task<string> ChooseAsync(ChoiceRequest choice, CancellationToken token)
    {
        var turn = Current();
        if (turn is null) return "";
        var question = new PipeEvent { Event = "choose", Reason = choice.Question, Options = [.. choice.Options], Recommended = choice.Recommended };
        return await AskAttachedAsync(turn, question, token) ?? "";
    }

    /// <summary>The question goes to the first attached client still there; null when there is nobody to ask.</summary>
    private async Task<string?> AskAttachedAsync(Turn turn, PipeEvent question, CancellationToken token)
    {
        while (true)
        {
            Client? asked;
            lock (_gate) asked = turn.Clients.FirstOrDefault();
            if (asked is null) return null;

            var answer = await asked.AskAsync(question, token);
            if (answer is not null) return answer;
            Forget(asked);
        }
    }

    private Turn? Current() { lock (_gate) return _turn; }

    // ------------------------------------------------------------- status

    private string StatusText()
    {
        var lines = new List<string>();
        Turn? turn;
        PipeEvent? last;
        int attached = 0;
        lock (_gate) { turn = _turn; last = _last; attached = turn?.Clients.Count ?? 0; }

        if (turn is not null)
        {
            lines.Add($"state     working · {Seconds(turn.Clock.Elapsed)} · {turn.Steps} step{(turn.Steps == 1 ? "" : "s")}" +
                      (turn.Activity.Length > 0 ? $" · now: {turn.Activity}" : "") +
                      (attached == 0 ? " · nobody attached" : ""));
            lines.Add($"request   {Clip(turn.Text, 100)}");
        }
        else
        {
            lines.Add("state     idle");
            if (last is not null)
                lines.Add($"last      {(last.Ok == true ? "✓" : "✗")} {last.Kind} · {Seconds(TimeSpan.FromMilliseconds(last.ElapsedMs ?? 0))} · \"{Clip(last.Request ?? "", 60)}\"");
        }

        try { lines.AddRange(_session.Stats().Describe()); }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or AggregateException) { lines.Add("stats     unavailable: " + ex.Message); }
        return string.Join("\n", lines);
    }

    private int? SafeTurns()
    {
        try { return _session.Stats().Counters.Turns; }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or AggregateException) { return null; }
    }

    private static string Clip(string text, int max)
    {
        var flat = text.ReplaceLineEndings(" ");
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }

    internal static string Seconds(TimeSpan t) => t.TotalSeconds < 60 ? $"{t.TotalSeconds:0.0}s" : $"{(int)t.TotalMinutes}m{t.Seconds:00}s";

    // -------------------------------------------------------------- types

    private sealed class Turn(string text, bool yes, CancellationTokenSource cancel)
    {
        public string Text { get; } = text;
        public bool Yes { get; } = yes;
        public CancellationTokenSource Cancel { get; } = cancel;
        public System.Diagnostics.Stopwatch Clock { get; } = System.Diagnostics.Stopwatch.StartNew();
        public TaskCompletionSource<PipeEvent> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<Client> Clients { get; } = [];
        public string Activity { get; set; } = "";
        public int Steps { get; set; }

        /// <summary>Answer characters already sent — a client attaching after the first one has missed some.</summary>
        public int Streamed { get; set; }
    }

    /// <summary>One connection. Writes are serialised; a failed write marks it gone.</summary>
    private sealed class Client(StreamReader reader, StreamWriter writer)
    {
        private readonly SemaphoreSlim _write = new(1, 1);

        public StreamReader Reader { get; } = reader;
        public bool Gone { get; private set; }

        public async Task SendAsync(PipeEvent e)
        {
            await _write.WaitAsync();
            try { await writer.WriteLineAsync(JsonSerializer.Serialize(e, AgentOneWireJson.Default.PipeEvent)); }
            finally { _write.Release(); }
        }

        public bool TrySend(PipeEvent e)
        {
            if (Gone) return false;
            try { SendAsync(e).GetAwaiter().GetResult(); return true; }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { Gone = true; return false; }
        }

        /// <summary>Asks and reads the "answer" line. Null when the client is gone.</summary>
        public async Task<string?> AskAsync(PipeEvent question, CancellationToken ct)
        {
            if (!TrySend(question)) return null;
            try
            {
                var line = await Reader.ReadLineAsync(ct);
                if (line is null) { Gone = true; return null; }
                var reply = JsonSerializer.Deserialize(line, AgentOneWireJson.Default.PipeRequest);
                return reply?.Op == "answer" ? reply.Text : "";
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonException)
            {
                Gone = true;
                return null;
            }
        }
    }
}
