namespace Agent.Common.Data.Entities;

/// <summary>
/// User-assigned friendly name for a (vendor, accountKey) pair. The dashboard
/// shows the alias instead of the raw orgUuid / plan_type when one exists.
/// </summary>
public class TokenAccountAlias
{
    public int Id { get; set; }
    public string Vendor { get; set; } = "";       // "anthropic" | "openai"
    public string AccountKey { get; set; } = "";   // orgUuid (Claude) or plan_type:cli_version (Codex)
    public string Alias { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}
