using AgentOne.Agent;
using AgentOne.Llm;
using AgentOne.Services;
using AgentOne.Tools;

namespace AgentOne.Commands;

/// <summary>
/// The same loop as <see cref="RunCommand"/>, kept alive across turns so the
/// conversation accumulates. No TUI in v0 — a plain readline REPL is the part
/// that has to work on Windows, macOS and Linux terminals alike.
/// </summary>
public sealed class ChatCommand
{
    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (!AgentOptions.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine($"agent-one chat: {error}");
            return 2;
        }

        if (options.Help)
        {
            PrintHelp();
            return 0;
        }

        IChatProvider provider;
        try
        {
            provider = ChatProviderFactory.Create(options.Config);
        }
        catch (ChatProviderException ex)
        {
            Console.Error.WriteLine("agent-one chat: " + ex.Message);
            return 2;
        }

        using var disposable = provider as IDisposable;

        using var toolbelt = new CompositeToolbelt(
            (ToolCatalog.FilesFamily, new LocalFileToolbelt(options.Root)),
            (ToolCatalog.WebFamily, new WebToolbelt(TimeSpan.FromSeconds(options.Config.TimeoutSeconds))));
        var loop = new AgentLoop(provider, toolbelt, options.Config.MaxSteps);
        loop.Reset();

        SessionStore? session = options.Config.SaveSessions ? SessionStore.Create("chat") : null;

        loop.StepCompleted += step =>
        {
            session?.Step(step);
            if (options.Verbose)
                Console.Error.WriteLine($"  [{step.Index}] {step.Tool}: {step.Detail}");
        };

        Console.WriteLine($"agent-one chat — provider {provider.Name}, model {options.Config.Model}");
        Console.WriteLine($"tools:     {toolbelt.Scope}");
        if (session is not null) Console.WriteLine($"session:   {session.Path}");
        Console.WriteLine("/reset clears the conversation, /exit or Ctrl+C quits.");
        Console.WriteLine();

        while (!ct.IsCancellationRequested)
        {
            Console.Write("> ");
            var line = Console.ReadLine();
            if (line is null) break;                     // EOF (piped input ended)

            line = line.Trim();
            if (line.Length == 0) continue;

            if (line is "/exit" or "/quit") break;
            if (line is "/reset")
            {
                loop.Reset();
                Console.WriteLine("(conversation cleared)");
                continue;
            }

            session?.Prompt(line);
            var run = await loop.RunAsync(line, ct);
            session?.Result(run);

            Console.WriteLine(run.Succeeded
                ? run.Text
                : $"[stopped: {run.Reason}] {run.Text}");
            Console.WriteLine();
        }

        return 0;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            agent-one chat              Interactive session; the conversation carries over.

            Options: the same as `agent-one run` (see `agent-one help run`).

            In-session commands:
              /reset      Start the conversation over (tools and config unchanged)
              /exit       Leave
            """);
    }
}
