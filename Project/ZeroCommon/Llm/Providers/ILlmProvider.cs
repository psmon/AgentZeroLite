namespace Agent.Common.Llm.Providers;

/// <summary>
/// External LLM provider abstraction. All providers (OpenAI, LMStudio, Ollama,
/// Webnori) speak the OpenAI-compatible REST protocol, so a single
/// implementation (<see cref="OpenAiCompatibleProvider"/>) covers them all —
/// the factory varies only base URL + key + provider label.
/// </summary>
public interface ILlmProvider
{
    string ProviderName { get; }

    Task<List<LlmModelInfo>> ListModelsAsync(CancellationToken ct = default);

    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default);

    IAsyncEnumerable<LlmStreamChunk> StreamAsync(LlmRequest request, CancellationToken ct = default);
}
