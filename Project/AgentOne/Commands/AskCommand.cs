using System.Text.Json;
using AgentOne.Agent;
using AgentOne.Services;

namespace AgentOne.Commands;

/// <summary>
/// One request to the background session, printed the way the REPL prints a
/// turn: progress on stderr, the answer streamed to stdout, questions asked
/// back here. `--json` prints the result event alone, for a script or
/// another agent; `--yes` approves commands without asking back.
/// </summary>
public sealed class AskCommand
{
    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        bool yes = false, json = false, quiet = false, help = false;
        var words = new List<string>();

        foreach (var arg in args)
        {
            switch (arg)
            {
                case "-y" or "--yes": yes = true; break;
                case "--json": json = true; break;
                case "-q" or "--quiet": quiet = true; break;
                case "-h" or "--help": help = true; break;
                default:
                    if (arg.StartsWith('-') && arg.Length > 1)
                    {
                        Console.Error.WriteLine($"agent-one ask: unknown option '{arg}'");
                        return 2;
                    }
                    words.Add(arg);
                    break;
            }
        }

        if (help)
        {
            SessionCommand.PrintHelp();
            return 0;
        }

        var text = string.Join(' ', words).Trim();
        if (text.Length == 0 && Console.IsInputRedirected)
            text = (await StandardInput.ReadToEndAsync(ct)).Trim();
        if (text.Length == 0)
        {
            Console.Error.WriteLine("agent-one ask: no request given (pass it as an argument or on stdin)");
            return 2;
        }

        var record = SessionRegistry.LoadAlive();
        if (record is null)
        {
            Console.Error.WriteLine("agent-one ask: no background session is running — `agent-one session start` first");
            return 1;
        }

        var interactive = !Console.IsInputRedirected && !json;
        var showProgress = !json && !quiet;
        using var progress = ProgressDisplay.For(showProgress);
        var wroteAnything = false;

        Task OnEvent(PipeEvent e)
        {
            if (json) return Task.CompletedTask;
            switch (e.Event)
            {
                case "activity":
                    progress.Activity(e.Text);
                    break;
                case "step":
                    if (e.Tool is not ("final" or "unwrapped")) progress.Done(e.Tool ?? "?", e.Ok ?? true);
                    break;
                case "delta":
                    if (!wroteAnything) { progress.Stop(); wroteAnything = true; }
                    Console.Out.Write(e.Text);
                    Console.Out.Flush();
                    break;
                case "decided":
                    if (wroteAnything && e.Kind == "escalation") Console.WriteLine();
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
                    var options = e.Options ?? [];
                    for (var i = 0; i < options.Count; i++)
                        Console.WriteLine($"  {i + 1}. {options[i]}{(i == e.Recommended ? "  (recommended)" : "")}");
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
                if (!interactive && !yes && !json) Console.WriteLine("  (skipped — not interactive; pass --yes to approve)");
            }
            else
            {
                reply = interactive ? (LineEditor.Read("  pick a number, Enter for the recommendation, or type your own › ") is { Kind: LineKind.Entered } a ? a.Text : "") : "";
                if (!interactive && !json) Console.WriteLine("  (taking the recommendation — not interactive)");
            }
            if (!json) progress.Restart();
            return Task.FromResult(reply);
        }

        var result = await SessionClient.SendAsync(record.Pipe, new PipeRequest { Op = "ask", Text = text, Yes = yes }, OnEvent, Answer, ct);
        progress.Stop();

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, AgentOneWireJson.Default.PipeEvent));
            return result.Event == "result" && result.Ok == true ? 0 : 1;
        }

        if (result.Event == "error")
        {
            Console.Error.WriteLine("agent-one ask: " + result.Text);
            return 1;
        }

        var streamed = result.Streamed ?? 0;
        var remainder = wroteAnything && streamed > 0 && streamed <= result.Text.Length ? result.Text[streamed..] : result.Text;
        Console.WriteLine(remainder);
        if (result.Ok != true && result.Kind is not ("Status" or "Reset" or "Empty")) Console.Error.WriteLine($"agent-one: stopped ({result.Kind})");

        return result.Ok == true ? 0 : 1;
    }
}
