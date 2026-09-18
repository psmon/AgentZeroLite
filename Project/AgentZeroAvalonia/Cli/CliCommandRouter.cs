using System.Text.Json;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Agent.Common;
using Agent.Common.Module;
using Agent.Common.Platform;
using Agent.Common.Services;

namespace AgentZeroAvalonia.Cli;

/// <summary>
/// Dispatches one CLI request to the running GUI. M0034: <c>status</c>, <c>close-win</c>;
/// M0035: the four terminal verbs, answered from the same catalog the WPF host uses
/// (<see cref="TerminalCatalogJson"/>) with the same JSON shapes. The bot and web verbs
/// follow in M0039. Runs on the UI thread.
/// </summary>
internal sealed class CliCommandRouter
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;

    /// <summary>The live workspaces; set by the app once the view model exists.</summary>
    public Func<IReadOnlyList<ICliGroupInfo>>? Groups { get; set; }

    public CliCommandRouter(IClassicDesktopStyleApplicationLifetime desktop) => _desktop = desktop;

    public Task<string> HandleAsync(string json, CancellationToken ct)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { return Task.FromResult(CliIpcProtocol.ErrorJson("bad request json: " + ex.Message)); }

        using (doc)
        {
            var root = doc.RootElement;
            var command = root.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
            try
            {
                switch (command)
                {
                    case "status":
                        return Task.FromResult(StatusJson());

                    case "close-win":
                        Dispatcher.UIThread.Post(() => _desktop.Shutdown(), DispatcherPriority.Background);
                        return Task.FromResult("{\"ok\":true}");

                    case "terminal-list":
                        return Task.FromResult(TerminalCatalogJson.BuildTerminalListJson(CurrentGroups(), null));

                    case "terminal-send":
                        return Task.FromResult(TerminalSend(root));

                    case "terminal-key":
                        return Task.FromResult(TerminalKey(root));

                    case "terminal-read":
                        return Task.FromResult(TerminalRead(root));

                    default:
                        return Task.FromResult(CliIpcProtocol.ErrorJson($"unknown command '{command}' (not ported to the Avalonia host yet)"));
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[IPC] {command} failed: {ex.GetType().Name}: {ex.Message}");
                return Task.FromResult(CliIpcProtocol.ErrorJson($"{command} failed: {ex.Message}"));
            }
        }
    }

    private IReadOnlyList<ICliGroupInfo> CurrentGroups() => Groups?.Invoke() ?? Array.Empty<ICliGroupInfo>();

    private string StatusJson()
    {
        var groups = CurrentGroups();
        var terminals = groups.Sum(g => g.TabsView.Count);
        return "{\"ok\":true,\"running\":true,\"host\":\"avalonia\"" +
               $",\"version\":\"{CliIpcProtocol.Escape(AppVersionProvider.GetDisplayVersion())}\"" +
               $",\"os\":\"{CliIpcProtocol.Escape(Environment.OSVersion.ToString())}\"" +
               $",\"groups\":{groups.Count},\"terminals\":{terminals}}}";
    }

    // ── terminal verbs (WPF MainWindow.HandleTerminalSend/Key/Read, same replies) ──

    private static bool TryTarget(JsonElement root, out int g, out int t, out string? error)
    {
        g = t = -1;
        error = null;
        if (root.TryGetProperty("group_index", out var gp) && gp.TryGetInt32(out g)
            && root.TryGetProperty("tab_index", out var tp) && tp.TryGetInt32(out t))
            return true;
        error = "Expected group_index and tab_index. Use terminal-list to see available terminals.";
        return false;
    }

    private bool TryResolve(JsonElement root, out int g, out int t, out ITerminalSession? session, out string? errorJson)
    {
        session = null;
        if (!TryTarget(root, out g, out t, out var err))
        {
            errorJson = CliIpcProtocol.ErrorJson(err!);
            return false;
        }
        return TerminalCatalogJson.TryResolveSession(CurrentGroups(), g, t, out _, out _, out session, out errorJson,
            $"Invalid group_index {g}. Use terminal-list to see available groups.",
            $"Invalid tab_index {t} in group {g}. Use terminal-list to see available tabs.",
            $"Terminal [{g}:{t}] is not started. Activate the tab in AgentZero first.");
    }

    private string TerminalSend(JsonElement root)
    {
        if (!TryResolve(root, out var g, out var t, out var session, out var errorJson)) return errorJson!;
        var text = root.TryGetProperty("text", out var tp) ? tp.GetString() ?? "" : "";
        if (!session!.IsRunning)
        {
            AppLogger.Log($"[IPC] terminal-send REJECTED [{g}:{t}] | label=\"{session.SessionId}\" running=false");
            return CliIpcProtocol.ErrorJson($"Terminal [{g}:{t}] session is not running (PTY dead). id={session.InternalId}");
        }
        session.WriteAndSubmit(text);
        var preview = text.Length <= 30 ? text : text[..30] + "…";
        AppLogger.Log($"[IPC] terminal-send [{g}:{t}] | label=\"{session.SessionId}\" len={text.Length} preview=\"{preview}\"");
        return $"{{\"ok\":true,\"group_index\":{g},\"tab_index\":{t},\"sent_length\":{text.Length}}}";
    }

    private string TerminalKey(JsonElement root)
    {
        if (!TryResolve(root, out var g, out var t, out var session, out var errorJson)) return errorJson!;
        var key = (root.TryGetProperty("key", out var kp) ? kp.GetString() ?? "" : "").ToLowerInvariant();
        var seq = KeySequence(key);
        if (seq.Length == 0)
            return CliIpcProtocol.ErrorJson($"Unknown key: {key}. Use terminal-key --help.");
        session!.NoteInputAttempt("cli");
        session.Write(seq.AsSpan());
        AppLogger.Log($"[IPC] terminal-key [{g}:{t}] | label=\"{session.SessionId}\" key={key} bytes={seq.Length}");
        return $"{{\"ok\":true,\"group_index\":{g},\"tab_index\":{t},\"key\":\"{CliIpcProtocol.Escape(key)}\"}}";
    }

    /// <summary>The WPF host's key alias table (HandleTerminalKey), verbatim.</summary>
    internal static string KeySequence(string key) => key switch
    {
        "cr" or "enter" => "\r",
        "lf" => "\n",
        "crlf" => "\r\n",
        "esc" => "\x1B",
        "tab" => "\t",
        "shifttab" or "backtab" => "\x1b[Z",
        "backspace" => "\x08",
        "del" => "\x7F",
        "ctrlc" => "\x03",
        "ctrld" => "\x04",
        "up" => "\x1B[A",
        "down" => "\x1B[B",
        "right" => "\x1B[C",
        "left" => "\x1B[D",
        _ when key.StartsWith("hex:", StringComparison.Ordinal) => ParseHexKey(key[4..]),
        _ => "",
    };

    private static string ParseHexKey(string hex)
    {
        hex = hex.Replace(" ", "").Replace("-", "");
        if (hex.Length == 0 || hex.Length % 2 != 0) return "";
        var chars = new char[hex.Length / 2];
        for (var i = 0; i < chars.Length; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out var b)) return "";
            chars[i] = (char)b;
        }
        return new string(chars);
    }

    private string TerminalRead(JsonElement root)
    {
        if (!TryResolve(root, out var g, out var t, out var session, out var errorJson)) return errorJson!;
        var lastN = root.TryGetProperty("last", out var lp) && lp.TryGetInt32(out var n) ? n : 0;
        string text;
        if (lastN > 0)
        {
            var total = session!.OutputLength;
            var start = Math.Max(0, total - lastN);
            var len = total - start;
            text = len > 0 ? session.ReadOutput(start, len) : "";
        }
        else
        {
            text = session!.GetConsoleText();
        }
        text = ApprovalParser.StripAnsiCodes(text);
        AppLogger.Log($"[IPC] terminal-read [{g}:{t}] | label=\"{session.SessionId}\" last_n={lastN} returned={text.Length}");
        return $"{{\"ok\":true,\"group_index\":{g},\"tab_index\":{t},\"length\":{text.Length},\"text\":\"{CliIpcProtocol.Escape(text)}\"}}";
    }
}
