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

    /// <summary>How many fragments each reply is chopped into when streaming.</summary>
    public int ChunkSize { get; set; } = 7;

    public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct, Action<string>? onDelta = null)
    {
        Calls.Add([.. messages]);
        var reply = replies[Math.Min(_index, replies.Length - 1)];
        _index++;

        // Deliver it the way a real provider would: in pieces that fall wherever
        // they fall, including mid-escape and mid-key.
        if (onDelta is not null)
            for (int i = 0; i < reply.Length; i += ChunkSize)
                onDelta(reply[i..Math.Min(i + ChunkSize, reply.Length)]);

        return Task.FromResult(reply);
    }
}

/// <summary>A provider that always fails, for the error path.</summary>
internal sealed class FailingChatProvider(string message) : IChatProvider
{
    public string Name => "failing";

    public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct, Action<string>? onDelta = null) =>
        throw new ChatProviderException(message);
}
