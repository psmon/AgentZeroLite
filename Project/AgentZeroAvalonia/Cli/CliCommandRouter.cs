using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Agent.Common;
using Agent.Common.Agents;
using Agent.Common.Llm.Tools;
using Agent.Common.Module;
using Agent.Common.Platform;
using Agent.Common.Services;
using Agent.Common.Web;
using AgentZeroAvalonia.Layout;

namespace AgentZeroAvalonia.Cli;

/// <summary>
/// Dispatches one CLI request to the running GUI (M0034–M0039). Runs on the UI thread;
/// every request and reply is the WPF host's JSON shape, so <c>-cli help agentzero</c> and
/// the printers apply unchanged. Verbs: <c>status</c>, <c>close-win</c>, <c>terminal-list</c>,
/// <c>terminal-send/-key/-read</c> (by index or <c>alias</c>), <c>terminal-alias</c>,
/// <c>layout</c>, <c>bot-chat</c>, <c>bot-ask</c>, <c>web</c> (off the UI thread, <c>req</c> echoed).
/// The hooks below are set by the app; the tests set fakes.
/// </summary>
internal sealed class CliCommandRouter
{
    private readonly IClassicDesktopStyleApplicationLifetime? _desktop;

    /// <summary>The live workspaces; set by the app once the view model exists.</summary>
    public Func<IReadOnlyList<ICliGroupInfo>>? Groups { get; set; }

    /// <summary>Run a window command by id (the hotkey table's ids) — <c>-cli layout &lt;verb&gt;</c>.</summary>
    public Action<string>? ExecuteWindowCommand { get; set; }

    /// <summary>The active workspace's split layout as stored JSON (null while unsplit), plus its pane count.</summary>
    public Func<(string? Json, int Panes, string? Workspace)>? LayoutStatus { get; set; }

    /// <summary><c>bot-chat</c>: (from, message) into the AgentBot pane and on to the bot actor.</summary>
    public Action<string, string>? BotChat { get; set; }

    /// <summary><c>bot-ask</c>: hand a request to the agent loop as if typed in AI mode (agent-facing smoke).</summary>
    public Action<string>? BotAsk { get; set; }

    /// <summary>Where the alias registry lives; null = the shared default file. Tests point it at a temp file.</summary>
    public string? AliasRegistryPath { get; set; }

    /// <summary>The web surface <c>web</c> drives; the headless one until a Browser page exists in this host.</summary>
    public IWebToolSurface? WebSurface { get; set; }

    public CliCommandRouter(IClassicDesktopStyleApplicationLifetime? desktop) => _desktop = desktop;

    public Task<string> HandleAsync(string json, CancellationToken ct)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { return Task.FromResult(CliIpcProtocol.ErrorJson("bad request json: " + ex.Message)); }

