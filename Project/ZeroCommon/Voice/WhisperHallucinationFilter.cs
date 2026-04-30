using System.Text;

namespace Agent.Common.Voice;

/// <summary>
/// Drops transcripts that match known Whisper hallucination patterns —
/// strings the model emits on near-silence / quiet input because they
/// dominate its training corpus (YouTube outros, podcast sign-offs).
///
/// In AgentZeroLite the voice pipeline feeds the transcript straight into
/// the agent as instruction text, so a single dropped "감사합니다" (sent
/// when the user merely cleared their throat) ends up as a no-op turn —
/// or worse, the agent treats it as a real instruction. None of the
/// patterns below are useful as commands, so dropping them outright is
/// preferable to forwarding hallucinated speech.
///
/// <para>Match rule is conservative: only the *whole* transcript is
/// compared after normalising (Unicode letters/digits only, lowercased).
/// A genuine utterance that contains the phrase plus other words passes
/// through unchanged — e.g. "감사합니다 잠깐만요" survives, "감사합니다."
/// alone is dropped.</para>
/// </summary>
public static class WhisperHallucinationFilter
{
    private static readonly (HashSet<string> Normalised, HashSet<string> Symbolic) Patterns = BuildPatterns();

    private static (HashSet<string> normalised, HashSet<string> symbolic) BuildPatterns()
    {
        // Raw forms — what Whisper actually emits. Patterns that contain at
        // least one letter/digit are stored normalised (lowercase letters &
        // digits only) so the runtime check is a single dictionary lookup.
        // Pure-symbol patterns (e.g. "♪") would normalise to an empty string,
        // so they're stored separately and matched against the trimmed raw
        // transcript.
        string[] raw =
        {
            // Korean — YouTube creator outros (the dominant Whisper
            // hallucination on Korean near-silence input).
            "감사합니다",
            "감사합니다.",
            "시청해주셔서 감사합니다",
            "시청해주셔서 감사합니다.",
            "시청해 주셔서 감사합니다",
            "시청해 주셔서 감사합니다.",
            "시청해주셔서 감사합니다 다음 영상에서 만나요",
            "구독과 좋아요 부탁드립니다",
            "구독 좋아요 부탁드립니다",
            "다음 영상에서 만나요",
            "다음 영상에서 뵙겠습니다",
            "이상입니다",
            "이상입니다.",

            // English — appears on quiet input regardless of language hint.
            "thank you",
            "thank you.",
            "thanks for watching",
            "thanks for watching.",
            "subscribe to my channel",
            "see you next time",
            "bye",
            "bye.",

            // Japanese — YouTube outros, frequent on Japanese near-silence.
            "ご視聴ありがとうございました",
            "ご視聴ありがとうございました。",
            "チャンネル登録お願いします",

            // Chinese — same pattern.
            "谢谢观看",
            "感谢观看",

            // Whisper's untranslated music token, sometimes leaks as text.
            "[music]",
            "(music)",
            "♪",
            "[음악]",
        };

        var normalised = new HashSet<string>(StringComparer.Ordinal);
        var symbolic = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in raw)
        {
            var n = Normalize(p);
            if (n.Length > 0) normalised.Add(n);
            else symbolic.Add(p.Trim());
        }
        return (normalised, symbolic);
    }

    /// <summary>
    /// Returns true if <paramref name="transcript"/> is one of the known
    /// Whisper-hallucination outros and should be dropped instead of
    /// forwarded as a user instruction.
    /// </summary>
    public static bool IsLikelyHallucination(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return false;
        var trimmed = transcript.Trim();
        if (Patterns.Symbolic.Contains(trimmed)) return true;
        var n = Normalize(trimmed);
        if (n.Length == 0) return false;
        return Patterns.Normalised.Contains(n);
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
