using System.Text.RegularExpressions;

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

    /// <summary>
    /// M0017 — user said "에이전트 위임" / "위임 시작" / "delegate on" etc.
    /// AgentBot enters delegation mode: subsequent AI-mode utterances are
    /// force-routed to a Claude terminal via Mode 2 (terminal relay).
    /// </summary>
    DelegateOn,

    /// <summary>
    /// M0017 — user said "에이전트 위임 중단" / "위임 해제" / "delegate off".
    /// Leave delegation mode and return to normal mixed Mode 1/2 behaviour.
    /// </summary>
    DelegateOff,
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

        // ── Delegation mode toggle (M0017, trigger v3 — 에이전트 + alias set) ──
        //
        // v1 = "위임" (Whisper-fragile), v2 = "에이전트큐" (cleaner).
        // Whisper still transcribes the SAME spoken phrase differently
        // across runs depending on pace / mic / surrounding utterance.
        // Operator-observed variants of "에이전트큐":
        //   "에이전트큐"  "에이전트 큐"  "에이전트 Q"  "에이전트 cue"
        //   "에이전트 위임"  "에이전트 weim"  "agent q"  "agent cue"
        //
        // v3 strategy: require a fixed ANCHOR ("에이전트" or "agent") AND
        // any one of a generous TRIGGER set. The anchor is rare in casual
        // Korean / English speech so false-positive risk stays low even
        // with the broader trigger set.
        //
        // The bare letter "Q" is a special case — too short to substring-
        // match safely (would fire on "quick", "queue", etc.). Word-
        // boundary regex restricts it to standalone tokens.
        //
        // OFF markers (중단/해제/끝/중지/종료/off/stop) win over ON. The
        // Stop check above runs FIRST so "그만" alone always cancels TTS
        // and never reaches here.
        var hasAnchor = trimmed.Contains("에이전트", StringComparison.OrdinalIgnoreCase)
                     || trimmed.Contains("agent",   StringComparison.OrdinalIgnoreCase);
        if (hasAnchor)
        {
            var hasTriggerWord = ContainsAny(trimmed,
                    "큐", "위임", "weim", "cue", "queue", "delegate", "delegation")
                || Regex.IsMatch(trimmed, @"\bQ\b", RegexOptions.IgnoreCase);

            if (hasTriggerWord)
            {
                if (ContainsAny(trimmed, "중단", "해제", "끝", "중지", "종료", "off", "stop"))
                    return VoiceCommandIntent.DelegateOff;
                return VoiceCommandIntent.DelegateOn;
            }
        }

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

    private static bool ContainsAny(string haystack, params string[] needles)
    {
        foreach (var n in needles)
        {
            if (haystack.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
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
