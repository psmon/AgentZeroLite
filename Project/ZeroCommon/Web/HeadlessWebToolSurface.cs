using System.Text.Json;
using Agent.Common.Llm.Tools;

namespace Agent.Common.Web;

/// <summary>
/// The web tools without a browser: fetch, extract, remember the page per "tab" so a
/// <c>web_read</c> can follow a <c>web_open</c> the same way it does in the GUI (M0032).
/// Tabs are just the last few pages this process fetched; nothing renders.
/// </summary>
public sealed class HeadlessWebToolSurface : IWebToolSurface
{
    private sealed class Tab
    {
        public int Id;
        public string Url = "";
        public string Title = "";
        public string Html = "";
        public long Opened;
    }

    /// <summary>Text a <c>web_open</c> reply carries before the model decides to read more.</summary>
    public const int OpenSummaryChars = 1500;

    private readonly object _lock = new();
    private readonly Dictionary<int, Tab> _tabs = new();
    private readonly int _maxTabs;
    private int _next = 1;

    public HeadlessWebToolSurface(int maxTabs = 8)
    {
        _maxTabs = Math.Max(1, maxTabs);
    }

    public async Task<string> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return ToolJson.Fail("query must not be empty");
        var url = new Uri(WebSearchParser.BuildDuckDuckGoUrl(query));
        var fetched = await HeadlessWebFetcher.FetchAsync(url, 1_000_000, ct);
        if (!fetched.Ok) return ToolJson.Fail($"search failed: {fetched.Error}");

        var results = WebSearchParser.ParseDuckDuckGo(fetched.Html, maxResults);
        if (results.Count == 0 && WebSearchParser.LooksBlocked(fetched.Html))
            return ToolJson.Fail("the search engine refused the request (bot check); try again later");

        // The click: open the first result now so the reply carries the page, not just
        // a promise of it. A page that will not load simply leaves top_page out.
        System.Text.Json.Nodes.JsonObject? topPage = null;
        var top = results.FirstOrDefault();
        if (top is not null && HeadlessWebFetcher.TryValidate(top.Url, out var topUri, out _))
        {
            var page = await HeadlessWebFetcher.FetchAsync(topUri, HeadlessWebFetcher.DefaultMaxBytes, ct);
            if (page.Ok)
            {
                var content = WebPageExtractor.Extract(page.Html, page.FinalUrl);
                var tab = Remember(page.FinalUrl, page.Html, content.Title, reuseTab: 0);
                topPage = WebSearchParser.TopPage(content, page.FinalUrl, tab);
            }
        }
        return WebSearchParser.ToJson(query, results, topPage);
    }

    /// <summary>Store a fetched page as a tab (0 = new) and return the tab id.</summary>
    private int Remember(string url, string html, string title, int reuseTab)
    {
        lock (_lock)
        {
            Tab target;
            if (reuseTab > 0 && _tabs.TryGetValue(reuseTab, out var existing))
                target = existing;
            else
            {
                if (_tabs.Count >= _maxTabs)
                {
                    var oldest = _tabs.Values.OrderBy(t => t.Opened).First();
                    _tabs.Remove(oldest.Id);
                }
                target = new Tab { Id = _next++ };
                _tabs[target.Id] = target;
            }
            target.Url = url;
            target.Html = html;
            target.Title = title;
            target.Opened = DateTime.UtcNow.Ticks;
            return target.Id;
        }
    }

    public async Task<string> OpenAsync(string url, int tab, CancellationToken ct)
    {
        if (!HeadlessWebFetcher.TryValidate(url, out var uri, out var error)) return ToolJson.Fail(error);
        var fetched = await HeadlessWebFetcher.FetchAsync(uri, HeadlessWebFetcher.DefaultMaxBytes, ct);
        if (!fetched.Ok) return ToolJson.Fail($"could not open {uri}: {fetched.Error}");

        var content = WebPageExtractor.Extract(fetched.Html, fetched.FinalUrl);
        var id = Remember(fetched.FinalUrl, fetched.Html, content.Title, tab);
        return WebPageExtractor.ToJson(content, "summary", null, OpenSummaryChars, fetched.FinalUrl, id);
    }

    public Task<string> ReadAsync(int tab, string? mode, string? find, int maxChars, CancellationToken ct)
    {
        Tab? target;
        lock (_lock)
        {
            target = tab > 0
                ? _tabs.GetValueOrDefault(tab)
                : _tabs.Values.OrderByDescending(t => t.Opened).FirstOrDefault();
        }
        if (target is null)
            return Task.FromResult(ToolJson.Fail(tab > 0 ? $"tab {tab} is not open" : "no page is open; call web_open first"));

        var content = WebPageExtractor.Extract(target.Html, target.Url);
        return Task.FromResult(WebPageExtractor.ToJson(content, mode, find, maxChars, target.Url, target.Id));
    }

    public Task<string> ListTabsAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            var tabs = _tabs.Values.OrderBy(t => t.Id)
                .Select(t => new { tab = t.Id, url = t.Url, title = t.Title }).ToArray();
            return Task.FromResult(JsonSerializer.Serialize(new { ok = true, count = tabs.Length, tabs }, ToolJson.Options));
        }
    }
}
