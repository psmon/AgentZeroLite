using System.Globalization;
using System.Text.Json;
using AgentOne.Llm.Decision;
using AgentOne.Services;

namespace AgentOne.Commands;

/// <summary>
/// `agent-one decide` — the decision engine as a plain command, for scripts and
/// for other agents: put a question and options in, get one JSON object out.
///
/// `jev choose` is the bench a person reads (bars, latency, --repeat); this is
/// the contract a program parses. It shares the option reader and the result
/// shape with the agent's own `decide` tool (<see cref="DecisionInput"/>), so a
/// list written for one works for the other and both report the same fields.
///
/// Exit codes: 0 decided · 1 the engine could not (no key, refused, unreachable)
/// · 2 the request itself was wrong. Every outcome still prints one JSON object
/// on stdout unless --text was asked for, so a caller never has to scrape stderr.
/// </summary>
public sealed class DecideCommand
{
    /// <summary>What one invocation asks, however it was given.</summary>
    internal sealed record Request(string Question, string Context, List<DecisionOption> Options);

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length > 0 && args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        var config = ConfigStore.Load();
        var floor = config.JevConfidenceFloor;
        var text = args.Contains("--text");

        var (request, error, newFloor) = await ParseAsync(args, floor, ct);
        floor = newFloor;

        if (request is null)
            return Emit(DecideResult.Failed("", error, floor), text, exit: 2);

        using var engine = new JevClient(config);
        var state = DecisionInput.BuildState(null, request.Context, request.Question);
        var decision = await engine.ChooseAsync(state, request.Question, request.Options, ct);
        var result = DecisionInput.Shape(decision, request.Options, request.Question, floor);

