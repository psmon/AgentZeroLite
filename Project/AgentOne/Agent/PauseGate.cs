namespace AgentOne.Agent;

/// <summary>
/// The pause between steps. A running turn cannot stop mid-request — the
/// model is answering, a command is running — so the loop asks this gate
/// before every step and waits there when the person pressed pause. Resume
/// may carry a refinement: what the person said while it was paused, which
/// the loop puts in front of the model as the next thing it reads.
///
/// One owner writes (the person, through the session); the loop only reads.
/// </summary>
public sealed class PauseGate
{
    private readonly Lock _gate = new();
    private TaskCompletionSource? _paused;
    private string? _refinement;

    /// <summary>True while the next step will wait.</summary>
    public bool IsPaused
    {
        get { lock (_gate) return _paused is not null; }
    }

    /// <summary>Holds the loop at its next step. Idempotent.</summary>
    public void Pause()
    {
        lock (_gate) _paused ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Lets the loop go on — with something new for the model to read first, when given.</summary>
    public void Resume(string? refinement = null)
    {
        TaskCompletionSource? release;
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(refinement)) _refinement = refinement.Trim();
            release = _paused;
            _paused = null;
        }
        release?.TrySetResult();
    }

    /// <summary>Waits while paused; returns at once otherwise. Cancelling the token releases the wait as cancelled.</summary>
    public async Task WaitAsync(CancellationToken ct)
    {
        Task? wait;
        lock (_gate) wait = _paused?.Task;
        if (wait is null) return;
        await wait.WaitAsync(ct);
    }

    /// <summary>The refinement left by the last resume, once; null when there was none.</summary>
    public string? TakeRefinement()
    {
        lock (_gate)
        {
            var r = _refinement;
            _refinement = null;
            return r;
        }
    }

    /// <summary>Forgets a pause and any refinement — the turn ended.</summary>
    public void Clear()
    {
        TaskCompletionSource? release;
        lock (_gate)
        {
            release = _paused;
            _paused = null;
            _refinement = null;
        }
        release?.TrySetResult();
    }
}

/// <summary>What the person meant by what they typed while the turn was paused.</summary>
public enum PauseVerdict
{
    /// <summary>Go on as it was.</summary>
    Resume,
    /// <summary>Abandon the turn.</summary>
    Stop,
    /// <summary>Go on, with the new instruction in front of the model.</summary>
    Refine
}

/// <summary>What the session did with a line typed during a pause.</summary>
/// <param name="Decision">The engine's judgement, when one was asked; null for the plain rule.</param>
/// <param name="Message">One line for the person: "resuming", "stopped", "refining: …".</param>
public sealed record PauseOutcome(PauseVerdict Verdict, Llm.Decision.Decision? Decision, string Message);
