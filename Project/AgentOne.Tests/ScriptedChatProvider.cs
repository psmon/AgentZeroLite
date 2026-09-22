using AgentOne.Llm;

namespace AgentOne.Tests;

/// <summary>
/// A provider that replays a fixed script of replies, so a loop test asserts on
/// the loop instead of on a model's mood. Replies past the end of the script
/// repeat the last one — that is what makes the repeat guard testable.
/// </summary>
internal sealed class ScriptedChatProvider(params string[] replies) : IChatProvider
{
    private int _index;

    public string Name => "scripted";

    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

    public int CallCount => Calls.Count;

    public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        Calls.Add([.. messages]);
        var reply = replies[Math.Min(_index, replies.Length - 1)];
        _index++;
        return Task.FromResult(reply);
    }
}

/// <summary>A provider that always fails, for the error path.</summary>
internal sealed class FailingChatProvider(string message) : IChatProvider
{
    public string Name => "failing";

    public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct) =>
        throw new ChatProviderException(message);
}
