using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agent.Common;

namespace Agent.Common.Llm.Providers;

/// <summary>
/// OpenAI-compatible REST provider. Same wire format works for OpenAI, LM Studio,
/// Ollama (/v1 endpoint) and Webnori. Streaming uses SSE (data: lines).
///
/// Trimmed v1: text-only (no audio, no multimodal, no tool_calls). The Gemma 4
/// AIMODE toolchain emits textual JSON envelopes in-context, so OpenAI native
/// tool_calls aren't needed. Add them back if non-Gemma external AIMODE is
/// pursued as a follow-up.
/// </summary>
public sealed class OpenAiCompatibleProvider : ILlmProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _providerName;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public OpenAiCompatibleProvider(string providerName, string baseUrl, string apiKey,
        TimeSpan? timeout = null)
    {
        _providerName = providerName;
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/')),
            Timeout = timeout ?? TimeSpan.FromMinutes(3),
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public string ProviderName => _providerName;

    public async Task<List<LlmModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("/v1/models", ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        var models = new List<LlmModelInfo>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                if (!string.IsNullOrEmpty(id))
                    models.Add(new LlmModelInfo { Id = id });
            }
        }
        return models;
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        var body = BuildRequestBody(request, stream: false);
        var resp = await PostJsonAsync("/v1/chat/completions", body, ct);
        using var doc = JsonDocument.Parse(resp);

        if (doc.RootElement.TryGetProperty("error", out var errorEl))
        {
            var errMsg = errorEl.TryGetProperty("message", out var m) ? m.GetString() : "unknown";
            var errType = errorEl.TryGetProperty("type", out var t) ? t.GetString() : "unknown";
            AppLogger.Log($"[LLM-EXT] {_providerName} returned error: type={errType}, message={errMsg}");
            return new LlmResponse { Text = $"[Provider error: {errType}] {errMsg}", FinishReason = "error" };
        }

        if (!doc.RootElement.TryGetProperty("choices", out var choicesEl) || choicesEl.GetArrayLength() == 0)
            return new LlmResponse { Text = "", FinishReason = "no_choices" };

        var choice = choicesEl[0];
        var message = choice.GetProperty("message");
        var text = message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
            ? content.GetString() ?? ""
            : "";
        var finishReason = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;
        return new LlmResponse { Text = text, FinishReason = finishReason };
    }

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = BuildRequestBody(request, stream: true);
        var jsonBody = JsonSerializer.Serialize(body, JsonOpts);

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        };
        httpReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var httpResp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!httpResp.IsSuccessStatusCode)
        {
            var errBody = await httpResp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"{_providerName} stream failed: HTTP {(int)httpResp.StatusCode} {errBody[..Math.Min(500, errBody.Length)]}");
        }

        using var stream = await httpResp.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line[5..].TrimStart();
            if (payload == "[DONE]") break;

            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("choices", out var choices))
            {
                if (doc.RootElement.TryGetProperty("error", out var err))
                {
                    var errMsg = err.TryGetProperty("message", out var m) ? m.GetString() : err.ToString();
                    throw new HttpRequestException($"{_providerName} stream error: {errMsg}");
                }
                continue;
            }
            foreach (var choice in choices.EnumerateArray())
            {
                var delta = choice.GetProperty("delta");
                var text = "";
                if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    text = c.GetString() ?? "";

                string? finishReason = null;
                if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                    finishReason = fr.GetString();

                if (!string.IsNullOrEmpty(text) || finishReason != null)
                    yield return new LlmStreamChunk { Text = text, FinishReason = finishReason };
            }
        }
    }

    private static Dictionary<string, object> BuildRequestBody(LlmRequest request, bool stream)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = request.Model,
            ["messages"] = request.Messages.Select(m => new Dictionary<string, object>
            {
                ["role"] = m.Role,
                ["content"] = m.Content,
            }),
            ["stream"] = stream,
        };
        if (request.Temperature.HasValue) body["temperature"] = request.Temperature.Value;
        if (request.MaxTokens.HasValue) body["max_tokens"] = request.MaxTokens.Value;
        return body;
    }

    private async Task<string> PostJsonAsync(string path, object body, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, JsonOpts);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync(path, content, ct);
        var respBody = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{_providerName} {path} failed: HTTP {(int)resp.StatusCode} {respBody}");
        return respBody;
    }

    public void Dispose() => _http.Dispose();
}
