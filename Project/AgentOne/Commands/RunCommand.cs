using System.Text;
using System.Text.Json;
using AgentOne.Agent;
using AgentOne.Llm;
using AgentOne.Services;
using AgentOne.Tools;

namespace AgentOne.Commands;

/// <summary>One question, one answer, one exit code — the form other programs script against.</summary>
public sealed class RunCommand
{
    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (!AgentOptions.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine($"agent-one run: {error}");
            return 2;
        }

        if (options.Help)
        {
            PrintHelp();
            return 0;
        }

        var prompt = string.Join(' ', options.Positional).Trim();
        if (prompt.Length == 0)
        {
            // Allow `echo "question" | agent-one run` so the prompt can be piped.
            // Read the pipe as UTF-8 explicitly: Console.In decodes redirected
            // input with the console's code page, which mangles anything
            // non-ASCII on a Windows box running a legacy ANSI page.
            if (Console.IsInputRedirected)
            {
                using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
                prompt = (await stdin.ReadToEndAsync(ct)).Trim();
            }
            if (prompt.Length == 0)
            {
                Console.Error.WriteLine("agent-one run: no prompt given (pass it as an argument or on stdin)");
                return 2;
            }
        }

        IChatProvider provider;
        try
        {
            provider = ChatProviderFactory.Create(options.Config);
        }
        catch (ChatProviderException ex)
        {
            Console.Error.WriteLine("agent-one run: " + ex.Message);
            return 2;
        }

        using var disposable = provider as IDisposable;

        using var toolbelt = new CompositeToolbelt(
            (ToolCatalog.FilesFamily, new LocalFileToolbelt(options.Root)),
            (ToolCatalog.WebFamily, new WebToolbelt(TimeSpan.FromSeconds(options.Config.TimeoutSeconds))));
        var loop = new AgentLoop(provider, toolbelt, options.Config.MaxSteps);

        SessionStore? session = options.Config.SaveSessions ? SessionStore.Create("run") : null;
        session?.Prompt(prompt);

        loop.StepCompleted += step =>
        {
            session?.Step(step);
            if (options.Verbose && !options.Json)
                Console.Error.WriteLine($"  [{step.Index}] {step.Tool}: {step.Detail}");
        };

        var run = await loop.RunAsync(prompt, ct);
        session?.Result(run);

        if (options.Json)
        {
            var report = new RunReport
            {
                Ok = run.Succeeded,
                StopReason = run.Reason.ToString(),
                Text = run.Text,
                Steps = run.Steps.Count,
                ElapsedMs = (long)run.Elapsed.TotalMilliseconds,
                Provider = provider.Name,
                Model = options.Config.Model,
                Session = session?.Path
            };
            Console.WriteLine(JsonSerializer.Serialize(report, AgentOneWireJson.Default.RunReport));
        }
        else if (run.Succeeded)
        {
            Console.WriteLine(run.Text);
        }
        else
        {
            Console.Error.WriteLine($"agent-one: stopped ({run.Reason}) — {run.Text}");
        }

        return run.ExitCode;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            agent-one run <prompt>      Ask once, print the answer, exit.

            Options:
              -r, --root <dir>        Workspace the tools may read (default: cwd)
              -p, --provider <name>   echo | openai            (default: from config)
              -m, --model <name>      Model id                 (default: from config)
                  --base-url <url>    OpenAI-compatible endpoint base URL
                  --max-steps <n>     Tool-loop budget, 1..100
                  --temperature <t>   0..2
                  --timeout <secs>    Per-request timeout
                  --no-session        Do not write a transcript to ~/.agent-one/sessions
                  --json              Print one JSON object instead of prose
              -v, --verbose           Trace each tool call on stderr

            Exit codes: 0 answered, 1 stopped early, 2 usage error, 130 cancelled.

            Examples:
              agent-one run "what does this project do?" --provider echo
              agent-one run "summarize the README" -r ./src -v
              echo "list the top-level files" | agent-one run --json
            """);
    }
}
