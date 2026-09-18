using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Common.Agents;
using Agent.Common.Platform;

namespace AgentZeroAvalonia.Cli;

/// <summary>
/// <c>terminal-wait</c>, <c>terminal-alias</c> and <c>web</c> — the WPF <c>CliHandler</c> /
/// <c>WebCliCommands</c> client logic over the pipe (M0039). Same options, same output.
/// </summary>
internal static class MoreVerbs
{
    // ── terminal-wait: poll the screen until it goes idle, or until an agent state ──

    public static int TerminalWait(CliClient client, string[] args)
    {
        if (!TerminalVerbs.TryParseTarget(args,
                "terminal-wait <group> <tab> [--until <working|blocked|idle|done>] [--agent <name>] [--timeout-ms N] [--idle-ms N] [--stall-ms N]",
                out var target, out var consumed))
            return 1;

        int timeoutMs = 60000, idleMs = 1500, pollMs = 400, stallMs = 8000;
        string? until = null;
        var agentHint = "";
        for (var i = consumed; i < args.Length; i++)
        {
            if (args[i].Equals("--timeout-ms", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[i + 1], out var t)) timeoutMs = t;
            else if (args[i].Equals("--idle-ms", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[i + 1], out var d)) idleMs = d;
            else if (args[i].Equals("--stall-ms", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[i + 1], out var sm)) stallMs = sm;
            else if (args[i].Equals("--until", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) until = args[i + 1];
            else if (args[i].Equals("--agent", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) agentHint = args[i + 1];
        }

        if (until is not null)
        {
            if (!Enum.TryParse<AgentActivity>(until, ignoreCase: true, out var targetState))
            {
                Console.Error.WriteLine($"Invalid --until state '{until}' (working|blocked|idle|done)");
                return 1;
            }
            var manifest = AgentManifestCatalog.ForAgent(agentHint);
            var totalU = Stopwatch.StartNew();
            var sinceChange = Stopwatch.StartNew();
            var prev = AgentActivity.Unknown;
            while (totalU.ElapsedMilliseconds < timeoutMs)
            {
                var text = TerminalVerbs.ReadText(client, target, 4000, out var err);
                if (text is null && err is not null) { Console.Error.WriteLine("Error: " + err); return 1; }
                text ??= "";
                var snap = new ScreenSnapshot(text.Replace("\r\n", "\n").Split('\n'));
                var res = AgentStateDetector.Detect(manifest, snap);
                var state = res.StateChanged ? res.State : prev;
                if (state == targetState)
                {
                    Console.WriteLine($"reached {targetState} after {totalU.ElapsedMilliseconds}ms" + (res.MatchedRuleId is null ? "" : $" (rule {res.MatchedRuleId})"));
                    return 0;
                }
                if (state != prev) { prev = state; sinceChange.Restart(); }
                else if (sinceChange.ElapsedMilliseconds >= stallMs)
                {
                    Console.Error.WriteLine($"stalled: state '{state}' unchanged {stallMs}ms, target '{targetState}' not reached");
                    return 3;
                }
                Thread.Sleep(pollMs);
            }
            Console.Error.WriteLine($"terminal-wait: timed out after {timeoutMs}ms waiting for {targetState}");
            return 2;
        }

        var total = Stopwatch.StartNew();
        var idle = Stopwatch.StartNew();
        var last = TerminalVerbs.ReadText(client, target, 2000, out var firstErr);
        if (last is null && firstErr is not null) { Console.Error.WriteLine("Error: " + firstErr); return 1; }
        last ??= "";
        while (total.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(pollMs);
            var cur = TerminalVerbs.ReadText(client, target, 2000, out _) ?? last;
            if (!string.Equals(cur, last, StringComparison.Ordinal))
            {
                last = cur;
                idle.Restart();
            }
            else if (idle.ElapsedMilliseconds >= idleMs)
            {
                Console.WriteLine($"idle (stable {idleMs}ms) after {total.ElapsedMilliseconds}ms");
                return 0;
            }
        }
        Console.Error.WriteLine($"terminal-wait: timed out after {timeoutMs}ms without going idle");
        return 2;
    }

    // ── terminal-alias ──

    public static int TerminalAlias(CliClient client, string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: terminal-alias <list | set <group> <tab> <name> | rm <name>>");
            Console.Error.WriteLine("  Names a terminal so other commands can target it with --alias <name>.");
            return 1;
        }
        var sub = args[0].ToLowerInvariant();
        var request = "{\"command\":\"terminal-alias\",\"sub\":\"" + CliIpcProtocol.Escape(sub) + "\"";
        if (sub == "set")
        {
            if (args.Length < 4 || !int.TryParse(args[1], out var g) || !int.TryParse(args[2], out var t))
            {
                Console.Error.WriteLine("Usage: terminal-alias set <group_index> <tab_index> <name>");
                return 1;
            }
            request += $",\"group_index\":{g},\"tab_index\":{t},\"name\":\"{CliIpcProtocol.Escape(args[3])}\"";
        }
        else if (sub == "rm")
        {
            if (args.Length < 2) { Console.Error.WriteLine("Usage: terminal-alias rm <name>"); return 1; }
            request += ",\"name\":\"" + CliIpcProtocol.Escape(args[1]) + "\"";
        }
        else if (sub != "list")
        {
            Console.Error.WriteLine($"Unknown subcommand '{sub}'. Use: list | set | rm.");
            return 1;
        }
        request += "}";

        var reply = client.Send(request);
        if (reply is null) return client.NoWait ? 0 : 1;
        var root = CliClient.Parse(reply);
        var ok = CliClient.Ok(root);
        if (sub == "list")
        {
            if (!ok) { Console.Error.WriteLine($"Error: {CliClient.Str(root, "error", "unknown")}"); return 1; }
            var n = 0;
            if (root.TryGetProperty("aliases", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in arr.EnumerateArray())
                {
                    n++;
                    var name = a.GetProperty("alias").GetString();
                    var group = a.GetProperty("group").GetString();
                    var title = a.GetProperty("title").GetString();
                    var live = a.TryGetProperty("live", out var lp) && lp.GetBoolean();
                    var gi = a.TryGetProperty("group_index", out var gip) ? gip.GetInt32() : -1;
                    var ti = a.TryGetProperty("tab_index", out var tip) ? tip.GetInt32() : -1;
                    Console.WriteLine($"  {name,-16} -> {group}/{title}  {(live ? $"[{gi}:{ti}]" : "(not live)")}");
                }
            }
            if (n == 0) Console.WriteLine("No aliases. Add one: terminal-alias set <group> <tab> <name>");
            return 0;
        }
        if (ok)
        {
            Console.WriteLine(sub == "set"
                ? $"Alias '{CliClient.Str(root, "alias")}' -> {CliClient.Str(root, "group")}/{CliClient.Str(root, "title")}"
                : $"Removed alias '{CliClient.Str(root, "alias")}'.");
            return 0;
        }
        Console.Error.WriteLine($"Error: {CliClient.Str(root, "error", "unknown")}");
        return 1;
    }

    // ── web (the WPF WebCliCommands: one JSON object out, exit 0 when ok) ──

    public static int Web(CliClient client, string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            WebUsage();
            return args.Length == 0 ? 1 : 0;
        }
        var verb = args[0].ToLowerInvariant();
        var positional = new List<string>();
        int tab = 0, max = 5, maxChars = 0;
        string mode = "", find = "";
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--tab" when i + 1 < args.Length: int.TryParse(args[++i], out tab); break;
                case "--max" when i + 1 < args.Length: int.TryParse(args[++i], out max); break;
                case "--max-chars" when i + 1 < args.Length: int.TryParse(args[++i], out maxChars); break;
                case "--mode" when i + 1 < args.Length: mode = args[++i]; break;
                case "--find" when i + 1 < args.Length: find = args[++i]; break;
                default: positional.Add(args[i]); break;
            }
        }
        var payload = new JsonObject { ["command"] = "web", ["verb"] = verb, ["req"] = Guid.NewGuid().ToString("N")[..8] };
        switch (verb)
        {
            case "open":
                if (positional.Count == 0) { Console.Error.WriteLine("Usage: web open <url> [--tab N]"); return 1; }
                payload["url"] = positional[0];
                payload["tab"] = tab;
                break;
            case "search":
                if (positional.Count == 0) { Console.Error.WriteLine("Usage: web search <query...> [--max N]"); return 1; }
                payload["query"] = string.Join(" ", positional);
                payload["max"] = max;
                break;
            case "read":
                payload["tab"] = tab;
                payload["mode"] = mode;
                payload["find"] = find;
                payload["max_chars"] = maxChars;
                break;
            case "tabs":
                break;
            default:
                WebUsage();
                return 1;
        }
        if (client.TimeoutMs < 45000) client.TimeoutMs = 45000;
        var reply = client.Send(payload.ToJsonString());
        if (reply is null) return client.NoWait ? 0 : 1;
        Console.WriteLine(reply);
        return CliClient.Ok(CliClient.Parse(reply)) ? 0 : 1;
    }

    private static void WebUsage()
    {
        Console.WriteLine("Usage: AgentZeroLite -cli web <verb> [options]");
        Console.WriteLine();
        Console.WriteLine("Drives the host's web surface (headless in the Avalonia host). The GUI must be running.");
        Console.WriteLine();
        Console.WriteLine("Verbs:");
        Console.WriteLine("  open <url> [--tab N]                 Open a URL (tab 0 = new tab); prints tab id + page summary");
        Console.WriteLine("  search <query...> [--max N]          Search the web; prints {title,url,snippet} results");
        Console.WriteLine("  read [--tab N] [--mode summary|links|find] [--find <kw>] [--max-chars N]");
        Console.WriteLine("                                       Read an open tab (tab 0 = the active one)");
        Console.WriteLine("  tabs                                 List open tabs");
        Console.WriteLine();
        Console.WriteLine("Output is one JSON object; exit code 0 when ok is true.");
        Console.WriteLine("Default --timeout for this group is 45000 ms (page loads are slow).");
    }
}
