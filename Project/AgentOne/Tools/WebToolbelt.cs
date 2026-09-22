using System.Text;
using AgentOne.Agent;
using AgentOne.Tools.Web;

namespace AgentOne.Tools;

/// <summary>
/// The web half of the toolbelt: search, and read a page.
///
/// Read-only by construction — it issues GETs and returns text, and there is no
/// verb here that posts, logs in, or carries a credential. That is what makes it
/// safe to ship before the approval gate exists.
///
/// The text it returns is the least trustworthy input in the whole system: it is
/// written by strangers and may contain instructions aimed at the model. It is
/// handed back as data, under a header naming its source, and the system prompt
/// says in as many words that tool output is not an instruction.
/// </summary>
public sealed class WebToolbelt(TimeSpan timeout) : IToolbelt, IDisposable
{
    private readonly WebFetcher _fetcher = new(timeout);

    public string Scope => "the web (read-only: search and fetch)";

    public async Task<ToolResult> InvokeAsync(ToolCall call, CancellationToken ct)
    {
        return call.Tool.ToLowerInvariant() switch
        {
            "web_search" => await SearchAsync(call.Arg("query"), call.Arg("count"), ct),
            "web_read" => await ReadAsync(call.Arg("url"), ct),
            _ => ToolResult.Failure($"unknown tool '{call.Tool}'")
        };
    }

    private async Task<ToolResult> SearchAsync(string query, string countArg, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length == 0) return ToolResult.Failure("web_search needs a 'query' argument");

        var count = int.TryParse(countArg, out var n) ? Math.Clamp(n, 1, 10) : WebFetcher.DefaultResults;

        var (ok, message, hits) = await _fetcher.SearchAsync(query, count, ct);
        if (!ok) return ToolResult.Failure(message);

        var sb = new StringBuilder();
        sb.Append(message).Append(":\n");

        for (int i = 0; i < hits.Count; i++)
        {
            sb.Append('\n').Append(i + 1).Append(". ").AppendLine(hits[i].Title);
            sb.Append("   ").AppendLine(hits[i].Url);
            if (hits[i].Snippet.Length > 0) sb.Append("   ").AppendLine(hits[i].Snippet);
        }

        // Search results are a menu, not an answer — say so, or a small model
        // will summarise the snippets and call it research.
        sb.Append("\n(snippets only — call web_read on a URL above for the actual page)");

        return ToolResult.Success(sb.ToString().TrimEnd());
    }

    private async Task<ToolResult> ReadAsync(string url, CancellationToken ct)
    {
        url = url.Trim();
        if (url.Length == 0) return ToolResult.Failure("web_read needs a 'url' argument");

        var result = await _fetcher.ReadAsync(url, ct);
        return result.Ok ? ToolResult.Success(result.Text) : ToolResult.Failure(result.Message);
    }

    public void Dispose() => _fetcher.Dispose();
}
