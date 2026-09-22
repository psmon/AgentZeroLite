using System.Reflection;
using AgentOne.Commands;
using AgentOne.Services;

namespace AgentOne;

/// <summary>
/// Entry point and command router. Deliberately a plain switch over argv rather
/// than a parser library: it keeps the AOT binary free of reflection-based
/// command binding and the whole surface visible in one screen.
/// </summary>
internal static class Program
{
    internal static string Version =>
        typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // First Ctrl+C unwinds the run cleanly; a second one lets the runtime kill us.
            e.Cancel = !cts.IsCancellationRequested;
            cts.Cancel();
        };

        if (args.Length == 0)
        {
            PrintHelp();
            return 0;
        }

        if (args[0] is "-v" or "--version")
        {
            Console.WriteLine($"agent-one v{Version}");
            return 0;
        }

        if (args[0] is "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var rest = args[1..];

        try
        {
            return command switch
            {
                "run" => await new RunCommand().ExecuteAsync(rest, cts.Token),
                "chat" or "repl" => await new ChatCommand().ExecuteAsync(rest, cts.Token),
                "session" => await new SessionCommand().ExecuteAsync(rest, cts.Token),
                "ask" => await new AskCommand().ExecuteAsync(rest, cts.Token),
                // `setup` is the name; `tui` stays as the alias the release smoke test and old notes use.
                "setup" or "tui" when rest.Length > 0 && rest[0] == "--selftest" => await Tui.ConfigTuiApp.SelfTestAsync(),
                "setup" or "tui" => await Tui.ConfigTuiApp.RunAsync(),
                "config" when rest.Length > 0 && rest[0] == "tui" => await Tui.ConfigTuiApp.RunAsync(),
                "config" => new ConfigCommand().Execute(rest),
                "models" => await new ModelsCommand().ExecuteAsync(rest, cts.Token),
                "auth" => await new AuthCommand().ExecuteAsync(rest, cts.Token),
                "jev" => await new JevCommand().ExecuteAsync(rest, cts.Token),
                "tools" => new ToolsCommand().Execute(rest),
                "memory" => new MemoryCommand().Execute(rest),
                "version" => PrintVersion(),
                "home" => PrintHome(),
                "help" => Help(rest),
                _ => Unknown(command)
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("agent-one: cancelled");
            return 130;
        }
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"agent-one v{Version}");
        return 0;
    }

    private static int PrintHome()
    {
        Console.WriteLine(AppPaths.BaseDir);
        Console.WriteLine($"  config:   {AppPaths.ConfigPath}");
        Console.WriteLine($"  workspaces: {AppPaths.WorkspacesDir}   (per-root memory.md + sessions/)");
        Console.WriteLine($"  this one:   {AppPaths.WorkspaceDir(Directory.GetCurrentDirectory())}");
        Console.WriteLine($"  sessions: {AppPaths.SessionDir}   (legacy, before workspaces)");
        Console.WriteLine($"  logs:     {AppPaths.LogDir}");
        return 0;
    }

    private static int Help(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 0;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "run": RunCommand.PrintHelp(); return 0;
            case "chat" or "repl": ChatCommand.PrintHelp(); return 0;
            case "session" or "ask": SessionCommand.PrintHelp(); return 0;
            case "config": ConfigCommand.PrintHelp(); return 0;
            case "models": ModelsCommand.PrintHelp(); return 0;
            case "auth": AuthCommand.PrintHelp(); return 0;
            case "jev": JevCommand.PrintHelp(); return 0;
            case "tools": ToolsCommand.PrintHelp(); return 0;
            case "memory": MemoryCommand.PrintHelp(); return 0;
            default:
                Console.Error.WriteLine($"agent-one help: no such command '{args[0]}'");
                return 2;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"agent-one: unknown command '{command}'");
        Console.Error.WriteLine("Run `agent-one help` for the command list.");
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
            agent-one v{Version} — a standalone CLI agent.

            Usage:
              agent-one <command> [options]

            Commands:
              run <prompt>     Ask once and print the answer
              chat             Interactive session
              session          Background session: start · status · stop · selftest
              ask <request>    Send one request to the background session
              setup            Set it up on a screen: connection, model, reasoning model, options, smart mode
              config           Show or change settings from the command line (~/.agent-one/config.json)
              auth             Store or inspect the API keys
              jev              Put a decision to TypeSafe / Jev (smart-mode bench)
              models           List what the configured endpoint can run
              tools            List the verbs the agent can call
              memory           What the workspace's knowledge graph holds · search · Cypher
              home             Print where agent-one keeps its files
              version          Print the version
              help <command>   Detailed help for one command

            Quick start (no API key, fully offline):
              agent-one setup                  ← set things up, then press t to test
              agent-one run "hello" --provider echo
              agent-one tools prompt

            With a real model:
              agent-one config set provider openai
              agent-one config set model gpt-4o-mini
              agent-one auth set               # paste the key; it is never echoed
              agent-one run "what does this project do?" -v
            """);
    }
}
