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
            _ => Unknown(command),
        };
    }

    // ── verbs ────────────────────────────────────────────────────────────────

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
        Console.WriteLine("  layout [status|split-right|split-down|close-tab|close-pane|add|next-tab|prev-tab|move-tab|focus-<dir>]");
        Console.WriteLine("  bot-chat <message> [--from <name>]   Deliver a message to AgentBot (DONE(...) is the peer envelope)");
        Console.WriteLine("  bot-ask <text...>                    Ask AgentBot in AI mode (starts an agent-loop turn)");
        Console.WriteLine();
        Console.WriteLine("The GUI answers over a named pipe (AgentZeroLite.cli); it must be running.");
    }
}
