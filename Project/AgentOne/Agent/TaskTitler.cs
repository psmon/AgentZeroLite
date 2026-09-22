using AgentOne.Llm;

namespace AgentOne.Agent;

/// <summary>
/// Names the task a session is on, in a few words, so the header, the status
/// block, the resume list and the workspace memory all say what was being
/// done rather than quoting the first prompt. One small model call, after the
/// turn's answer is already on screen — never on the way to it.
/// </summary>
public static class TaskTitler
{
    public const int MaxChars = 60;

    public const string SystemPrompt =
        "You name the task a person is working on with an assistant. Reply with the name only: " +
        "at most eight words, in the user's language, no quotes, no trailing punctuation, no explanation. " +
        "Name the work, not the conversation — \"게시판 API 빌드 오류 수정\", not \"user asked about a build\".";

    public static string Request(string request, string outcome, string? currentTitle) =>
        (currentTitle is { Length: > 0 } ? $"The task so far was called: {currentTitle}\n\n" : "") +
        "Latest request:\n" + request +
        "\n\nWhat happened:\n" + (outcome.Length > 600 ? outcome[..600] + "…" : outcome);

    public static async Task<string> NameAsync(
        IChatProvider provider, string request, string outcome, string? currentTitle, CancellationToken ct)
    {
        var reply = await provider.CompleteAsync(
            [ChatMessage.System(SystemPrompt), ChatMessage.User(Request(request, outcome, currentTitle))],
            ct);

        return Clean(reply);
    }

    /// <summary>A small model still wraps names in quotes or a JSON envelope now and then; take the words.</summary>
    public static string Clean(string reply)
    {
        var text = reply.Trim();

        // A final envelope, if the model fell back into the loop's habit.
        if (text.StartsWith('{') && ToolCall.TryParse(text, out var call, out _) && call.IsFinal) text = call.Arg("text").Trim();

        text = text.Split('\n')[0].Trim().Trim('"', '\'', '“', '”', '`', '*').TrimEnd('.', '。', '!', '?').Trim();
        if (text.StartsWith("Title:", StringComparison.OrdinalIgnoreCase)) text = text[6..].Trim();
        if (text.Length > MaxChars) text = text[..MaxChars].TrimEnd() + "…";
        return text;
    }
}
