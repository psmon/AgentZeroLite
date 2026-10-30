using System.Text.Json;
using System.Text.Json.Nodes;
using Akka.Actor;
using Agent.Common.Llm.Tools;
using Agent.Common.Mcp;
using Agent.Common.Wearable;
using Xunit;

namespace ZeroCommon.Tests.Wearable;

/// <summary>
/// The watch's share of the app-wide MCP servers: the toolbelt routes <c>server.tool</c> to
/// <see cref="McpHub"/> only when the host hands one in (Wearable page → MCP tools). No server
/// is started here — the hub's allow list answers before any connection is made.
/// </summary>
public sealed class WearableMcpToolbeltTests
{
    private static McpHub Hub() => new(() => new McpSettings
    {
        Servers =
        [
            new McpServerConfig
            {
                Name = "chrome",
                Command = "npx",
                Tools = [new McpToolInfo { Name = "list_pages" }, new McpToolInfo { Name = "evaluate_script" }],
                DisabledTools = ["evaluate_script"],
            },
        ],
    });

    /// <summary>A chrome-devtools-mcp entry; <paramref name="cdp"/> null = it launches its own Chrome.</summary>
    private static McpHub BrowserHub(string? cdp) => new(() => new McpSettings
    {
        Servers =
        [
            new McpServerConfig
            {
                Name = "chrome",
                Command = "npx",
                Args = cdp is null ? ["-y", "chrome-devtools-mcp@latest"] : ["-y", "chrome-devtools-mcp@latest", "--browser-url", cdp],
                Tools = [new McpToolInfo { Name = "new_page" }, new McpToolInfo { Name = "select_page" }, new McpToolInfo { Name = "take_snapshot" }],
            },
        ],
    });

    private static string Error(string json) => JsonDocument.Parse(json).RootElement.GetProperty("error").GetString()!;

    [Fact]
    public async Task Without_a_hub_an_mcp_call_is_refused_as_turned_off()
    {
        var belt = new WearableToolbelt(ActorRefs.Nobody, ActorRefs.Nobody);

        var reply = await belt.CallDynamicToolAsync("chrome.list_pages", new JsonObject(), CancellationToken.None);

        Assert.Contains("turned off for the watch", Error(reply));
    }

    [Fact]
    public async Task With_a_hub_the_call_goes_through_its_allow_list()
    {
        await using var hub = Hub();
        var belt = new WearableToolbelt(ActorRefs.Nobody, ActorRefs.Nobody, mcp: hub);

        var reply = await belt.CallDynamicToolAsync("chrome.evaluate_script", new JsonObject(), CancellationToken.None);

        Assert.Contains("is not allowed", Error(reply));
    }

    [Fact]
    public void Mcp_tools_are_on_by_default_and_survive_a_round_trip()
    {
        Assert.True(new WearableSettings().McpToolsEnabled);

        var loaded = JsonSerializer.Deserialize<WearableSettings>(JsonSerializer.Serialize(new WearableSettings { McpToolsEnabled = false }))!;

        Assert.False(loaded.McpToolsEnabled);
    }

    // ── A controllable browser comes before os_launch ─────────────────────────

    [Fact]
    public void Browser_is_found_from_tool_names_chrome_devtools_first_then_playwright()
    {
        var chrome = McpBrowser.Find([new DynamicToolSpec("memorizer.search", null, null),
            new DynamicToolSpec("chrome.new_page", null, null), new DynamicToolSpec("chrome.select_page", null, null)])!;
        Assert.Equal("chrome", chrome.Server);
        Assert.Equal("chrome.new_page", chrome.NewTab);
        Assert.Equal("chrome.select_page", chrome.Front);

        var pw = McpBrowser.Find([new DynamicToolSpec("playwright.browser_navigate", null, null),
            new DynamicToolSpec("playwright.browser_snapshot", null, null)])!;
        Assert.Equal("playwright.browser_navigate", pw.Navigate);
        Assert.Equal("playwright.browser_snapshot", pw.Snapshot);

        Assert.Null(McpBrowser.Find([new DynamicToolSpec("memorizer.search", null, null)]));
    }

