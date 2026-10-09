using System.Text;
using Agent.Common.Llm;
using Agent.Common.Voice;

namespace Agent.Common.Wearable;

/// <summary>
/// The wearable panel's checks that are not UI: what the host will load, a HUD post, and
/// the small rules for the folder list. Shared so the WPF and Avalonia panels cannot drift
/// (the WPF panel keeps its own copies until it is moved onto this).
/// </summary>
public static class WearableDiagnostics
{
    /// <summary>Brain choices in picker order (<see cref="WearableBrainNames"/>).</summary>
    public static readonly IReadOnlyList<string> Brains =
        [WearableBrainNames.AgentExternal, WearableBrainNames.AgentLocal, WearableBrainNames.Cli];

    /// <summary>Agent CLIs the Cli brain can drive.</summary>
    public static readonly IReadOnlyList<string> CliProviders = ["echo", "claude", "netclaw"];

    /// <summary>
    /// One line saying what the host will actually load, from the same stores it reads —
    /// the answer to "why does my watch not speak?", which is usually TTS not being
    /// Supertonic or a model never downloaded.
    /// </summary>
    public static string DescribeModels()
    {
        try
        {
            var voice = VoiceSettingsStore.Load();
            var llm = LlmSettingsStore.Load();

            var tts = string.Equals(voice.TtsProvider, TtsProviderNames.Supertonic, StringComparison.OrdinalIgnoreCase)
                ? SuperTonicModelStore.IsModelPresent(SuperTonicModelStore.ResolveModelDir(voice))
                    ? $"voice Supertonic {voice.SupertonicVoice}/{voice.SupertonicLanguage}"
                    : "voice Supertonic (model not installed → text only)"
                : $"voice off (TTS = {voice.TtsProvider})";

            var stt = string.Equals(voice.SttProvider, SttProviderNames.WhisperLocal, StringComparison.OrdinalIgnoreCase)
                ? WhisperModelStore.IsDownloaded(voice.SttWhisperModel)
                    ? $"ear whisper-{voice.SttWhisperModel}"
                    : $"ear whisper-{voice.SttWhisperModel} (model not installed → mic off)"
                : $"ear off (STT = {voice.SttProvider})";

            var brain = llm.ActiveBackend == LlmActiveBackend.Local
                ? $"brain on-device {LlmModelCatalog.FindById(llm.ModelId).Id}"
                : $"brain {llm.External.Provider} / {llm.ResolveExternalModel()}";

            return $"{tts} · {stt} · {brain}";
        }
        catch (Exception ex)
        {
            return $"could not read the settings: {ex.Message}";
        }
    }

    /// <summary>
    /// Posts one status and one event to the HUD endpoint, shaped like the hooks in
    /// <c>~/.claude/hud_amoled</c>. The reply says whether the endpoint took the line; the
    /// host's following <c>[hud/…]</c> line says whether it reached the watch.
    /// </summary>
    public static async Task<(string Status, string Event)> PostHudTestAsync(int port, CancellationToken ct = default)
    {
        const string status =
            """{"session":"panel-test","model":"AgentZero panel","cost_usd":0,"context_used_pct":0,"label":"wearable"}""";
        const string hookEvent =
            """{"type":"tool","tool":"Panel","target":"Test HUD","msg":"HUD test from the Wearable panel","session":"panel-test"}""";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        return (await PostAsync(http, port, "/status", status, ct), await PostAsync(http, port, "/event", hookEvent, ct));
    }

    private static async Task<string> PostAsync(HttpClient http, int port, string path, string json, CancellationToken ct)
    {
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        using var reply = await http.PostAsync($"http://127.0.0.1:{port}{path}", body, ct);
        return $"{(int)reply.StatusCode} {await reply.Content.ReadAsStringAsync(ct)}";
    }

    /// <summary>The alias a newly added folder gets: its name, normalised, made unique with -2, -3…</summary>
    public static string UniqueAlias(string folder, IEnumerable<string> taken)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        var alias = AllowedRoot.NormalizeAlias(name);
        if (string.IsNullOrEmpty(alias)) alias = "root";
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        var candidate = alias;
        for (var n = 2; used.Contains(candidate); n++) candidate = $"{alias}-{n}";
        return candidate;
    }
}
