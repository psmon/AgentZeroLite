using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using AgentZeroWpf.Services;

namespace AgentZeroWpf.Module;

internal static class CliTerminalIpcHelper
{
    public static string BuildTerminalListJson(IReadOnlyList<CliGroupInfo> groups, Func<string, string> escapeJson)
    {
        var sb = new StringBuilder();
        sb.Append("{\"groups\":[");

        for (int gi = 0; gi < groups.Count; gi++)
        {
            var group = groups[gi];
            if (gi > 0)
                sb.Append(',');

            sb.Append('{');
            sb.Append($"\"group_index\":{gi}");
            sb.Append($",\"group_name\":\"{escapeJson(group.DisplayName)}\"");
            sb.Append($",\"directory\":\"{escapeJson(group.DirectoryPath)}\"");
            sb.Append(",\"tabs\":[");

            for (int ti = 0; ti < group.Tabs.Count; ti++)
            {
                var tab = group.Tabs[ti];
                if (ti > 0)
                    sb.Append(',');

                string hwndHex = "";
                if (tab.Terminal is not null)
                {
                    try
                    {
                        if (PresentationSource.FromVisual(tab.Terminal) is HwndSource src)
                            hwndHex = $"0x{src.Handle:X8}";
                    }
                    catch
                    {
                        // Ignore HWND lookup failures for detached/unloaded terminals.
                    }
                }

                sb.Append('{');
                sb.Append($"\"tab_index\":{ti}");
                sb.Append($",\"title\":\"{escapeJson(tab.Title)}\"");
                sb.Append($",\"active\":{(ti == group.ActiveTabIndex ? "true" : "false")}");
                sb.Append($",\"running\":{(tab.IsTerminalStarted ? "true" : "false")}");
                sb.Append($",\"hwnd\":\"{hwndHex}\"");
                sb.Append($",\"session_id\":\"{escapeJson(tab.Session?.SessionId ?? "")}\"");
                sb.Append('}');
            }

            sb.Append("]}");
        }

        sb.Append("]}");
        return sb.ToString();
    }

    public static bool TryResolveSession(
        IReadOnlyList<CliGroupInfo> groups,
        int groupIdx,
        int tabIdx,
        string invalidGroupError,
        string invalidTabError,
        string notStartedError,
        out CliGroupInfo? group,
        out ConsoleTabInfo? tab,
        out ConPtyTerminalSession? session,
        out string? errorJson)
    {
        group = null;
        tab = null;
        session = null;
        errorJson = null;

        if (groupIdx < 0 || groupIdx >= groups.Count)
        {
            errorJson = $"{{\"ok\":false,\"error\":\"{invalidGroupError}\"}}";
            return false;
        }

        group = groups[groupIdx];
        if (tabIdx < 0 || tabIdx >= group.Tabs.Count)
        {
            errorJson = $"{{\"ok\":false,\"error\":\"{invalidTabError}\"}}";
            return false;
        }

        tab = group.Tabs[tabIdx];
        if (tab.Session is null)
        {
            errorJson = $"{{\"ok\":false,\"error\":\"{notStartedError}\"}}";
            return false;
        }

        session = tab.Session;
        return true;
    }
}
