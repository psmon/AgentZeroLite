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
public sealed class OpenAiCompatChatProvider : IChatProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly double _temperature;

    public string Name => "openai";

    public OpenAiCompatChatProvider(AgentConfig config, HttpMessageHandler? handler = null)
    {
        _model = config.Model;
        _temperature = config.Temperature;

        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.BaseAddress = new Uri(config.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);

        var key = Environment.GetEnvironmentVariable(config.ApiKeyEnv);
        if (!string.IsNullOrWhiteSpace(key))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("agent-one");
    }

    public async Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
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

    private static string Trim(string body) =>
        body.Length <= 400 ? body : body[..400] + "...";

    public void Dispose() => _http.Dispose();
}
