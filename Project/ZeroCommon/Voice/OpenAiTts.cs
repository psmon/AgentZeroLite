using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Agent.Common.Voice;

/// <summary>
/// Cloud TTS via OpenAI <c>/v1/audio/speech</c> (model <c>tts-1</c>). Voices
/// are an enum surface fixed by OpenAI; the user picks one in the Voice tab.
/// Pure HTTP — lives in ZeroCommon, no Win-only deps.
/// </summary>
public sealed class OpenAiTts : ITextToSpeech
{
    public string ProviderName => "OpenAITTS";
    public string AudioFormat => "wav";

    /// <summary>
    /// OpenAI TTS speed, 0.25..4.0. 1.0 = default, 0.85 ≈ 15% slower (the
    /// virtual voice tester's default — slightly slower delivery improves
    /// Whisper recognition on synthesised speech).
    /// </summary>
    public double Speed { get; set; } = 1.0;

    private readonly string _apiKey;
    private readonly HttpClient _http;

    public static readonly string[] Voices =
        ["alloy", "echo", "fable", "onyx", "nova", "shimmer", "ash", "ballad", "coral", "sage", "verse"];

    public OpenAiTts(string apiKey)
    {
        _apiKey = apiKey;
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.openai.com"),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public Task<IReadOnlyList<string>> GetAvailableVoicesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Voices);

    public async Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var body = JsonSerializer.Serialize(new
        {
            model = "tts-1",
            input = text,
            voice = string.IsNullOrEmpty(voice) ? "alloy" : voice,
            response_format = "wav",
            speed = Math.Clamp(Speed, 0.25, 4.0),
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/audio/speech");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(ct);
    }
}
