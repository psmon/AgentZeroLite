using AgentOne.Agent;
using AgentOne.Llm;
using AgentOne.Llm.Decision;
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
            (ToolCatalog.WebFamily, new WebToolbelt(TimeSpan.FromSeconds(options.Config.WebTimeoutSeconds))));
        var loop = new AgentLoop(provider, toolbelt, options.Config.MaxSteps);
        loop.Reset();

        SessionStore? session = options.Config.SaveSessions ? SessionStore.Create("chat") : null;

        var showProgress = !options.Quiet;
        using var progress = ProgressDisplay.For(showProgress);

        loop.Streaming = showProgress;
        loop.ActivityStarted += what => progress.Activity(what);

        loop.StepCompleted += step =>
        {
            session?.Step(step);
            if (options.Verbose)
                Console.Error.WriteLine($"  [{step.Index}] {step.Tool}: {step.Detail}");
            else if (step.Tool != Agent.ToolCall.FinalTool)
                progress.Done(step.Tool, step.Ok);
        };

        var wroteAnything = false;
        loop.AnswerDelta += fragment =>
        {
            if (!wroteAnything) { progress.Stop(); wroteAnything = true; }
            Console.Out.Write(fragment);
            Console.Out.Flush();
        };

        using var engine = new JevClient(options.Config);
        var smart = new SmartTurn(provider, engine, options.Config.JevConfidenceFloor);
        smart.ActivityStarted += what => progress.Activity(what);

        // Smart mode starts where the config left it, but only if there is a key
        // to make it work. Starting "on" with no key would fail on the first turn.
        var state = new SessionState(options.Config.SmartMode && engine.HasKey);

        Console.WriteLine($"agent-one chat — provider {provider.Name}, model {options.Config.Model}");
        Console.WriteLine($"tools:     {toolbelt.Scope}");
        if (session is not null) Console.WriteLine($"session:   {session.Path}");
        Console.WriteLine(engine.HasKey
            ? "Shift+Tab switches basic ↔ smart · /reset clears the conversation · /exit quits."
            : "/reset clears the conversation, /exit or Ctrl+C quits.  (no TypeSafe key — smart mode unavailable)");
        Console.WriteLine();

        while (!ct.IsCancellationRequested)
        {
            var session_ = state.Read();
            var input = LineEditor.Read(session_.Smart ? "[smart] > " : "[basic] > ");

            if (input.Kind == LineKind.EndOfInput) break;

            if (input.Kind == LineKind.ToggleMode)
            {
                if (!engine.HasKey)
                    Console.WriteLine("(no TypeSafe key — set one on the Smart step of `agent-one tui`)");
                else
                {
                    var now = state.ToggleSmart();
                    Console.WriteLine(now
                        ? $"(smart mode on — plan first, decide, act above confidence {options.Config.JevConfidenceFloor:0.00})"
                        : "(basic mode — straight to the tool loop)");
                }
                continue;
            }

            var line = input.Text.Trim();
            if (line.Length == 0) continue;

            if (line is "/exit" or "/quit") break;
            if (line is "/reset")
            {
                loop.Reset();
                state.Reset();
                Console.WriteLine("(conversation cleared)");
                continue;
            }

            // A turn parked for review resumes here: whatever was typed is the
            // person's answer, and it carries the original request with it.
            if (state.TakeReview() is { } review)
            {
                session?.Prompt(line);
                wroteAnything = false;
                progress.Restart();

                var resumed = await loop.RunAsync(
                    $"{review.Request}{Environment.NewLine}{Environment.NewLine}[approved] {line}", ct);

                session?.Result(resumed);
                progress.Stop();

                Console.WriteLine(resumed.Succeeded
                    ? (wroteAnything ? resumed.Unstreamed : resumed.Text)
                    : $"[stopped: {resumed.Reason}] {resumed.Text}");
                Console.WriteLine();
                continue;
            }

            session?.Prompt(line, session_.Smart);
            state.CountTurn();
            wroteAnything = false;
            progress.Restart();

            var prompt = line;

            if (session_.Smart)
            {
                var plan = await smart.PrepareAsync(line, toolbelt.Scope, ct);
                progress.Stop();
                session?.Plan(plan);

                // The decision was that a person has to settle it: park the turn
                // and let the next line typed be the answer.
                if (plan.NeedsReview)
                {
                    Announce(plan, options.Config.JevConfidenceFloor);
                    state.AwaitReview(new PendingReview(line, plan));
                    Console.WriteLine();
                    continue;
                }

                prompt = await ApplyPlanAsync(plan, line, options.Config.JevConfidenceFloor, ct);
                progress.Restart();
            }

            var run = await loop.RunAsync(prompt, ct);
            session?.Result(run);
            progress.Stop();

            Console.WriteLine(run.Succeeded
                ? (wroteAnything ? run.Unstreamed : run.Text)
                : $"[stopped: {run.Reason}] {run.Text}");
            Console.WriteLine();
        }

        return 0;
    }

    /// <summary>Says why a person is being asked, and what the alternatives were.</summary>
    private static void Announce(SmartPlan plan, double floor)
    {
        var decision = plan.Decision!;

        Console.WriteLine($"⚠ this needs you (confidence {decision.Confidence:0.00})");
        Console.WriteLine($"  {SmartTurn.ReviewDescription}");
        Console.WriteLine();
        Console.WriteLine("  the approaches that were considered:");

        foreach (var option in plan.Options.Where(o => o.Name != SmartTurn.ReviewOption))
        {
            var probability = decision.Probabilities.TryGetValue(option.Name, out var value) ? value : 0;
            Console.WriteLine($"    {option.Name}  ({probability:0.00})  {option.Description}");
        }

        Console.WriteLine();
        Console.WriteLine("  answer, approve or redirect on the next line — it continues from there.");
    }

    /// <summary>
    /// Reports what the decision was and, when it was not confident enough,
    /// hands the choice to the person sitting there. `run` cannot do this, which
    /// is why the policy lives here rather than in <see cref="SmartTurn"/>.
    /// </summary>
    private static async Task<string> ApplyPlanAsync(SmartPlan plan, string request, double floor, CancellationToken ct)
    {
        if (plan.Options.Count < 2)
        {
            if (plan.Options.Count == 1)
                Console.WriteLine($"(one approach: {plan.Options[0].Name})");
            return request;
        }

        if (plan.Decision is not { Ok: true } decision)
        {
            Console.WriteLine($"(decision unavailable: {plan.Decision?.Message}) — running without a plan");
            return request;
        }

        if (plan.Confident)
        {
            Console.WriteLine($"(plan: {decision.Choice} · confidence {decision.Confidence:0.00})");
            return plan.Guidance(request);
        }

        // Below the floor the options usually failed to separate, so show them
        // and let the operator settle it in one keystroke.
        Console.WriteLine($"(unsure — confidence {decision.Confidence:0.00} is below {floor:0.00})");

        for (int i = 0; i < plan.Options.Count; i++)
        {
            var option = plan.Options[i];
            var probability = decision.Probabilities.TryGetValue(option.Name, out var value) ? value : 0;
            Console.WriteLine($"  {i + 1}. {option.Name}  ({probability:0.00})  {option.Description}");
        }

        Console.Write($"pick 1-{plan.Options.Count}, or Enter for {decision.Choice}: ");
        var answer = (await StandardInput.ReadLineAsync(ct) ?? "").Trim();

        if (int.TryParse(answer, out var picked) && picked >= 1 && picked <= plan.Options.Count)
        {
            var chosen = plan.Options[picked - 1];
            Console.WriteLine($"(plan: {chosen.Name})");
            return SmartPlan.GuidanceFor(request, chosen);
        }

        Console.WriteLine($"(plan: {decision.Choice})");
        return plan.Guidance(request);
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
