namespace AgentOne.Agent;

public enum StopReason
{
    /// <summary>The model called "final" — the only clean ending.</summary>
    Final,
    /// <summary>Step budget exhausted before the model answered.</summary>
    MaxSteps,
    /// <summary>The model kept repeating one tool call.</summary>
    Repeat,
    /// <summary>The model never produced a parseable envelope.</summary>
    ParseFailure,
    /// <summary>The provider errored (network, auth, HTTP status).</summary>
    ProviderError,
    /// <summary>Smart mode decided a person has to settle this before anything runs.</summary>
    NeedsReview,
    Cancelled
}

/// <param name="Index">1-based turn number within the run.</param>
public sealed record AgentStep(int Index, string Tool, string Detail, bool Ok);

/// <summary>Everything one <c>RunAsync</c> produced — what the CLI prints and what the session file records.</summary>
/// <param name="Streamed">
/// How much of <paramref name="Text"/> was already shown as it was generated.
/// The caller prints the remainder, so a streamed answer is not repeated and a
/// provider that could not stream still prints in full.
/// </param>
public sealed record AgentRun(
    StopReason Reason,
    string Text,
    IReadOnlyList<AgentStep> Steps,
    TimeSpan Elapsed,
    string Streamed = "")
{
    /// <summary>The part of the answer nobody has seen yet.</summary>
    public string Unstreamed =>
        Streamed.Length > 0 && Text.StartsWith(Streamed, StringComparison.Ordinal)
            ? Text[Streamed.Length..]
            : Text;

    public bool Succeeded => Reason == StopReason.Final;

    /// <summary>Process exit code: 0 only for a run that actually answered.</summary>
    public int ExitCode => Reason switch
    {
        StopReason.Final => 0,
        StopReason.Cancelled => 130,
        StopReason.NeedsReview => 3,
        _ => 1
    };
}
