namespace AgentOne.Llm;

/// <summary>
/// The one seam between the agent loop and the outside world. The loop never
/// learns which provider it is talking to, which is what keeps `--provider echo`
/// an honest test of the loop itself rather than a separate code path.
/// </summary>
public interface IChatProvider
{
    string Name { get; }

    /// <summary>Returns the assistant's raw reply text for the given conversation.</summary>
    Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct);
}

/// <summary>A provider-side failure the CLI should report as a clean error, not a stack trace.</summary>
public sealed class ChatProviderException(string message, Exception? inner = null)
    : Exception(message, inner);
