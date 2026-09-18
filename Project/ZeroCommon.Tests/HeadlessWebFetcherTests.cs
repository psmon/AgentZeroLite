using Agent.Common.Web;

namespace ZeroCommon.Tests;

/// <summary>
/// URL policy of the wearable host's headless fetch (M0032) — no network involved. The
/// GUI's Browser page applies the same <see cref="HeadlessWebFetcher.TryValidate"/> before
/// navigating, so this is the policy for both paths.
/// </summary>
[Trait("Category", "Web")]
public sealed class HeadlessWebFetcherTests
{
    [Theory]
    [InlineData("https://example.org/a?b=1", "https://example.org/a?b=1")]
    [InlineData("example.org/path", "https://example.org/path")]        // scheme added
    [InlineData("  HTTP://Example.org  ", "http://example.org/")]
    public void Http_and_https_pass_with_a_scheme_filled_in(string input, string expected)
    {
        Assert.True(HeadlessWebFetcher.TryValidate(input, out var uri, out var error), error);
        Assert.Equal(expected, uri.ToString());
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.org/x")]
    [InlineData("http://localhost:8765/status")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://[::1]:8080/")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://app.localhost/")]
    [InlineData("")]
    public void Non_http_and_local_targets_are_refused(string input)
    {
        Assert.False(HeadlessWebFetcher.TryValidate(input, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("example.org", false)]
    [InlineData("192.168.0.10", false)]   // LAN is allowed on purpose: a home NAS is a legitimate target
    [InlineData("8.8.8.8", false)]
    public void Local_host_detection(string host, bool local)
        => Assert.Equal(local, HeadlessWebFetcher.IsLocalHost(host));
}
