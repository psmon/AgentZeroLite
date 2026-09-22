using AgentOne.Agent;
using AgentOne.Tools;
using AgentOne.Tools.Web;

namespace AgentOne.Tests;

/// <summary>
/// The web tools, tested without the web. Parsing someone else's markup and
/// unwrapping someone else's redirect are the two things that will break when
/// they change their page, so both get fixtures rather than a live call.
/// </summary>
public class HtmlTextTests
{
    [Fact]
    public void ScriptsAndStylesAreNotProse()
    {
        var html = """
            <html><head><title>T</title><style>body{color:red}</style></head>
            <body><script>alert('x')</script><p>Real text.</p></body></html>
            """;

        var text = HtmlText.Extract(html);

        Assert.Contains("Real text.", text);
        Assert.DoesNotContain("alert", text);
        Assert.DoesNotContain("color:red", text);
    }

    [Fact]
    public void EntitiesAreDecoded()
    {
        Assert.Equal("a & b < c", HtmlText.Extract("<p>a &amp; b &lt; c</p>"));
    }

    [Fact]
    public void BlockTagsBecomeLineBreaksSoParagraphsDoNotRunTogether()
    {
        var text = HtmlText.Extract("<p>one</p><p>two</p>");

        Assert.Contains("one", text);
        Assert.Contains("two", text);
        Assert.DoesNotContain("onetwo", text);
    }

    [Fact]
    public void WhitespaceIsCollapsedButParagraphsSurvive()
    {
        var text = HtmlText.Extract("<p>a     b</p>\n\n\n\n<p>c</p>");

        Assert.Contains("a b", text);
        Assert.DoesNotContain("\n\n\n", text);
    }

    [Fact]
    public void TitleIsFound()
    {
        Assert.Equal("Hello & Goodbye", HtmlText.Title("<html><head><title>Hello &amp; Goodbye</title></head></html>"));
    }

    [Fact]
    public void NoTitleIsEmptyRatherThanAnError()
    {
        Assert.Equal("", HtmlText.Title("<html><body>x</body></html>"));
    }

    [Fact]
    public void EmptyInputIsEmptyOutput()
    {
        Assert.Equal("", HtmlText.Extract(""));
        Assert.Equal("", HtmlText.Extract("   "));
    }

    [Fact]
    public void TruncationSaysThatItTruncated()
    {
        var capped = HtmlText.Cap(new string('x', 100), 10);

        Assert.StartsWith(new string('x', 10), capped);
        Assert.Contains("truncated", capped);
        Assert.Contains("100", capped);
    }

    [Fact]
    public void ShortTextIsLeftAlone()
    {
        Assert.Equal("short", HtmlText.Cap("short", 100));
    }
}

public class SearchParsingTests
{
    // Trimmed from a real response, keeping the shapes that matter: the engine's
    // redirect wrapper, an HTML entity in the href, and markup inside the title.
    private const string Fixture = """
        <div class="result results_links">
          <a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fgetakka.net%2F&amp;rut=abc">Akka<b>.NET</b></a>
          <a class="result__snippet">Build powerful &amp; concurrent applications.</a>
        </div>
        <div class="result results_links">
          <a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fgithub.com%2Fakkadotnet%2Fakka.net&amp;rut=def">akkadotnet/akka.net</a>
          <a class="result__snippet">Canonical actor model implementation.</a>
        </div>
        """;

    [Fact]
    public void ResultsAreParsedWithRealUrls()
    {
        var hits = WebFetcher.ParseResults(Fixture, 10);

        Assert.Equal(2, hits.Count);
        Assert.Equal("https://getakka.net/", hits[0].Url);
        Assert.Equal("https://github.com/akkadotnet/akka.net", hits[1].Url);
    }

    [Fact]
    public void TitlesAndSnippetsLoseTheirMarkup()
    {
        var hits = WebFetcher.ParseResults(Fixture, 10);

        Assert.Equal("Akka.NET", hits[0].Title);
        Assert.Equal("Build powerful & concurrent applications.", hits[0].Snippet);
    }

