using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Agent.Common.Llm.Providers;

namespace Agent.Common.Voice;

/// <summary>
/// Webnori-hosted Gemma audio STT. Webnori exposes an OpenAI-compatible chat
/// endpoint, so we hand the audio in via a Chat-Completions <c>input_audio</c>
/// content block and ask the model to transcribe verbatim. The credentials
/// are owned by the LLM tab (<c>WebnoriDefaults.BaseUrl</c> /
/// <c>WebnoriDefaults.ApiKey</c>) — the user does not maintain a second slot.
///
/// The actual audio capability of a given Webnori model is server-side; if
/// the endpoint refuses the audio content type we surface the upstream error
/// verbatim so the user can pick a different model on the Voice tab.
/// </summary>
public sealed class WebnoriGemmaStt : ISpeechToText
{
    public string ProviderName => "WebnoriGemma";

    private readonly string _model;
    private readonly HttpClient _http;

    public WebnoriGemmaStt(string model)
    {
        _model = string.IsNullOrEmpty(model) ? WebnoriDefaults.DefaultModel : model;
        _http = new HttpClient
        {
            BaseAddress = new Uri(WebnoriDefaults.BaseUrl),
            Timeout = TimeSpan.FromSeconds(60),
        };
    }

    public Task<bool> EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report($"Webnori Gemma STT ready (model={_model}).");
        return Task.FromResult(true);
    }

    public async Task<string> TranscribeAsync(byte[] pcm16kMono, string language = "auto", CancellationToken ct = default)
    {
        if (pcm16kMono.Length == 0) return "";

        var wavBytes = WavWriter.WrapPcmAsWav(pcm16kMono, sampleRate: 16_000, bitsPerSample: 16, channels: 1);
        var b64 = Convert.ToBase64String(wavBytes);

        // Chat Completions with an input_audio content block. Models that don't
        // implement audio input return 400 here — we let the exception propagate
        // so the UI surfaces the message instead of silently returning "".
        var instruction = language == "auto"
            ? "Transcribe the spoken audio verbatim. Output only the transcription, no commentary."
            : $"Transcribe the spoken audio verbatim in {language}. Output only the transcription, no commentary.";

        var body = JsonSerializer.Serialize(new
        {
            model = _model,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = instruction },
                        new
                        {
                            type = "input_audio",
                            input_audio = new { data = b64, format = "wav" }
                        }
                    }
                }
            },
            temperature = 0.0,
            max_tokens = 1024,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", WebnoriDefaults.ApiKey);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Webnori STT failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var text = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
        return text.Trim();
    }
}
