using System.Diagnostics;
using System.Text.Json;
using Agent.Common;
using Agent.Common.Module;
using Agent.Common.Agents;
using Agent.Common.Platform;

namespace AgentZeroAvalonia.Cli;

/// <summary>
/// <c>AgentZeroLite -cli &lt;verb&gt;</c> on the Avalonia host (M0034 skeleton; the full
/// verb set lands in M0039). Global options and the usage text follow the WPF
/// <c>CliHandler</c> so scripts written for one host run against the other.
/// </summary>
internal static class CliMain
{
    public static int Run(string[] args)
    {
        if (OperatingSystem.IsWindows()) WindowsConsole.Attach();
        try
        {
            return RunCore(args);
        }
        finally
        {
            if (OperatingSystem.IsWindows()) WindowsConsole.Detach();
        }
    }

    private static int RunCore(string[] args)
    {
        var cliArgs = args.SkipWhile(a => !a.Equals("-cli", StringComparison.OrdinalIgnoreCase)).Skip(1).ToList();
        var client = new CliClient();

        for (var i = cliArgs.Count - 1; i >= 0; i--)
        {
            if (cliArgs[i].Equals("--no-wait", StringComparison.OrdinalIgnoreCase))
            {
                client.NoWait = true;
                cliArgs.RemoveAt(i);
            }
            else if (cliArgs[i].Equals("--timeout", StringComparison.OrdinalIgnoreCase) && i + 1 < cliArgs.Count)
            {
                if (int.TryParse(cliArgs[i + 1], out var t)) client.TimeoutMs = t;
                cliArgs.RemoveAt(i + 1);
                cliArgs.RemoveAt(i);
            }
        }

        if (cliArgs.Count == 0)
        {
            PrintUsage();
            return 0;
        }

        var command = cliArgs[0].ToLowerInvariant();
        var rest = cliArgs.Skip(1).ToArray();
        return command switch
        {
            "help" or "--help" or "-h" or "/?" => Help(rest),
            "version" or "--version" or "-v" => PrintVersion(),
            "status" => Status(client),
            "open-win" => OpenWin(),
            "close-win" => Simple(client, "{\"command\":\"close-win\"}", "Close signal sent to AgentZero Lite."),
            "selftest" => SelfTest.Run(rest),
            "terminal-list" => TerminalVerbs.List(client),
            "terminal-send" => TerminalVerbs.Send(client, rest),
            "terminal-key" => TerminalVerbs.Key(client, rest),
            "terminal-read" => TerminalVerbs.Read(client, rest),
            "layout" => TerminalVerbs.Layout(client, rest),
            "bot-chat" => TerminalVerbs.BotChat(client, rest),
            "bot-ask" => TerminalVerbs.BotAsk(client, rest),
            "terminal-wait" => MoreVerbs.TerminalWait(client, rest),
            "terminal-alias" => MoreVerbs.TerminalAlias(client, rest),
            "web" => MoreVerbs.Web(client, rest),
            "mcp" => MoreVerbs.Mcp(client, rest),
            "plugin" => PluginVerbs.Run(client, rest),
            "code" => Code(client, rest),
            "worktree" => Worktree(client, rest),
            "agent-state" => AgentState(client),
            "attention" => Attention(client, rest),
            "cost" => Cost(rest),
            "webview2" => WebView2(rest),
            _ => Unknown(command),
        };
    }

    // ── verbs ────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>webview2 status|install</c> — runs here, not in the GUI: the case it exists for is a
    /// machine where the GUI's terminals cannot render. JSON on stdout, exit 0 when installed.
    /// </summary>
    private static int WebView2(string[] args)
    {
        var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
        if (verb is not ("status" or "install"))
        {
            Console.Error.WriteLine("Usage: webview2 status | install");
            return 2;
        }
        var status = Agent.Common.Platform.WebView2Runtime.Probe();
        if (verb == "install" && !status.Installed && OperatingSystem.IsWindows())
            status = Agent.Common.Platform.WebView2Runtime.InstallAsync(line => Console.Error.WriteLine(line))
                .GetAwaiter().GetResult();
        Console.WriteLine(new System.Text.Json.Nodes.JsonObject
        {
            ["ok"] = true,
            ["installed"] = status.Installed,
            ["version"] = status.Version,
            ["scope"] = status.Scope,
            ["packaged"] = OperatingSystem.IsWindows() && Agent.Common.Services.DesktopAppBreakaway.IsPackagedProcess(),
        }.ToJsonString());
        return status.Installed ? 0 : 1;
    }

