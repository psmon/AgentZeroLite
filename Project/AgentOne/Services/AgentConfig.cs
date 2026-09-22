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

    /// <summary>
    /// Seconds to wait on one web page. Its own number because a page is not a
    /// model call: a slow site once held a turn for two full minutes on the
    /// provider timeout, for a page that was never going to load.
    /// </summary>
    [JsonPropertyName("webTimeoutSeconds")]
    public int WebTimeoutSeconds { get; set; } = 20;

    /// <summary>Seconds one run_command may take before it is killed. Builds and test runs are the long ones.</summary>
    [JsonPropertyName("commandTimeoutSeconds")]
    public int CommandTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Plan first and let the decision engine choose the approach. Off by
    /// default: it costs an extra LLM turn and an extra service.
    /// </summary>
    [JsonPropertyName("smartMode")]
    public bool SmartMode { get; set; }

    /// <summary>
    /// Below this confidence the decision is not acted on unasked. Observed
    /// range on real decisions: 0.19 when the options are indistinguishable,
    /// 0.88–1.00 when they are not — so the middle is empty and 0.60 sits in it.
    /// </summary>
    [JsonPropertyName("jevConfidenceFloor")]
    public double JevConfidenceFloor { get; set; } = 0.60;

    /// <summary>Base URL of the TypeSafe System One API used by smart mode.</summary>
    [JsonPropertyName("jevBaseUrl")]
    public string JevBaseUrl { get; set; } = "https://api.typesafe.ai/v1";

    /// <summary>Which System One model answers smart mode's questions.</summary>
    [JsonPropertyName("jevModel")]
    public string JevModel { get; set; } = "jev-latest";

    /// <summary>Append every run's transcript to ~/.agent-one/sessions/.</summary>
    [JsonPropertyName("saveSessions")]
    public bool SaveSessions { get; set; } = true;

    /// <summary>
    /// Endpoint of the reasoning model — the slow, strong one a hard question is
    /// escalated to. Empty means the same endpoint as <see cref="BaseUrl"/>,
    /// which is the common case: one gateway, two model sizes.
    /// </summary>
    [JsonPropertyName("reasoningBaseUrl")]
    public string ReasoningBaseUrl { get; set; } = "";

    /// <summary>
    /// The reasoning model itself. Empty means there is none, and nothing is
    /// ever escalated. The everyday model (<see cref="Model"/>) is small and
    /// fast and answers first; this one is asked only when the decision engine
    /// judges the problem needs it.
    /// </summary>
    [JsonPropertyName("reasoningModel")]
    public string ReasoningModel { get; set; } = "";

    /// <summary>Which credential the provider built from this config authenticates with. Not persisted.</summary>
    [JsonIgnore]
    public CredentialStore.Slot KeySlot { get; private set; } = CredentialStore.Slot.Provider;

    /// <summary>True when a reasoning model is configured to escalate to.</summary>
    [JsonIgnore]
    public bool HasReasoningModel => ReasoningModel.Length > 0;

    /// <summary>
    /// This config with the reasoning model in the everyday model's place, so
    /// the same provider code talks to it. Endpoint and key fall back to the
    /// Connection step's when the Reasoning step left them empty.
    /// </summary>
    public AgentConfig ForReasoning()
    {
        var derived = (AgentConfig)MemberwiseClone();
        derived.KeySlot = CredentialStore.Slot.Reasoning;
        derived.Model = ReasoningModel;
        if (ReasoningBaseUrl.Length > 0) derived.BaseUrl = ReasoningBaseUrl;
        return derived;
    }

    public static readonly string[] Keys =
    [
        "provider", "baseUrl", "model", "apiKeyEnv",
        "reasoningBaseUrl", "reasoningModel",
        "maxSteps", "temperature", "timeoutSeconds", "webTimeoutSeconds", "commandTimeoutSeconds", "saveSessions",
        "jevBaseUrl", "jevModel", "smartMode", "jevConfidenceFloor"
    ];

    public string? Get(string key) => key switch
    {
        "provider"       => Provider,
        "baseUrl"        => BaseUrl,
        "model"          => Model,
        "apiKeyEnv"      => ApiKeyEnv,
        "reasoningBaseUrl" => ReasoningBaseUrl,
        "reasoningModel" => ReasoningModel,
        "maxSteps"       => MaxSteps.ToString(),
        "temperature"    => Temperature.ToString("0.###"),
        "timeoutSeconds" => TimeoutSeconds.ToString(),
        "webTimeoutSeconds" => WebTimeoutSeconds.ToString(),
        "commandTimeoutSeconds" => CommandTimeoutSeconds.ToString(),
        "saveSessions"   => SaveSessions ? "true" : "false",
        "jevBaseUrl"     => JevBaseUrl,
        "jevModel"       => JevModel,
        "smartMode"      => SmartMode ? "on" : "off",
        "jevConfidenceFloor" => JevConfidenceFloor.ToString("0.00"),
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
            case "reasoningBaseUrl":
                // Empty is a value here: "same endpoint as the connection".
                if (value.Length > 0 && !Uri.TryCreate(value, UriKind.Absolute, out _))
                { error = "reasoningBaseUrl must be an absolute URL, or empty for the same endpoint"; return false; }
                ReasoningBaseUrl = value.TrimEnd('/');
                return true;
            case "reasoningModel":
                ReasoningModel = value.Trim();          // empty turns escalation off
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
            case "webTimeoutSeconds":
                if (!int.TryParse(value, out var webSecs) || webSecs is < 1 or > 600) { error = "webTimeoutSeconds must be 1..600"; return false; }
                WebTimeoutSeconds = webSecs;
                return true;
            case "commandTimeoutSeconds":
                if (!int.TryParse(value, out var cmdSecs) || cmdSecs is < 1 or > 3600) { error = "commandTimeoutSeconds must be 1..3600"; return false; }
                CommandTimeoutSeconds = cmdSecs;
                return true;
            case "saveSessions":
                if (!bool.TryParse(value, out var save)) { error = "saveSessions must be true or false"; return false; }
                SaveSessions = save;
                return true;
            case "jevBaseUrl":
                if (!Uri.TryCreate(value, UriKind.Absolute, out _)) { error = "jevBaseUrl must be an absolute URL"; return false; }
                JevBaseUrl = value.TrimEnd('/');
                return true;
            case "jevModel":
                if (value.Length == 0) { error = "jevModel must not be empty"; return false; }
                JevModel = value;
                return true;
            case "smartMode":
                var on = value.ToLowerInvariant();
                if (on is not ("on" or "off" or "true" or "false")) { error = "smartMode must be on or off"; return false; }
                SmartMode = on is "on" or "true";
                return true;
            case "jevConfidenceFloor":
                if (!double.TryParse(value, out var floor) || floor is < 0 or > 1)
                { error = "jevConfidenceFloor must be 0..1"; return false; }
                JevConfidenceFloor = floor;
                return true;
            default:
                error = $"unknown key '{key}' (known: {string.Join(", ", Keys)})";
                return false;
        }
    }
}
