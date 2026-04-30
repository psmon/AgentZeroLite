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

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_passes_through(string? transcript)
    {
        Assert.Equal(VoiceCommandIntent.PassThrough, VoiceCommandInterceptor.Classify(transcript));
    }
}