    [Fact]
    public void SnippetsStayWithTheirOwnResult()
    {
        var hits = WebFetcher.ParseResults(Fixture, 10);
        Assert.Equal("Canonical actor model implementation.", hits[1].Snippet);
    }

    [Fact]
    public void TheCountIsRespected()
    {
        Assert.Single(WebFetcher.ParseResults(Fixture, 1));
    }

    [Fact]
    public void UnparseableMarkupYieldsNothingRatherThanNonsense()
    {
        Assert.Empty(WebFetcher.ParseResults("<html><body>the layout changed</body></html>", 10));
    }

    [Theory]
    [InlineData("//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fa&rut=x", "https://example.com/a")]
    [InlineData("https://example.com/plain", "https://example.com/plain")]
    [InlineData("//example.com/scheme-relative", "https://example.com/scheme-relative")]
    public void RedirectsAreUnwrapped(string href, string expected)
    {
        Assert.Equal(expected, WebFetcher.Unwrap(href));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("//duckduckgo.com/l/?uddg=javascript%3Aalert(1)")]
    [InlineData("not a url at all")]
    public void NonWebSchemesAreRefused(string href)
    {
        Assert.Null(WebFetcher.Unwrap(href));
    }
}

public class CompositeToolbeltTests
{
    private static CompositeToolbelt Build(string root)
    {
        var files = new LocalFileToolbelt(root);
        return new CompositeToolbelt(
            (ToolCatalog.FilesFamily, files),
            (ToolCatalog.EditFamily, files),
            (ToolCatalog.WebFamily, new WebToolbelt(TimeSpan.FromSeconds(5))),
            (ToolCatalog.ExecFamily, new ShellToolbelt(root, TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public async Task EveryCatalogFamilyHasABelt()
    {
        using var belt = Build(Path.GetTempPath());

        foreach (var spec in ToolCatalog.All.Where(t => t.Family != ToolCatalog.LoopFamily))
        {
            var result = await belt.InvokeAsync(new ToolCall { Tool = spec.Name }, CancellationToken.None);

            // Missing arguments are fine here; being unrouted is not.
            Assert.DoesNotContain("unknown tool", result.Text);
            Assert.DoesNotContain("not available in this session", result.Text);
        }
    }

    [Fact]
    public async Task AVerbOutsideTheCatalogIsRefusedWithTheList()
    {
        using var belt = Build(Path.GetTempPath());

        var result = await belt.InvokeAsync(new ToolCall { Tool = "delete_everything" }, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("unknown tool", result.Text);
        Assert.Contains("web_search", result.Text);
    }

    [Fact]
    public async Task AFamilyWithNoBeltSaysSoInsteadOfThrowing()
    {
        using var belt = new CompositeToolbelt(
            (ToolCatalog.FilesFamily, new LocalFileToolbelt(Path.GetTempPath())));

        var result = await belt.InvokeAsync(new ToolCall { Tool = "web_search" }, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("not available", result.Text);
    }

    [Fact]
    public void TheScopeNamesBothHalves()
    {
        using var belt = Build(Path.GetTempPath());

        Assert.Contains("files:", belt.Scope);
        Assert.Contains("web:", belt.Scope);
    }

    [Fact]
    public async Task WebVerbsValidateTheirArguments()
    {
        using var belt = Build(Path.GetTempPath());

        var search = await belt.InvokeAsync(new ToolCall { Tool = "web_search" }, CancellationToken.None);
        Assert.False(search.Ok);
        Assert.Contains("query", search.Text);

        var read = await belt.InvokeAsync(new ToolCall { Tool = "web_read" }, CancellationToken.None);
        Assert.False(read.Ok);
        Assert.Contains("url", read.Text);
    }

    [Fact]
    public async Task ANonWebUrlIsRefusedBeforeAnyRequest()
    {
        using var belt = Build(Path.GetTempPath());

        var result = await belt.InvokeAsync(
            new ToolCall { Tool = "web_read", Args = { ["url"] = "file:///etc/passwd" } },
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("http(s)", result.Text);
    }
}
