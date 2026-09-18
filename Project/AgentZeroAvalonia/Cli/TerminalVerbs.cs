using System.Text;
using System.Text.Json;
using Agent.Common.Platform;

namespace AgentZeroAvalonia.Cli;

/// <summary>
/// The client side of the terminal, layout and bot verbs (M0035–M0039): the same arguments,
/// request JSON and printed shapes as the WPF <c>CliHandler</c>, over the pipe instead of
/// WM_COPYDATA. A target is <c>&lt;group&gt; &lt;tab&gt;</c> or <c>--alias &lt;name&gt;</c>.
/// </summary>
internal static class TerminalVerbs
{
    public static int List(CliClient client)
    {
        var json = client.Send("{\"command\":\"terminal-list\"}");
        if (json is null) return client.NoWait ? 0 : 1;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("groups", out var groups))
        {
            Console.WriteLine("=== Active Terminal Sessions ===");
            Console.WriteLine();
            foreach (var group in groups.EnumerateArray())
            {
                var gIdx = group.GetProperty("group_index").GetInt32();
                var gName = group.GetProperty("group_name").GetString() ?? "";
                var gDir = group.GetProperty("directory").GetString() ?? "";
                Console.WriteLine($"  Group {gIdx}: {gName}  ({gDir})");
                if (group.TryGetProperty("tabs", out var tabs))
                {
                    foreach (var tab in tabs.EnumerateArray())
                    {
                        var tIdx = tab.GetProperty("tab_index").GetInt32();
                        var title = tab.GetProperty("title").GetString() ?? "";
                        var active = tab.GetProperty("active").GetBoolean();
                        var running = tab.GetProperty("running").GetBoolean();
                        var sessionId = tab.GetProperty("session_id").GetString() ?? "";
                        Console.WriteLine($"    Tab {tIdx}: {title}{(active ? " *" : "")}{(running ? "" : " [not started]")}");
                        Console.WriteLine($"      ID: {sessionId}");
                    }
                }
                Console.WriteLine();
            }
        }
        else if (!CliClient.Ok(root))
        {
            Console.Error.WriteLine($"Error: {CliClient.Str(root, "error", "unknown")}");
            return 1;
        }
        Console.WriteLine("--- JSON ---");
        Console.WriteLine(json);
        return 0;
    }

    public static int Send(CliClient client, string[] args)
    {
        if (!TryParseTarget(args, "terminal-send <group_index> <tab_index> <text...>", out var target, out var consumed)) return 1;
        if (args.Length <= consumed)
        {
            Console.Error.WriteLine("Usage: terminal-send <group_index> <tab_index> <text...>   (or --alias <name>)");
            return 1;
        }
        var text = string.Join(" ", args.Skip(consumed));
        var request = "{\"command\":\"terminal-send\"" + target + ",\"text\":\"" + CliIpcProtocol.Escape(text) + "\"}";
        return Simple(client, request, root => $"Sent {CliClient.Str(root, "sent_length", "?")} chars to terminal [{CliClient.Str(root, "group_index", "?")}:{CliClient.Str(root, "tab_index", "?")}].");
    }

    public static int Key(CliClient client, string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine("Keys: cr|enter lf crlf esc tab shifttab|backtab backspace del ctrlc ctrld up down left right hex:<bytes>");
            return 0;
        }
        if (!TryParseTarget(args, "terminal-key <group_index> <tab_index> <key>", out var target, out var consumed)) return 1;
        if (args.Length <= consumed)
        {
            Console.Error.WriteLine("Usage: terminal-key <group_index> <tab_index> <key>   (or --alias <name>)");
            return 1;
        }
        var key = args[consumed];
        var request = "{\"command\":\"terminal-key\"" + target + ",\"key\":\"" + CliIpcProtocol.Escape(key) + "\"}";
        return Simple(client, request, root => $"Sent key '{key}' to terminal [{CliClient.Str(root, "group_index", "?")}:{CliClient.Str(root, "tab_index", "?")}].");
    }

    public static int Read(CliClient client, string[] args)
    {
        if (!TryParseTarget(args, "terminal-read <group_index> <tab_index> [--last N]", out var target, out var consumed)) return 1;
        var lastN = 0;
        for (var i = consumed; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--last", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out var n))
            {
                lastN = n;
                break;
            }
        }
        var text = ReadText(client, target, lastN, out var error);
        if (text is null)
        {
            if (error is not null) Console.Error.WriteLine("Error: " + error);
            return client.NoWait && error is null ? 0 : 1;
        }
        Console.WriteLine(text);
        return 0;
    }

    /// <summary>One terminal-read round trip; null with <paramref name="error"/> set on a reply error, null without one when there was no reply.</summary>
    internal static string? ReadText(CliClient client, string targetJson, int lastN, out string? error)
    {
        error = null;
        var reply = client.Send("{\"command\":\"terminal-read\"" + targetJson + ",\"last\":" + lastN + "}");
        if (reply is null) return null;
        var root = CliClient.Parse(reply);
        if (!CliClient.Ok(root))
        {
            error = CliClient.Str(root, "error", "unknown");
            return null;
        }
        return CliClient.Str(root, "text", "");
    }

    public static int Layout(CliClient client, string[] args)
    {
        var sub = args.Length > 0 ? args[0] : "status";
        var reply = client.Send("{\"command\":\"layout\",\"sub\":\"" + CliIpcProtocol.Escape(sub) + "\"}");
        if (reply is null) return client.NoWait ? 0 : 1;
        var root = CliClient.Parse(reply);
        if (!CliClient.Ok(root))
        {
            Console.Error.WriteLine($"Error: {CliClient.Str(root, "error", "unknown")}");
            return 1;
        }
        var panes = root.TryGetProperty("panes", out var p) ? p.GetInt32() : 0;
        Console.WriteLine($"layout {CliClient.Str(root, "sub")} | workspace={CliClient.Str(root, "workspace", "-")} panes={panes}");
        Console.WriteLine("--- JSON ---");
        Console.WriteLine(reply);
        return 0;
    }

    public static int BotChat(CliClient client, string[] args)
    {
        var from = "CLI";
        var parts = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--from", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) { from = args[++i]; continue; }
            parts.Add(args[i]);
        }
        var message = string.Join(" ", parts).Trim();
        if (message.Length == 0)
        {
            Console.Error.WriteLine("Usage: bot-chat <message> [--from <name>]");
            return 1;
        }
        var request = "{\"command\":\"bot-chat\",\"message\":\"" + CliIpcProtocol.Escape(message) + "\",\"from\":\"" + CliIpcProtocol.Escape(from) + "\"}";
        return Simple(client, request, root => $"Delivered to AgentBot (from={CliClient.Str(root, "from", from)}, {CliClient.Str(root, "message_length", "?")} chars).");
    }

    public static int BotAsk(CliClient client, string[] args)
    {
        var text = string.Join(" ", args).Trim();
        if (text.Length == 0)
        {
            Console.Error.WriteLine("Usage: bot-ask <text...>");
            return 1;
        }
        var request = "{\"command\":\"bot-ask\",\"text\":\"" + CliIpcProtocol.Escape(text) + "\"}";
        return Simple(client, request, _ => "Sent to AgentBot (AI mode). Watch the pane or the log for [AIMODE] result.");
    }

    /// <summary>
    /// <c>&lt;g&gt; &lt;t&gt;</c> → <c>,"group_index":g,"tab_index":t</c>; <c>--alias name</c> → <c>,"alias":"name"</c>.
    /// <paramref name="consumed"/> is how many leading args the target took.
    /// </summary>
    internal static bool TryParseTarget(string[] args, string usage, out string targetJson, out int consumed)
    {
        targetJson = "";
        consumed = 0;
        if (args.Length >= 2 && args[0].Equals("--alias", StringComparison.OrdinalIgnoreCase))
        {
            targetJson = ",\"alias\":\"" + CliIpcProtocol.Escape(args[1]) + "\"";
            consumed = 2;
            return true;
        }
        if (args.Length >= 2 && int.TryParse(args[0], out var g) && int.TryParse(args[1], out var t))
        {
            targetJson = $",\"group_index\":{g},\"tab_index\":{t}";
            consumed = 2;
            return true;
        }
        Console.Error.WriteLine("Usage: " + usage);
        Console.Error.WriteLine("  Target is '<group_index> <tab_index>' or '--alias <name>'. Use 'terminal-list' / 'terminal-alias list'.");
        return false;
    }

    internal static int Simple(CliClient client, string request, Func<JsonElement, string> okLine)
    {
        var reply = client.Send(request);
        if (reply is null) return client.NoWait ? 0 : 1;
        var root = CliClient.Parse(reply);
        if (CliClient.Ok(root))
        {
            Console.WriteLine(okLine(root));
            return 0;
        }
        Console.Error.WriteLine($"Error: {CliClient.Str(root, "error", "unknown")}");
        return 1;
    }
}
