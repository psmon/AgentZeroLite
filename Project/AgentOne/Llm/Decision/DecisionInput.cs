using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOne.Llm.Decision;

/// <param name="Name">The option's identifier, as the engine returns it.</param>
/// <param name="Probability">The engine's probability for it, 0 when the engine gave none.</param>
public sealed record DecideRanked(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("probability")] double Probability);

/// <summary>
/// One decision as the `decide` tool and the `agent-one decide` command report it —
/// the contract another program reads, so its fields only ever grow.
/// </summary>
/// <param name="Confident">True when <paramref name="Confidence"/> clears <paramref name="Floor"/>: act on it, or treat it as a lean.</param>
/// <param name="Called">False when no service call was needed (or none was possible).</param>
public sealed record DecideResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("choice")] string Choice,
    [property: JsonPropertyName("choiceDescription")] string ChoiceDescription,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("confident")] bool Confident,
    [property: JsonPropertyName("floor")] double Floor,
    [property: JsonPropertyName("ranked")] IReadOnlyList<DecideRanked> Ranked,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("elapsedMs")] long ElapsedMs,
    [property: JsonPropertyName("called")] bool Called)
{
    public static DecideResult Failed(string question, string message, double floor, long elapsedMs = 0) =>
        new(false, question, "", "", 0, false, floor, [], message, elapsedMs, false);
}

/// <summary>
/// What the `decide` tool and the `agent-one decide` command share: reading a
/// list of options however a model or a script writes it, and turning the
/// engine's answer into one result. One copy, so the tool the agent calls and
/// the command another agent calls can never disagree about either.
/// </summary>
public static class DecisionInput
{
    /// <summary>Fewer is not a decision.</summary>
    public const int MinOptions = 2;

    /// <summary>More than this is a search, not a judgment — and the distribution flattens into noise.</summary>
    public const int MaxOptions = 12;

    /// <summary>How much state the engine is sent. Enough to judge, not a transcript.</summary>
    public const int MaxStateChars = 6000;

    /// <summary>
    /// Reads options from any of the shapes a caller is likely to write:
    /// a JSON object (name → description), a JSON array (of "name: desc"
    /// strings or {name, description} objects), or plain text — one option per
    /// line, or on one line separated by ';' or '|' — each "name: description",
    /// "name=description" or just a description (then its name is made from it).
    /// </summary>
    public static bool TryParseOptions(string? text, out List<DecisionOption> options, out string error)
    {
        options = [];
        error = "";
        var raw = (text ?? "").Trim();

        if (raw.Length == 0)
        {
            error = $"no options — give {MinOptions} to {MaxOptions}, e.g. \"patch: fix the one call; rewrite: replace the module\"";
            return false;
        }

        var items = new List<(string? Name, string Description)>();

        if (raw[0] is '{' or '[')
        {
            if (!TryReadJson(raw, items, out error)) return false;
        }
        else
        {
            // Newlines win when there are any: a description may well contain ';'.
            var lines = raw.Contains('\n')
                ? raw.Split('\n')
                : raw.Split([';', '|']);

            foreach (var line in lines)
            {
                var item = StripBullet(line.Trim());
                if (item.Length == 0) continue;
                items.Add(SplitNamed(item));
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < items.Count; i++)
        {
            var (name, description) = items[i];
            name = string.IsNullOrWhiteSpace(name) ? NameFrom(description, i) : name.Trim();
            description = description.Trim();
            if (description.Length == 0) description = name;

            if (!seen.Add(name))
            {
                error = $"option '{name}' is given twice";
                return false;
            }

            options.Add(new DecisionOption(name, description));
        }

        if (options.Count < MinOptions)
        {
            error = options.Count == 1
                ? "only one option — a decision needs at least two"
                : $"no options found — give {MinOptions} to {MaxOptions}";
            return false;
        }

        if (options.Count > MaxOptions)
        {
            error = $"{options.Count} options — at most {MaxOptions}; narrow the list first";
            return false;
        }

        return true;
    }

    /// <summary>The state the engine judges: the person's request first, then what the caller says is known.</summary>
    public static string BuildState(string? request, string? context, string question)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(request)) sb.Append("The user's request: ").AppendLine(request.Trim()).AppendLine();
        if (!string.IsNullOrWhiteSpace(context)) sb.Append("What is known: ").AppendLine(context.Trim());

