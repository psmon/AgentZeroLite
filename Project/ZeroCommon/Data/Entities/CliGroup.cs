namespace Agent.Common.Data.Entities;

public class CliGroup
{
    public int Id { get; set; }
    public string DirectoryPath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int SortOrder { get; set; }
    public ICollection<CliTab> Tabs { get; set; } = [];
}
