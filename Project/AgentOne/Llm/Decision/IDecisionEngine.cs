using System.Text.Json.Serialization;

namespace AgentOne.Llm.Decision;

/// <param name="Name">The identifier the engine returns when this option wins.</param>
/// <param name="Description">What the option means. Both name and description are sent.</param>
public readonly record struct DecisionOption(string Name, string Description);

/// <param name="Ok">False when no decision could be made; <paramref name="Message"/> says why.</param>
/// <param name="Choice">The winning option's name, or empty when Ok is false.</param>
/// <param name="Confidence">0–1, derived from how spread the distribution is. Flat means unsure.</param>
/// <param name="Probabilities">The full distribution, so callers are never limited to our reading of it.</param>
/// <param name="Called">False when the answer was reached without asking the service.</param>
public sealed record Decision(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("choice")] string Choice,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("probabilities")] IReadOnlyDictionary<string, double> Probabilities,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("elapsedMs")] long ElapsedMs,
    [property: JsonPropertyName("called")] bool Called = true)
{
    public static Decision Failed(string message, long elapsedMs = 0) =>
        new(false, "", 0, new Dictionary<string, double>(), message, elapsedMs);

    /// <summary>The options ordered by probability, strongest first — for showing the runner-up.</summary>
    [JsonIgnore]
    public IEnumerable<KeyValuePair<string, double>> Ranked =>
        Probabilities.OrderByDescending(p => p.Value);
}

/// <summary>
/// Picks one option from a short list, and says how sure it is.
///
/// This is the seam smart mode will hang on. It is deliberately not an
/// <see cref="IChatProvider"/>: it neither generates text nor holds a
/// conversation. The LLM proposes the options; this decides between them, and
/// the confidence is what lets calling code choose whether to act on the answer
/// without asking a person.
/// </summary>
public interface IDecisionEngine
{
    string Name { get; }

    /// <param name="state">The context to judge: the request, plus what is known so far.</param>
    /// <param name="question">What is being decided, phrased as a question.</param>
    /// <param name="options">The candidates. Fewer than two is not a decision.</param>
    Task<Decision> ChooseAsync(
        string state,
        string question,
        IReadOnlyList<DecisionOption> options,
        CancellationToken ct);
}
