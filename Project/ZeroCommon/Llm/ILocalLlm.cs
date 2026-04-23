namespace Agent.Common.Llm;

public interface ILocalLlm : IAsyncDisposable
{
    Task<string> CompleteAsync(string prompt, CancellationToken ct = default);

    IAsyncEnumerable<string> StreamAsync(string prompt, CancellationToken ct = default);

    ILocalChatSession CreateSession();
}
