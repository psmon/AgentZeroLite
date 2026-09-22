using System.Net;
using System.Text.RegularExpressions;

namespace AgentOne.Tools.Web;

/// <param name="Title">The link text.</param>
/// <param name="Url">The real destination, with the search engine's redirect unwrapped.</param>
public sealed record SearchHit(string Title, string Url, string Snippet);

/// <param name="Ok">False for a refusal or a failed fetch; Message then says why.</param>
public sealed record WebResult(bool Ok, string Message, string Text = "");

/// <summary>
/// The agent's window onto the web: a keyless search and a page reader.
///
/// Search goes through DuckDuckGo's HTML endpoint because it needs no API key
/// and no account — the tool works the moment agent-one is installed. The cost
/// is that it parses someone's markup, so a layout change degrades to "no
/// results parsed" rather than to wrong results, and the message says which.
/// </summary>
public sealed partial class WebFetcher : IDisposable
{
    public const int MaxDownloadBytes = 4 * 1024 * 1024;
    public const int MaxTextChars = 24_000;
    public const int MaxSnippetChars = 300;
    public const int DefaultResults = 5;

    private readonly HttpClient _http;

    public WebFetcher(TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        _http = handler is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
            : new HttpClient(handler, disposeHandler: true);

        _http.Timeout = timeout;

        // Identify honestly. A site that wants to refuse an agent should be able to.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("agent-one/1.0 (+https://github.com/psmon/AgentZeroLite)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain;q=0.9,*/*;q=0.8");
    }

    // ------------------------------------------------------------------ search

    [GeneratedRegex(@"<a[^>]+class=""result__a""[^>]*href=""([^""]+)""[^>]*>(.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ResultLink();

    [GeneratedRegex(@"<a[^>]+class=""result__snippet""[^>]*>(.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ResultSnippet();

    public async Task<(bool Ok, string Message, IReadOnlyList<SearchHit> Hits)> SearchAsync(
        string query, int max, CancellationToken ct)
    {
        var url = "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query);

        var page = await GetTextAsync(url, ct);
        if (!page.Ok) return (false, page.Message, []);

        var hits = ParseResults(page.Text, max);

        return hits.Count == 0
            ? (false, $"no results parsed for '{query}' — the search page returned {page.Text.Length} bytes but nothing matched", [])
            : (true, $"{hits.Count} results for '{query}'", hits);
    }

    /// <summary>Exposed for tests: parsing someone else's markup deserves fixtures.</summary>
    internal static List<SearchHit> ParseResults(string html, int max)
    {
        var links = ResultLink().Matches(html);
        var snippets = ResultSnippet().Matches(html);

        var hits = new List<SearchHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < links.Count && hits.Count < max; i++)
        {
            var href = WebUtility.HtmlDecode(links[i].Groups[1].Value);
            var target = Unwrap(href);
            if (target is null || !seen.Add(target)) continue;

            // The snippet that belongs to this link is the first one after it.
            var snippet = "";
            foreach (Match s in snippets)
            {
                if (s.Index <= links[i].Index) continue;
                snippet = HtmlText.Strip(s.Groups[1].Value);
                break;
            }

            hits.Add(new SearchHit(
                HtmlText.Strip(links[i].Groups[2].Value),
                target,
                snippet.Length > MaxSnippetChars ? snippet[..MaxSnippetChars] + "…" : snippet));
        }

        return hits;
    }

    /// <summary>
    /// DuckDuckGo hands back its own redirect (<c>/l/?uddg=…</c>). The model
    /// should be given the real destination — it is what it will be asked to
    /// read next, and what a person would check.
    /// </summary>
    internal static string? Unwrap(string href)
    {
        if (href.StartsWith("//", StringComparison.Ordinal)) href = "https:" + href;
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) return null;

        if (uri.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.StartsWith("/l/", StringComparison.Ordinal))
        {
            foreach (var part in uri.Query.TrimStart('?').Split('&'))
            {
                if (!part.StartsWith("uddg=", StringComparison.Ordinal)) continue;

                var real = Uri.UnescapeDataString(part[5..]);
                return Uri.TryCreate(real, UriKind.Absolute, out var parsed) && IsWebScheme(parsed)
                    ? parsed.ToString()
                    : null;
            }
            return null;
        }

        return IsWebScheme(uri) ? uri.ToString() : null;
    }

    private static bool IsWebScheme(Uri uri) =>
        uri.Scheme is "http" or "https";

    // -------------------------------------------------------------------- read

    /// <summary>Fetches a page and returns its readable text.</summary>
    public async Task<WebResult> ReadAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsWebScheme(uri))
            return new WebResult(false, $"not an http(s) URL: {url}");

        var page = await GetTextAsync(uri.ToString(), ct);
        if (!page.Ok) return page;

        var title = HtmlText.Title(page.Text);
        var body = HtmlText.Extract(page.Text);

        if (body.Length == 0)
            return new WebResult(false, $"{uri} returned no readable text ({page.Text.Length} bytes of markup)");

        var header = title.Length > 0 ? $"{title}\n{uri}\n\n" : $"{uri}\n\n";
        return new WebResult(true, $"read {uri}", header + HtmlText.Cap(body, MaxTextChars));
    }

    /// <summary>One place where every network failure becomes a sentence.</summary>
    private async Task<WebResult> GetTextAsync(string url, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new WebResult(false, $"timed out fetching {url}");
        }
        catch (HttpRequestException ex)
        {
            return new WebResult(false, $"cannot fetch {url} — {ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return new WebResult(false, $"HTTP {(int)response.StatusCode} from {url}");

            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (type.Length > 0 && !type.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                               && !type.Contains("html", StringComparison.OrdinalIgnoreCase)
                               && !type.Contains("json", StringComparison.OrdinalIgnoreCase)
                               && !type.Contains("xml", StringComparison.OrdinalIgnoreCase))
            {
                return new WebResult(false, $"{url} is {type}, not text — nothing to read");
            }

            try
            {
                // Read with a hard cap: a model's context is not the place to
                // discover that a URL served a 300 MB file.
                using var stream = await response.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[MaxDownloadBytes];
                var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);

                return new WebResult(true, "ok", System.Text.Encoding.UTF8.GetString(buffer, 0, read));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                return new WebResult(false, $"failed reading {url} — {ex.Message}");
            }
        }
    }

    public void Dispose() => _http.Dispose();
}
