namespace Agent.Common.Data.Entities;

/// <summary>
/// A scheduled agent run: a prompt fired into a workspace on a recurring
/// schedule (Automations). The GUI scheduler dispatches due automations to the
/// bot; the schedule math is the pure <c>AutomationSchedule</c>.
/// </summary>
public class Automation
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Schedule spec: "every 30m" | "hourly" | "daily 09:00".</summary>
    public string Schedule { get; set; } = "";

    /// <summary>The prompt handed to the agent when the automation fires.</summary>
    public string Prompt { get; set; } = "";

    /// <summary>Workspace folder the run targets (empty = active workspace).</summary>
    public string WorkspacePath { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public DateTime? LastRunUtc { get; set; }
    public DateTime? NextRunUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
