namespace Agent.Common.Data.Entities;

/// <summary>
/// One unit of work in an <see cref="OrchestrationRun"/> (mission W6). Carries
/// the prompt handed to a worker agent and its dependencies (as a JSON array of
/// task keys) which form the run's DAG.
/// </summary>
public class OrchestrationTask
{
    public int Id { get; set; }

    public int RunId { get; set; }
    public OrchestrationRun? Run { get; set; }

    /// <summary>Stable key used to express dependencies (unique within a run).</summary>
    public string TaskKey { get; set; } = "";

    /// <summary>The prompt/instruction dispatched to the worker agent.</summary>
    public string Prompt { get; set; } = "";

    /// <summary>JSON array of TaskKeys this task depends on, e.g. <c>["a","b"]</c>.</summary>
    public string DependsOnJson { get; set; } = "[]";

    /// <summary>pending | dispatched | done | failed.</summary>
    public string Status { get; set; } = "pending";

    /// <summary>The worker's final message once done.</summary>
    public string ResultMessage { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAtUtc { get; set; }
}
