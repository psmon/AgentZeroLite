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
    Cancelled
}

/// <param name="Index">1-based turn number within the run.</param>
public sealed record AgentStep(int Index, string Tool, string Detail, bool Ok);

/// <summary>Everything one <c>RunAsync</c> produced — what the CLI prints and what the session file records.</summary>
public sealed record AgentRun(
    StopReason Reason,
    string Text,
    IReadOnlyList<AgentStep> Steps,
    TimeSpan Elapsed)
{
    public bool Succeeded => Reason == StopReason.Final;

    /// <summary>Process exit code: 0 only for a run that actually answered.</summary>
    public int ExitCode => Reason switch
    {
        StopReason.Final => 0,
        StopReason.Cancelled => 130,
        _ => 1
    };
}
