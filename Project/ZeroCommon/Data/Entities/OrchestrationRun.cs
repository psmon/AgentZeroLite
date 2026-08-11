namespace Agent.Common.Data.Entities;

/// <summary>
/// A supervised multi-agent orchestration run (mission W6, orca-adoption) — a
/// namespace/inbox owning a DAG of <see cref="OrchestrationTask"/>s. Modeled on
/// orca's Run/Task/Dispatch (skill-guides/orchestration.md).
/// </summary>
public class OrchestrationRun
{
    public int Id { get; set; }

    /// <summary>Human-readable name / goal of the run.</summary>
    public string Name { get; set; } = "";

    /// <summary>pending | running | done | failed | cancelled.</summary>
    public string Status { get; set; } = "pending";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAtUtc { get; set; }

    public List<OrchestrationTask> Tasks { get; set; } = new();
}
