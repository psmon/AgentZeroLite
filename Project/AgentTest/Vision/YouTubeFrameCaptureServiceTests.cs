using AgentZeroWpf.Services.Vision;
using Xunit;

namespace AgentTest.Vision;

// TryParseVideoId delegates to the tested WebDevHost.ParseYouTubeId; these assert
// the wrapper's bool/out contract holds across the URL shapes the Vision tab accepts.
public sealed class YouTubeFrameCaptureServiceTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=UG05pxLGwzc&list=RDUG05pxLGwzc", "UG05pxLGwzc")]
    [InlineData("https://youtu.be/UG05pxLGwzc?t=10", "UG05pxLGwzc")]
    [InlineData("https://www.youtube.com/shorts/UG05pxLGwzc", "UG05pxLGwzc")]
    [InlineData("https://www.youtube.com/embed/UG05pxLGwzc", "UG05pxLGwzc")]
    [InlineData("UG05pxLGwzc", "UG05pxLGwzc")]
    public void TryParseVideoId_ExtractsId(string url, string expected)
    {
        Assert.True(YouTubeFrameCaptureService.TryParseVideoId(url, out var id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData("https://example.com/not-a-video")]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParseVideoId_RejectsNonYouTube(string url)
    {
        Assert.False(YouTubeFrameCaptureService.TryParseVideoId(url, out _));
    }
}
