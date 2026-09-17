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

    /// <summary>Which tab this workspace was last on — persisted so returning to the
    /// workspace starts that terminal rather than the first one.</summary>
    int ActiveTabIndex { get; }
}
