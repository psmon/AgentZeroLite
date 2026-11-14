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
    private readonly double? _temperature;
    private readonly int? _maxTokens;
    private readonly string _keySource;
    private readonly string _keyAdvice;
    private readonly bool _hasKey;

    public string Name => "openai";

    /// <summary>How long a stream may go silent before it counts as stalled. Null: the request timeout. Tests shorten it.</summary>
    internal TimeSpan? StreamIdleTimeout { get; set; }

    public OpenAiCompatChatProvider(AgentConfig config, HttpMessageHandler? handler = null)
    {
        _model = config.Model;
        // Measured 2026-10-09 against api.openai.com: gpt-5-mini refused temperature 0.2
        // ("unsupported_value", only the default 1), gpt-4o-mini took it. The model name
        // decides, not the base URL, so OpenRouter's "openai/gpt-5" is covered too.
        _temperature = IsReasoningModel(config.Model) ? null : config.Temperature;
        // Same rule as temperature: OpenAI reasoning models reject max_tokens (they use
        // max_completion_tokens), so leave it out for them. 0 means "server default".
        _maxTokens = IsReasoningModel(config.Model) || config.MaxTokens <= 0 ? null : config.MaxTokens;

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

        // The strong model is asked to think: a design or a redo plan legitimately
        // reasons far longer than a tool step. Measured (2026-11-07): qwen's redo
        // plan was cut at 60 000 characters of reasoning, 15 minutes in.
        if (config.KeySlot == CredentialStore.Slot.Reasoning)
        {
            ThinkingLimitChars = ReasoningThinkingLimitChars;
            ThinkingLimitTime = ReasoningThinkingLimitTime;
        }
    }

    /// <summary>The strong model's reasoning budget before a reply counts as a runaway.</summary>
    public const int ReasoningThinkingLimitChars = 240_000;

    /// <summary>The strong model's reasoning time before a reply counts as a runaway.</summary>
    public static readonly TimeSpan ReasoningThinkingLimitTime = TimeSpan.FromMinutes(40);

    /// <summary>OpenAI reasoning models — gpt-5 and later (not the -chat variants) and o1/o3/o4* — take only the default temperature.</summary>
    internal static bool IsReasoningModel(string? model)
    {
        if (string.IsNullOrEmpty(model)) return false;
        var m = model.ToLowerInvariant();
        var slash = m.LastIndexOf('/');
        if (slash >= 0) m = m[(slash + 1)..];
        if (m.StartsWith("gpt-", StringComparison.Ordinal))
        {
            // gpt-5, gpt-5.6-luna, gpt-6-luna …: every generation from 5 on is a reasoning
            // model (measured: gpt-6-luna refused max_tokens and temperature 0.2 like gpt-5-mini);
            // only the "-chat" variants still take a temperature.
            var end = 4;
            while (end < m.Length && char.IsDigit(m[end])) end++;
            return int.TryParse(m.AsSpan(4, end - 4), out var generation) && generation >= 5 && !m.Contains("-chat");
        }
        return m.Length > 1 && m[0] == 'o' && char.IsDigit(m[1]);
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
            MaxTokens = _maxTokens,
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
            throw new ChatProviderStalledException($"{_http.BaseAddress}chat/completions did not answer within {_http.Timeout.TotalSeconds:0}s");
        }
        catch (HttpRequestException ex)
        {
            throw new ChatProviderStalledException($"cannot reach {_http.BaseAddress}chat/completions: {ex.Message}", 0, ex, dropped: true);
        }

        string body;
        try { body = await response.Content.ReadAsStringAsync(ct); }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw new ChatProviderStalledException(
                $"{_http.BaseAddress}chat/completions: the connection dropped mid-answer: {ex.Message}", 0, ex, dropped: true);
        }

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
            MaxTokens = _maxTokens,
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
            throw new ChatProviderStalledException($"{_http.BaseAddress}chat/completions did not answer within {_http.Timeout.TotalSeconds:0}s");
        }
        catch (HttpRequestException ex)
        {
            throw new ChatProviderStalledException($"cannot reach {_http.BaseAddress}chat/completions: {ex.Message}", 0, ex, dropped: true);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new ChatProviderException($"HTTP {(int)response.StatusCode} from provider: {Trim(body)}");
            }

            var whole = new System.Text.StringBuilder();
            var thinkTail = new System.Text.StringBuilder();
            int thinking = 0, thinkingChecked = 0, contentChecked = 0;
            var started = System.Diagnostics.Stopwatch.StartNew();
            ChatProviderStalledException Degenerate(string what, int received) =>
                new($"{_model} {what} — the call was cut off", received, degenerate: true);

            // HttpClient.Timeout stops at the headers when they are read first, so
            // the body has no clock of its own. Measured (2026-09-29): a design
            // call got its headers, then nothing, and the chat sat for 36 minutes
            // with the socket open and no way out but Ctrl+C. Every line —
            // keep-alive comments included — restarts this timer; silence for a
            // whole timeout ends the read as a stall, which callers can retry.
            var idleLimit = StreamIdleTimeout ?? _http.Timeout;
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(idleLimit);

            try
            {
                using var stream = await response.Content.ReadAsStreamAsync(idle.Token);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);

                while (await reader.ReadLineAsync(idle.Token) is { } line)
                {
                    // Reset the idle clock only on a real SSE data frame. A server that
                    // keeps the socket warm with comment lines (": ping") or blank
                    // separators while the model produces no token would otherwise reset
                    // the clock on every keep-alive and never time out — the call then
                    // hangs forever on a stream that is technically "not silent" but is
                    // sending nothing. Only forward progress (a data frame) earns more time.
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                    idle.CancelAfter(idleLimit);

                    var payload = line[5..].Trim();
                    if (payload.Length == 0 || payload == "[DONE]") continue;

                    var (piece, thought) = ChunkParts(payload);

                    // A thinking model streams its reasoning first, and every such
                    // frame is forward progress to the idle clock above — so a
                    // reasoning that never ends never times out. Measured
                    // (2026-11-07, gemma-4 on LM Studio): one step sat 44 minutes
                    // on "thinking about what came back" with nothing on screen.
                    if (thought.Length > 0)
                    {
                        thinking += thought.Length;
                        thinkTail.Append(thought);
                        if (thinkTail.Length > 8_192) thinkTail.Remove(0, thinkTail.Length - 4_096);
                        if (whole.Length == 0 && thinking > ThinkingLimitChars)
                            throw Degenerate($"thought for {thinking:N0} chars without starting an answer", whole.Length);
                        // A slow server makes a character budget a long wait: measured
                        // (2026-11-07) gemma at ~15 tok/s sat 34 minutes on one step's
                        // reasoning, still under the budget. Time bounds it too.
                        if (whole.Length == 0 && started.Elapsed > ThinkingLimitTime)
                            throw Degenerate($"thought for {started.Elapsed.TotalMinutes:0} minutes ({thinking:N0} chars) without starting an answer", whole.Length);
                        if (thinking - thinkingChecked >= 2_000)
                        {
                            thinkingChecked = thinking;
                            if (RepeatingTail(thinkTail) is { } period)
                                throw Degenerate($"its reasoning went into a loop (a {period}-char cycle)", whole.Length);
                        }
                    }

                    if (piece.Length == 0) continue;

                    whole.Append(piece);
                    onDelta(piece);

                    // The same collapse in the answer itself ("을 아수 을 아수 …"):
                    // tokens keep flowing, so only the content shows it.
                    if (whole.Length - contentChecked >= 2_000)
                    {
                        contentChecked = whole.Length;
                        if (RepeatingTail(whole) is { } period)
                            throw Degenerate($"its answer went into a loop (a {period}-char cycle)", whole.Length);
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ChatProviderStalledException(
                    $"{_http.BaseAddress}chat/completions stopped sending for {idleLimit.TotalSeconds:0}s " +
                    $"({whole.Length:N0} chars received)", whole.Length);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                // "The response ended prematurely", a reset socket: the request
                // was fine, the line was not. The same messages may be sent again.
                throw new ChatProviderStalledException(
                    $"{_http.BaseAddress}chat/completions: the connection dropped mid-answer " +
                    $"({whole.Length:N0} chars received): {ex.Message}", whole.Length, ex, dropped: true);
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
    /// <summary>Reasoning past this many characters, with no answer begun, is a runaway — not thinking.</summary>
    public int ThinkingLimitChars { get; init; } = 60_000;

    /// <summary>Reasoning longer than this, with no answer begun, is a runaway too — the everyday model's step is not a design.</summary>
    public TimeSpan ThinkingLimitTime { get; init; } = TimeSpan.FromMinutes(12);

    /// <summary>The window a repetition must fill to count as a loop, and the longest cycle looked for.</summary>
    internal const int LoopWindow = 1_600, LoopMaxPeriod = 256;

    /// <summary>
    /// The period of a cycle the text's tail is stuck in, or null. Exact
    /// repetition over 1 600 characters is not prose: a sentence, a table rule
    /// or a code line repeats a few times, not forty.
    /// </summary>
    internal static int? RepeatingTail(System.Text.StringBuilder text)
    {
        if (text.Length < LoopWindow + LoopMaxPeriod) return null;
        var s = text.ToString(text.Length - LoopWindow - LoopMaxPeriod, LoopWindow + LoopMaxPeriod);
        var tail = s.AsSpan(LoopMaxPeriod);
        for (var p = 1; p <= LoopMaxPeriod; p++)
        {
            if (!tail.SequenceEqual(s.AsSpan(LoopMaxPeriod - p, LoopWindow))) continue;
            // A run of one character (a long ---- rule, spaces) is layout, not a loop.
            return tail.ToString().Distinct().Count() <= 2 ? null : p;
        }
        return null;
    }

    /// <summary>The answer text and the reasoning text of one SSE payload; empty when it carries neither.</summary>
    internal static (string Content, string Reasoning) ChunkParts(string payload)
    {
        try
        {
            var delta = JsonSerializer.Deserialize(payload, AgentOneWireJson.Default.ChatCompletionChunk)?.Choices?.FirstOrDefault()?.Delta;
            return (delta?.Content ?? "", delta?.ReasoningContent ?? delta?.Reasoning ?? "");
        }
        catch (JsonException)
        {
            return ("", "");
        }
    }

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
