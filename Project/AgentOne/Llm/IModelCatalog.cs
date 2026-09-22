using System.Text.Json.Serialization;

namespace AgentOne.Llm;

/// <param name="Ok">True when the endpoint answered with at least one model.</param>
/// <param name="Models">Model ids, as the endpoint names them.</param>
/// <param name="Message">One line for the operator — the count, or exactly what went wrong.</param>
public readonly record struct ModelCatalogResult(bool Ok, IReadOnlyList<string> Models, string Message)
{
    public static ModelCatalogResult Success(IReadOnlyList<string> models, string message) =>
        new(true, models, message);

    public static ModelCatalogResult Failure(string message) =>
        new(false, [], message);
}

/// <summary>
/// Asking an endpoint what it can run. Listing is also the cheapest honest
/// health check there is: it exercises the base URL, the network path and the
/// API key in one request, and an empty or rejected answer says which of them
/// is wrong — before a single token is spent.
/// </summary>
public interface IModelCatalog
{
    Task<ModelCatalogResult> ListModelsAsync(CancellationToken ct);
}

// The /v1/models response. OpenAI, Ollama, LM Studio, vLLM and llama.cpp all
// return this shape, which is why one type covers every provider agent-one has.

public sealed class ModelListResponse
{
    [JsonPropertyName("data")]
    public List<ModelEntry>? Data { get; set; }

    [JsonPropertyName("error")]
    public ApiError? Error { get; set; }
}

public sealed class ModelEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("owned_by")]
    public string? OwnedBy { get; set; }
}
