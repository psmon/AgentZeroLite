using Agent.Common.Voice.Streams;

namespace Agent.Common.Voice;

/// <summary>
/// Two calibration curves for the voice activity detector. The slider on
/// the Voice Test panel still picks a sensitivity 0–100; the profile
/// chosen here selects which curve that slider drives.
/// </summary>
public enum VoiceSensitivityLevel
{
    /// <summary>
    /// Origin-proven default — calibrated for a close personal microphone.
    /// Rejects ambient noise aggressively; misses soft / distant audio.
    /// </summary>
    Strict = 0,

    /// <summary>
    /// Lower threshold + longer hangover for lecture-style audio (speaker
    /// far from mic, ambient noise, intermittent pauses). Accepts softer
    /// frames at the cost of a small false-positive risk in very loud
    /// rooms — operator must be on a quiet baseline for best results.
    /// </summary>
    Loose = 1,
}

/// <summary>
/// Pure helpers for converting a (<see cref="VoiceSensitivityLevel"/>,
/// slider 0–100) pair into a <see cref="VadConfig"/>. Lives in
/// ZeroCommon so it stays headlessly testable — the WPF runtime calls
/// into here from <c>VoiceRuntimeFactory</c>.
/// </summary>
public static class VoiceSensitivityProfile
{
    /// <summary>Floor for the Strict curve — origin-proven personal-mic minimum.</summary>
    private const float StrictMinThreshold = 0.005f;

    /// <summary>
    /// Floor for the Loose curve. Set above the Strict floor so that at
    /// max sensitivity (slider 100) Loose still discriminates ambient
    /// noise from speech. The recording log on 2026-05-09 (M0015 / 후속
    /// 진행 #2) showed sens=100 collapsing both curves to 0.005 — at
    /// that point Loose's 0.4× multiplier was a no-op and continuous
    /// ambient noise was treated as continuous speech, producing a
    /// 89-second mega-utterance and Whisper hallucinations on the
    /// short ones. 0.012 sits above typical room-noise RMS while still
    /// catching the 0.04-band distant-lecturer fixture.
    /// </summary>
    private const float LooseMinThreshold = 0.012f;

    /// <summary>Hard cap on a single utterance — see VadConfig.MaxUtteranceSeconds.</summary>
    private const double LooseMaxUtteranceSeconds = 25.0;
    private const double StrictMaxUtteranceSeconds = 30.0;

    /// <summary>
    /// Build a <see cref="VadConfig"/> for the given profile + slider.
    ///
    /// Strict mirrors the legacy <c>(100 - sens) / 400</c> curve so
    /// existing personal-mic users see no behavioural shift. Loose
    /// scales the threshold down (multiplier 0.4) and lengthens the
    /// hangover so a lecturer's natural pauses don't split a single
    /// utterance into chopped fragments. Both profiles set a duration
    /// cap on a single utterance to keep the STT queue moving even when
    /// the hangover doesn't trip (M0015 / 후속 진행 #2).
    /// </summary>
    public static VadConfig BuildVadConfig(
        VoiceSensitivityLevel level,
        double sensitivityPercent)
    {
        var rawThreshold = Math.Max(0.0, 100.0 - sensitivityPercent) / 400.0;

        return level switch
        {
            // Loose retuned for sentence-pace segmentation (M0015 / 후속
            // 진행 #3). Hangover 80→24 frames (4s→1.2s), PreRoll 1.5→0.5s.
            // Operator observation: "lecture tempo is faster than command
            // tempo" — a 4-second hangover rides through inter-sentence
            // silence and merges every sentence into one blob. 1.2s sits
            // between intra-sentence micro-pauses (~0.3s) and natural
            // sentence boundaries (~0.8–1.5s) so each sentence emits as
            // its own segment for line-by-line note saving.
            VoiceSensitivityLevel.Loose => new VadConfig(
                VadThreshold: Math.Max(LooseMinThreshold, (float)(rawThreshold * 0.4)),
                PreRollSeconds: 0.5,
                UtteranceHangoverFrames: 24,
                MaxUtteranceSeconds: LooseMaxUtteranceSeconds),

            _ => new VadConfig(
                VadThreshold: Math.Max(StrictMinThreshold, (float)rawThreshold),
                PreRollSeconds: 1.0,
                UtteranceHangoverFrames: 40,
                MaxUtteranceSeconds: StrictMaxUtteranceSeconds),
        };
    }

    /// <summary>
    /// Parse the persisted string token back to an enum. Unknown / empty
    /// / mis-cased values fall back to <see cref="VoiceSensitivityLevel.Strict"/>
    /// so a user is never silently switched into Loose by a config typo.
    /// </summary>
    public static VoiceSensitivityLevel Parse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return VoiceSensitivityLevel.Strict;
        return Enum.TryParse<VoiceSensitivityLevel>(token, ignoreCase: true, out var v)
            ? v
            : VoiceSensitivityLevel.Strict;
    }

    /// <summary>String token used by <see cref="VoiceSettings"/> to persist the choice.</summary>
    public static string ToToken(VoiceSensitivityLevel level) => level.ToString();

    /// <summary>
    /// Resolve a persisted token in context. Most tokens map straight
    /// through <see cref="Parse"/>; the special <c>"Auto"</c> token
    /// defers the decision to runtime context — currently
    /// "is the caller in an agent-style interaction?" — so AgentBot's
    /// AiMode lands on Loose while ChatMode keeps Strict. The boolean
    /// is intentionally a generic flag (not a ChatMode enum) so
    /// non-AgentBot callers (voice-note plugin, future surfaces) can
    /// reuse the same resolver without depending on WPF types.
    /// </summary>
    public static VoiceSensitivityLevel ResolveAuto(string? token, bool isAgentMode)
    {
        if (!string.IsNullOrWhiteSpace(token) &&
            string.Equals(token, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            return isAgentMode ? VoiceSensitivityLevel.Loose : VoiceSensitivityLevel.Strict;
        }
        return Parse(token);
    }
}
