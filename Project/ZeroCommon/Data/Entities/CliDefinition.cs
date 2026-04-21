namespace Agent.Common.Data.Entities;

public class CliDefinition
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string? Arguments { get; set; }
    public bool IsBuiltIn { get; set; }
    public int SortOrder { get; set; }
}
