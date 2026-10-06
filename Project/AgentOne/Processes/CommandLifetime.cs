using System.Text.RegularExpressions;

namespace AgentOne.Processes;

/// <summary>
/// Two questions about a command that the shell cannot answer for us: will it
/// end on its own, and — when it will not — has it come up yet.
///
/// Measured (2026-10-06, board-web): the model wrote a Flask app and ran
/// <c>python app.py</c> as a one-shot command. A dev server never ends, so the
/// step could only end by timeout, and the timeout's kill missed the reloader
/// child. Asking up front — background, smoke run, or not at all — is cheaper
/// than every way of finding out afterwards. A miss here is not fatal: a
/// one-shot command that overruns is put to the person anyway.
/// </summary>
public static partial class CommandLifetime
{
    /// <summary>Markers that make a script a server when it is run directly (<c>python app.py</c>, <c>node server.js</c>).</summary>
    private static readonly string[] ServerMarkers =
    [
        "app.run(", "uvicorn.run(", "serve_forever(", "web.run_app(", "socketio.run(", "waitress.serve(",
        ".listen(", "createServer(", "HttpListener", "app.Run(", "ListenAndServe(",
    ];

    /// <summary>
    /// True when <paramref name="command"/> looks like something that keeps
    /// running: a dev server, a watcher, a follow. <paramref name="root"/> lets
    /// a directly-run script be read for server markers.
    /// </summary>
    public static bool LooksLongRunning(string command, string? root = null)
    {
        if (KnownLongRunning().IsMatch(command)) return true;

        var script = DirectScript().Match(command);
        if (!script.Success || root is null) return false;

        try
        {
            var path = Path.GetFullPath(Path.Combine(root, script.Groups["file"].Value.Trim('"', '\'')));
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 1_000_000) return false;
            var text = File.ReadAllText(path);
            return ServerMarkers.Any(m => text.Contains(m, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether this much of a service's output says it is up, and where. A URL
    /// is taken as ready on its own; a phrase without one is ready with no URL.
    /// 0.0.0.0 and [::] are reported as the loopback address a browser can open.
    /// </summary>
    public static bool DetectReady(string recent, out string? url)
    {
        url = null;
        var match = LocalUrl().Match(recent);
        if (match.Success)
        {
            url = match.Value.TrimEnd('.', ',', ')', ']', '/') ;
            url = url.Replace("://0.0.0.0", "://127.0.0.1").Replace("://[::]", "://127.0.0.1");
            return true;
        }
        return ReadyPhrase().IsMatch(recent);
    }

    [GeneratedRegex(
        @"\bflask\s+run\b|\b(uvicorn|gunicorn|hypercorn|daphne|waitress-serve|nodemon|live-server|http-server)\b" +
        @"|manage\.py\s+runserver|-m\s+http\.server|\bstreamlit\s+run\b|\bjupyter\s+(notebook|lab)\b" +
        @"|\b(npm|pnpm|yarn|bun)\s+(start|dev|serve|run\s+(dev|serve|start|watch))\b" +
        @"|\bnpx\s+(vite|serve|next\s+dev|http-server|live-server|nodemon)\b|^\s*(vite|next\s+dev)\b" +
        @"|\bdotnet\s+watch\b|\bhugo\s+server\b|\bjekyll\s+serve\b|\brails\s+s(erver)?\b|\bphp\s+-S\b" +
        @"|\bdocker(-compose|\s+compose)\s+up\b(?!.*\s-d\b)|\btail\s+-f\b|Get-Content\b.*-Wait\b|\bping\s+-t\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex KnownLongRunning();

    [GeneratedRegex(@"^\s*(python3?|py|node|deno\s+run|bun|ruby|php)\s+(?<file>""[^""]+""|'[^']+'|\S+\.(py|js|mjs|cjs|ts|rb|php))(\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DirectScript();

    [GeneratedRegex(@"https?://(localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1?\]|[\w-]+(\.[\w-]+)*):\d{2,5}[^\s'""<>]*", RegexOptions.IgnoreCase)]
    private static partial Regex LocalUrl();

    [GeneratedRegex(@"running on|listening on|now listening|serving (http|at|on)|ready in \d|application started|server started|started server|compiled successfully|webpack compiled|watching for (file )?changes",
        RegexOptions.IgnoreCase)]
    private static partial Regex ReadyPhrase();
}
