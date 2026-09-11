using ZeroCommon.Tests.Remote;

namespace ZeroCommon.Tests;

public class TerminalLinkScannerTests
{
    private const string OAuthUrl =
        "https://claude.ai/oauth/authorize?code=true&client_id=9d1c250a-e61b-44d9-88ed-5944d1962f5e" +
        "&response_type=code&redirect_uri=http%3A%2F%2Flocalhost%3A54545%2Fcallback" +
        "&scope=org%3Acreate_api_key+user%3Aprofile+user%3Ainference&state=abc123XYZ";

    [Fact]
    public void Scan_PlainUrl_TrimsTrailingPunctuation()
    {
        var links = TerminalLinkScanner.Scan("Open https://example.com/docs, then continue.\n");
        var l = Assert.Single(links);
        Assert.Equal("https://example.com/docs", l.Url);
        Assert.False(l.MayContinue);
    }

    [Fact]
    public void Scan_JoinsSoftWrappedUrl_AcrossThreeRows()
    {
        const int cols = 80;
        var rows = new[] { OAuthUrl[..cols], OAuthUrl[cols..(2 * cols)], OAuthUrl[(2 * cols)..] };
        var text = "Use the url below to sign in:\n\n" + string.Join("\n", rows) + "\n\nPaste code here:";

        var links = TerminalLinkScanner.Scan(text, columns: cols);

        var l = Assert.Single(links);
        Assert.Equal(OAuthUrl, l.Url);
    }

    [Fact]
    public void Scan_JoinsSoftWrappedUrl_WithoutKnownColumns_UsesDefaultWidth()
    {
        // Row is wider than DefaultWrapWidth → treated as a wrapped row.
        var text = OAuthUrl[..100] + "\n" + OAuthUrl[100..] + "\n";
        var l = Assert.Single(TerminalLinkScanner.Scan(text));
        Assert.Equal(OAuthUrl, l.Url);
    }

    [Fact]
    public void Scan_DoesNotJoin_WhenRowIsShort()
    {
        var text = "see https://x.com/a\nThen run the build\n";
        var l = Assert.Single(TerminalLinkScanner.Scan(text, columns: 80));
        Assert.Equal("https://x.com/a", l.Url);
    }

    [Fact]
    public void Scan_DoesNotJoin_WhenNextRowStartsWithNewUrl()
    {
        var first = "https://a.example.com/" + new string('x', 60); // 82 chars, full row at 83 cols
        var text = first + "\nhttps://b.example.com/y\n";
        var links = TerminalLinkScanner.Scan(text, columns: 83);
        Assert.Equal(2, links.Count);
        Assert.Equal(first, links[0].Url);
        Assert.Equal("https://b.example.com/y", links[1].Url);
    }

    [Fact]
    public void Scan_DoesNotJoin_WhenNextRowIsNotUrlText()
    {
        var first = "https://a.example.com/" + new string('x', 60);
        var text = first + "\n한글 안내문\n";
        var l = Assert.Single(TerminalLinkScanner.Scan(text, columns: 83));
        Assert.Equal(first, l.Url);
    }

    [Fact]
    public void Scan_FlagsMayContinue_WhenUrlReachesEndOfText_AndNotFinal()
    {
        var partial = "https://claude.ai/oauth/authorize?code=true";
        var streaming = Assert.Single(TerminalLinkScanner.Scan("go " + partial, final: false));
        Assert.True(streaming.MayContinue);

        var settled = Assert.Single(TerminalLinkScanner.Scan("go " + partial, final: true));
        Assert.False(settled.MayContinue);
        Assert.Equal(partial, settled.Url);
    }

    [Fact]
    public void Scan_FlagsMayContinue_WhenFullRowEndsAndNextRowNotYetReceived()
    {
        var text = OAuthUrl[..80] + "\n";
        var l = Assert.Single(TerminalLinkScanner.Scan(text, columns: 80, final: false));
        Assert.True(l.MayContinue);
    }

    [Fact]
    public void Scan_Localhost_GetsHttpScheme()
    {
        var l = Assert.Single(TerminalLinkScanner.Scan("Listening on localhost:5173/app\n"));
        Assert.Equal("http://localhost:5173/app", l.Url);
    }

    [Fact]
    public void Scan_SkipsEllipsisTruncatedUrl()
    {
        Assert.Empty(TerminalLinkScanner.Scan("see https://example.com/very/long/path...\n"));
    }

