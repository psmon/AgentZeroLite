using System.Text.Json;
using Agent.Common.Web;

namespace ZeroCommon.Tests;

/// <summary>DuckDuckGo HTML results → rows (M0032), including the redirect unwrapping.</summary>
[Trait("Category", "Web")]
public sealed class WebSearchParserTests
{
    private const string Fixture = """
        <html><body>
        <div class="result results_links results_links_deep web-result result--ad">
          <h2 class="result__title"><a rel="nofollow" class="result__a" href="https://duckduckgo.com/y.js?ad_provider=x&amp;u3=https%3A%2F%2Fads.example">Sponsored thing</a></h2>
          <a class="result__snippet" href="https://duckduckgo.com/y.js?x">Buy now.</a>
        </div>
        <div class="result results_links results_links_deep web-result">
          <h2 class="result__title"><a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.org%2Fwatch%3Fq%3D1%26b%3D2&amp;rut=abc">Smartwatch <b>host</b> guide</a></h2>
          <a class="result__snippet" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.org%2Fwatch&amp;rut=abc">How a <b>host</b> keeps a BLE link up.</a>
        </div>
        <div class="result">
          <h2 class="result__title"><a class="result__a" href="https://plain.example/page">Plain link</a></h2>
          <a class="result__snippet">Snippet without href.</a>
        </div>
        </body></html>
        """;

    [Fact]
    public void Parses_results_unwraps_redirects_and_skips_ads()
    {
        var results = WebSearchParser.ParseDuckDuckGo(Fixture);
        Assert.Equal(2, results.Count);

        Assert.Equal("Smartwatch host guide", results[0].Title);
        Assert.Equal("https://example.org/watch?q=1&b=2", results[0].Url);
        Assert.Equal("How a host keeps a BLE link up.", results[0].Snippet);

        Assert.Equal("Plain link", results[1].Title);
        Assert.Equal("https://plain.example/page", results[1].Url);
        Assert.Equal("Snippet without href.", results[1].Snippet);
    }

    [Fact]
    public void Max_caps_the_rows()
        => Assert.Single(WebSearchParser.ParseDuckDuckGo(Fixture, 1));

    [Theory]
    [InlineData("//duckduckgo.com/l/?uddg=https%3A%2F%2Fa.example%2Fp&rut=1", "https://a.example/p")]
    [InlineData("https://plain.example/x", "https://plain.example/x")]
    [InlineData("", "")]
    public void Redirect_normalisation(string href, string expected)
        => Assert.Equal(expected, WebSearchParser.NormalizeResultUrl(href));

    [Fact]
    public void Search_url_encodes_the_query()
        => Assert.Equal("https://html.duckduckgo.com/html/?q=%EC%8A%A4%EB%A7%88%ED%8A%B8%EC%9B%8C%EC%B9%98%20host", WebSearchParser.BuildDuckDuckGoUrl(" 스마트워치 host "));

    [Fact]
    public void Json_shape_matches_the_tool_contract()
    {
        var json = WebSearchParser.ToJson("watch", WebSearchParser.ParseDuckDuckGo(Fixture));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("watch", root.GetProperty("query").GetString());
        Assert.Equal(2, root.GetProperty("count").GetInt32());
        Assert.Equal("Smartwatch host guide", root.GetProperty("results")[0].GetProperty("title").GetString());
        Assert.Equal(WebPageExtractor.UntrustedNote, root.GetProperty("source").GetString());
    }

    [Fact]
    public void Top_page_rides_along_with_the_results_and_is_capped()
    {
        var content = WebPageExtractor.Extract("<html><title>T</title><body><p>" + new string('a', 3000) + "</p></body></html>", "https://x.example/");
        var top = WebSearchParser.TopPage(content, "https://x.example/", 4);
        var json = WebSearchParser.ToJson("q", WebSearchParser.ParseDuckDuckGo(Fixture), top);
        using var doc = JsonDocument.Parse(json);
        var page = doc.RootElement.GetProperty("top_page");
        Assert.Equal(4, page.GetProperty("tab").GetInt32());
        Assert.Equal("T", page.GetProperty("title").GetString());
        Assert.Equal(WebSearchParser.TopPageChars, page.GetProperty("text").GetString()!.Length);
        Assert.True(page.GetProperty("truncated").GetBoolean());
        Assert.Contains("hint", json);
    }

    [Fact]
    public void Bot_challenge_pages_are_detected()
    {
        Assert.True(WebSearchParser.LooksBlocked("<form id=\"challenge-form\">"));
        Assert.False(WebSearchParser.LooksBlocked(Fixture));
    }
}
