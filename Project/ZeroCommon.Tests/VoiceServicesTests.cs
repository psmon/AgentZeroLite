using Agent.Common.Voice;

namespace ZeroCommon.Tests;

public class VoiceServicesTests
{
    [Fact]
    public async Task NullTextToSpeech_Synthesize_ReturnsEmpty()
    {
        var tts = VoiceServices.CreateTextToSpeech();
        Assert.Equal("none", tts.ProviderName);
        var audio = await tts.SynthesizeAsync("hello", "default");
        Assert.Empty(audio);
    }

    [Fact]
    public async Task NullSpeechToText_NotReady_TranscribesEmpty()
    {
        var stt = VoiceServices.CreateSpeechToText();
        Assert.False(await stt.EnsureReadyAsync());
        Assert.Equal("", await stt.TranscribeAsync(new byte[16]));
    }

    [Fact]
    public void NullAudioPlaybackQueue_NotBusy()
    {
        using var q = VoiceServices.CreateAudioPlaybackQueue();
        q.Enqueue(new byte[4], "pcm16");
        Assert.False(q.IsBusy);
    }
}
