using Agent.Common.Voice;
using AgentZeroAvalonia.ViewModels;
using Xunit;

namespace AgentZeroAvalonia.Tests;

/// <summary>
/// voice-settings.json is shared with the WPF host and the wearable host, so this page may
/// only change what it shows.
/// </summary>
public class VoiceSettingsViewModelTests
{
    [Fact]
    public void Saving_keeps_the_fields_this_page_does_not_show()
    {
        var stored = new VoiceSettings
        {
            InputDeviceId = "3", VadThreshold = 40, MicMuted = true, UseStreamPipeline = true, SttLanguage = "ko",
        };
        var vm = new VoiceSettingsViewModel(stored);
        vm.SttLanguage = "en";

        var saved = vm.ApplyTo(stored);

        Assert.Equal("en", saved.SttLanguage);
        Assert.Equal("3", saved.InputDeviceId);
        Assert.Equal(40, saved.VadThreshold);
        Assert.True(saved.MicMuted);
        Assert.True(saved.UseStreamPipeline);
    }

    [Fact]
    public void KittenTTS2_is_offered_and_its_fields_round_trip()
    {
        var stored = new VoiceSettings
        {
            TtsProvider = TtsProviderNames.KittenTts2, Kitten2Language = "en", Kitten2Device = "cpu",
        };
        var vm = new VoiceSettingsViewModel(stored);

        Assert.Contains(TtsProviderNames.KittenTts2, vm.TtsProviders);
        Assert.DoesNotContain("KittenTTS", vm.TtsProviders);       // KittenTTS 0.8 is not offered any more
        Assert.True(vm.ShowKitten2);
        Assert.True(vm.TtsRunsHere);
        Assert.Equal(Kitten2Devices.Cpu, vm.Kitten2Device);          // stored lower-case, shown as the picker's value
        Assert.Equal(Kitten2Devices.All, vm.Kitten2DeviceChoices);
        vm.Kitten2Device = Kitten2Devices.Gpu;
        vm.Kitten2Language = "ko";

        var saved = vm.ApplyTo(stored);
        Assert.Equal(Kitten2Devices.Gpu, saved.Kitten2Device);
        Assert.Equal("ko", saved.Kitten2Language);
        Assert.Equal(TtsProviderNames.KittenTts2, saved.TtsProvider);
    }

    [Fact]
    public void Streaming_STT_is_offered_first_with_nemotron_as_the_official_model()
    {
        var stored = new VoiceSettings { SttProvider = SttProviderNames.SherpaStreaming, StreamingSttModel = "", StreamingEndpointMs = 50 };
        var vm = new VoiceSettingsViewModel(stored);

        Assert.Equal(SttProviderNames.SherpaStreaming, vm.SttProviders[0]);
        Assert.True(vm.ShowStreaming);
        Assert.False(vm.ShowWhisper);
        Assert.True(vm.SttRunsHere);
        Assert.Equal(Agent.Common.Voice.Streaming.StreamingSttModelCatalog.Default, vm.StreamingModel!.Model);
        Assert.StartsWith("Nemotron 3.5", vm.StreamingModel.Model.Label);
        Assert.Contains("official", vm.StreamingModel.ToString());
        Assert.Equal(300, vm.StreamingEndpointMs);                    // clamped on the way in

        vm.StreamingModel = vm.StreamingModels.Single(c => c.Model.Id == "nemotron-3.5-160ms");
        vm.StreamingEndpointMs = 600;
        var saved = vm.ApplyTo(stored);
        Assert.Equal("nemotron-3.5-160ms", saved.StreamingSttModel);
        Assert.Equal(600, saved.StreamingEndpointMs);
    }

    [Fact]
    public void An_unknown_device_value_reads_as_auto() =>
        Assert.Equal(Kitten2Devices.Auto, new VoiceSettingsViewModel(new VoiceSettings { Kitten2Device = "npu" }).Kitten2Device);

    [Fact]
    public void A_provider_only_the_windows_host_runs_survives_a_save()
    {
        var stored = new VoiceSettings { TtsProvider = TtsProviderNames.WindowsTts, SttProvider = "LocalGemma" };
        var vm = new VoiceSettingsViewModel(stored);

        Assert.Contains(TtsProviderNames.WindowsTts, vm.TtsProviders);
        Assert.False(vm.TtsRunsHere);
        var saved = vm.ApplyTo(stored);
        Assert.Equal(TtsProviderNames.WindowsTts, saved.TtsProvider);
        Assert.Equal("LocalGemma", saved.SttProvider);
    }

    [Fact]
    public void Supertonic_shows_the_voice_the_desktop_speaks_and_saves_it_to_both_fields()
    {
        // Measured on a real file: TtsVoice M2 overrode SupertonicVoice F1, so the desktop
        // spoke M2 while the watch, which reads SupertonicVoice only, spoke F1.
        var stored = new VoiceSettings { TtsProvider = TtsProviderNames.Supertonic, TtsVoice = "M2", SupertonicVoice = "F1" };
        var vm = new VoiceSettingsViewModel(stored);
        Assert.Equal("M2", vm.SupertonicVoice);

        var saved = vm.ApplyTo(stored);
        Assert.Equal("M2", saved.SupertonicVoice);
        Assert.Equal("M2", saved.TtsVoice);
    }
}
