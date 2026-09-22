using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentOne.Services;

namespace AgentOne.Llm.Decision;

// The TypeSafe System One wire format, as documented at
// https://docs.typesafe.ai/primitives/choice — one request carries a state and
// a map of typed questions; one response carries an answer per question id.

public sealed class JevRequest
{
    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "jev-latest";

    [JsonPropertyName("questions")]
    public Dictionary<string, JevQuestion> Questions { get; set; } = [];
}

public sealed class JevQuestion
{
    /// <summary>"choice", "score" or "noul".</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "noul";

    [JsonPropertyName("instructions")]
    public string Instructions { get; set; } = "";

    /// <summary>Choice options or Score levels, as name → description. Omitted for a bare Noul.</summary>
    [JsonPropertyName("criteria")]
    public Dictionary<string, string>? Criteria { get; set; }
}

public sealed class JevResponse
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("answers")]
    public Dictionary<string, JevAnswer>? Answers { get; set; }

    [JsonPropertyName("usage")]
    public JevUsage? Usage { get; set; }

    [JsonPropertyName("error")]
    public ApiError? Error { get; set; }
}

public sealed class JevAnswer
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>The winning option, for a Choice.</summary>
    [JsonPropertyName("choice")]
    public string? Choice { get; set; }

    /// <summary>The 0–1 truth probability, for a Noul.</summary>
    [JsonPropertyName("noul")]
    public double? Noul { get; set; }

    [JsonPropertyName("score")]
    public string? Score { get; set; }

    /// <summary>Derived from how the distribution is spread: flat means unsure.</summary>
    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("probabilities")]
    public Dictionary<string, double>? Probabilities { get; set; }
}

public sealed class JevUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }
}

/// <param name="Ok">True when the service answered with a usable result.</param>
/// <param name="Message">One line for the operator — what worked, or exactly what did not.</param>
public readonly record struct JevCheck(bool Ok, string Message);

/// <summary>
/// Talks to TypeSafe's System One endpoint. Only what smart mode will need, and
/// for now only the health check — the decision engine lands on top of this once
/// the key is in place and the round trip is measured.
///
/// Jev is not an LLM: it returns typed judgments, not text. It never replaces
/// <see cref="IChatProvider"/>; it answers questions the code asks about a state.
/// </summary>
public sealed class JevClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly bool _hasKey;

    public JevClient(AgentConfig config, HttpMessageHandler? handler = null)
    {
        _model = config.JevModel;

        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.BaseAddress = new Uri(config.JevBaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);

        var key = CredentialStore.Load(CredentialStore.Slot.Jev)
                  ?? Environment.GetEnvironmentVariable("TYPESAFE_API_KEY");

        _hasKey = !string.IsNullOrWhiteSpace(key);
        if (_hasKey)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key!.Trim());

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("agent-one");
    }

    /// <summary>True when a key was found in the store or the environment.</summary>
    public bool HasKey => _hasKey;

    /// <summary>
    /// Sends the smallest real question there is and reports what came back.
    ///
    /// A real call rather than a models listing: it proves the base URL, the
    /// network path, the key AND that the account can actually evaluate a
    /// question — which is the thing smart mode depends on. It also gives the
    /// first honest latency number, which no amount of reading the docs would.
    /// </summary>
    public async Task<JevCheck> CheckAsync(CancellationToken ct)
    {
        if (!_hasKey)
            return new JevCheck(false, "no TypeSafe key — set it on the Smart step, or `agent-one auth set --jev`");

        var request = new JevRequest
        {
            State = "The agent is verifying that its TypeSafe credentials work.",
            Model = _model,
            Questions = new Dictionary<string, JevQuestion>
            {
                ["reachable"] = new()
                {
                    Type = "noul",
                    Instructions = "Is this text about verifying credentials?"
                }
            }
        };

        var sw = Stopwatch.StartNew();
        HttpResponseMessage response;

        try
        {
            response = await _http.PostAsJsonAsync("systemone", request, AgentOneWireJson.Default.JevRequest, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new JevCheck(false, $"timed out calling {_http.BaseAddress}systemone");
        }
        catch (HttpRequestException ex)
        {
            return new JevCheck(false, $"cannot reach {_http.BaseAddress}systemone — {ex.Message}");
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        sw.Stop();

        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            return new JevCheck(false, $"HTTP {(int)response.StatusCode} — the TypeSafe key was rejected");

        if (!response.IsSuccessStatusCode)
            return new JevCheck(false, $"HTTP {(int)response.StatusCode} from {_http.BaseAddress}systemone: {Trim(body)}");

        JevResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(body, AgentOneWireJson.Default.JevResponse);
        }
        catch (JsonException)
        {
            return new JevCheck(false, $"{_http.BaseAddress}systemone did not return JSON: {Trim(body)}");
        }

        if (parsed?.Error is { } error)
            return new JevCheck(false, "TypeSafe error: " + (error.Message ?? error.Type ?? "unknown"));

        if (parsed?.Answers is null || !parsed.Answers.TryGetValue("reachable", out var answer))
            return new JevCheck(false, $"answered without the question that was asked: {Trim(body)}");

        var served = parsed.Model ?? _model;
        var tokens = parsed.Usage is { } usage ? $", {usage.InputTokens}+{usage.OutputTokens} tokens" : "";
        var value = answer.Noul is { } noul ? $", noul {noul:0.00}" : "";

        return new JevCheck(true, $"✓ {served} · {sw.ElapsedMilliseconds} ms{value}{tokens}");
    }

    private static string Trim(string body) => body.Length <= 300 ? body : body[..300] + "...";

    public void Dispose() => _http.Dispose();
}
