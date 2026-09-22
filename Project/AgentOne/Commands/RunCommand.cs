using System.Text;
using System.Text.Json;
using AgentOne.Agent;
using AgentOne.Llm;
using AgentOne.Llm.Decision;
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
            if (Console.IsInputRedirected)
                prompt = (await StandardInput.ReadToEndAsync(ct)).Trim();
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

        // Progress goes to stderr and the answer to stdout, so a pipe still gets
        // exactly the answer. --json and --quiet silence the display entirely.
        var showProgress = !options.Json && !options.Quiet;
        using var progress = ProgressDisplay.For(showProgress);

        loop.Streaming = showProgress && !Console.IsOutputRedirected;

        loop.ActivityStarted += what => progress.Activity(what);

        loop.StepCompleted += step =>
        {
            session?.Step(step);
            if (options.Verbose && !options.Json)
                Console.Error.WriteLine($"  [{step.Index}] {step.Tool}: {step.Detail}");
            else if (step.Tool != Agent.ToolCall.FinalTool)
                progress.Done(step.Tool, step.Ok);
        };

        var wroteAnything = false;
        loop.AnswerDelta += fragment =>
        {
            // The first fragment is the moment the spinner has to go: the answer
            // is about to occupy the screen.
            if (!wroteAnything) { progress.Stop(); wroteAnything = true; }
            Console.Out.Write(fragment);
            Console.Out.Flush();
        };

        // Smart mode plans and decides before the loop starts. `run` cannot ask
        // anybody, so a weak decision is taken anyway and said out loud on
        // stderr rather than silently followed.
        if (options.Config.SmartMode)
        {
            using var engine = new JevClient(options.Config);
            var smart = new SmartTurn(provider, engine, options.Config.JevConfidenceFloor);
            smart.ActivityStarted += what => progress.Activity(what);

            var plan = await smart.PrepareAsync(prompt, toolbelt.Scope, ct);

            // The decision was that a person has to settle it, and `run` has no
            // person. Doing it anyway would be the one thing the option exists
            // to prevent, so nothing is run and the exit code says so.
            if (plan.NeedsReview)
            {
                var decision = plan.Decision!;
                session?.Step(new AgentStep(0, "plan", $"needs_review ({decision.Confidence:0.00})", false));
                session?.Result(new AgentRun(StopReason.NeedsReview, SmartTurn.ReviewDescription, [], TimeSpan.Zero));

                if (options.Json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new RunReport
                    {
                        Ok = false,
                        StopReason = nameof(StopReason.NeedsReview),
                        Text = SmartTurn.ReviewDescription,
                        Provider = provider.Name,
                        Model = options.Config.Model,
                        Session = session?.Path
                    }, AgentOneWireJson.Default.RunReport));
                }
                else
                {
                    Console.Error.WriteLine($"agent-one: this needs a person (confidence {decision.Confidence:0.00})");
                    Console.Error.WriteLine("  " + SmartTurn.ReviewDescription);
                    foreach (var option in plan.Options.Where(o => o.Name != SmartTurn.ReviewOption))
                        Console.Error.WriteLine($"    {option.Name}: {option.Description}");
                    Console.Error.WriteLine("  run it in `agent-one chat`, where it can ask you, or use --basic.");
                }

                return 3;
            }

            if (plan.HasChoice)
            {
                var decision = plan.Decision!;

                // Only a confident decision steers the loop. An unsure one means
                // the approaches did not separate, and pushing the agent down one
                // of them anyway is worse than letting it work the problem out —
                // measured: an unsure plan turned a one-step answer into an
                // exhausted step budget.
                if (plan.Confident)
                {
                    prompt = plan.Guidance(prompt);
                    if (!options.Json)
                        Console.Error.WriteLine($"  plan: {decision.Choice} (confidence {decision.Confidence:0.00})");
                }
                else if (!options.Json)
                {
                    Console.Error.WriteLine(
                        $"  plan: unsure ({decision.Confidence:0.00} < {options.Config.JevConfidenceFloor:0.00}) — not steering");
                }

                session?.Step(new AgentStep(0, "plan", $"{decision.Choice} ({decision.Confidence:0.00})", plan.Confident));
            }
            else if (plan.Decision is { Ok: false } failed && !options.Json)
            {
                Console.Error.WriteLine($"  plan: unavailable ({failed.Message}) — running without one");
            }
        }

        var run = await loop.RunAsync(prompt, ct);
        session?.Result(run);
        progress.Stop();

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
            // Whatever streamed is already on screen; print only the rest, then
            // the newline the stream never wrote.
            Console.WriteLine(wroteAnything ? run.Unstreamed : run.Text);
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
              -q, --quiet             No progress display, no streaming
                  --smart             Plan first, then let the decision engine choose
                  --basic             Straight to the tool loop (the default)

            While it works, a live line on stderr says what it is doing, and the
            answer streams to stdout as the model writes it. Redirect stdout and
            you get exactly the answer; --json or --quiet silence the display.

            Exit codes: 0 answered, 1 stopped early, 2 usage error, 130 cancelled.

            Examples:
              agent-one run "what does this project do?" --provider echo
              agent-one run "summarize the README" -r ./src -v
              echo "list the top-level files" | agent-one run --json
            """);
    }
}
