namespace Agent.Common.Data.Entities;

/// <summary>
/// A single assignment of an <see cref="OrchestrationTask"/> attempt to a worker
/// (a hosted terminal/agent) within a run (mission W6). Records which worker got
/// the task and the outcome, so retries and supervision have an audit trail.
/// </summary>
public class OrchestrationDispatch
{
    public int Id { get; set; }

    public int RunId { get; set; }
    public int TaskId { get; set; }

    /// <summary>Worker identity — the target agent/terminal name.</summary>
    public string WorkerName { get; set; } = "";

    /// <summary>dispatched | done | failed | timed_out.</summary>
    public string Status { get; set; } = "dispatched";

    public DateTime DispatchedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>Last heartbeat time — supervision uses this to detect stalls.</summary>
    public DateTime? LastHeartbeatUtc { get; set; }
}
