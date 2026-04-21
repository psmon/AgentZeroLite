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
}
