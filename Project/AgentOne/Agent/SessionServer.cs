using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AgentOne.Services;

namespace AgentOne.Agent;

/// <summary>
/// A <see cref="ChatSession"/> behind a local named pipe: one client at a
/// time sends a request, gets the turn's events as they happen and one result
/// at the end, and can be asked back — a command to approve, a design choice
/// to make — on the same connection. This is what `agent-one ask` talks to,
/// what the chat mode's self-test drives, and what another agent scripts
/// against. The session itself is the same one the window and the REPL use.
/// </summary>
public sealed class SessionServer
{
    private static readonly UTF8Encoding NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ChatSession _session;
    private readonly string _pipe;
    private readonly CancellationTokenSource _stop = new();

    public SessionServer(ChatSession session, string pipeName)
    {
        _session = session;
        _pipe = pipeName;
    }

    public string PipeName => _pipe;

    /// <summary>Set once a client has asked the session to stop; the accept loop ends after that connection.</summary>
    public bool StopRequested => _stop.IsCancellationRequested;

    /// <summary>Signalled after the first connection has been accepted or the loop is waiting — for tests.</summary>
    public TaskCompletionSource Listening { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Accepts clients until one says stop or the token is cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);

        while (!linked.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            Listening.TrySetResult();

            try
            {
                await pipe.WaitForConnectionAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await ServeAsync(pipe, linked.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException)
            {
                // A client that went away mid-turn takes nothing down with it.
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, NoBom, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(pipe, NoBom, 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        var gate = new SemaphoreSlim(1, 1);

        async Task SendAsync(PipeEvent e)
        {
            await gate.WaitAsync(ct);
            try { await writer.WriteLineAsync(JsonSerializer.Serialize(e, AgentOneWireJson.Default.PipeEvent)); }
            finally { gate.Release(); }
        }
        void Send(PipeEvent e) => SendAsync(e).GetAwaiter().GetResult();

        async Task<string> AskBackAsync(PipeEvent question)
        {
            await SendAsync(question);
            var line = await reader.ReadLineAsync(ct);
            if (line is null) return "";
            var reply = JsonSerializer.Deserialize(line, AgentOneWireJson.Default.PipeRequest);
            return reply?.Op == "answer" ? reply.Text : "";
        }

        var first = await reader.ReadLineAsync(ct);
        if (first is null) return;
        var request = JsonSerializer.Deserialize(first, AgentOneWireJson.Default.PipeRequest);
        if (request is null) { await SendAsync(new PipeEvent { Event = "error", Text = "unreadable request" }); return; }

        switch (request.Op)
        {
            case "status":
                await SendAsync(new PipeEvent { Event = "result", Ok = true, Kind = "Status", Text = string.Join("\n", _session.Stats().Describe()) });
                return;

            case "stop":
                await SendAsync(new PipeEvent { Event = "result", Ok = true, Kind = "Stopped", Text = "background session stopped" });
                _stop.Cancel();
                return;

            case "ask":
                break;

            default:
                await SendAsync(new PipeEvent { Event = "error", Text = $"unknown op '{request.Op}'" });
                return;
        }

        // Every event of the turn goes down the pipe as it happens; the
        // subscriptions are undone when the turn is over, whatever happened.
        var streamed = 0;
        Action<string> onActivity = what => Send(new PipeEvent { Event = "activity", Text = what });
        Action<AgentStep> onStep = step => Send(new PipeEvent { Event = "step", Tool = step.Tool, Ok = step.Ok, Text = step.Detail, ElapsedMs = step.ElapsedMs });
        Action<string> onDelta = fragment => { streamed += fragment.Length; Send(new PipeEvent { Event = "delta", Text = fragment }); };
        Action<string> onNote = note => Send(new PipeEvent { Event = "note", Text = note });
        Action<SmartNote> onDecided = note => Send(new PipeEvent { Event = "decided", Kind = note.Kind, Text = note.Verdict, Confidence = note.Decision.Confidence, Ok = note.Decision.Ok });
        Action<string> onTitle = title => Send(new PipeEvent { Event = "title", Text = title });
        Action<IReadOnlyList<string>> onDesign = lines => Send(new PipeEvent { Event = "design", Options = [.. lines] });

        _session.ActivityStarted += onActivity;
        _session.StepCompleted += onStep;
        _session.AnswerDelta += onDelta;
        _session.Noted += onNote;
        _session.Decided += onDecided;
        _session.TitleChanged += onTitle;
        _session.DesignMade += onDesign;

        var yes = request.Yes;
        _session.Approver = async (approval, token) =>
        {
            if (yes) { await SendAsync(new PipeEvent { Event = "note", Text = $"running (yes): {approval.Command}" }); return true; }
            var answer = await AskBackAsync(new PipeEvent { Event = "ask", Text = approval.Command, Reason = approval.Reason });
            return Commands.ChatCommand.IsYes(answer);
        };
        _session.Chooser = async (choice, token) =>
            await AskBackAsync(new PipeEvent { Event = "choose", Reason = choice.Question, Options = [.. choice.Options], Recommended = choice.Recommended });

        try
        {
            var text = request.Text.Trim();

            if (text == "/status")
            {
                await SendAsync(new PipeEvent { Event = "result", Ok = true, Kind = "Status", Text = string.Join("\n", _session.Stats().Describe()) });
                return;
            }
            if (text is "/new" or "/reset")
            {
                if (text == "/new") _session.NewSession(); else _session.Reset();
                await SendAsync(new PipeEvent { Event = "result", Ok = true, Kind = "Reset", Text = text == "/new" ? $"new session: {_session.LogPath ?? "not saved"}" : "conversation cleared" });
                return;
            }

            var run = await _session.SubmitAsync(text, ct);
            if (run is null)
            {
                await SendAsync(new PipeEvent { Event = "result", Ok = true, Kind = "Empty", Text = "" });
                return;
            }

            await SendAsync(new PipeEvent
            {
                Event = "result",
                Ok = run.Succeeded,
                Kind = run.Reason.ToString(),
                Text = run.Text,
                Streamed = run.Streamed.Length > 0 && run.Text.StartsWith(run.Streamed, StringComparison.Ordinal) ? run.Streamed.Length : 0,
                ElapsedMs = (long)run.Elapsed.TotalMilliseconds
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await SendAsync(new PipeEvent { Event = "error", Text = ex.Message });
        }
        finally
        {
            _session.ActivityStarted -= onActivity;
            _session.StepCompleted -= onStep;
            _session.AnswerDelta -= onDelta;
            _session.Noted -= onNote;
            _session.Decided -= onDecided;
            _session.TitleChanged -= onTitle;
            _session.DesignMade -= onDesign;
            _session.Approver = null;
            _session.Chooser = null;
        }
    }
}
