using System.Text.Json;
using AgentOne.Llm.Decision;
using AgentOne.Services;

namespace AgentOne.Commands;

/// <summary>
/// Drives the decision engine by hand, without the agent loop.
///
/// Smart mode is not built yet, and this is how its parts get judged before it
/// is: throw real decisions at Jev, see the choice, the confidence and the full
/// distribution, and measure what a call actually costs. Confidence thresholds
/// should come from watching this, not from guessing.
/// </summary>
public sealed class JevCommand
{
    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        var sub = args.Length > 0 ? args[0] : "help";

        return sub switch
        {
            "choose" => await ChooseAsync(args[1..], ct),
            "check" => await CheckAsync(ct),
            "-h" or "--help" or "help" => Help(),
            _ => Unknown(sub)
        };
    }

    private static async Task<int> CheckAsync(CancellationToken ct)
    {
        var message = await Tui.ConfigTuiProbe.CheckSmartAsync(ConfigStore.Load(), ct);
        Console.WriteLine(message);
        return message.StartsWith('✗') ? 1 : 0;
    }

    private static async Task<int> ChooseAsync(string[] args, CancellationToken ct)
    {
        var options = new List<DecisionOption>();
        var positional = new List<string>();
        var question = "Which option best fits the state?";
        var repeat = 1;
        var json = false;

        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (args[i])
            {
                case "-o" or "--option":
                    var pair = Next();
                    if (pair is null) return Fail("--option needs name=description");
                    var split = pair.IndexOf('=');
                    if (split <= 0) return Fail($"--option must be name=description, got '{pair}'");
                    options.Add(new DecisionOption(pair[..split].Trim(), pair[(split + 1)..].Trim()));
                    break;

                case "-q" or "--question":
                    question = Next() ?? question;
                    break;

                case "--repeat":
                    if (!int.TryParse(Next(), out repeat) || repeat is < 1 or > 50)
                        return Fail("--repeat must be 1..50");
                    break;

                case "--json":
                    json = true;
                    break;

                default:
                    if (args[i].StartsWith('-')) return Fail($"unknown option '{args[i]}'");
                    positional.Add(args[i]);
                    break;
            }
        }

        var state = string.Join(' ', positional).Trim();
        if (state.Length == 0 && Console.IsInputRedirected)
            state = (await StandardInput.ReadToEndAsync(ct)).Trim();

        if (state.Length == 0) return Fail("no state given (pass it as an argument or on stdin)");
        if (options.Count == 0) return Fail("no options given — pass --option name=description at least twice");

        using var engine = new JevClient(ConfigStore.Load());

        Decision decision = Decision.Failed("not run");
        var timings = new List<long>();

        for (int i = 0; i < repeat; i++)
        {
            decision = await engine.ChooseAsync(state, question, options, ct);
            if (!decision.Ok) break;
            if (decision.Called) timings.Add(decision.ElapsedMs);
        }

        if (!decision.Ok)
        {
            Console.Error.WriteLine("agent-one jev choose: " + decision.Message);
            return 1;
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(decision, AgentOneWireJson.Default.Decision));
            return 0;
        }

        Console.WriteLine($"choice      {decision.Choice}");
        Console.WriteLine($"confidence  {decision.Confidence:0.000}");
        Console.WriteLine("distribution");

        foreach (var (name, probability) in decision.Ranked)
            Console.WriteLine($"  {Bar(probability)} {probability:0.000}  {name}");

        Console.Error.WriteLine($"({decision.Message})");

        // Repeated calls are the point of --repeat: one measurement includes
        // connection setup and says almost nothing about what a decision costs.
        if (timings.Count > 1)
        {
            var sorted = timings.Order().ToList();
            Console.Error.WriteLine(
                $"({timings.Count} calls — min {sorted[0]} ms, median {sorted[sorted.Count / 2]} ms, max {sorted[^1]} ms)");
        }

        return 0;
    }

    /// <summary>A probability as something the eye reads faster than a number.</summary>
    private static string Bar(double probability)
    {
        var filled = (int)Math.Round(Math.Clamp(probability, 0, 1) * 20);
        return "[" + new string('#', filled) + new string('·', 20 - filled) + "]";
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("agent-one jev choose: " + message);
        return 2;
    }

    private static int Unknown(string sub)
    {
        Console.Error.WriteLine($"agent-one jev: unknown subcommand '{sub}'");
        PrintHelp();
        return 2;
    }

    private static int Help()
    {
        PrintHelp();
        return 0;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            agent-one jev check            Verify the TypeSafe key with one real question
            agent-one jev choose <state>   Put a decision to Jev and print the distribution

            Options for `choose`:
              -o, --option name=desc   An option. Give at least two; fewer is not a decision.
              -q, --question <text>    What is being decided (default: which option fits)
                  --repeat <n>         Call n times and report min/median/max latency
                  --json               Print the decision as one JSON object

            The state may also arrive on stdin. Exit 1 if the service refused.

            Smart mode is not built yet — this is the bench for judging whether the
            answers and the confidence are good enough to act on automatically.

            Example:
              agent-one jev choose "The user asked how to build this repository." \
                -q "How should the agent answer?" \
                -o read_local="The answer is in files here; read them." \
                -o search_web="It needs outside information; search the web." \
                -o answer_now="Enough is already known; just answer."
            """);
    }
}