    private static int Status(CliClient client)
    {
        var reply = client.Send("{\"command\":\"status\"}");
        if (reply is null) return 1;
        var root = CliClient.Parse(reply);
        Console.WriteLine("AgentZero Lite (Avalonia) is running.");
        Console.WriteLine($"  host    : {CliClient.Str(root, "host")}");
        Console.WriteLine($"  version : {CliClient.Str(root, "version")}");
        Console.WriteLine($"  os      : {CliClient.Str(root, "os")}");
        if (root.TryGetProperty("groups", out var groups)) Console.WriteLine($"  groups  : {groups}");
        if (root.TryGetProperty("terminals", out var terms)) Console.WriteLine($"  terminals: {terms}");
        return CliClient.Ok(root) ? 0 : 1;
    }

    /// <summary>The WPF host's output for the same reply: a rollup line, then one line per terminal.</summary>
    private static int AgentState(CliClient client)
    {
        var reply = client.Send("{\"command\":\"agent-state\"}");
        if (reply is null) return client.NoWait ? 0 : 1;
        var root = CliClient.Parse(reply);
        if (!CliClient.Ok(root))
        {
            Console.Error.WriteLine($"Error: {CliClient.Str(root, "error", "unknown")}");
            return 1;
        }
        int attention = root.TryGetProperty("attention", out var ap) ? ap.GetInt32() : 0;
        Console.WriteLine($"Agents needing attention: {attention}");
        if (root.TryGetProperty("tabs", out var tabs) && tabs.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var t in tabs.EnumerateArray())
            {
                var g = t.GetProperty("group").GetInt32();
                var tb = t.GetProperty("tab").GetInt32();
                var title = t.GetProperty("title").GetString() ?? "";
                var state = t.GetProperty("state").GetString() ?? "";
                var seen = t.TryGetProperty("seen", out var sp) && sp.GetBoolean();
                var flag = state == "blocked" || (state == "done" && !seen) ? " ←" : "";
                Console.WriteLine($"  [{g}:{tb}] {state,-8} {(seen ? " " : "*")} {title}{flag}");
            }
        }
        return 0;
    }

    private static int Simple(CliClient client, string request, string okLine)
    {
        var reply = client.Send(request);
        if (reply is null) return 1;
        var root = CliClient.Parse(reply);
        if (CliClient.Ok(root))
        {
            Console.WriteLine(okLine);
            return 0;
        }
        Console.Error.WriteLine($"Error: {CliClient.Str(root, "error", "unknown")}");
        return 1;
    }

    private static int OpenWin()
    {
        var self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self))
        {
            Console.Error.WriteLine("Error: cannot resolve the executable path.");
            return 1;
        }
        Process.Start(new ProcessStartInfo(self) { UseShellExecute = false });
        Console.WriteLine("Launched AgentZero Lite GUI.");
        return 0;
    }

    private static int Help(string[] args)
    {
        if (args.Length > 0 && !args[0].StartsWith("--"))
        {
            var guide = AgentSkillGuides.Get(args[0]);
            if (guide is not null)
            {
                Console.WriteLine(guide);
                return 0;
            }
            Console.Error.WriteLine($"Unknown help topic: {args[0]}");
            Console.WriteLine("Topics: " + string.Join(", ", AgentSkillGuides.Topics));
            return 1;
        }
        PrintUsage();
        return 0;
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"AgentZero Lite (Avalonia) {AppVersionProvider.GetDisplayVersion()}");
        Console.WriteLine($"  exe : {Environment.ProcessPath}");
        Console.WriteLine($"  os  : {Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier})");
        return 0;
    }

    /// <summary><c>code open [path] | close | status</c> — the code view in the bottom panel.</summary>
    private static int Code(CliClient client, string[] args)
    {
        var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
        if (verb is "help" or "--help" or "-h")
        {
            Console.WriteLine("Usage: AgentZeroLite -cli code open [path] | close | status");
            Console.WriteLine("  open [path]   Show the code view (bottom panel, CODE tab); open a file of the active workspace");
            Console.WriteLine("                (relative to the workspace, or absolute inside it)");
            Console.WriteLine("  view preview|plain   Markdown: rendered with mermaid diagrams (default), or its source");
            Console.WriteLine("  tab files|gitlog     The files, or the GIT LOG tab beside CODE (recent commits, read with git)");
            Console.WriteLine("  close         Close the code view");
            Console.WriteLine("  status        Root, open file, kind, visible/docked");
            return 0;
        }
        var payload = new System.Text.Json.Nodes.JsonObject
        {
            ["command"] = "code",
            ["verb"] = verb,
            ["path"] = args.Length > 1 ? string.Join(" ", args.Skip(1)) : "",
        };
        var reply = client.Send(payload.ToJsonString());
        if (reply is null) return client.NoWait ? 0 : 1;
        Console.WriteLine(reply);
        return CliClient.Ok(CliClient.Parse(reply)) ? 0 : 1;
    }

    private static int Worktree(CliClient client, string[] args)
    {
        var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        if (verb is "help" or "--help" or "-h")
        {
            Console.WriteLine("Usage: AgentZeroLite -cli worktree list | open <path|branch> | close <path|branch>");
            Console.WriteLine("  list               Workspaces with their git branch, which row each is nested under,");
            Console.WriteLine("                     and the repository's other worktrees detected under the host row");
            Console.WriteLine("  open <path|branch> Switch to a detected worktree (opens it as a workspace the first time)");
            Console.WriteLine("  close <path|branch> Close the workspace a worktree is open as (the folder stays)");
            return 0;
        }
        var payload = new System.Text.Json.Nodes.JsonObject
        {
            ["command"] = "worktree",
            ["verb"] = verb,
            ["arg"] = args.Length > 1 ? string.Join(" ", args.Skip(1)) : "",
        };
        var reply = client.Send(payload.ToJsonString());
        if (reply is null) return client.NoWait ? 0 : 1;
        Console.WriteLine(reply);
        return CliClient.Ok(CliClient.Parse(reply)) ? 0 : 1;
    }

    private static int Attention(CliClient client, string[] args)
    {
        var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
        if (verb is "help" or "--help" or "-h")
        {
            Console.WriteLine("Usage: AgentZeroLite -cli attention status | test");
            Console.WriteLine("  status   The notification settings in effect (Settings -> Notifications)");
            Console.WriteLine("  test     Fire a sample \"needs you\" alert: corner popup + taskbar flash / Dock bounce");
            return 0;
        }
        var payload = new System.Text.Json.Nodes.JsonObject { ["command"] = "attention", ["verb"] = verb };
        var reply = client.Send(payload.ToJsonString());
        if (reply is null) return client.NoWait ? 0 : 1;
        Console.WriteLine(reply);
        return CliClient.Ok(CliClient.Parse(reply)) ? 0 : 1;
    }

    /// <summary>
    /// Month-to-date agent spend, by model and project, priced with Settings → Budget. Reads the
    /// shared database in this process, like the WPF host's <c>cost</c> — no GUI needed.
    /// </summary>
    private static int Cost(string[] args)
    {
        if (args.Any(a => a is "help" or "--help" or "-h"))
        {
            Console.WriteLine("Usage: AgentZeroLite -cli cost [--all] [--json]");
            Console.WriteLine("  Estimated spend of the agents in your terminals this month (and all time), by model and");
            Console.WriteLine("  project folder, against the monthly cap. Prices: Settings -> Budget (budget-settings.json).");
            return 0;
        }
        try
        {
            try { Agent.Common.Data.AppDbContext.InitializeDatabase(); } catch { /* the GUI may hold it; reading still works */ }
            var settings = Agent.Common.Telemetry.BudgetSettingsStore.Load();
            var r = Agent.Common.Telemetry.CostReport.Load(settings, DateTime.UtcNow);
            if (args.Contains("--json"))
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    ok = true,
                    since = r.SinceUtc,
                    month = new { spentUsd = r.Month.SpentUsd, capUsd = r.Month.CapUsd, hasCap = r.Month.HasCap, overBudget = r.Month.OverBudget, nearingCap = r.Month.NearingCap(), turns = r.MonthTurns },
                    byModel = r.MonthByModel.Select(l => new { model = l.Key, usd = l.Usd, turns = l.Turns, tokens = l.Tokens }),
                    byProject = r.MonthByProject.Select(l => new { project = l.Key, usd = l.Usd, turns = l.Turns, tokens = l.Tokens }),
                    allTime = new { usd = r.AllTimeUsd, turns = r.AllTimeTurns },
                    unpriced = r.UnpricedModels,
                }));
                return 0;
            }
            if (r.AllTimeTurns == 0)
            {
                Console.WriteLine("No token usage recorded yet.");
                return 0;
            }
            var m = r.Month;
            Console.WriteLine(m.HasCap
                ? $"This month: ${m.SpentUsd:F2} of ${m.CapUsd:F2} ({m.FractionUsed * 100:F0}%){(m.OverBudget ? "  OVER BUDGET" : m.NearingCap() ? "  nearing cap" : "")}  ({r.MonthTurns:N0} turns)"
                : $"This month: ${m.SpentUsd:F2}  (no cap)  ({r.MonthTurns:N0} turns)");
            Console.WriteLine("By model:");
            foreach (var l in r.MonthByModel) Console.WriteLine($"  {l.Key,-32} ${l.Usd,10:F2}   ({l.Turns:N0} turns)");
            var top = args.Contains("--all") ? int.MaxValue : 12;
            Console.WriteLine("By project:");
            foreach (var l in r.MonthByProject.Take(top)) Console.WriteLine($"  {l.Key,-32} ${l.Usd,10:F2}   ({l.Turns:N0} turns)");
            if (r.MonthByProject.Count > top)
                Console.WriteLine($"  … {r.MonthByProject.Count - top} more (${r.MonthByProject.Skip(top).Sum(l => l.Usd):F2}) — --all lists them");
            Console.WriteLine($"All time: ${r.AllTimeUsd:F2}  ({r.AllTimeTurns:N0} turns)");
            if (r.UnpricedModels.Count > 0)
                Console.WriteLine($"No price for: {string.Join(", ", r.UnpricedModels)} (counted as $0 — add one in Settings -> Budget)");
            Console.WriteLine("(estimate — prices are editable defaults, not a live feed)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown or not-yet-ported command: {command}");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine($"AgentZero Lite CLI (Avalonia host) {AppVersionProvider.GetDisplayVersion()}");
        Console.WriteLine();
        Console.WriteLine("Usage: AgentZeroLite -cli <command> [--no-wait] [--timeout N]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  status                 Show app state");
        Console.WriteLine("  version                Print the version of this exe");
        Console.WriteLine("  open-win / close-win   Launch / close the GUI");
        Console.WriteLine("  help [agentzero]       This text, or the agent-facing guide");
        Console.WriteLine("  selftest pty|ipc|secrets   CI self-checks (no GUI needed)");
        Console.WriteLine("  terminal-list          List workspaces and terminal tabs");
        Console.WriteLine("  terminal-send <g> <t> <text...>   Type text + Enter into a terminal");
        Console.WriteLine("  terminal-key <g> <t> <key>        Send a key (cr, esc, tab, ctrlc, up, ...)");
        Console.WriteLine("  terminal-read <g> <t> [--last N]  Read the screen text (or the last N chars)");
        Console.WriteLine("  terminal-wait <g> <t> [--until working|blocked|idle|done] [--agent <name>] [--timeout-ms N] [--idle-ms N]");
        Console.WriteLine("  terminal-alias list | set <g> <t> <name> | rm <name>   Name a terminal; then use --alias <name> as the target");
        Console.WriteLine("  web open|search|read|tabs ...        Web surface (headless in this host); 'web help' for options");
        Console.WriteLine("  mcp list|import|refresh|allow|deny|call ...   MCP servers for AI mode; 'mcp help' for options");
        Console.WriteLine("  plugin list|open|call|capture ...    The Plugins page (WebDev plugins); 'plugin help' for options");
        Console.WriteLine("  agent-state                          Detected agent state per terminal + attention rollup");
        Console.WriteLine("  attention status | test              \"An agent needs you\" notifications (popup + taskbar / Dock)");
        Console.WriteLine("  cost [--all] [--json]                Agent spend this month by model / project vs the cap (no GUI needed)");
        Console.WriteLine("  webview2 status | install            The WebView2 runtime terminals need on Windows (no GUI needed)");
        Console.WriteLine("  code open [path] | close | status    The code view (bottom panel): the active workspace's files");
        Console.WriteLine("  worktree list | open | close <path|branch>   Git worktrees detected under each workspace (sidebar nesting)");
        Console.WriteLine("  layout [status|split-right|split-down|close-tab|close-pane|add|next-tab|prev-tab|move-tab|focus-<dir>]");
        Console.WriteLine("  bot-chat <message> [--from <name>]   Deliver a message to AgentBot (DONE(...) is the peer envelope)");
        Console.WriteLine("  bot-ask <text...>                    Ask AgentBot in AI mode (starts an agent-loop turn)");
        Console.WriteLine();
        Console.WriteLine("The GUI answers over a named pipe (AgentZeroLite.cli); it must be running.");
    }
}
