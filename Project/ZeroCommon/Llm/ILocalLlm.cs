namespace Agent.Common.Llm;

public interface ILocalLlm : IAsyncDisposable
{
    Task<string> CompleteAsync(string prompt, CancellationToken ct = default);

    IAsyncEnumerable<string> StreamAsync(string prompt, CancellationToken ct = default);

    ILocalChatSession CreateSession();

    /// <summary>
    /// Open a chat session using an explicit chat template (Gemma, Llama-3.1, …).
    /// Picked per-model by <see cref="LlmService.OpenSession"/> based on the
    /// loaded model's catalog entry.
    /// </summary>
    ILocalChatSession CreateSession(Tools.ChatTemplate template);
}
