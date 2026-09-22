using AgentOne.Llm;

namespace AgentOne.Agent;

/// <summary>One distilled thing to keep.</summary>
public sealed record Distilled(string Kind, string Title, string Text);

/// <summary>
/// Turns a finished turn into one to three facts a future session should
/// know — off the turn, once the decision engine has said the turn is worth
/// keeping. The everyday model does it; the wording is kept short so a small
/// model produces lines, not an essay.
/// </summary>
public static class KnowledgeDistiller
{
    public const int MaxItems = 3;

    public static readonly string[] Kinds = ["fact", "decision", "fix", "procedure", "constraint"];

    public const string SystemPrompt =
        "You extract durable knowledge from one turn of an assistant working in a software project, for the project's " +
        "long-term memory. Reply with 1 to 3 lines and nothing else. Each line: kind | title | text — where kind is one of " +
        "fact, decision, fix, procedure, constraint; title is at most 8 words; text is one or two sentences that a future " +
        "session can act on alone (name files by their relative path, commands verbatim, errors by their code or message). " +
        "Skip anything the files already state plainly. Write in the user's language, except paths, commands and identifiers.";

    public static string Request(string request, string did, string outcome) =>
        "Request:\n" + request + "\n\nWhat the agent did:\n" + did + "\n\nOutcome:\n" + Clip(outcome, 2000);

    public static async Task<IReadOnlyList<Distilled>> DistillAsync(
        IChatProvider provider, string request, string did, string outcome, CancellationToken ct)
    {
        var reply = await provider.CompleteAsync(
            [ChatMessage.System(SystemPrompt), ChatMessage.User(Request(request, did, outcome))], ct);
        return Parse(reply);
    }

    /// <summary>
    /// Lines of "kind | title | text". A line without the separators is kept
    /// too — as a fact whose title is its first words — because a small model
    /// forgets the format more often than it forgets the content.
    /// </summary>
    public static IReadOnlyList<Distilled> Parse(string reply)
    {
        var items = new List<Distilled>();
        var text = reply.Trim();

        // The loop's habit, if the model fell back into it.
        if (text.StartsWith('{') && ToolCall.TryParse(text, out var call, out _) && call.IsFinal) text = call.Arg("text");

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('-', '*', '•', ' ').Trim();
            if (line.Length < 8 || line.StartsWith("```", StringComparison.Ordinal)) continue;
            if (line.StartsWith("kind", StringComparison.OrdinalIgnoreCase) && line.Contains("title", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = line.Split('|', 3, StringSplitOptions.TrimEntries);
            Distilled item;
            if (parts.Length == 3 && Kinds.Contains(parts[0].ToLowerInvariant()))
                item = new Distilled(parts[0].ToLowerInvariant(), parts[1], parts[2]);
            else if (parts.Length == 3)
                item = new Distilled("fact", parts[1], parts[2]);
            else
                item = new Distilled("fact", Clip(line, 60), line);

            if (item.Title.Length == 0 || item.Text.Length == 0) continue;
            items.Add(item);
            if (items.Count == MaxItems) break;
        }

        return items;
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + "…";
}
