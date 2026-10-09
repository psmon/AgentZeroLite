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
