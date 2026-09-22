using System.Text.Json.Serialization;

namespace AgentOne.Services;

/// <summary>
/// The whole of agent-one's persisted settings. Kept flat and small on
/// purpose: `agent-one config set &lt;key&gt; &lt;value&gt;` addresses one field by
/// name, so every field here must be settable from a single string.
/// </summary>
public sealed class AgentConfig
{
    /// <summary>"echo" (offline, deterministic) or "openai" (any OpenAI-compatible endpoint).</summary>
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "echo";

    /// <summary>Base URL of the OpenAI-compatible API, without the trailing /chat/completions.</summary>
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// Name of the environment variable holding the API key. The key itself is
    /// never written to config.json — that file is plain text on disk.
    /// </summary>
    [JsonPropertyName("apiKeyEnv")]
    public string ApiKeyEnv { get; set; } = "OPENAI_API_KEY";

    /// <summary>Hard stop for the tool loop: how many model turns one run may take.</summary>
    [JsonPropertyName("maxSteps")]
    public int MaxSteps { get; set; } = 8;

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 0.2;

    /// <summary>Seconds to wait on one provider request.</summary>
    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Append every run's transcript to ~/.agent-one/sessions/.</summary>
    [JsonPropertyName("saveSessions")]
    public bool SaveSessions { get; set; } = true;

    public static readonly string[] Keys =
    [
        "provider", "baseUrl", "model", "apiKeyEnv",
        "maxSteps", "temperature", "timeoutSeconds", "saveSessions"
    ];

    public string? Get(string key) => key switch
    {
        "provider"       => Provider,
        "baseUrl"        => BaseUrl,
        "model"          => Model,
        "apiKeyEnv"      => ApiKeyEnv,
        "maxSteps"       => MaxSteps.ToString(),
        "temperature"    => Temperature.ToString("0.###"),
        "timeoutSeconds" => TimeoutSeconds.ToString(),
        "saveSessions"   => SaveSessions ? "true" : "false",
        _                => null
    };

    /// <summary>Returns false with a reason when the key is unknown or the value does not parse.</summary>
    public bool TrySet(string key, string value, out string error)
    {
        error = "";
        switch (key)
        {
            case "provider":
                var p = value.ToLowerInvariant();
                if (p is not ("echo" or "openai")) { error = "provider must be 'echo' or 'openai'"; return false; }
                Provider = p;
                return true;
            case "baseUrl":
                if (!Uri.TryCreate(value, UriKind.Absolute, out _)) { error = "baseUrl must be an absolute URL"; return false; }
                BaseUrl = value.TrimEnd('/');
                return true;
            case "model":
                if (value.Length == 0) { error = "model must not be empty"; return false; }
                Model = value;
                return true;
            case "apiKeyEnv":
                if (value.Length == 0) { error = "apiKeyEnv must not be empty"; return false; }
                // This field holds the NAME of a variable, never a key. Pasting the
                // key here used to be accepted silently and then surface as an
                // unexplained 401, so it is refused where the mistake is made.
                if (!ApiKey.LooksLikeVariableName(value))
                {
                    error = "apiKeyEnv is the NAME of an environment variable (letters, digits, underscore) — " +
                            "it looks like you pasted the key itself. Put the key on the Connection step of " +
                            "`agent-one tui`, or run `agent-one auth set`.";
                    return false;
                }
                ApiKeyEnv = value;
                return true;
            case "maxSteps":
                if (!int.TryParse(value, out var steps) || steps is < 1 or > 100) { error = "maxSteps must be 1..100"; return false; }
                MaxSteps = steps;
                return true;
            case "temperature":
                if (!double.TryParse(value, out var t) || t is < 0 or > 2) { error = "temperature must be 0..2"; return false; }
                Temperature = t;
                return true;
            case "timeoutSeconds":
                if (!int.TryParse(value, out var secs) || secs is < 1 or > 3600) { error = "timeoutSeconds must be 1..3600"; return false; }
                TimeoutSeconds = secs;
                return true;
            case "saveSessions":
                if (!bool.TryParse(value, out var save)) { error = "saveSessions must be true or false"; return false; }
                SaveSessions = save;
                return true;
            default:
                error = $"unknown key '{key}' (known: {string.Join(", ", Keys)})";
                return false;
        }
    }
}
