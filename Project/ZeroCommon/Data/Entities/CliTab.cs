namespace Agent.Common.Data.Entities;

public class CliTab
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public CliGroup Group { get; set; } = null!;
    public string Title { get; set; } = "";
    public int CliDefinitionId { get; set; }
    public CliDefinition CliDefinition { get; set; } = null!;
    public int SortOrder { get; set; }

    /// <summary>
    /// The agent conversations this tab ran, newest first (<c>claude:&lt;id&gt;|codex:&lt;id&gt;</c>,
    /// see <see cref="Agents.TabSessionMemory"/>), or null. Lets the resume question offer
    /// "this tab's last session" instead of only the folder's whole list, which several agent
    /// tabs in one workspace share.
    /// </summary>
    public string? AgentSessions { get; set; }
}
