using AgentOne.Agent;
using AgentOne.Llm;
using AgentOne.Services;
using AgentOne.Tui;

namespace AgentOne.Commands;

/// <summary>
/// Interactive conversation. In a terminal it opens the chat window; when input
/// or output is a pipe — or with --plain — it runs the line-at-a-time REPL, which
/// is what scripts and tests drive. Both sit on the same <see cref="ChatSession"/>,
/// so a turn behaves identically in either.
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

        var interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        var window = interactive && !options.Plain;

        ChatSession session;
        try
        {
            session = new ChatSession(options.Config, options.Root, streaming: !options.Quiet);
        }
        catch (ChatProviderException ex)
        {
            Console.Error.WriteLine("agent-one chat: " + ex.Message);
            return 2;
        }

        using (session)
        {
            return window
                ? await ChatTuiApp.RunAsync(session)
                : await RunPlainAsync(session, options, ct);
        }
    }

    /// <summary>The REPL: one prompt per line, progress on stderr, the answer streamed to stdout.</summary>
    private static async Task<int> RunPlainAsync(ChatSession session, AgentOptions options, CancellationToken ct)
    {
        var showProgress = !options.Quiet;
        using var progress = ProgressDisplay.For(showProgress);

        session.ActivityStarted += what => progress.Activity(what);
        session.StepCompleted += step =>
        {
            if (options.Verbose)
                Console.Error.WriteLine($"  [{step.Index}] {step.Tool}: {step.Detail}");
            else if (step.Tool is not ("final" or "unwrapped"))
                progress.Done(step.Tool, step.Ok);
        };

        var wroteAnything = false;
        session.AnswerDelta += fragment =>
        {
            if (!wroteAnything) { progress.Stop(); wroteAnything = true; }
            Console.Out.Write(fragment);
            Console.Out.Flush();
        };

        session.Decided += note =>
        {
            // A streamed draft that is about to be replaced needs its own line end.
            if (wroteAnything && note.Kind == "escalation") Console.WriteLine();
            Console.WriteLine($"({note.Kind}: {note.Verdict} · confidence {note.Decision.Confidence:0.00})");
        };

        Console.WriteLine($"agent-one chat — provider {session.ProviderName}, model {session.Model}" +
                          (session.ReasoningModel is { } strong ? $" (reasoning: {strong})" : ""));
        Console.WriteLine($"tools:     {session.ToolScope}");
        if (session.LogPath is { } log) Console.WriteLine($"session:   {log}");
        Console.WriteLine(session.SmartAvailable
            ? "Shift+Tab switches basic ↔ smart · /reset clears the conversation · /exit quits."
            : "/reset clears the conversation, /exit or Ctrl+C quits.  (no TypeSafe key — smart mode unavailable)");
        Console.WriteLine();

        while (!ct.IsCancellationRequested)
        {
            var input = LineEditor.Read(session.Smart ? "[smart] > " : "[basic] > ");
            if (input.Kind == LineKind.EndOfInput) break;

            if (input.Kind == LineKind.ToggleMode)
            {
                session.TryToggleSmart(out var message);
                Console.WriteLine($"({message})");
                continue;
            }

            var line = input.Text.Trim();
            if (line is "/exit" or "/quit") break;
            if (line == "/reset")
            {
                session.Reset();
                Console.WriteLine("(conversation cleared)");
                continue;
            }
            if (line.Length == 0) continue;

            wroteAnything = false;
            progress.Restart();

            var run = await session.SubmitAsync(line, ct);
            progress.Stop();
            if (run is null) continue;

            Console.WriteLine(run.Succeeded
                ? (wroteAnything ? run.Unstreamed : run.Text)
                : $"[stopped: {run.Reason}] {run.Text}");
            Console.WriteLine();
        }

        return 0;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            agent-one chat              Interactive session; the conversation carries over.

            In a terminal this opens the chat window: transcript above, your line at
            the bottom, the mode in the header. Piped, or with --plain, it is a
            line-at-a-time REPL with the same behaviour.

            Options: the same as `agent-one run`, plus
              --plain      The line REPL even in a terminal

            Keys (window):    Enter send · Shift+Tab basic/smart · wheel or PageUp/PageDown scroll
                              Ctrl+End follow · Esc clear the line (twice: quit) · Ctrl+D quit
            Commands (both):  /reset  start the conversation over · /exit  leave
            """);
    }
}
