using System.Text.Json;
using AgentOne.Agent;
using AgentOne.Services;

namespace AgentOne.Commands;

/// <summary>
/// One request to the background session — the headless face of chat. With
/// no session running it starts one first (in the current folder, or -r), so
/// the first `ask` is the only command a script needs; every later `ask`
/// carries the same conversation on. Printed the way the REPL prints a turn:
/// progress on stderr, the answer streamed to stdout, one summary line on
/// stderr at the end, questions asked back here.
///
/// `--json` prints the result event alone; `--jsonl` prints every event as it
/// happens, the result last — for a script or another agent. `--detach` hands
/// the request over and returns at once; `session wait` (the same printing,
/// attached to the running turn) collects it later.
///
/// Exit codes: 0 answered · 1 failed / stopped · 2 usage · 3 busy (a turn is
/// already running — wait for it).
/// </summary>
public sealed class AskCommand
{
    public const int BusyExitCode = 3;

    public Task<int> ExecuteAsync(string[] args, CancellationToken ct) => RunAsync(args, wait: false, ct);

    /// <summary>`session wait`: attach to the running turn, or print the last result.</summary>
    public Task<int> WaitAsync(string[] args, CancellationToken ct) => RunAsync(args, wait: true, ct);

    private static async Task<int> RunAsync(string[] args, bool wait, CancellationToken ct)
    {
        var name = wait ? "agent-one session wait" : "agent-one ask";
        bool yes = false, json = false, jsonl = false, quiet = false, detach = false, noStart = false, resume = false;
        var rest = new List<string>();

        foreach (var arg in args)
        {
            switch (arg)
            {
                case "-y" or "--yes": yes = true; break;
                case "--json": json = true; break;
                case "--jsonl": jsonl = true; break;
                case "-q" or "--quiet": quiet = true; break;
                case "--detach": detach = true; break;
                case "--no-start": noStart = true; break;
                case "--resume": resume = true; break;
                case "-h" or "--help":
                    SessionCommand.PrintHelp();
                    return 0;
                default: rest.Add(arg); break;
            }
        }

        // What is left is the request and, for a session this call starts, the chat options.
        if (!AgentOptions.TryParse([.. rest], out var options, out var error))
        {
            Console.Error.WriteLine($"{name}: {error}");
            return 2;
        }

        var text = string.Join(' ', options.Positional).Trim();
        if (!wait)
        {
            if (text.Length == 0 && Console.IsInputRedirected)
                text = (await StandardInput.ReadToEndAsync(ct)).Trim();
            if (text.Length == 0)
            {
                Console.Error.WriteLine($"{name}: no request given (pass it as an argument or on stdin)");
                return 2;
            }
        }

        var chatty = !quiet && !json && !jsonl;
        var record = SessionRegistry.LoadAlive();

        if (record is null)
        {
            if (wait || noStart)
            {
                Console.Error.WriteLine($"{name}: no background session is running — `agent-one session start`, or just `agent-one ask \"…\"`");
                return 1;
            }

            // The first ask starts the session — same options, this folder unless -r says otherwise.
            List<string> startArgs = [.. options.Flags];
            if (!options.Flags.Any(f => f is "-r" or "--root")) startArgs.AddRange(["-r", options.Root]);
            if (resume) startArgs.Add("--resume");

            var (started, failure) = await SessionCommand.SpawnAsync([.. startArgs], options.Root, ct);
            if (started is null)
            {
                Console.Error.WriteLine($"{name}: could not start the background session: {failure}");
                return 1;
            }
            record = started;
            if (!quiet) Console.Error.WriteLine($"(started the background session · pid {record.Pid} · {(record.Smart ? "smart" : "basic")} · {record.Root})");
        }
        else if (!wait && !quiet)
        {
            if (!SamePath(record.Root, options.Root))
                Console.Error.WriteLine($"(note: the running session works in {record.Root} — `agent-one session stop` first to switch folders)");
            if (options.Flags.Any(f => f is not ("-r" or "--root") && f.StartsWith('-')) || resume)
                Console.Error.WriteLine("(note: the session is already running — start options are ignored)");
        }

        var interactive = !Console.IsInputRedirected && !json && !jsonl;
        using var progress = ProgressDisplay.For(chatty);
        var deltaChars = 0;
        var attached = false;          // wait joined a running turn (vs. got the last result)
        var attachedMidway = false;    // …after some of its answer had already streamed

        Task OnEvent(PipeEvent e)
        {
            if (jsonl)
            {
                WriteJsonLine(e);
                return Task.CompletedTask;
            }
            if (!chatty) return Task.CompletedTask;

            switch (e.Event)
            {
                case "attached":
                    attached = true;
                    attachedMidway = (e.Streamed ?? 0) > 0;
                    progress.Stop();
                    Console.Error.WriteLine($"(attached to \"{e.Text}\" · running {SessionServer.Seconds(TimeSpan.FromMilliseconds(e.ElapsedMs ?? 0))} · {e.Steps ?? 0} steps{(string.IsNullOrEmpty(e.Kind) ? "" : " · now: " + e.Kind)})");
                    progress.Restart();
                    break;
                case "activity":
                    progress.Activity(e.Text);
                    break;
                case "step":
                    if (e.Tool is not ("final" or "unwrapped")) progress.Done(e.Tool ?? "?", e.Ok ?? true);
                    break;
                case "delta":
                    if (deltaChars == 0) progress.Stop();
                    deltaChars += e.Text.Length;
                    Console.Out.Write(e.Text);
                    Console.Out.Flush();
                    break;
                case "decided":
                    if (deltaChars > 0 && e.Kind == "escalation") Console.WriteLine();
                    Console.WriteLine($"({e.Kind}: {e.Text} · confidence {e.Confidence:0.00})");
                    break;
                case "note":
                    Console.WriteLine($"({e.Text})");
                    break;
                case "title":
                    Console.WriteLine($"(task: {e.Text})");
                    break;
                case "design":
                    progress.Stop();
                    Console.WriteLine("── design ──");
                    foreach (var l in e.Options ?? []) Console.WriteLine("  " + l);
                    Console.WriteLine("────────────");
                    progress.Restart();
                    break;
                case "ask":
                    progress.Stop();
                    Console.WriteLine();
                    Console.WriteLine($"⚠ run this command?  {e.Text}");
                    Console.WriteLine($"  not run unasked because: {e.Reason}");
                    break;
                case "choose":
                    progress.Stop();
                    Console.WriteLine();
                    Console.WriteLine($"? {e.Reason}");
                    var choices = e.Options ?? [];
                    for (var i = 0; i < choices.Count; i++)
                        Console.WriteLine($"  {i + 1}. {choices[i]}{(i == e.Recommended ? "  (recommended)" : "")}");
                    break;
            }
            return Task.CompletedTask;
        }

        Task<string> Answer(PipeEvent e)
        {
            string reply;
            if (e.Event == "ask")
            {
                reply = yes ? "y" : interactive ? (LineEditor.Read("  approve (y/n) › ") is { Kind: LineKind.Entered } a ? a.Text : "") : "";
                if (!interactive && !yes && chatty) Console.WriteLine("  (skipped — not interactive; pass --yes to approve)");
            }
            else
            {
                reply = interactive ? (LineEditor.Read("  pick a number, Enter for the recommendation, or type your own › ") is { Kind: LineKind.Entered } a ? a.Text : "") : "";
                if (!interactive && chatty) Console.WriteLine("  (taking the recommendation — not interactive)");
            }
            if (chatty) progress.Restart();
            return Task.FromResult(reply);
        }

        var request = wait
            ? new PipeRequest { Op = "wait" }
            : new PipeRequest { Op = "ask", Text = text, Yes = yes, Detach = detach };
        var result = await SessionClient.SendAsync(record.Pipe, request, OnEvent, Answer, ct);
        progress.Stop();

        var code = result switch
        {
            { Event: "error", Kind: "Busy" } => BusyExitCode,
            { Event: "result", Ok: true } => 0,
            _ => 1
        };

        if (jsonl || json)
        {
            WriteJsonLine(result);
            return code;
        }

        if (result.Event == "error")
        {
            Console.Error.WriteLine($"{name}: {result.Text}");
            return code;
        }

        if (result.Kind == "Accepted")
        {
            Console.WriteLine(result.Text);
            return code;
        }

        if (wait && !attached && !quiet) Console.Error.WriteLine($"(nothing running — the last result, for \"{result.Request}\")");

        // The answer: whatever the deltas did not already print.
        var streamed = result.Streamed ?? 0;
        if (deltaChars > 0 && !attachedMidway && streamed == deltaChars && streamed <= result.Text.Length)
            Console.WriteLine(result.Text[streamed..]);
        else
        {
            if (deltaChars > 0) Console.WriteLine();
            Console.WriteLine(result.Text);
        }

        if (!quiet && result.Kind is not ("Status" or "Reset" or "Empty"))
            Console.Error.WriteLine(Summary(result));

        return code;
    }

    /// <summary>The one line a headless caller reads to know how the turn went and that it can carry on.</summary>
    internal static string Summary(PipeEvent result)
    {
        var parts = new List<string>
        {
            result.Ok == true ? "✓ done" : $"✗ stopped ({result.Kind})",
            SessionServer.Seconds(TimeSpan.FromMilliseconds(result.ElapsedMs ?? 0))
        };
        if (result.Steps is { } steps) parts.Add($"{steps} step{(steps == 1 ? "" : "s")}");
        if (result.Turn is { } turn) parts.Add($"turn {turn}");
        return "── " + string.Join(" · ", parts) + " — `agent-one ask \"…\"` carries on";
    }

    private static void WriteJsonLine(PipeEvent e)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(e, AgentOneWireJson.Default.PipeEvent));
        Console.Out.Flush();
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