    [Theory]
    [InlineData("https://claude.ai/oauth/authorize?x=1", true)]
    [InlineData("http://localhost:3000", true)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ms-settings:display", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void IsOpenableWebUrl_AllowsOnlyHttpAndHttps(string? url, bool expected)
    {
        Assert.Equal(expected, TerminalLinkScanner.IsOpenableWebUrl(url));
    }
}

public class TerminalLinkDetectorTests
{
    private const string OAuthUrl =
        "https://claude.ai/oauth/authorize?code=true&client_id=9d1c250a-e61b-44d9-88ed-5944d1962f5e" +
        "&response_type=code&redirect_uri=http%3A%2F%2Flocalhost%3A54545%2Fcallback" +
        "&scope=org%3Acreate_api_key+user%3Aprofile&state=abc123XYZ";

    [Fact]
    public void Detector_JoinsUrlWrappedAcrossFrames_AndStripsAnsi()
    {
        var session = new FakeTerminalSession();
        using var det = new TerminalLinkDetector(session, columns: 80);
        var found = new List<string>();
        det.LinkDetected += found.Add;

        // Frame 1: heading + first full row of the URL (Ink hard-wraps at 80).
        session.RaiseOutput("\x1b[32mBrowser didn't open? Use the url below:\x1b[0m\r\n\r\n" + OAuthUrl[..80] + "\r\n");
        Assert.Empty(found); // held back — continuation row not here yet

        // Frame 2: the rest.
        session.RaiseOutput(OAuthUrl[80..] + "\r\n\r\nPaste code here if prompted >\r\n");

        Assert.Equal([OAuthUrl], found);
    }

    [Fact]
    public void Detector_EmitsCompleteUrl_Immediately()
    {
        var session = new FakeTerminalSession();
        using var det = new TerminalLinkDetector(session);
        var found = new List<string>();
        det.LinkDetected += found.Add;

        session.RaiseOutput("Visit https://github.com/login/device and enter code ABCD-1234\r\n");

        Assert.Equal(["https://github.com/login/device"], found);
    }

    [Fact]
    public void Detector_DedupesRedrawsOfTheSameScreen()
    {
        var session = new FakeTerminalSession();
        using var det = new TerminalLinkDetector(session);
        var found = new List<string>();
        det.LinkDetected += found.Add;

        for (var i = 0; i < 5; i++)
            session.RaiseOutput("\x1b[2J\x1b[1;1HOpen https://example.com/login to continue\r\n");

        Assert.Single(found);
    }

    [Fact]
    public void Detector_Flush_EmitsUrlHeldAtBufferEnd()
    {
        var session = new FakeTerminalSession();
        using var det = new TerminalLinkDetector(session);
        var found = new List<string>();
        det.LinkDetected += found.Add;

        session.RaiseOutput("Sign in: https://accounts.example.com/device?user_code=XYZ");
        Assert.Empty(found); // no delimiter after the URL yet

        det.Flush();          // what the idle timer does after 600 ms of silence
        Assert.Equal(["https://accounts.example.com/device?user_code=XYZ"], found);
    }

    [Fact]
    public void Detector_IgnoresNonWebSchemes()
    {
        var session = new FakeTerminalSession();
        using var det = new TerminalLinkDetector(session);
        var found = new List<string>();
        det.LinkDetected += found.Add;

        session.RaiseOutput("cached at file:///C:/temp/x.txt and ftp://host/y\r\n");

        Assert.Empty(found);
    }

    [Fact]
    public void Detector_Dispose_StopsListening()
    {
        var session = new FakeTerminalSession();
        var det = new TerminalLinkDetector(session);
        var found = new List<string>();
        det.LinkDetected += found.Add;
        det.Dispose();

        session.RaiseOutput("https://example.com/after-dispose\r\n");

        Assert.Empty(found);
    }

    [Fact]
    public void EventStream_UrlDetected_JoinsWrappedUrl()
    {
        var session = new FakeTerminalSession();
        using var stream = new AgentEventStream(session);
        var urls = new List<string>();
        stream.EventReceived += e => { if (e is UrlDetected u) urls.Add(u.Url); };

        session.RaiseOutput("Use the url below:\n" + OAuthUrl[..100] + "\n");
        Assert.Empty(urls);
        session.RaiseOutput(OAuthUrl[100..] + "\n\n");

        Assert.Equal([OAuthUrl], urls);
    }
}
