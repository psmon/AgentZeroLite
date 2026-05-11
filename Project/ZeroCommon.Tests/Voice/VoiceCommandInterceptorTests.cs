using Agent.Common.Voice;

namespace ZeroCommon.Tests.Voice;

public sealed class VoiceCommandInterceptorTests
{
    [Theory]
    [InlineData("그만")]
    [InlineData("그만.")]
    [InlineData("그만!")]
    [InlineData("  그만  ")]
    [InlineData("그만해")]
    [InlineData("그만해줘")]
    [InlineData("그만하세요")]
    [InlineData("stop")]
    [InlineData("Stop.")]
    [InlineData("STOP")]
    [InlineData("shut up")]
    public void Detects_stop(string transcript)
    {
        Assert.Equal(VoiceCommandIntent.StopSpeaking, VoiceCommandInterceptor.Classify(transcript));
    }

    [Theory]
    [InlineData("터미널 작업 요약해")]
    [InlineData("터미널 요약해 줘")]
    [InlineData("지금 활성 터미널 내용 요약해 줘")]
    [InlineData("요약 좀 해줘 터미널")]
    [InlineData("summarize the terminal")]
    [InlineData("Terminal summary please")]
    public void Detects_summarize_terminal(string transcript)
    {
        Assert.Equal(VoiceCommandIntent.SummarizeTerminal, VoiceCommandInterceptor.Classify(transcript));
    }

    [Theory]
    [InlineData("이거 그만 하면 좋겠어")]              // "그만" inside a sentence — not a stop command
    [InlineData("터미널 열어줘")]                       // 터미널 alone, no 요약
    [InlineData("요약 좀 해줘")]                        // 요약 alone, no 터미널
    [InlineData("안녕하세요")]
    [InlineData("file.txt 만들어 줘")]
    [InlineData("create a new file")]
    public void Passes_through_regular_speech(string transcript)
    {
        Assert.Equal(VoiceCommandIntent.PassThrough, VoiceCommandInterceptor.Classify(transcript));
    }

    // ── Delegation mode toggle (M0017, trigger v3 — anchor + alias set) ──
    //
    // v3 contract: require an ANCHOR ("에이전트" or "agent") AND a TRIGGER
    // word from {큐, 위임, weim, cue, queue, Q (word-boundary), delegate,
    // delegation}. Whisper's transcription of the SAME spoken phrase varies
    // across runs ("에이전트 Q" / "에이전트 cue" / "에이전트 위임" / etc.);
    // accepting the alias set keeps the operator's spoken trigger reliable
    // without retraining their habit.

    // ON — anchored variants firing across the alias set.
    [Theory]
    [InlineData("에이전트큐")]
    [InlineData("에이전트큐 시작")]
    [InlineData("에이전트 큐")]                       // Whisper space insertion
    [InlineData("에이전트 큐 시작해")]
    [InlineData("에이전트 Q")]                        // Operator-observed transcript
    [InlineData("에이전트 q")]                        // case-insensitive
    [InlineData("에이전트 cue")]
    [InlineData("에이전트 위임")]                     // v1 phrase, now an alias
    [InlineData("에이전트 위임 시작해")]
    [InlineData("에이전트 weim")]                     // English transliteration
    [InlineData("agent cue")]
    [InlineData("Agent Q")]
    [InlineData("agent queue")]
    [InlineData("agent delegate")]                    // English form, anchored
    [InlineData("agent delegation start")]
    public void Detects_delegate_on(string transcript)
    {
        Assert.Equal(VoiceCommandIntent.DelegateOn, VoiceCommandInterceptor.Classify(transcript));
    }

    // OFF — OFF markers (중단/해제/끝/중지/종료/off/stop) win over plain trigger.
    [Theory]
    [InlineData("에이전트큐 중단")]
    [InlineData("에이전트큐중단")]
    [InlineData("에이전트 큐 해제")]
    [InlineData("에이전트큐 끝")]
    [InlineData("에이전트큐 중지")]
    [InlineData("에이전트큐 종료")]
    [InlineData("에이전트 Q 중단")]
    [InlineData("에이전트 위임 중단")]
    [InlineData("agent cue off")]
    [InlineData("agent delegation stop")]            // anchored English OFF
    public void Detects_delegate_off(string transcript)
    {
        Assert.Equal(VoiceCommandIntent.DelegateOff, VoiceCommandInterceptor.Classify(transcript));
    }

    // Stop intent still wins over delegation when the WHOLE utterance is a
    // stop phrase.
    [Fact]
    public void Stop_alone_is_still_stop_not_delegation()
    {
        Assert.Equal(VoiceCommandIntent.StopSpeaking, VoiceCommandInterceptor.Classify("그만"));
    }

    // Anchorless utterances MUST NOT fire — these are normal speech where
    // 큐/위임/Q/cue can legitimately appear. The "에이전트" anchor is what
    // makes the trigger set safe even though some tokens are short.
    [Theory]
    [InlineData("위임")]
    [InlineData("위임 시작")]
    [InlineData("큐 두 개 있어")]
    [InlineData("Q 키 눌러")]
    [InlineData("cue card")]
    [InlineData("queue size")]
    [InlineData("delegate to me")]    // English "delegate" alone, no agent anchor
    public void Anchorless_trigger_word_does_not_fire(string transcript)
    {
        Assert.Equal(VoiceCommandIntent.PassThrough, VoiceCommandInterceptor.Classify(transcript));
    }

    // "Q" word-boundary check: must NOT fire when Q appears inside another
    // word (queue, quick, sequel) — only standalone Q after anchor counts.
    [Theory]
    [InlineData("에이전트 quick start")]      // "quick" — Q inside word
    [InlineData("agent sequel")]              // "sequel" — Q inside word
    public void Anchor_with_Q_inside_word_does_not_fire(string transcript)
    {
        Assert.Equal(VoiceCommandIntent.PassThrough, VoiceCommandInterceptor.Classify(transcript));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_passes_through(string? transcript)
    {
        Assert.Equal(VoiceCommandIntent.PassThrough, VoiceCommandInterceptor.Classify(transcript));
    }
}
