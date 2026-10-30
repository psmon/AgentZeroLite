// ───────────────────────────────────────────────────────────
// Live speaker diarization — runs only where the Sherpa diarization
// models and the Korean Zipformer test clips are installed. Guards the
// sherpa-onnx minor bump (1.10.46 → 1.13.8, taken for the streaming
// recognizer): the offline diarization config must still load and run.
// ───────────────────────────────────────────────────────────

using Agent.Common.Voice;
using Agent.Common.Voice.Diarization;
using Agent.Common.Voice.Streaming;
using Xunit.Abstractions;

namespace ZeroCommon.Tests.Voice;

public sealed class SherpaDiarizationLiveTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Diarizes_a_korean_clip_after_the_sherpa_upgrade()
    {
        var clip = Path.Combine(StreamingSttModelCatalog.ModelDirectory(StreamingSttModelCatalog.ZipformerKorean), "test_wavs", "2.wav");
        if (!File.Exists(DiarizationSettingsStore.DefaultSegmentationPath)
            || !File.Exists(DiarizationSettingsStore.DefaultEmbeddingPath) || !File.Exists(clip))
        {
            output.WriteLine("skipped — diarization models or the Zipformer test clips are not installed");
            return;
        }

        await using var diarizer = new SherpaSpeakerDiarizer(new DiarizationSettings { Provider = DiarizationProviderNames.SherpaPyannote3D });
        Assert.True(await diarizer.EnsureReadyAsync(new Progress<string>(output.WriteLine)));
        var result = await diarizer.DiarizeAsync(WavPcm.To16kMono(await File.ReadAllBytesAsync(clip)));
        output.WriteLine($"speakers={result.SpeakerCount} segments={result.Segments.Count} in {result.InferenceTime.TotalMilliseconds:F0} ms");
        Assert.NotEmpty(result.Segments);
    }
}
