namespace Agent.Common.Module;

public interface IConsoleTabInfo
{
    string Title { get; }
    int CliDefinitionId { get; }
}

public interface ICliGroupInfo
{
    string DirectoryPath { get; }
    string DisplayName { get; }
    IReadOnlyList<IConsoleTabInfo> TabsView { get; }
}