        using (doc)
        {
            var root = doc.RootElement;
            var command = Str(root, "command");
            try
            {
                switch (command)
                {
                    case "status":
                        return Task.FromResult(StatusJson());

                    case "close-win":
                        if (_desktop is null) return Task.FromResult(CliIpcProtocol.ErrorJson("no window to close"));
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

                    case "terminal-alias":
                        return Task.FromResult(TerminalAlias(root));

                    case "layout":
                        return Task.FromResult(Layout(root));

                    case "bot-ask":
                    {
                        var text = Str(root, "text");
                        if (BotAsk is null) return Task.FromResult(CliIpcProtocol.ErrorJson("AgentBot is not available in this host"));
                        if (text.Trim().Length == 0) return Task.FromResult(CliIpcProtocol.ErrorJson("text is empty"));
                        BotAsk(text);
                        AppLogger.Log($"[IPC] bot-ask len={text.Length}");
                        return Task.FromResult($"{{\"ok\":true,\"length\":{text.Length}}}");
                    }

                    case "bot-chat":
                    {
                        var message = Str(root, "message");
                        var from = root.TryGetProperty("from", out var fp) ? fp.GetString() ?? "CLI" : "CLI";
                        if (BotChat is null) return Task.FromResult(CliIpcProtocol.ErrorJson("AgentBot is not available in this host"));
                        BotChat(from, message);
                        AppLogger.Log($"[IPC] bot-chat from={from}, len={message.Length}");
                        return Task.FromResult($"{{\"ok\":true,\"from\":\"{CliIpcProtocol.Escape(from)}\",\"message_length\":{message.Length}}}");
                    }

                    case "web":
                        return WebAsync(root.Clone());

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

    private static string Str(JsonElement r, string name)
        => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement r, string name, int fallback)
        => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : fallback;

    private IReadOnlyList<ICliGroupInfo> CurrentGroups() => Groups?.Invoke() ?? Array.Empty<ICliGroupInfo>();

    private TerminalAliasRegistry LoadAliases() => TerminalAliasRegistry.Load(AliasRegistryPath);

    private string StatusJson()
    {
        var groups = CurrentGroups();
        var terminals = groups.Sum(g => g.TabsView.Count);
        return "{\"ok\":true,\"running\":true,\"host\":\"avalonia\"" +
               $",\"version\":\"{CliIpcProtocol.Escape(AppVersionProvider.GetDisplayVersion())}\"" +
               $",\"os\":\"{CliIpcProtocol.Escape(Environment.OSVersion.ToString())}\"" +
               $",\"groups\":{groups.Count},\"terminals\":{terminals}}}";
    }

    // ── target resolution: {group_index, tab_index} or {alias} (WPF TryResolveCliTarget) ──

    private bool TryTarget(JsonElement root, out int g, out int t, out string? error)
    {
        g = t = -1;
        error = null;
        if (root.TryGetProperty("alias", out var ap) && ap.ValueKind == JsonValueKind.String)
        {
            var alias = ap.GetString() ?? "";
            var target = LoadAliases().Resolve(alias);
            if (target is null)
            {
                error = $"Alias '{alias}' is not defined. Assign it with: terminal-alias set <group> <tab> {alias}";
                return false;
            }
            var groups = CurrentGroups();
            for (var gi = 0; gi < groups.Count; gi++)
                for (var ti = 0; ti < groups[gi].TabsView.Count; ti++)
                    if (groups[gi].DisplayName == target.GroupName && groups[gi].TabsView[ti].Title == target.Title)
                    {
                        g = gi;
                        t = ti;
                        return true;
                    }
            error = $"Alias '{alias}' → \"{target.GroupName}/{target.Title}\" matches no live terminal.";
            return false;
        }
        if (root.TryGetProperty("group_index", out var gp) && gp.TryGetInt32(out g)
            && root.TryGetProperty("tab_index", out var tp) && tp.TryGetInt32(out t))
            return true;
        error = "Expected group_index and tab_index (or alias). Use terminal-list to see available terminals.";
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

    // ── terminal verbs (WPF MainWindow.HandleTerminalSend/Key/Read, same replies) ──

    private string TerminalSend(JsonElement root)
    {
        if (!TryResolve(root, out var g, out var t, out var session, out var errorJson)) return errorJson!;
        var text = Str(root, "text");
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
        var key = Str(root, "key").ToLowerInvariant();
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
        var lastN = Int(root, "last", 0);
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

    // ── terminal-alias (WPF HandleTerminalAlias, same replies) ──

    private string TerminalAlias(JsonElement root)
    {
        var sub = Str(root, "sub").ToLowerInvariant();
        var groups = CurrentGroups();
        var reg = LoadAliases();
        switch (sub)
        {
            case "set":
            {
                var g = Int(root, "group_index", -1);
                var t = Int(root, "tab_index", -1);
                var name = Str(root, "name");
                if (g < 0 || g >= groups.Count || t < 0 || t >= groups[g].TabsView.Count)
                    return CliIpcProtocol.ErrorJson($"Invalid [{g}:{t}]. Use terminal-list.");
                var groupName = groups[g].DisplayName;
                var title = groups[g].TabsView[t].Title;
                if (!reg.Set(name, groupName, title))
                    return CliIpcProtocol.ErrorJson($"Invalid alias '{name}' (letters/digits/-/_, 1–64 chars).");
                reg.Save(AliasRegistryPath);
                return $"{{\"ok\":true,\"alias\":\"{CliIpcProtocol.Escape(name)}\",\"group\":\"{CliIpcProtocol.Escape(groupName)}\",\"title\":\"{CliIpcProtocol.Escape(title)}\"}}";
            }
            case "rm":
            {
                var name = Str(root, "name");
                var removed = reg.Remove(name);
                if (removed) reg.Save(AliasRegistryPath);
                return removed
                    ? $"{{\"ok\":true,\"alias\":\"{CliIpcProtocol.Escape(name)}\"}}"
                    : $"{{\"ok\":false,\"alias\":\"{CliIpcProtocol.Escape(name)}\",\"error\":\"alias not found\"}}";
            }
            default:
            {
                var sb = new StringBuilder("{\"ok\":true,\"aliases\":[");
                var first = true;
                foreach (var kv in reg.Entries)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    int rg = -1, rt = -1;
                    for (var gi = 0; gi < groups.Count && rg < 0; gi++)
                        for (var ti = 0; ti < groups[gi].TabsView.Count; ti++)
                            if (groups[gi].DisplayName == kv.Value.GroupName && groups[gi].TabsView[ti].Title == kv.Value.Title)
                            {
                                rg = gi;
                                rt = ti;
                                break;
                            }
                    sb.Append('{')
                      .Append($"\"alias\":\"{CliIpcProtocol.Escape(kv.Key)}\"")
                      .Append($",\"group\":\"{CliIpcProtocol.Escape(kv.Value.GroupName)}\"")
                      .Append($",\"title\":\"{CliIpcProtocol.Escape(kv.Value.Title)}\"")
                      .Append($",\"group_index\":{rg},\"tab_index\":{rt}")
                      .Append($",\"live\":{(rg >= 0 ? "true" : "false")}")
                      .Append('}');
                }
                return sb.Append("]}").ToString();
            }
        }
    }

    // ── layout (WPF -cli layout <verb>) ──

    private string Layout(JsonElement root)
    {
        var sub = Str(root, "sub").ToLowerInvariant();
        var id = sub switch
        {
            "split-right" => WindowCommandIds.SplitRight,
            "split-down" => WindowCommandIds.SplitDown,
            "close-tab" => WindowCommandIds.CloseTab,
            "add" or "new-tab" => WindowCommandIds.TerminalAdd,
            "close-pane" => HotkeyTable.ClosePane,
            "next-tab" => HotkeyTable.NextTab,
            "prev-tab" => HotkeyTable.PrevTab,
            "move-tab" => HotkeyTable.MoveTabNextPane,
            "focus-left" => HotkeyTable.FocusLeft,
            "focus-right" => HotkeyTable.FocusRight,
            "focus-up" => HotkeyTable.FocusUp,
            "focus-down" => HotkeyTable.FocusDown,
            "bot-toggle" => HotkeyTable.BotToggle,
            "bot-embed" => HotkeyTable.BotEmbedToggle,
            "status" or "" => null,
            _ => sub.Contains('.') ? sub : "?",
        };
        if (id == "?")
            return CliIpcProtocol.ErrorJson($"Unknown layout verb '{sub}'. Use: status, split-right, split-down, close-tab, close-pane, add, next-tab, prev-tab, move-tab, focus-left|right|up|down, bot-toggle, bot-embed");
        if (id is not null)
        {
            if (ExecuteWindowCommand is null) return CliIpcProtocol.ErrorJson("layout commands are not wired");
            ExecuteWindowCommand(id);
        }
        var (json, panes, ws) = LayoutStatus?.Invoke() ?? (null, 0, null);
        var wsJson = ws is null ? "null" : "\"" + CliIpcProtocol.Escape(ws) + "\"";
        return "{\"ok\":true,\"sub\":\"" + CliIpcProtocol.Escape(sub.Length == 0 ? "status" : sub) + "\""
               + ",\"workspace\":" + wsJson
               + ",\"panes\":" + panes
               + ",\"layout\":" + (json ?? "null") + "}";
    }

    // ── web (WPF HandleWebCommand: runs off the UI thread, echoes req) ──

    private async Task<string> WebAsync(JsonElement root)
    {
        var verb = Str(root, "verb");
        var req = Str(root, "req");
        var url = Str(root, "url");
        var query = Str(root, "query");
        var mode = Str(root, "mode");
        var find = Str(root, "find");
        var tab = Int(root, "tab", 0);
        var max = Int(root, "max", 5);
        var maxChars = Int(root, "max_chars", 0);
        var surface = WebSurface;
        string json;
        if (surface is null)
        {
            json = ToolJson.Fail("the web surface is not available in this host");
        }
        else
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                json = await Task.Run(async () => verb switch
                {
                    "open" => await surface.OpenAsync(url, tab, cts.Token),
                    "search" => await surface.SearchAsync(query, Math.Clamp(max, 1, 10), cts.Token),
                    "read" => await surface.ReadAsync(tab, string.IsNullOrEmpty(mode) ? "summary" : mode,
                                  string.IsNullOrEmpty(find) ? null : find, maxChars, cts.Token),
                    "tabs" => await surface.ListTabsAsync(cts.Token),
                    _ => ToolJson.Fail($"unknown web verb '{verb}'"),
                });
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[IPC] web {verb} failed: {ex.GetType().Name}: {ex.Message}");
                json = ToolJson.Fail(ex.Message);
            }
        }
        json = WithReq(json, req);
        AppLogger.Log($"[IPC] web {verb} -> {json.Length} chars");
        return json;
    }

    private static string WithReq(string json, string req)
    {
        if (string.IsNullOrEmpty(req)) return json;
        try
        {
            var node = JsonNode.Parse(json)!.AsObject();
            node["req"] = req;
            return node.ToJsonString(ToolJson.Options);
        }
        catch
        {
            return json;
        }
    }
}
