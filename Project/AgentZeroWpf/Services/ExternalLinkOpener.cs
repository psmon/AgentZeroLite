using System.Diagnostics;
using Agent.Common;
using Agent.Common.Services;

namespace AgentZeroWpf.Services;

/// <summary>
/// Single choke point for "open this URL in the user's default browser".
/// Used by the terminal link strip, xterm.js Ctrl+click and anything else
/// that hands terminal-derived text to the OS shell. Only absolute http/https
/// URLs get through — terminal output is untrusted, and ShellExecute would
/// happily launch <c>file:</c> or custom protocol handlers otherwise.
/// </summary>
public static class ExternalLinkOpener
{
    /// <summary>Open <paramref name="url"/> with the default browser. Returns
    /// false (and logs) when the URL is rejected or the shell launch fails.</summary>
    public static bool TryOpen(string? url, string source)
    {
        if (!TerminalLinkScanner.IsOpenableWebUrl(url))
        {
            AppLogger.Log($"[Link] rejected (not http/https) | source={source} url=[{url}]");
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url!,
                UseShellExecute = true,
            });
            AppLogger.Log($"[Link] opened in browser | source={source} url=[{url}]");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Link] open failed | source={source} url=[{url}] err={ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
