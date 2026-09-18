using System.Text;
using Agent.Common.Platform;
using Agent.Common.Services;

namespace Agent.Common.Module;

/// <summary>
/// The <c>terminal-list</c> reply and the group/tab → session lookup, WPF-free (M0033).
/// A port of the WPF host's <c>CliTerminalIpcHelper</c> over the <see cref="ICliGroupInfo"/>
/// contract, producing the same JSON shape so every CLI printer and the agent's
/// <c>list_terminals</c> tool keep working unchanged. The WPF host keeps its own copy;
/// the window handle is the one field only it can fill, so it is a callback here.
/// </summary>
public static class TerminalCatalogJson
{
    public static string BuildTerminalListJson(IReadOnlyList<ICliGroupInfo> groups,
        Func<IConsoleTabInfo, string>? windowHandle = null)
    {
        var sb = new StringBuilder();
        sb.Append("{\"groups\":[");

        for (var gi = 0; gi < groups.Count; gi++)
        {
            var group = groups[gi];
            if (gi > 0) sb.Append(',');

            sb.Append('{');
            sb.Append($"\"group_index\":{gi}");
            sb.Append($",\"group_name\":\"{CliIpcProtocol.Escape(group.DisplayName)}\"");
            sb.Append($",\"directory\":\"{CliIpcProtocol.Escape(group.DirectoryPath)}\"");
            sb.Append(",\"tabs\":[");

            var tabs = group.TabsView;
            for (var ti = 0; ti < tabs.Count; ti++)
            {
                var tab = tabs[ti];
                if (ti > 0) sb.Append(',');

                var hwnd = "";
                if (windowHandle is not null)
                {
                    try { hwnd = windowHandle(tab) ?? ""; }
                    catch { hwnd = ""; }
                }

                sb.Append('{');
                sb.Append($"\"tab_index\":{ti}");
                sb.Append($",\"title\":\"{CliIpcProtocol.Escape(tab.Title)}\"");
                sb.Append($",\"active\":{(ti == group.ActiveTabIndex ? "true" : "false")}");
                sb.Append($",\"running\":{(tab.IsTerminalStarted ? "true" : "false")}");
                sb.Append($",\"hwnd\":\"{CliIpcProtocol.Escape(hwnd)}\"");
                sb.Append($",\"session_id\":\"{CliIpcProtocol.Escape(tab.Session?.SessionId ?? "")}\"");
                sb.Append('}');
            }

            sb.Append("]}");
        }

        sb.Append("]}");
        return sb.ToString();
    }

    public static bool TryResolveSession(
        IReadOnlyList<ICliGroupInfo> groups,
        int groupIdx,
        int tabIdx,
        out ICliGroupInfo? group,
        out IConsoleTabInfo? tab,
        out ITerminalSession? session,
        out string? errorJson,
        string invalidGroupError = "invalid group index",
        string invalidTabError = "invalid tab index",
        string notStartedError = "terminal not started")
    {
        group = null;
        tab = null;
        session = null;
        errorJson = null;

        if (groupIdx < 0 || groupIdx >= groups.Count)
        {
            errorJson = CliIpcProtocol.ErrorJson(invalidGroupError);
            return false;
        }
        group = groups[groupIdx];
        var tabs = group.TabsView;
        if (tabIdx < 0 || tabIdx >= tabs.Count)
        {
            errorJson = CliIpcProtocol.ErrorJson(invalidTabError);
            return false;
        }
        tab = tabs[tabIdx];
        if (tab.Session is null)
        {
            errorJson = CliIpcProtocol.ErrorJson(notStartedError);
            return false;
        }
        session = tab.Session;
        return true;
    }
}
