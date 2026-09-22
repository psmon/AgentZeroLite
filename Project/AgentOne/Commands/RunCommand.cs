using System.Text.Json;
using AgentOne.Actors;
using AgentOne.Agent;
using AgentOne.Llm;
using AgentOne.Services;

namespace AgentOne.Commands;

/// <summary>
/// One question, one answer, one exit code — the form other programs script
/// against. A single-turn <see cref="ChatSession"/>, so smart mode's routing
/// and escalation are exactly the chat's, and there is one place they live.
/// </summary>
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

        // Progress goes to stderr and the answer to stdout, so a pipe still gets
        // exactly the answer. --json and --quiet silence the display entirely.
        var showProgress = !options.Json && !options.Quiet;
        var streaming = showProgress && !Console.IsOutputRedirected;

        IAgentSession session;
        try
        {
            session = AgentGateway.Start(options.Config, options.Root, streaming, logKind: "run");
        }
        catch (ChatProviderException ex)
        {
            Console.Error.WriteLine("agent-one run: " + ex.Message);
            return 2;
        }

        using (session)
        {
            using var progress = ProgressDisplay.For(showProgress);

            session.ActivityStarted += what => progress.Activity(what);

            session.StepCompleted += step =>
            {
                if (options.Verbose && !options.Json)
                    Console.Error.WriteLine($"  [{step.Index}] {step.Tool}: {step.Detail}");
                else if (step.Tool != ToolCall.FinalTool)
                    progress.Done(step.Tool, step.Ok);
            };

            var wroteAnything = false;
            session.AnswerDelta += fragment =>
            {
                // The first fragment is the moment the spinner has to go: the answer
                // is about to occupy the screen.
                if (!wroteAnything) { progress.Stop(); wroteAnything = true; }
                Console.Out.Write(fragment);
                Console.Out.Flush();
            };

            session.Decided += note =>
            {
                if (options.Json) return;
                // A draft that streamed and is now being replaced needs a line
                // break before the verdict, or the two answers run together.
                if (wroteAnything && note.Kind == "escalation") Console.Out.WriteLine();
                Console.Error.WriteLine($"  {note.Kind}: {note.Verdict} (confidence {note.Decision.Confidence:0.00})");
            };

            session.Noted += note => { if (!options.Json) Console.Error.WriteLine($"  {note}"); };
            session.DesignMade += lines =>
            {
                if (options.Json) return;
                Console.Error.WriteLine("  design:");
                foreach (var l in lines) Console.Error.WriteLine("    " + l);
            };

            // Nobody to ask: the design's own recommendation stands, and stderr says so.
            session.Chooser = (choice, _) =>
            {
                if (!options.Json)
                    Console.Error.WriteLine($"  decision: {choice.Options[choice.Recommended]} (the recommendation — run cannot ask)");
                return Task.FromResult("");
            };

            // `run` has nobody to ask. --yes stands in for the person; without it a
            // command the gate does not trust is skipped, and stderr says which.
            session.Approver = (request, _) =>
            {
                if (!options.Json)
                    Console.Error.WriteLine(options.Yes
                        ? $"  running (--yes): {request.Command}"
                        : $"  skipped — needs approval, and nobody is here to give it (use --yes): {request.Command}\n    because: {request.Reason}");
                return Task.FromResult(options.Yes);
            };

            var run = (await session.SubmitAsync(prompt, ct))!;
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
                    Provider = session.ProviderName,
                    Model = options.Config.Model,
                    Session = session.LogPath
                };
                Console.WriteLine(JsonSerializer.Serialize(report, AgentOneWireJson.Default.RunReport));
            }
            else
            {
                // Whatever streamed is already on screen; print only the rest, then
                // the newline the stream never wrote. A stopped turn still prints
                // its summary — that is the useful part — and names the stop on stderr.
                Console.WriteLine(wroteAnything ? run.Unstreamed : run.Text);
                if (!run.Succeeded) Console.Error.WriteLine($"agent-one: stopped ({run.Reason})");
            }

            return run.ExitCode;
        }
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            agent-one run <prompt>      Ask once, print the answer, exit.

            Options:
              -r, --root <dir>        Workspace the tools may read and write (default: cwd)
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
              -y, --yes               Run commands the gate would have asked you about
                  --smart             Route through the decision engine, escalate hard questions
                  --basic             Straight to the tool loop (the default)

            The agent can create files under the workspace root and run commands
            there (PowerShell on Windows, bash elsewhere). A command the gate does
            not trust is skipped — there is nobody to ask — unless --yes is given.

            While it works, a live line on stderr says what it is doing, and the
            answer streams to stdout as the model writes it. Redirect stdout and
            you get exactly the answer; --json or --quiet silence the display.

            Exit codes: 0 answered, 1 stopped early, 2 usage error, 130 cancelled.

            Examples:
              agent-one run "what does this project do?" --provider echo
              agent-one run "summarize the README" -r ./src -v
              agent-one run "scaffold a FastAPI hello service and run its tests" --smart --yes
              echo "list the top-level files" | agent-one run --json
            """);
    }
}
