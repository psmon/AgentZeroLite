using AgentOne.Agent;
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
    /// <summary>A note from the agent about what it did — a tool step, a plan.</summary>
    Note,
    /// <summary>Something that needs the person's attention.</summary>
    Alert
}

public readonly record struct TranscriptLine(LineKind Kind, string Text);

/// <summary>What the person asked the transcript to do.</summary>
public enum ScrollRequest { Up, Down, Bottom }

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

    public ChatTuiViewModel(ChatSession session, ChatTuiModel model)
    {
        Session = session;
        Model = model;

        session.ActivityStarted += what => { Model.SetStatus("… " + what); Bump(); };
        session.StepCompleted += step =>
        {
            if (step.Tool is "final" or "unwrapped") return;
            var seconds = step.ElapsedMs > 0 ? $"  ({step.ElapsedMs / 1000.0:0.0}s)" : "";
            _lines.OnNext(new TranscriptLine(LineKind.Note, $"{(step.Ok ? "✓" : "✗")} {step.Tool}{seconds}"));
        };
        session.AnswerDelta += fragment =>
        {
            if (!_answering) { _answering = true; _lines.OnNext(new TranscriptLine(LineKind.AnswerStart, "")); }
            _lines.OnNext(new TranscriptLine(LineKind.Delta, fragment));
        };
        session.PlanMade += plan =>
        {
            if (plan.Decision is not { Ok: true } d) return;
            var verdict = plan.NeedsReview ? "needs you" : plan.Confident ? $"→ {d.Choice}" : "unsure";
            _lines.OnNext(new TranscriptLine(LineKind.Note, $"plan: {verdict}  (confidence {d.Confidence:0.00})"));
        };
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

            case ChatEffect.Quit:
                _cts.Cancel();
                Shutdown();
                return;
        }

        Bump();
    }

    private async Task SubmitAsync(string text)
    {
        if (text is "/exit" or "/quit") { _cts.Cancel(); Shutdown(); return; }

        if (text == "/reset")
        {
            Session.Reset();
            Model.SetAwaitingPerson(false);
            _lines.OnNext(new TranscriptLine(LineKind.Note, "conversation cleared"));
            Bump();
            return;
        }

        _lines.OnNext(new TranscriptLine(LineKind.User, text.Length == 0 ? "(accepted)" : text));
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
            // Paused for the person: show what the decision was about.
            if (Session.Pending is { } pending) ShowPause(pending);
            Model.SetAwaitingPerson(true);
            Model.SetBusy(false, "waiting for you — answer, pick a number, or press Enter to accept");
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

    private void ShowPause(PendingReview pending)
    {
        var decision = pending.Plan.Decision!;
        var head = pending.Reason == PauseReason.NeedsPerson
            ? $"⚠ this needs you (confidence {decision.Confidence:0.00})"
            : $"⚠ unsure — confidence {decision.Confidence:0.00} is below {Session.ConfidenceFloor:0.00}";

        _lines.OnNext(new TranscriptLine(LineKind.Alert, head));
        if (pending.Reason == PauseReason.NeedsPerson)
            _lines.OnNext(new TranscriptLine(LineKind.Note, SmartTurn.ReviewDescription));

        var i = 0;
        foreach (var option in pending.Plan.Options.Where(o => o.Name != SmartTurn.ReviewOption))
        {
            var probability = decision.Probabilities.TryGetValue(option.Name, out var p) ? p : 0;
            _lines.OnNext(new TranscriptLine(LineKind.Note, $"  {++i}. {option.Name}  ({probability:0.00})  {option.Description}"));
        }
    }

    private void Bump() => Revision.Value++;

    public override void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _lines.Dispose();
        _scroll.Dispose();
        Revision.Dispose();
        base.Dispose();
    }
}
