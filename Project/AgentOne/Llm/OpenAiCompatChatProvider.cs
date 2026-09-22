using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentOne.Services;

namespace AgentOne.Llm;

/// <summary>
/// Talks to any OpenAI-compatible <c>/chat/completions</c> endpoint — the one
/// wire format that covers OpenAI, Ollama, LM Studio, vLLM, llama.cpp's server
/// and the gateway crowd, which is why v0 implements it and nothing else.
///
/// The API key is read from the environment variable named in the config; it is
/// never persisted. An empty key is allowed, because local servers do not want one.
/// </summary>
public sealed class OpenAiCompatChatProvider : IChatProvider, IModelCatalog, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly double _temperature;
    private readonly string _keySource;
    private readonly string _keyAdvice;
    private readonly bool _hasKey;

    public string Name => "openai";

    public OpenAiCompatChatProvider(AgentConfig config, HttpMessageHandler? handler = null)
    {
        _model = config.Model;
        _temperature = config.Temperature;

        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.BaseAddress = new Uri(config.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);

        // One resolver decides where the key comes from — stored first, then the
        // environment — so every message here can name the same places.
        var resolved = ApiKey.Resolve(config);
        _keySource = resolved.Source;
        _keyAdvice = ApiKey.WhereToPutIt(config);
        _hasKey = resolved.Found;
        if (_hasKey)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", resolved.Value!);

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("agent-one");
    }

    public async Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct, Action<string>? onDelta = null)
    {
        // Streaming is asked for only when someone is watching. It costs an extra
        // parse per chunk and, on a provider that mishandles it, an extra way to
        // fail — so a non-interactive run keeps the simpler request.
        if (onDelta is not null) return await StreamAsync(messages, onDelta, ct);

        var request = new ChatCompletionRequest
        {
            Model = _model,
            Messages = [.. messages],
            Temperature = _temperature,
            Stream = false
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync(
                "chat/completions", request, AgentOneWireJson.Default.ChatCompletionRequest, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ChatProviderException($"request to {_http.BaseAddress}chat/completions timed out");
        }
        catch (HttpRequestException ex)
        {
            throw new ChatProviderException($"cannot reach {_http.BaseAddress}chat/completions: {ex.Message}", ex);
        }

        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new ChatProviderException($"HTTP {(int)response.StatusCode} from provider: {Trim(body)}");

        ChatCompletionResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(body, AgentOneWireJson.Default.ChatCompletionResponse);
        }
        catch (JsonException ex)
        {
            throw new ChatProviderException($"provider returned non-JSON body: {Trim(body)}", ex);
        }

        if (parsed?.Error is { } error)
            throw new ChatProviderException($"provider error: {error.Message ?? error.Type ?? "unknown"}");

        var content = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
            throw new ChatProviderException($"provider returned no message content: {Trim(body)}");

        return content;
    }

    /// <summary>
    /// The same completion over server-sent events. Each chunk is handed to
    /// <paramref name="onDelta"/> as it lands and also accumulated, so the caller
    /// still gets one whole reply to parse — the deltas are for showing progress,
    /// never for deciding what the model said.
    /// </summary>
    private async Task<string> StreamAsync(IReadOnlyList<ChatMessage> messages, Action<string> onDelta, CancellationToken ct)
    {
        var request = new ChatCompletionRequest
        {
            Model = _model,
            Messages = [.. messages],
            Temperature = _temperature,
            Stream = true
        };

        var message = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(request, AgentOneWireJson.Default.ChatCompletionRequest)
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ChatProviderException($"request to {_http.BaseAddress}chat/completions timed out");
        }
        catch (HttpRequestException ex)
        {
            throw new ChatProviderException($"cannot reach {_http.BaseAddress}chat/completions: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new ChatProviderException($"HTTP {(int)response.StatusCode} from provider: {Trim(body)}");
            }

            var whole = new System.Text.StringBuilder();

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                var payload = line[5..].Trim();
                if (payload.Length == 0 || payload == "[DONE]") continue;

                var piece = ChunkContent(payload);
                if (piece.Length == 0) continue;

                whole.Append(piece);
                onDelta(piece);
            }

            if (whole.Length == 0)
                throw new ChatProviderException($"{_http.BaseAddress}chat/completions streamed no content");

            return whole.ToString();
        }
    }

    /// <summary>
    /// The text in one SSE payload, or nothing. A chunk that does not parse is
    /// skipped rather than fatal: providers interleave keep-alives and metadata
    /// frames, and losing the run over one of those would be absurd.
    /// </summary>
    internal static string ChunkContent(string payload)
    {
        try
        {
            var chunk = JsonSerializer.Deserialize(payload, AgentOneWireJson.Default.ChatCompletionChunk);
            return chunk?.Choices?.FirstOrDefault()?.Delta?.Content ?? "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>
    /// GET /models. Every failure is translated into something the operator can
    /// act on — above all a 401/403, which almost always means the API key, and
    /// which is the whole reason this doubles as the health check.
    /// </summary>
    public async Task<ModelCatalogResult> ListModelsAsync(CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync("models", ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ModelCatalogResult.Failure($"timed out asking {_http.BaseAddress}models");
        }
        catch (HttpRequestException ex)
        {
            return ModelCatalogResult.Failure($"cannot reach {_http.BaseAddress}models — {ex.Message}");
        }

        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            return ModelCatalogResult.Failure(_hasKey
                ? $"HTTP {(int)response.StatusCode} — the endpoint rejected the key from {_keySource}"
                : $"HTTP {(int)response.StatusCode} — no API key found · {_keyAdvice}");
        }

        if (!response.IsSuccessStatusCode)
            return ModelCatalogResult.Failure($"HTTP {(int)response.StatusCode} from {_http.BaseAddress}models: {Trim(body)}");

        ModelListResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(body, AgentOneWireJson.Default.ModelListResponse);
        }
        catch (JsonException)
        {
            return ModelCatalogResult.Failure($"{_http.BaseAddress}models did not return JSON: {Trim(body)}");
        }

        if (parsed?.Error is { } error)
            return ModelCatalogResult.Failure("endpoint error: " + (error.Message ?? error.Type ?? "unknown"));

        var models = (parsed?.Data ?? [])
            .Select(m => m.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (models.Length == 0)
            return ModelCatalogResult.Failure($"{_http.BaseAddress}models answered, but listed no models");

        return ModelCatalogResult.Success(models, $"{models.Length} models from {_http.BaseAddress}models");
    }

    private static string Trim(string body) =>
        body.Length <= 400 ? body : body[..400] + "...";

    public void Dispose() => _http.Dispose();
}
