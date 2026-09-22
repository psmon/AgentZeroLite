using AgentOne.Llm;
using AgentOne.Llm.Decision;

namespace AgentOne.Agent;

/// <summary>
/// Asks the model for a handful of genuinely different ways to answer, before
/// any of them is tried.
///
/// This is the half Jev cannot do: a System One model chooses between options,
/// it does not invent them. So the LLM proposes and the decision engine
/// disposes — which is also why the prompt leans so hard on the approaches
/// being <i>distinct</i>. Two options that mean the same thing produce a flat
/// distribution and a low-confidence decision, and the fix for that is better
/// options, not a lower threshold.
/// </summary>
public sealed class Planner(IChatProvider provider, int maxOptions = 4)
{
    public const string PlanTool = "plan";

    /// <summary>What the decision engine is asked, once the options exist.</summary>
    public const string DecisionQuestion =
        "Which approach should the agent take to answer the user's request?";

    // A plain raw string, not interpolated: the body is mostly JSON braces, and
    // every interpolation form makes one or other of them ambiguous.
    private const string PromptBody =
        """
        You are planning how an agent should answer a request. You are NOT answering it.

        Reply with ONE JSON object and nothing else:
          {"tool":"plan","args":{"<short_name>":"<one sentence describing the approach>"}}

        Rules:
        - Give 2 to 4 approaches that are genuinely DIFFERENT from each other. If two of them
          would do the same work, give one instead of two.
        - If only one approach makes sense, give exactly one.
        - Names are short snake_case identifiers, e.g. read_local, search_web, answer_now.
        - Each description is one plain sentence saying what that approach does.
        - Do not propose anything the agent cannot do.
        - If the agent can already answer from what it knows — including anything listed under
          "Already in the conversation" — say so as one approach ("answer_now") rather than
          inventing work for it.

        Reply with the JSON object only.
        """;

    public static string Prompt(string toolScope) =>
        PromptBody + Environment.NewLine + Environment.NewLine + "The agent can reach: " + toolScope;

    /// <summary>
    /// The approaches the model proposed, strongest-effort first. An empty list
    /// means planning failed — the caller then runs as it always would.
    /// </summary>
    /// <param name="context">
    /// What the conversation already holds — pages read, files opened, answers
    /// given. Without it the planner proposes rediscovering things the agent
    /// has in front of it; measured, that cost a follow-up turn 15 seconds of
    /// planning for a steer the model then rightly ignored.
    /// </param>
    public async Task<IReadOnlyList<DecisionOption>> PlanAsync(
        string request, string toolScope, string context, CancellationToken ct)
    {
        var user = context.Length == 0
            ? request
            : $"{request}{Environment.NewLine}{Environment.NewLine}Already in the conversation:{Environment.NewLine}{context}";

        var messages = new List<ChatMessage>
        {
            ChatMessage.System(Prompt(toolScope)),
            ChatMessage.User(user)
        };

        string raw;
        try
        {
            raw = await provider.CompleteAsync(messages, ct);
        }
        catch (ChatProviderException)
        {
            return [];
        }

        return Parse(raw, maxOptions);
    }

    /// <summary>
    /// Pulls the options out of a plan envelope. Tolerant in the same way the
    /// tool parser is, and silent about a reply that is not a plan: a failed
    /// plan degrades to the ordinary loop rather than failing the turn.
    /// </summary>
    internal static IReadOnlyList<DecisionOption> Parse(string raw, int maxOptions)
    {
        if (!ToolCall.TryParse(raw, out var call, out _)) return [];
        if (!string.Equals(call.Tool, PlanTool, StringComparison.OrdinalIgnoreCase)) return [];

        return call.Args
            .Where(a => a.Key.Length > 0 && a.Value.Trim().Length > 0)
            .Take(maxOptions)
            .Select(a => new DecisionOption(a.Key, a.Value.Trim()))
            .ToList();
    }
}
