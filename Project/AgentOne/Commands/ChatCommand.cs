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

        session.Noted += note => Console.WriteLine($"({note})");

        // A command the gate will not run on its own: the REPL is the person.
        // The turn is on this thread, so reading a line here is exactly right.
        session.Approver = (request, _) =>
        {
            progress.Stop();
            Console.WriteLine();
            Console.WriteLine($"⚠ run this command?  {request.Command}");
            Console.WriteLine($"  in {request.WorkingDirectory}");
            Console.WriteLine($"  not run unasked because: {request.Reason}");
            var answer = LineEditor.Read("  approve (y/n) › ");
            var yes = answer.Kind == LineKind.Entered && IsYes(answer.Text);
            progress.Restart();
            return Task.FromResult(yes);
        };

        Console.WriteLine($"agent-one chat — provider {session.ProviderName}, model {session.Model}" +
                          (session.ReasoningModel is { } strong ? $" (reasoning: {strong})" : ""));
        Console.WriteLine($"tools:     {session.ToolScope}");
        if (session.LogPath is { } log) Console.WriteLine($"session:   {log}");
        Console.WriteLine(session.SmartAvailable
            ? "Shift+Tab switches basic ↔ smart · /status · /new starts over · /exit quits."
            : "/status · /new starts over · /exit or Ctrl+C quits.  (no TypeSafe key — smart mode unavailable)");
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
            if (line is "/reset" or "/new")
            {
                if (line == "/new") session.NewSession(); else session.Reset();
                Console.WriteLine(line == "/new" ? $"(new session: {session.LogPath ?? "not saved"})" : "(conversation cleared)");
                continue;
            }
            if (line == "/status")
            {
                foreach (var row in session.Stats().Describe()) Console.WriteLine("  " + row);
                Console.WriteLine();
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

    /// <summary>What counts as yes at an approval prompt, in the languages this tool is used in.</summary>
    public static bool IsYes(string answer) =>
        answer.Trim().ToLowerInvariant() is "y" or "yes" or "ok" or "approve" or "네" or "예" or "응" or "ㅇ" or "승인";

    public static void PrintHelp()
    {
        Console.WriteLine("""
            agent-one chat              Interactive session; the conversation carries over.

            In a terminal this opens the chat window: transcript above, your line at
            the bottom, the mode in the header. Piped, or with --plain, it is a
            line-at-a-time REPL with the same behaviour.

            Options: the same as `agent-one run`, plus
              --plain      The line REPL even in a terminal

            Keys (window):    Enter send · Shift+Tab basic/smart · F2 status
                              wheel or PageUp/PageDown scroll · Ctrl+End follow
                              Esc clear the line (twice: quit) · Ctrl+D quit
            Commands (both):  /status  context, counters, grants · /new  fresh session
                              /reset  clear the conversation · /exit  leave

            The agent can create files (workspace root only) and run commands
            (PowerShell on Windows, bash elsewhere). A command the gate does not
            trust is shown to you first: answer y to run it, anything else to skip.
            A folder you name by its absolute path becomes readable for the session.
            """);
    }
}
