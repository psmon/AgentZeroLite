namespace Agent.Common.Llm;

public interface ILocalChatSession : IAsyncDisposable
{
    int TurnCount { get; }

    IAsyncEnumerable<string> SendStreamAsync(string userMessage, CancellationToken ct = default);

    Task<string> SendAsync(string userMessage, CancellationToken ct = default);
}
