using System.IO;
using System.Speech.Synthesis;
using Agent.Common.Voice;

namespace AgentZeroWpf.Services.Voice;

/// <summary>
/// Local TTS via Windows SAPI voices (System.Speech.Synthesis). Quality and
/// available voices depend on installed Windows language packs (e.g. Korean
/// neural voices arrive with the optional speech feature on Win 11). No cloud,
/// no key.
/// </summary>
public sealed class WindowsTts : ITextToSpeech
{
    public string ProviderName => "WindowsTTS";
    public string AudioFormat => "wav";

    /// <summary>
    /// SAPI rate, integer -10..10. 0 = default. Negative = slower, positive
    /// = faster. The virtual voice tester sets this to -2 because Heami's
    /// default delivery is fast enough to confuse Whisper on phoneme
    /// boundaries; slower delivery measurably improves recognition.
    /// </summary>
    public int Rate { get; set; }

    public Task<IReadOnlyList<string>> GetAvailableVoicesAsync(CancellationToken ct = default)
    {
        using var synth = new SpeechSynthesizer();
        var voices = synth.GetInstalledVoices()
            .Where(v => v.Enabled)
            .Select(v => v.VoiceInfo.Name)
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(voices);
    }

    public Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(Array.Empty<byte>());

        using var synth = new SpeechSynthesizer();

        if (!string.IsNullOrEmpty(voice))
        {
            try
            {
                synth.SelectVoice(voice);
            }
            catch
            {
                // Exact match failed — try a substring match before falling back to default.
                var installed = synth.GetInstalledVoices()
                    .FirstOrDefault(v => v.Enabled &&
                        v.VoiceInfo.Name.Contains(voice, StringComparison.OrdinalIgnoreCase));
                if (installed is not null)
                    synth.SelectVoice(installed.VoiceInfo.Name);
            }
        }

        synth.Rate = Math.Clamp(Rate, -10, 10);

        using var ms = new MemoryStream();
        synth.SetOutputToWaveStream(ms);
        synth.Speak(text);
        return Task.FromResult(ms.ToArray());
    }
}
