using AgentZeroWpf.Services;

namespace AgentZeroWpf.Module;

internal static class CliSessionAccessHelper
{
    public static string GetActiveSessionName(IReadOnlyList<CliGroupInfo> groups, int activeGroupIndex)
    {
        if (!TryGetActiveTab(groups, activeGroupIndex, out var group, out var tab))
            return activeGroupIndex >= 0 && activeGroupIndex < groups.Count
                ? $"{groups[activeGroupIndex].DisplayName} (No Tabs)"
                : "No Session";

        return $"{group!.DisplayName} / {tab!.Title}";
    }

    /// <summary>
    /// The active tab's session, or null when it has none yet.
    ///
    /// <para>This used to create the session on demand: the EasyConPty control
    /// produced its PTY asynchronously, so a tab could exist with no session behind
    /// it and every caller had to be able to conjure one. The xterm backend creates
    /// the session in the same breath as the pseudo-console, so "no session" now
    /// means exactly "not started yet" and there is nothing to conjure.</para>
    /// </summary>
    public static ITerminalSession? GetActiveSession(
        IReadOnlyList<CliGroupInfo> groups, int activeGroupIndex)
        => TryGetActiveTab(groups, activeGroupIndex, out _, out var tab) ? tab!.Session : null;

    private static bool TryGetActiveTab(
        IReadOnlyList<CliGroupInfo> groups,
        int activeGroupIndex,
        out CliGroupInfo? group,
        out ConsoleTabInfo? tab)
    {
        group = null;
        tab = null;

        if (activeGroupIndex < 0 || activeGroupIndex >= groups.Count)
            return false;

        group = groups[activeGroupIndex];
        var tabIndex = group.ActiveTabIndex;
        if (tabIndex < 0 || tabIndex >= group.Tabs.Count)
            return false;

        tab = group.Tabs[tabIndex];
        return true;
    }
}
