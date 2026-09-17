namespace Agent.Common.Data.Entities;

public class CliGroup
{
    public int Id { get; set; }
    public string DirectoryPath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int SortOrder { get; set; }

    /// <summary>
    /// Which tab this workspace was last looking at, so returning to it starts that
    /// terminal and not the first one.
    ///
    /// <para>It has to live on the group: terminals are started lazily — one at a
    /// time, on demand, because starting every tab of every workspace at once is the
    /// overhead that lazy start exists to avoid — so "which one" is per workspace,
    /// not one global value. WindowState's LastActiveTabIndex is only the tab the
    /// app as a whole was on when it closed.</para>
    /// </summary>
    public int ActiveTabIndex { get; set; }

    /// <summary>
    /// How this workspace's tabs were split, as
    /// <see cref="Agent.Common.Services.DockPaneLayout.ToJson"/> writes it. Null
    /// while the workspace is unsplit, which is most of them — an unsplit layout
    /// is not stored, so a new tab is never pinned into an arrangement nobody asked
    /// for.
    /// </summary>
    public string? LayoutJson { get; set; }

    public ICollection<CliTab> Tabs { get; set; } = [];
}
