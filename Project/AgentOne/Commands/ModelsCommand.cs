using AgentOne.Tui;

namespace AgentOne.Commands;

/// <summary>
/// The TUI picker's listing, on the command line — for scripts, for a quick
/// "is this endpoint alive and is my key right", and because the answer is
/// worth having without opening a screen.
/// </summary>
public sealed class ModelsCommand
{
    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (!AgentOptions.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine($"agent-one models: {error}");
            return 2;
        }

        if (options.Help)
        {
            PrintHelp();
            return 0;
        }

        var result = await ConfigTuiProbe.ListModelsAsync(options.Config, ct);

        if (!result.Ok)
        {
            Console.Error.WriteLine($"agent-one models: {result.Message}");
            Console.Error.WriteLine($"agent-one models: check baseUrl ({options.Config.BaseUrl}) and ${options.Config.ApiKeyEnv}");
            return 1;
        }

        foreach (var model in result.Models)
        {
            // Mark the configured one so `models` answers "what can I use" and
            // "what am I using" in the same glance.
            var marker = model == options.Config.Model ? "*" : " ";
            Console.WriteLine($"{marker} {model}");
        }

        Console.Error.WriteLine($"({result.Message})");
        return 0;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            agent-one models            List what the configured endpoint can run.

            The configured model is marked with *. The count goes to stderr, so
            `agent-one models` pipes cleanly into a picker of your own.

            Options: -p/--provider, -m/--model, --base-url, --timeout — the same
            overrides `run` takes, so you can list a different endpoint without
            changing your config.

            Exit codes: 0 listed at least one model, 1 the endpoint refused or
            answered with nothing, 2 usage error.

            It is also the cheapest health check there is: one request covers the
            base URL, the network path and the API key.

            Examples:
              agent-one models
              agent-one models --base-url http://localhost:11434/v1
              agent-one models | fzf | xargs agent-one config set model
            """);
    }
}
