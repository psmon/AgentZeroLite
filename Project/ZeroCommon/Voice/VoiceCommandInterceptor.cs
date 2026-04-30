namespace Agent.Common.Voice;

/// <summary>
/// What to do with a voice transcript before it reaches the LLM dispatch.
/// </summary>
public enum VoiceCommandIntent
{
    /// <summary>Forward the transcript verbatim to the existing send pipeline.</summary>
    PassThrough,

    /// <summary>User said "그만" / "stop" — cancel any in-flight TTS playback and skip LLM dispatch.</summary>
    StopSpeaking,

    /// <summary>
    /// User said something containing both "터미널" and "요약" (or "terminal"+"summarize"
    /// /"summary") — snapshot the active terminal output and ask the LLM to summarise it.
    /// </summary>
    SummarizeTerminal,
}

/// <summary>
/// Pure-logic classifier for voice transcripts. Decides whether a transcript
/// is a recognised in-app command ("그만", "터미널 작업 요약해 줘", …) or
/// just regular speech to be forwarded to the chat pipeline.
///
/// <para>Rules are deliberately simple — short transcript-level tests, no NLP —
/// because Whisper's output already varies across runs (punctuation, casing,
/// trailing periods) and we want predictable behaviour the user can rely on.</para>
/// </summary>
public static class VoiceCommandInterceptor
{
    /// <summary>
    /// "그만" alone (with optional punctuation/whitespace) cancels TTS. We
    /// match the *whole* utterance so a sentence that merely contains
    /// "그만" inside other text is left alone — e.g. "이거 그만 하면 좋겠어"
    /// passes through as a regular instruction.
    /// </summary>
    private static readonly HashSet<string> StopPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "그만",
        "그만해",
        "그만해줘",
        "그만하세요",
        "stop",
        "shut up",
        "be quiet",
        "quiet",
    };

    /// <summary>
    /// Classify a transcript. <paramref name="transcript"/> is the raw STT
    /// output — the caller is expected to have already trimmed/null-checked
    /// (this method does both anyway as a safety net).
    /// </summary>
    public static VoiceCommandIntent Classify(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return VoiceCommandIntent.PassThrough;
        var trimmed = transcript.Trim();

        // ── Stop ──
        // Strip trailing punctuation so "그만." / "stop!" still match.
        var stripped = StripTrailingPunctuation(trimmed);
        if (StopPhrases.Contains(stripped)) return VoiceCommandIntent.StopSpeaking;

        // ── Summarize terminal ──
        // Both keywords must appear (in either order). Korean: 터미널 + 요약.
        // English fallback: terminal + (summarize|summary). The English path
        // exists because Whisper occasionally transcribes Korean tech jargon
        // in English even with lang=ko.
        if (ContainsAll(trimmed, "터미널", "요약")) return VoiceCommandIntent.SummarizeTerminal;
        if (ContainsAll(trimmed, "terminal", "summar")) return VoiceCommandIntent.SummarizeTerminal;

        return VoiceCommandIntent.PassThrough;
    }

    private static bool ContainsAll(string haystack, params string[] needles)
    {
        foreach (var n in needles)
        {
            if (haystack.IndexOf(n, StringComparison.OrdinalIgnoreCase) < 0) return false;
        }
        return true;
    }

    private static string StripTrailingPunctuation(string s)
    {
        var end = s.Length;
        while (end > 0 && IsTrailingPunct(s[end - 1])) end--;
        return end == s.Length ? s : s[..end];
    }

    private static bool IsTrailingPunct(char c)
        => c is '.' or ',' or '!' or '?' or '~' or ' ' or '。' or '!' or '?';
}