    [Theory]
    [InlineData("chrome", true)]
    [InlineData("Chrome.exe", true)]
    [InlineData("Google Chrome", true)]
    [InlineData("whale", true)]
    [InlineData("notepad", false)]
    [InlineData("", false)]
    public void Browser_programs_are_recognised(string program, bool expected)
        => Assert.Equal(expected, McpBrowser.IsBrowserProgram(program));

    // A full Windows path only splits on '\' where it is a separator.
    [SkippableFact]
    public void Browser_programs_are_recognised_by_full_windows_path()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows path semantics");
        Assert.True(McpBrowser.IsBrowserProgram(@"C:\Program Files\Microsoft\Edge\msedge.exe"));
    }

    [SkippableFact]
    public void Browser_programs_are_recognised_by_full_posix_path()
    {
        Skip.If(OperatingSystem.IsWindows(), "POSIX path semantics");
        Assert.True(McpBrowser.IsBrowserProgram("/Applications/Microsoft Edge.app/Contents/MacOS/msedge"));
    }

    [Fact]
    public void Cdp_endpoint_is_read_from_either_servers_flag()
    {
        Assert.Equal("http://127.0.0.1:9222", McpBrowser.CdpEndpoint(new McpServerConfig { Args = ["chrome-devtools-mcp", "--browser-url", "http://127.0.0.1:9222"] }));
        Assert.Equal("http://h:1", McpBrowser.CdpEndpoint(new McpServerConfig { Args = ["@playwright/mcp", "--cdp-endpoint=http://h:1"] }));
        Assert.Null(McpBrowser.CdpEndpoint(new McpServerConfig { Args = ["chrome-devtools-mcp"] }));
    }

    [Fact]
    public void The_watch_prompt_carries_the_browser_rule_only_when_a_browser_is_configured()
    {
        var browser = new McpBrowser("chrome", "chrome.new_page", "chrome.navigate_page", "chrome.take_snapshot", "chrome.select_page");

        var with = WearablePromptFrame.Frame("크롬 열어줘", "ko", browser);
        var without = WearablePromptFrame.Frame("크롬 열어줘", "ko");

        Assert.Contains("chrome.new_page", with);
        Assert.Contains("Never start a browser with os_launch", with);
        Assert.DoesNotContain("os_launch: that one", without);
        Assert.EndsWith("크롬 열어줘", with.TrimEnd());
    }

    [Fact]
    public async Task Launching_a_browser_is_refused_while_the_mcp_browser_is_configured()
    {
        await using var hub = BrowserHub(cdp: null);   // launches its own Chrome: always available
        var belt = new WearableToolbelt(ActorRefs.Nobody, ActorRefs.Nobody, mcp: hub);

        var reply = await belt.OsLaunchAsync("chrome", null, CancellationToken.None);

        Assert.Contains("chrome.new_page", Error(reply));
        Assert.Contains("cannot be controlled", Error(reply));
    }

    [Fact]
    public async Task A_browser_launch_goes_ahead_when_the_cdp_endpoint_is_down()
    {
        // Port 9 (discard) has nothing listening: the probe fails fast and the launch is not refused —
        // it reaches the os surface, which this belt was built without.
        await using var hub = BrowserHub(cdp: "http://127.0.0.1:9");
        var belt = new WearableToolbelt(ActorRefs.Nobody, ActorRefs.Nobody, mcp: hub);

        var reply = await belt.OsLaunchAsync("chrome", null, CancellationToken.None);

        Assert.Contains("os tools are not wired", Error(reply));
    }

    [Fact]
    public async Task Other_programs_are_never_held_back_by_the_browser_rule()
    {
        await using var hub = BrowserHub(cdp: null);
        var belt = new WearableToolbelt(ActorRefs.Nobody, ActorRefs.Nobody, mcp: hub);

        var reply = await belt.OsLaunchAsync("notepad", null, CancellationToken.None);

        Assert.Contains("os tools are not wired", Error(reply));
    }
}
