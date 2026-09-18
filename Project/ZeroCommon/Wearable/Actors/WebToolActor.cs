using System.Text.Json.Nodes;
using Akka.Actor;
using Akka.Event;
using Agent.Common.Llm.Tools;
using Agent.Common.Web;

namespace Agent.Common.Wearable.Actors;

/// <summary>
/// The web side of the watch's toolbelt as an actor (M0032). Each request is tried on the
/// GUI browser first — the user sees the tab open — and falls back to a headless fetch only
/// when the GUI cannot be reached at all (<see cref="WebSurfaceUnavailableException"/>).
/// The work runs on the thread pool and is piped back, so a slow site never holds this
/// mailbox, and the reply says <c>via: gui | headless</c> so the watch's answer can be
/// traced to what actually happened.
/// </summary>
public sealed class WebToolActor : ReceiveActor
{
    public sealed record Search(string Query, int MaxResults);
    public sealed record Open(string Url, int Tab);
    public sealed record Read(int Tab, string? Mode, string? Find, int MaxChars);
    public sealed record Tabs;

    /// <summary>Longer than a page load, shorter than the watch's patience.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private readonly ILoggingAdapter _log = Context.GetLogger();
    private readonly IWebToolSurface? _gui;
    private readonly IWebToolSurface _headless;
    private readonly bool _enabled;
    private readonly int _defaultMaxChars;

    public WebToolActor(IWebToolSurface? gui, IWebToolSurface headless, bool enabled, int defaultMaxChars)
    {
        _gui = gui;
        _headless = headless;
        _enabled = enabled;
        _defaultMaxChars = defaultMaxChars > 0 ? defaultMaxChars : WebPageExtractor.DefaultMaxChars;

        Receive<Search>(m => Run("web_search", async (s, ct) =>
            WithWeather(await s.SearchAsync(m.Query, m.MaxResults, ct), await WeatherAsync(m.Query, ct))));
        Receive<Open>(m => Run("web_open", (s, ct) => s.OpenAsync(m.Url, m.Tab, ct)));
        Receive<Read>(m => Run("web_read", (s, ct) => s.ReadAsync(m.Tab, m.Mode, m.Find,
            m.MaxChars > 0 ? Math.Min(m.MaxChars, _defaultMaxChars) : _defaultMaxChars, ct)));
        Receive<Tabs>(_ => Run("web_tabs", (s, ct) => s.ListTabsAsync(ct)));
    }

    private void Run(string tool, Func<IWebToolSurface, CancellationToken, Task<string>> op)
    {
        var sender = Sender;
        if (!_enabled)
        {
            sender.Tell(ToolJson.Fail("web tools are turned off in the Wearable settings"), Self);
            return;
        }

        var gui = _gui;
        var headless = _headless;
        var log = _log;
        Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(RequestTimeout);
            if (gui is not null)
            {
                try
                {
                    var viaGui = Tag(await op(gui, cts.Token), "gui");
                    log.Info("{0} via gui: {1}", tool, Describe(viaGui));
                    return viaGui;
                }
                catch (WebSurfaceUnavailableException ex)
                {
                    log.Debug("{0}: GUI browser unavailable ({1}); using the headless fetch", tool, ex.Message);
                }
                catch (OperationCanceledException)
                {
                    return ToolJson.Fail($"{tool} timed out waiting for the browser");
                }
                catch (Exception ex)
                {
                    log.Warning("{0} via GUI threw: {1}", tool, ex.Message);
                }
            }
            try
            {
                var viaHeadless = Tag(await op(headless, cts.Token), "headless");
                log.Info("{0} via headless: {1}", tool, Describe(viaHeadless));
                return viaHeadless;
            }
            catch (OperationCanceledException)
            {
                return ToolJson.Fail($"{tool} timed out");
            }
            catch (Exception ex)
            {
                return ToolJson.Fail(ex.Message);
            }
        }).PipeTo(sender, Self);
    }

    /// <summary>
    /// A weather question gets the numbers, not a page about them: search engines answer
    /// it with JavaScript dashboards a headless fetch cannot read, so alongside the search
    /// the actor asks wttr.in and attaches a <c>weather</c> block (see <see cref="WeatherLookup"/>).
    /// Any failure just leaves the block out.
    /// </summary>
    private static async Task<JsonObject?> WeatherAsync(string query, CancellationToken ct)
    {
        if (!WeatherLookup.IsWeatherQuery(query)) return null;
        try
        {
            var place = WeatherLookup.ExtractPlace(query);
            var fetched = await HeadlessWebFetcher.FetchAsync(new Uri(WeatherLookup.BuildUrl(place)), 400_000, ct);
            return fetched.Ok ? WeatherLookup.Summarize(fetched.Html, place) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string WithWeather(string searchJson, JsonObject? weather)
    {
        if (weather is null) return searchJson;
        try
        {
            var node = JsonNode.Parse(searchJson)!.AsObject();
            node["weather"] = weather;
            node["hint"] = "weather holds the current conditions: answer from it directly (temperature, condition, rain chance)";
            return node.ToJsonString(ToolJson.Options);
        }
        catch
        {
            return searchJson;
        }
    }

    private static string Tag(string json, string via)
    {
        try
        {
            var node = JsonNode.Parse(json)!.AsObject();
            node["via"] = via;
            return node.ToJsonString(ToolJson.Options);
        }
        catch
        {
            return json;
        }
    }

    /// <summary>
    /// One host-log line per web call — what the model was handed, not what it said. "The
    /// watch answered vaguely" is diagnosed here first: an empty top_page means the site
    /// gave the headless fetch nothing, a full one means the model ignored it.
    /// </summary>
    private static string Describe(string json)
    {
        try
        {
            var node = JsonNode.Parse(json)!.AsObject();
            if (node["ok"]?.GetValue<bool>() != true) return $"ok:false {node["error"]}";
            var parts = new List<string>();
            if (node["count"] is { } count) parts.Add($"{count} results");
            if (node["weather"] is JsonObject w) parts.Add($"weather {w["location"]} {w["temp_c"]}°C {w["condition"]}");
            if (node["top_page"] is JsonObject top)
                parts.Add($"top_page \"{top["title"]}\" {top["text"]?.GetValue<string>().Length ?? 0} chars ({top["url"]})");
            if (node["text"] is JsonValue text) parts.Add($"text {text.GetValue<string>().Length} chars");
            if (node["title"] is { } title && node["top_page"] is null) parts.Add($"\"{title}\"");
            return string.Join(", ", parts);
        }
        catch
        {
            return $"{json.Length} chars";
        }
    }
}
