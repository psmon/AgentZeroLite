namespace Agent.Common.Web;

/// <summary>
/// Where the web tools actually browse (M0032). Two implementations: the GUI's Browser page
/// (a WebView2 with tabs the user can see) reached through the <c>-cli web</c> bridge, and
/// <see cref="HeadlessWebToolSurface"/> for when no GUI is running. Every method returns a
/// JSON envelope the agent loop forwards verbatim.
/// </summary>
public interface IWebToolSurface
{
    /// <summary>Search the web; returns <c>{ok, query, results:[{title,url,snippet}]}</c>.</summary>
    Task<string> SearchAsync(string query, int maxResults, CancellationToken ct);

    /// <summary>Open <paramref name="url"/> in tab <paramref name="tab"/> (0 = a new tab) and return a short summary.</summary>
    Task<string> OpenAsync(string url, int tab, CancellationToken ct);

    /// <summary>Read an open tab (0 = the most recent) in <paramref name="mode"/> summary | links | find.</summary>
    Task<string> ReadAsync(int tab, string? mode, string? find, int maxChars, CancellationToken ct);

    /// <summary>The open tabs: <c>{ok, tabs:[{tab,url,title}]}</c>.</summary>
    Task<string> ListTabsAsync(CancellationToken ct);
}

/// <summary>
/// Thrown by a surface that cannot be reached at all (the GUI is not running, its exe is not
/// found) — as opposed to a request it refused, which comes back as an <c>ok:false</c>
/// envelope. The web actor falls back to the headless surface on this and only this.
/// </summary>
public sealed class WebSurfaceUnavailableException : Exception
{
    public WebSurfaceUnavailableException(string message) : base(message) { }
}
