namespace Agent.Common.Services;

/// <summary>
/// The window actions AgentZero can perform on itself, named once.
///
/// <para>Every one of these has to be reachable from the CLI, because that is what
/// makes the app testable without a hand on the mouse — an agent can split a pane,
/// read the layout back and notice it is thrashing, all from a shell. Shortcuts are
/// the same list through a different door: one registry, two front ends, so a
/// command cannot exist for one and not the other.</para>
///
/// <para>Ids are strings rather than an enum because they cross the CLI's JSON
/// boundary and live in a settings file; a renamed enum member would silently
/// unbind someone's keyboard.</para>
/// </summary>
public static class WindowCommandIds
{
    public const string SplitRight = "layout.split-right";
    public const string SplitDown = "layout.split-down";
    public const string Float = "layout.float";
    public const string Dock = "layout.dock";
    public const string CloseTab = "layout.close-tab";

    public const string TerminalAdd = "terminal.add";

    public const string PanelToggle = "panel.toggle";
    public const string PanelCollapse = "panel.collapse";
    public const string PanelMaximize = "panel.maximize";

    /// <summary>Id → what it does, for Settings and for <c>-cli layout help</c>.</summary>
    public static readonly IReadOnlyList<(string Id, string Description)> All = new[]
    {
        (SplitRight,    "Split the active terminal to the right"),
        (SplitDown,     "Split the active terminal downwards"),
        (Float,         "Detach the active terminal into its own window"),
        (Dock,          "Dock the active terminal back into the main window"),
        (CloseTab,      "Close the active terminal tab"),
        (TerminalAdd,   "Add a terminal to the active workspace"),
        (PanelToggle,   "Show or hide the bottom panel"),
        (PanelCollapse, "Collapse or expand the bottom panel"),
        (PanelMaximize, "Maximize or restore the bottom panel"),
    };

    public static bool IsKnown(string? id) =>
        id is not null && All.Any(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
}
