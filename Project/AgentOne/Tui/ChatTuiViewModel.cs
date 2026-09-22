using AgentOne.Agent;
using AgentOne.Commands;
using AgentOne.Services;
using R3;
using Termina.Input;
using Termina.Reactive;

namespace AgentOne.Tui;

/// <summary>One thing to put on the transcript.</summary>
public enum LineKind
{
    /// <summary>What the person typed.</summary>
    User,
    /// <summary>The answer is starting; the next deltas belong to it.</summary>
    AnswerStart,
    /// <summary>A fragment of the answer, no newline.</summary>
    Delta,
    /// <summary>The answer is complete.</summary>
    AnswerEnd,
    /// <summary>A note from the agent about what it did — a tool step, a decision.</summary>
    Note,
    /// <summary>Something that needs the person's attention.</summary>
    Alert
}

public readonly record struct TranscriptLine(LineKind Kind, string Text);

/// <summary>What the person asked the transcript to do: a page by key, a few lines by wheel.</summary>
public enum ScrollRequest { Up, Down, Bottom, WheelUp, WheelDown }

/// <summary>
/// Glue between the chat screen and the conversation. Keys go to the model,
/// turns go to the session, and everything the session reports comes back out
/// as transcript lines and a Revision bump. No rules live here.
/// </summary>
public sealed class ChatTuiViewModel : ReactiveViewModel
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Subject<TranscriptLine> _lines = new();
    private readonly Subject<ScrollRequest> _scroll = new();
    private bool _answering;

    /// <summary>A command waiting for the person's yes or no; the next line typed answers it.</summary>
    private TaskCompletionSource<bool>? _approval;

    /// <summary>The list /resume last printed, so "/resume 2" means the same row the person saw.</summary>
    private IReadOnlyList<SessionSummary> _resumable = [];

    public ChatTuiViewModel(ChatSession session, ChatTuiModel model)
    {
        Session = session;
        Model = model;
        if (session.Title is { } title) model.SetTitle(title);

        session.ActivityStarted += what => { Model.SetStatus("… " + what); Bump(); };
        session.StepCompleted += step =>
        {
            if (step.Tool is "final" or "unwrapped") return;

            // The reasoning hand-off means the draft on screen is about to be
            // replaced: close it, so the final answer starts on its own line.
            if (step.Tool == ReasoningSubtask.Tag && _answering)
            {
                _lines.OnNext(new TranscriptLine(LineKind.AnswerEnd, ""));
                _answering = false;
            }

            _lines.OnNext(new TranscriptLine(LineKind.Note, StepLine(step)));
        };
        session.AnswerDelta += fragment =>
        {
            if (!_answering) { _answering = true; _lines.OnNext(new TranscriptLine(LineKind.AnswerStart, "")); }
            _lines.OnNext(new TranscriptLine(LineKind.Delta, fragment));
        };
        session.Decided += note =>
        {
            if (note.Kind == "escalation" && _answering && note.Verdict.StartsWith("escalating", StringComparison.Ordinal))
            {
                _lines.OnNext(new TranscriptLine(LineKind.AnswerEnd, ""));
                _answering = false;
            }
            var confidence = note.Decision.Ok ? $"  (confidence {note.Decision.Confidence:0.00})" : "";
            _lines.OnNext(new TranscriptLine(LineKind.Note, $"{note.Kind}: {note.Verdict}{confidence}"));
        };
        session.Noted += note => _lines.OnNext(new TranscriptLine(LineKind.Note, note));
        session.TitleChanged += title =>
        {
            Model.SetTitle(title);
            _lines.OnNext(new TranscriptLine(LineKind.Note, $"task: {title}"));
            Bump();
        };

        // A command the gate will not run on its own: park the turn, ask on the
        // input line, and let the next line typed settle it.
        session.Approver = (request, ct) =>
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _approval = tcs;
            ct.Register(() => tcs.TrySetResult(false));

            _lines.OnNext(new TranscriptLine(LineKind.Alert, $"⚠ run this command?  {request.Command}"));
            _lines.OnNext(new TranscriptLine(LineKind.Note, $"in {request.WorkingDirectory} · not run unasked because: {request.Reason}"));
            Model.SetAwaitingPerson(true);
            Model.SetBusy(false, "approve? y runs it, anything else skips it");
            Bump();
            return tcs.Task;
        };
    }

    private static string StepLine(AgentStep step)
    {
        var seconds = step.ElapsedMs > 0 ? $"  ({step.ElapsedMs / 1000.0:0.0}s)" : "";
        var detail = step.Tool is ReasoningSubtask.Tag or ReasoningSubtask.DesignTag ? "  " + step.Detail : "";
        return $"{(step.Ok ? "✓" : "✗")} {step.Tool}{detail}{seconds}";
    }

    public ChatSession Session { get; }
    public ChatTuiModel Model { get; }

    /// <summary>Bumped after every change the page should repaint for.</summary>
    public ReactiveProperty<int> Revision { get; } = new(0);

    /// <summary>Lines for the page to append to the transcript, in order.</summary>
    public Observable<TranscriptLine> Lines => _lines;

    /// <summary>Scroll requests from the keyboard, for the page that owns the transcript.</summary>
    public Observable<ScrollRequest> Scroll => _scroll;

    /// <summary>The page reports where the transcript is; the header shows it.</summary>
    public void SetScrolled(bool scrolledUp, int offset)
    {
        if (Model.SetScrolled(scrolledUp, offset)) Bump();
    }

    public override void OnActivated()
    {
        Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleKey)
            .DisposeWith(Subscriptions);

        // The wheel scrolls the transcript wherever the pointer is: there is
        // nothing else on this screen that could want it. A tick arrives as a
        // MouseScrollEvent from SGR mouse reporting, or as a MouseEvent with
        // EventType Scroll (the virtual source, and some terminals) — take both.
        Input.OfType<IInputEvent, MouseScrollEvent>()
            .Subscribe(wheel => Wheel(up: wheel.Delta > 0))
            .DisposeWith(Subscriptions);

        Input.OfType<IInputEvent, MouseEvent>()
            .Where(mouse => mouse.EventType == MouseEventType.Scroll)
            .Subscribe(mouse => Wheel(up: mouse.Button == MouseButton.WheelUp))
            .DisposeWith(Subscriptions);
    }

    private void Wheel(bool up)
    {
        _scroll.OnNext(up ? ScrollRequest.WheelUp : ScrollRequest.WheelDown);
        Bump();
    }

    private void HandleKey(KeyPressed key)
    {
        switch (Model.HandleKey(key.KeyInfo))
        {
            case ChatEffect.Submit:
                _ = SubmitAsync(Model.TakeInput());
                break;

            case ChatEffect.ToggleMode:
                Model.SetStatus(Session.TryToggleSmart(out var message) ? message : "✗ " + message);
                Model.SetSmart(Session.Smart);
                break;

            case ChatEffect.ScrollUp:
                _scroll.OnNext(ScrollRequest.Up);
                break;

            case ChatEffect.ScrollDown:
                _scroll.OnNext(ScrollRequest.Down);
                break;

            case ChatEffect.ScrollToBottom:
                _scroll.OnNext(ScrollRequest.Bottom);
                break;

            case ChatEffect.ShowStatus:
                ShowStatus();
                break;

            case ChatEffect.Quit:
                _approval?.TrySetResult(false);
                _cts.Cancel();
                Shutdown();
                return;
        }

        Bump();
    }

    private void ShowStatus()
    {
        _lines.OnNext(new TranscriptLine(LineKind.Note, "── status ──"));
        foreach (var row in Session.Stats().Describe())
            _lines.OnNext(new TranscriptLine(LineKind.Note, row));
    }

    /// <summary>/resume alone lists; /resume N loads that row and replays it on screen.</summary>
    private void Resume(string argument)
    {
        if (argument.Length == 0)
        {
            _resumable = Session.ListSessions();
            if (_resumable.Count == 0)
            {
                _lines.OnNext(new TranscriptLine(LineKind.Note, "no saved sessions for this workspace yet"));
                return;
            }

            _lines.OnNext(new TranscriptLine(LineKind.Note, "── sessions in this workspace (newest first) · /resume <n> to pick one ──"));
            var i = 0;
            foreach (var s in _resumable)
            {
                var current = s.Path == Session.LogPath ? "  (this one)" : "";
                _lines.OnNext(new TranscriptLine(LineKind.Note, $"{++i,2}. {s.Started:MM-dd HH:mm} · {s.Turns} turns · {s.Title}{current}"));
            }
            return;
        }

        if (_resumable.Count == 0) _resumable = Session.ListSessions();
        if (!int.TryParse(argument, out var n) || n < 1 || n > _resumable.Count)
        {
            _lines.OnNext(new TranscriptLine(LineKind.Alert, $"/resume needs a number from the list (1..{_resumable.Count}); /resume alone lists"));
            return;
        }

        var chosen = _resumable[n - 1];
        var entries = Session.Resume(chosen.Path);
        Model.SetTitle(Session.Title ?? "");

        _lines.OnNext(new TranscriptLine(LineKind.Note, $"── resumed {chosen.Id} · {chosen.Title} ──"));
        Replay(entries);
        _lines.OnNext(new TranscriptLine(LineKind.Note, "── continuing from here ──"));
    }

    /// <summary>The saved transcript, drawn the way it was drawn the first time.</summary>
    private void Replay(IReadOnlyList<SessionEntry> entries)
    {
        foreach (var e in entries)
        {
            switch (e.Kind)
            {
                case "prompt":
                    _lines.OnNext(new TranscriptLine(LineKind.User, e.Text));
                    break;
                case "step":
                    if (e.Tool is "final" or "unwrapped") break;
                    _lines.OnNext(new TranscriptLine(LineKind.Note,
                        StepLine(new AgentStep(0, e.Tool ?? "?", e.Text, e.Ok ?? true, e.ElapsedMs ?? 0))));
                    break;
                case "route" or "scope" or "safety" or "escalation":
                    _lines.OnNext(new TranscriptLine(LineKind.Note, $"{e.Kind}: {e.Text}"));
                    break;
                case "result":
                    if (e.Ok == true)
                    {
                        _lines.OnNext(new TranscriptLine(LineKind.AnswerStart, ""));
                        _lines.OnNext(new TranscriptLine(LineKind.Delta, e.Text));
                        _lines.OnNext(new TranscriptLine(LineKind.AnswerEnd, ""));
                    }
                    else
                    {
                        _lines.OnNext(new TranscriptLine(LineKind.Alert, $"stopped ({e.Tool}): {e.Text}"));
                    }
                    break;
                case "title":
                    _lines.OnNext(new TranscriptLine(LineKind.Note, $"task: {e.Text}"));
                    break;
                case "resumed":
                    _lines.OnNext(new TranscriptLine(LineKind.Note, "(resumed earlier)"));
                    break;
            }
        }
    }

    private async Task SubmitAsync(string text)
    {
        // A parked command takes the line as its answer; the turn goes on from there.
        if (_approval is { } approval)
        {
            _approval = null;
            var yes = ChatCommand.IsYes(text);
            _lines.OnNext(new TranscriptLine(LineKind.User, yes ? "y — run it" : "n — skip it"));
            Model.SetAwaitingPerson(false);
            Model.SetBusy(true, "… continuing");
            Bump();
            approval.TrySetResult(yes);
            return;
        }

        if (text is "/exit" or "/quit") { _cts.Cancel(); Shutdown(); return; }

        if (text is "/reset" or "/new")
        {
            if (text == "/new") Session.NewSession(); else Session.Reset();
            Model.SetTitle("");
            _lines.OnNext(new TranscriptLine(LineKind.Note,
                text == "/new" ? $"new session: {Session.LogPath ?? "not saved"}" : "conversation cleared"));
            Bump();
            return;
        }

        if (text == "/status")
        {
            ShowStatus();
            Bump();
            return;
        }

        if (text == "/resume" || text.StartsWith("/resume ", StringComparison.Ordinal))
        {
            Resume(text.Length > 7 ? text[7..].Trim() : "");
            Bump();
            return;
        }

        _lines.OnNext(new TranscriptLine(LineKind.User, text));
        Model.SetBusy(true, "… thinking");
        Model.CountTurn();
        _answering = false;
        Bump();

        AgentRun? run;
        try
        {
            run = await Session.SubmitAsync(text, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _lines.OnNext(new TranscriptLine(LineKind.Alert, "✗ " + ex.Message));
            Model.SetBusy(false, "failed");
            Bump();
            return;
        }

        if (run is null)
        {
            Model.SetBusy(false);
            Bump();
            return;
        }

        if (run.Succeeded)
        {
            if (!_answering) _lines.OnNext(new TranscriptLine(LineKind.AnswerStart, ""));
            if (run.Unstreamed.Length > 0) _lines.OnNext(new TranscriptLine(LineKind.Delta, run.Unstreamed));
            _lines.OnNext(new TranscriptLine(LineKind.AnswerEnd, ""));
        }
        else
        {
            _lines.OnNext(new TranscriptLine(LineKind.Alert, $"stopped ({run.Reason}): {run.Text}"));
        }

        Model.SetAwaitingPerson(false);
        Model.SetBusy(false, $"done in {run.Elapsed.TotalSeconds:0.0}s · {run.Steps.Count} steps");
        Bump();
    }

    private void Bump() => Revision.Value++;

    public override void Dispose()
    {
        _approval?.TrySetResult(false);
        _cts.Cancel();
        _cts.Dispose();
        _lines.Dispose();
        _scroll.Dispose();
        Revision.Dispose();
        base.Dispose();
    }
}
