using AgentZeroWpf.Services;
using EasyWindowsTerminalControl;

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

    public static ITerminalSession? GetActiveSession(
        IReadOnlyList<CliGroupInfo> groups,
        int activeGroupIndex,
        Action<ConsoleTabInfo, EasyTerminalControl?, string> ensureSession)
    {
        if (!TryGetActiveTab(groups, activeGroupIndex, out var group, out var tab))
            return null;

        if (tab!.Session is null)
            ensureSession(tab, tab.Terminal, group!.DisplayName);

        return tab.Session;
    }

    public static void EnsureSession(ConsoleTabInfo tab, EasyTerminalControl? terminal, string groupName)
    {
        if (tab.Session is not null)
            return;

        if (terminal?.ConPTYTerm is null)
        {
            AppLogger.Log($"[CLI] Session skip: terminal/ConPTYTerm null for {groupName}/{tab.Title}");
            return;
        }

        if (terminal.ConPTYTerm.ConsoleOutputLog is null)
        {
            var ptyHashPending = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(terminal.ConPTYTerm);
            AppLogger.Log($"[CLI] Session pending: output log not ready yet | label={groupName}/{tab.Title} pty_ref=0x{ptyHashPending:X8}");
            return;
        }

        var sessionId = $"{groupName}/{tab.Title}";
        var session = new ConPtyTerminalSession(terminal.ConPTYTerm, sessionId);
        tab.Session = session;
        var ptyHash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(terminal.ConPTYTerm);
        var outputLen = terminal.ConPTYTerm.ConsoleOutputLog?.Length ?? -1;
        AppLogger.Log($"[CLI] Session created (lazy) | label={sessionId} id={session.InternalId} pty_ref=0x{ptyHash:X8} output_len={outputLen}");
    }

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