        return Emit(result, text, exit: result.Ok ? 0 : 1);
    }

    /// <summary>
    /// Reads flags, an optional JSON request (--input file or -), and the context
    /// from the positional words or, failing those, from stdin.
    /// </summary>
    internal static async Task<(Request? Request, string Error, double Floor)> ParseAsync(
        string[] args, double floor, CancellationToken ct)
    {
        string? question = null, context = null, optionsText = null, input = null;
        var pairs = new List<string>();
        var positional = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (args[i])
            {
                case "-q" or "--question": question = Next(); break;
                case "-c" or "--context": context = Next(); break;
                case "-o" or "--option":
                    var pair = Next();
                    if (pair is null) return (null, "--option needs name=description", floor);
                    pairs.Add(pair);
                    break;
                case "--options": optionsText = Next(); break;
                case "-i" or "--input": input = Next() ?? "-"; break;
                case "--floor":
                    if (!double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out floor) || floor is < 0 or > 1)
                        return (null, "--floor must be a number from 0 to 1", floor);
                    break;
                case "--text": break;
                default:
                    if (args[i].StartsWith('-') && args[i].Length > 1) return (null, $"unknown option '{args[i]}'", floor);
                    positional.Add(args[i]);
                    break;
            }
        }

        // A JSON request supplies whatever the flags left out; a flag given
        // alongside it wins, so a script can override one field.
        if (input is not null)
        {
            string json;
            try
            {
                json = input == "-" ? await StandardInput.ReadToEndAsync(ct) : await File.ReadAllTextAsync(input, ct);
            }
            catch (IOException ex) { return (null, $"cannot read --input: {ex.Message}", floor); }
            catch (UnauthorizedAccessException ex) { return (null, $"cannot read --input: {ex.Message}", floor); }

            if (!TryReadRequestJson(json, out var q, out var c, out var o, out var jsonError))
                return (null, jsonError, floor);

            question ??= q;
            context ??= c;
            if (pairs.Count == 0) optionsText ??= o;
        }

        if (string.IsNullOrWhiteSpace(question))
            return (null, "no question — pass -q \"what is being decided?\" (or \"question\" in --input)", floor);

        // -o pairs and --options combine, in the order given: pairs first.
        var combined = string.Join('\n', pairs.Append(optionsText ?? "").Where(s => s.Length > 0));
        if (!DecisionInput.TryParseOptions(combined, out var options, out var optionError))
            return (null, optionError, floor);

        // Stdin last: a request already refused must not sit waiting for input.
        if (positional.Count > 0)
            context = context is null ? string.Join(' ', positional) : context + "\n" + string.Join(' ', positional);
        else if (context is null && input is null && Console.IsInputRedirected)
            context = await StandardInput.ReadToEndAsync(ct);

        return (new Request(question.Trim(), (context ?? "").Trim(), options), "", floor);
    }

    /// <summary>{"question": "...", "context": "...", "options": object | array | string}</summary>
    internal static bool TryReadRequestJson(string json, out string? question, out string? context,
        out string? options, out string error)
    {
        question = context = options = null;
        error = "";

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "--input must be a JSON object: {\"question\", \"context\", \"options\"}";
                return false;
            }

            foreach (var prop in root.EnumerateObject())
            {
                var value = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.GetRawText();
                switch (prop.Name.ToLowerInvariant())
                {
                    case "question": question = value; break;
                    case "context" or "state": context = value; break;
                    case "options": options = value; break;
                }
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = "--input is not valid JSON: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// The wire context with non-ASCII left as it is: the reader is often another
    /// model, and "어떤" is valid JSON that no model reads as Korean.
    /// The output is a single line on stdout, never embedded in HTML.
    /// </summary>
    private static readonly AgentOneWireJson ReadableJson = new(new JsonSerializerOptions(AgentOneWireJson.Default.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });

    private static int Emit(DecideResult result, bool text, int exit)
    {
        if (!text)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, ReadableJson.DecideResult));
            return exit;
        }

        if (!result.Ok)
        {
            Console.Error.WriteLine("agent-one decide: " + result.Message);
            return exit;
        }

        Console.WriteLine(DecisionInput.Describe(result));
        Console.Error.WriteLine($"({result.Message})");
        return exit;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            agent-one decide [context] -q <question> -o name=desc -o name=desc ...

            Put a judgment call to the decision engine (TypeSafe Jev) and print ONE JSON
            object: the choice, its confidence, whether that clears the floor, and every
            option with its probability. Made for scripts and other agents; the agent's
            own `decide` tool runs exactly the same code.

            Options:
              -q, --question <text>   What is being decided, as a question (required)
              -o, --option name=desc  One option (repeat; 2 to 12 in total)
                  --options <list>    Several at once: one per line, or "a: x; b: y",
                                      or JSON ({"a":"x"} / ["a: x", ...])
              -c, --context <text>    What the engine should judge (also: positional
                                      words, or stdin when nothing else gives it)
              -i, --input <file|->    The whole request as JSON:
                                      {"question":"…","context":"…","options":{…}}
                                      Flags given beside it override its fields.
                  --floor <0-1>       Confidence that counts as "confident"
                                      (default: jevConfidenceFloor from config)
                  --text              Human-readable output instead of JSON

            Output (stdout, one line):
              {"ok":true,"question":"…","choice":"patch","choiceDescription":"…",
               "confidence":0.81,"confident":true,"floor":0.6,
               "ranked":[{"name":"patch","description":"…","probability":0.88},…],
               "message":"jev-latest · 412 ms","elapsedMs":412,"called":true}

            Exit: 0 decided · 1 the engine could not (no key, refused, unreachable)
                  · 2 the request was wrong. A failure is still one JSON object, ok:false.

            Needs a TypeSafe key: `agent-one auth set --jev` or $TYPESAFE_API_KEY.

            Examples:
              agent-one decide "Tests fail only on Windows paths." \
                -q "Which fix?" -o patch="change the one call" -o rewrite="replace the module"

              echo '{"question":"Ship today?","context":"2 flaky tests, no failures",
                     "options":{"ship":"release now","wait":"fix the flakes first"}}' \
                | agent-one decide --input -
            """);
    }
}
