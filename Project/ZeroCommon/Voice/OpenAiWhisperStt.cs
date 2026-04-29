using System.Net.Http;
using System.Net.Http.Headers;

namespace Agent.Common.Voice;

/// <summary>
/// Cloud STT via OpenAI's <c>/v1/audio/transcriptions</c> endpoint. Wraps the
/// caller's PCM buffer in a minimal RIFF WAV header before posting (the API
/// requires a file-typed multipart part) and returns the transcribed text.
/// Lives in ZeroCommon because it is pure HTTP — no native or WPF deps.
/// </summary>
public sealed class OpenAiWhisperStt : ISpeechToText
{
    public string ProviderName => "OpenAIWhisper";

    private readonly string _apiKey;
    private readonly HttpClient _http;

    public OpenAiWhisperStt(string apiKey)
    {
        _apiKey = apiKey;
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.openai.com"),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public Task<bool> EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            progress?.Report("OpenAI API key is not configured.");
            return Task.FromResult(false);
        }
        progress?.Report("OpenAI Whisper API ready.");
        return Task.FromResult(true);
    }

    public async Task<string> TranscribeAsync(byte[] pcm16kMono, string language = "auto", CancellationToken ct = default)
    {
        if (pcm16kMono.Length == 0) return "";

        var wavBytes = WavWriter.WrapPcmAsWav(pcm16kMono, sampleRate: 16_000, bitsPerSample: 16, channels: 1);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/audio/transcriptions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(wavBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", "audio.wav");
        content.Add(new StringContent("whisper-1"), "model");
        content.Add(new StringContent("text"), "response_format");

        // Hallucination-suppression knobs (OpenAI Whisper API):
        //
        // temperature=0 — fully deterministic decoding; same input always
        //   yields the same transcript. Default 0..1 sliding (model picks
        //   higher temps after low-confidence segments) which actively
        //   *invites* hallucination on quiet/ambient input. Hard zero.
        //
        // prompt="" — explicitly empty seed prompt. The default behaviour
        //   on whisper-1 carries no prior context across requests, but
        //   passing an explicit empty string ensures the model doesn't fall
        //   back to language-style priors that bias towards YouTube-creator
        //   outros ("시청해주셔서 감사합니다", "Thank you for watching")
        //   on near-silence audio.
        content.Add(new StringContent("0"), "temperature");
        content.Add(new StringContent(string.Empty), "prompt");

        if (language != "auto" && !string.IsNullOrEmpty(language))
            content.Add(new StringContent(language), "language");

        request.Content = content;

        var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var text = await response.Content.ReadAsStringAsync(ct);
        return text.Trim();
    }
}