        var state = sb.ToString().Trim();
        if (state.Length == 0) state = question.Trim();
        return state.Length <= MaxStateChars ? state : state[..MaxStateChars] + " …";
    }

    public static DecideResult Shape(Decision decision, IReadOnlyList<DecisionOption> options, string question, double floor)
    {
        if (!decision.Ok) return DecideResult.Failed(question, decision.Message, floor, decision.ElapsedMs);

        var describe = options.ToDictionary(o => o.Name, o => o.Description, StringComparer.Ordinal);

        // Every offered option is listed, the engine's order first — a missing
        // probability is a zero, not a dropped option.
        var ranked = options
            .Select(o => new DecideRanked(o.Name, o.Description,
                decision.Probabilities.TryGetValue(o.Name, out var p) ? p : 0))
            .OrderByDescending(r => r.Probability)
            .ThenBy(r => r.Name == decision.Choice ? 0 : 1)
            .ToList();

        return new DecideResult(true, question, decision.Choice,
            describe.GetValueOrDefault(decision.Choice, ""),
            decision.Confidence, decision.Confidence >= floor, floor,
            ranked, decision.Message, decision.ElapsedMs, decision.Called);
    }

    /// <summary>The result as the model reads it in a tool message.</summary>
    public static string Describe(DecideResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"decision: {result.Choice} — {result.ChoiceDescription}");
        sb.AppendLine($"confidence: {result.Confidence:0.00} ({(result.Confident ? "confident" : $"below {result.Floor:0.00} — a lean, not a verdict")})");
        sb.AppendLine("distribution:");
        foreach (var r in result.Ranked)
            sb.AppendLine($"  {r.Probability:0.00}  {r.Name} — {r.Description}");

        sb.Append(result.Confident
            ? "Act on it unless the material clearly says otherwise."
            : "The engine is unsure: weigh it with what you know, and if it matters, say the choice was close.");
        return sb.ToString();
    }

    private static bool TryReadJson(string raw, List<(string? Name, string Description)> items, out string error)
    {
        error = "";
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in root.EnumerateObject())
                    items.Add((prop.Name, Text(prop.Value)));
                return true;
            }

            foreach (var el in root.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.Object)
                {
                    var name = el.TryGetProperty("name", out var n) ? Text(n) : null;
                    var description = el.TryGetProperty("description", out var d) ? Text(d) : "";
                    items.Add((name, description));
                }
                else
                {
                    items.Add(SplitNamed(Text(el).Trim()));
                }
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = "options look like JSON but do not parse: " + ex.Message;
            return false;
        }
    }

    private static string Text(JsonElement el) => el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.GetRawText();

    /// <summary>"name: description" or "name=description"; anything else is a bare description.</summary>
    private static (string? Name, string Description) SplitNamed(string item)
    {
        var at = item.IndexOfAny([':', '=']);

        // A name is short — up to three words ("SQLite FTS5" is what a model
        // writes); a sentence that happens to contain a colon is a description.
        if (at > 0 && at <= 40)
        {
            var name = item[..at].Trim();
            var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (name.Length > 0 && words <= 3) return (name, item[(at + 1)..].Trim());
        }

        return (null, item);
    }

    private static string StripBullet(string line)
    {
        var s = line.TrimStart('-', '*', '•', ' ', '\t');
        // "1." / "2)" numbering — the number is not part of the option.
        var i = 0;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        if (i > 0 && i < s.Length && s[i] is '.' or ')') s = s[(i + 1)..];
        return s.Trim();
    }

    private static string NameFrom(string description, int index)
    {
        var sb = new StringBuilder();
        foreach (var ch in description.Trim())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
            if (sb.Length >= 32) break;
        }

        var name = sb.ToString().Trim('_');
        return name.Length == 0 ? $"option_{index + 1}" : name;
    }
}
