using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agent.Common.Llm.Tools;

namespace Agent.Common.Web;

public sealed record WebSearchResult(string Title, string Url, string Snippet);

/// <summary>
/// Turns a DuckDuckGo HTML results page into <c>{title, url, snippet}</c> rows (M0032).
/// The HTML endpoint is used because it needs no API key and no JavaScript, which is what
/// lets the headless fallback and the GUI's WebView2 share this one parser. Result links
/// are redirect URLs (<c>/l/?uddg=…</c>); <see cref="NormalizeResultUrl"/> unwraps them so
/// the model sees, and can open, the real address.
/// </summary>
public static class WebSearchParser
{
    public const string DuckDuckGoHtmlEndpoint = "https://html.duckduckgo.com/html/";

    public static string BuildDuckDuckGoUrl(string query)
        => DuckDuckGoHtmlEndpoint + "?q=" + Uri.EscapeDataString(query.Trim());

    private static readonly Regex AnchorRx = new(@"<a\b([^>]*)>(.*?)</a\s*>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HrefRx = new(@"\bhref\s*=\s*[""']([^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ClassRx = new(@"\bclass\s*=\s*[""']([^""']*)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<WebSearchResult> ParseDuckDuckGo(string? html, int max = 8)
    {
        var results = new List<WebSearchResult>();
        if (string.IsNullOrEmpty(html)) return results;
        max = Math.Clamp(max, 1, 25);

        string? pendingTitle = null, pendingUrl = null;
        foreach (Match m in AnchorRx.Matches(html))
        {
            var attrs = m.Groups[1].Value;
            var cls = ClassRx.Match(attrs).Groups[1].Value;

            if (cls.Contains("result__a", StringComparison.Ordinal))
            {
                Flush();
                var href = HrefRx.Match(attrs).Groups[1].Value;
                var url = NormalizeResultUrl(href);
                if (url.Contains("duckduckgo.com/y.js", StringComparison.OrdinalIgnoreCase)) continue;   // ad slot
                pendingUrl = url;
                pendingTitle = WebPageExtractor.Clean(m.Groups[2].Value);
            }
            else if (cls.Contains("result__snippet", StringComparison.Ordinal) && pendingUrl is not null)
            {
                results.Add(new WebSearchResult(pendingTitle ?? "", pendingUrl, WebPageExtractor.Clean(m.Groups[2].Value)));
                pendingTitle = pendingUrl = null;
            }
            if (results.Count >= max) break;
        }
        Flush();
        return results.Count > max ? results.GetRange(0, max) : results;

        void Flush()
        {
            if (pendingUrl is null) return;
            results.Add(new WebSearchResult(pendingTitle ?? "", pendingUrl, ""));
            pendingTitle = pendingUrl = null;
        }
    }

    /// <summary>Unwrap DuckDuckGo's redirect (<c>//duckduckgo.com/l/?uddg=&lt;url&gt;&amp;rut=…</c>) to the target.</summary>
    public static string NormalizeResultUrl(string? href)
    {
        var h = WebUtility.HtmlDecode(href ?? "").Trim();
        if (h.StartsWith("//")) h = "https:" + h;
        var idx = h.IndexOf("uddg=", StringComparison.Ordinal);
        if (idx >= 0)
        {
            var value = h[(idx + 5)..];
            var amp = value.IndexOf('&');
            if (amp >= 0) value = value[..amp];
            h = WebUtility.UrlDecode(value);
        }
        return h;
    }

    /// <summary>True when the page is a bot challenge rather than results.</summary>
    public static bool LooksBlocked(string? html)
        => !string.IsNullOrEmpty(html) &&
           (html.Contains("anomaly", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("challenge-form", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("captcha", StringComparison.OrdinalIgnoreCase));

    /// <summary>Text a search reply carries from the first result's page — the "click" done for the model.</summary>
    public const int TopPageChars = 1500;

    /// <param name="topPage">
    /// The first result, already opened and reduced (M0032 follow-up #1): a small model
    /// reliably answers from text it was handed and unreliably decides to go and get it,
    /// so the search does the click. Null when nothing could be opened.
    /// </param>
    public static string ToJson(string query, IReadOnlyList<WebSearchResult> results, JsonObject? topPage = null)
    {
        var o = new JsonObject
        {
            ["ok"] = true,
            ["query"] = query,
            ["count"] = results.Count,
        };
        var arr = new JsonArray();
        foreach (var r in results)
            arr.Add(new JsonObject { ["title"] = r.Title, ["url"] = r.Url, ["snippet"] = r.Snippet });
        o["results"] = arr;
        if (topPage is not null)
        {
            o["top_page"] = topPage;
            o["hint"] = "top_page is the first result, already opened; answer from it when it holds the fact, web_open another result only if it does not";
        }
        o["source"] = WebPageExtractor.UntrustedNote;
        return o.ToJsonString(ToolJson.Options);
    }

    /// <summary>Builds the <c>top_page</c> object from an opened page.</summary>
    public static JsonObject TopPage(WebPageContent content, string url, int? tab)
    {
        var text = string.Join("\n", content.Paragraphs);
        var truncated = text.Length > TopPageChars;
        if (truncated) text = text[..TopPageChars];
        var o = new JsonObject();
        if (tab is { } id) o["tab"] = id;
        o["url"] = url;
        o["title"] = content.Title;
        o["text"] = text;
        o["truncated"] = truncated;
        return o;
    }
}
