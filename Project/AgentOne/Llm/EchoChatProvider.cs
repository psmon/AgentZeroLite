namespace AgentOne.Llm;

/// <summary>
/// Offline provider. It runs the real loop with no network and no API key, so
/// `agent-one run --provider echo` is a genuine end-to-end smoke test of
/// routing, the tool envelope, guards and session writing.
///
/// It answers with a final-tool envelope echoing the last user message. A
/// prompt beginning with "!tool " is passed through verbatim, which lets a
/// caller drive one specific tool call without an LLM:
///   agent-one run '!tool {"tool":"list_files","args":{"path":"."}}'
/// </summary>
public sealed class EchoChatProvider : IChatProvider
{
    public const string ToolPassthroughPrefix = "!tool ";

    public string Name => "echo";

    public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var lastUser = "";
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == "user") { lastUser = messages[i].Content; break; }
        }

        // A passthrough prompt is only honored on the first model turn; once a
        // tool result has come back, echo finalizes instead of looping forever.
        bool toolAlreadyRan = messages.Any(m => m.Role == "user" && m.Content.StartsWith("[tool:", StringComparison.Ordinal));

        if (!toolAlreadyRan && lastUser.StartsWith(ToolPassthroughPrefix, StringComparison.Ordinal))
            return Task.FromResult(lastUser[ToolPassthroughPrefix.Length..].Trim());

        var text = toolAlreadyRan ? SummarizeToolResults(messages) : lastUser;
        var envelope = Agent.ToolCall.Final(text).ToJson();
        return Task.FromResult(envelope);
    }

    private static string SummarizeToolResults(IReadOnlyList<ChatMessage> messages)
    {
        var last = messages.LastOrDefault(m => m.Role == "user" && m.Content.StartsWith("[tool:", StringComparison.Ordinal));
        return last?.Content ?? "";
    }
}
