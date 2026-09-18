using System.Text;
using System.Text.Json;
using Agent.Common.Platform;

namespace AgentZeroAvalonia.Cli;

/// <summary>
/// The client side of the four terminal verbs (M0035): the same arguments, request JSON
/// and printed shapes as the WPF <c>CliHandler</c>, over the pipe instead of WM_COPYDATA.
/// The alias form (<c>--alias name</c>) arrives with M0039.
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
        if (!TryTarget(args, "terminal-send <group_index> <tab_index> <text...>", out var g, out var t)) return 1;
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: terminal-send <group_index> <tab_index> <text...>");
            return 1;
        }
        var text = string.Join(" ", args.Skip(2));
        var request = $"{{\"command\":\"terminal-send\",\"group_index\":{g},\"tab_index\":{t},\"text\":\"{CliIpcProtocol.Escape(text)}\"}}";
        return Simple(client, request, root => $"Sent {CliClient.Str(root, "sent_length", "?")} chars to terminal [{g}:{t}].");
    }

    public static int Key(CliClient client, string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine("Keys: cr|enter lf crlf esc tab shifttab|backtab backspace del ctrlc ctrld up down left right hex:<bytes>");
            return 0;
        }
        if (!TryTarget(args, "terminal-key <group_index> <tab_index> <key>", out var g, out var t)) return 1;
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: terminal-key <group_index> <tab_index> <key>");
            return 1;
        }
        var request = $"{{\"command\":\"terminal-key\",\"group_index\":{g},\"tab_index\":{t},\"key\":\"{CliIpcProtocol.Escape(args[2])}\"}}";
        return Simple(client, request, _ => $"Sent key '{args[2]}' to terminal [{g}:{t}].");
    }

    public static int Read(CliClient client, string[] args)
    {
        if (!TryTarget(args, "terminal-read <group_index> <tab_index> [--last N]", out var g, out var t)) return 1;
        var lastN = 0;
        for (var i = 2; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--last", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out var n))
            {
                lastN = n;
                break;
            }
        }
        var request = $"{{\"command\":\"terminal-read\",\"group_index\":{g},\"tab_index\":{t},\"last\":{lastN}}}";
        var reply = client.Send(request);
        if (reply is null) return client.NoWait ? 0 : 1;
        var root = CliClient.Parse(reply);
        if (!CliClient.Ok(root))
        {
            Console.Error.WriteLine($"Error: {CliClient.Str(root, "error", "unknown")}");
            return 1;
        }
        Console.WriteLine(CliClient.Str(root, "text", ""));
        return 0;
    }

    private static bool TryTarget(string[] args, string usage, out int g, out int t)
    {
        g = t = -1;
        if (args.Length >= 2 && int.TryParse(args[0], out g) && int.TryParse(args[1], out t)) return true;
        Console.Error.WriteLine("Usage: " + usage);
        Console.Error.WriteLine("  Use 'terminal-list' to see group and tab indexes.");
        return false;
    }

    private static int Simple(CliClient client, string request, Func<JsonElement, string> okLine)
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
