using Agent.Common.Voice;

namespace ZeroCommon.Tests.Voice;

public sealed class WhisperHallucinationFilterTests
{
    [Theory]
    [InlineData("감사합니다")]
    [InlineData("감사합니다.")]
    [InlineData("  감사합니다  ")]
    [InlineData("감사합니다!")]
    [InlineData("시청해주셔서 감사합니다")]
    [InlineData("시청해 주셔서 감사합니다.")]
    [InlineData("Thank you")]
    [InlineData("Thank you.")]
    [InlineData("THANK YOU")]
    [InlineData("Thanks for watching")]
    [InlineData("ご視聴ありがとうございました")]
    [InlineData("谢谢观看")]
    [InlineData("[Music]")]
    [InlineData("♪")]
    public void Drops_known_outro_patterns(string transcript)
    {
        Assert.True(WhisperHallucinationFilter.IsLikelyHallucination(transcript),
            $"Expected '{transcript}' to be flagged as hallucination");
    }

    [Theory]
    [InlineData("감사합니다 잠깐만요")]            // outro phrase + extra words → real utterance
    [InlineData("정말 감사합니다 도와주셔서")]      // sandwiched in real speech
    [InlineData("Thank you for the help")]          // longer English sentence
    [InlineData("안녕하세요")]                       // unrelated common Korean greeting
    [InlineData("터미널 열어줘")]                    // realistic agent instruction
    [InlineData("create a new file")]
    public void Keeps_real_utterances(string transcript)
    {
        Assert.False(WhisperHallucinationFilter.IsLikelyHallucination(transcript),
            $"Expected '{transcript}' to pass through");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_or_null_is_not_flagged(string? transcript)
    {
        // Empty inputs are caller's responsibility — filter returns false so
        // the upstream "empty transcript" branch handles them with its own log.
        Assert.False(WhisperHallucinationFilter.IsLikelyHallucination(transcript));
    }
}
