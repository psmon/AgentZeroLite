using AgentOne.Agent;
using AgentOne.Services;
using AgentOne.Tools;
using AgentOne.Tools.Browser;
using AgentOne.Tools.Cli;
using Xunit.Abstractions;

namespace AgentOne.Tests;

/// <summary>
/// A playwright-cli that answers from a script and records what it was asked —
/// the browser belt's whole contract with the outside world.
/// </summary>
internal sealed class FakeBrowserDriver : IBrowserDriver
{
    public List<string> Calls { get; } = [];
    public bool CdpAlive { get; set; }
    public string? Missing { get; set; }
    public string WorkingDirectory { get; set; } = Path.GetTempPath();

    /// <summary>Answers by the command (the first argument after -s=… and --raw).</summary>
    public Func<string, IReadOnlyList<string>, CliRun>? Answer { get; set; }

    public Task<CliRun> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        Calls.Add(string.Join(' ', args.Skip(1)));
        var verb = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "";
        return Task.FromResult(Answer?.Invoke(verb, args) ?? Default(verb));
    }

    public Task<bool> CdpAliveAsync(string cdpUrl, CancellationToken ct) => Task.FromResult(CdpAlive);

    private static CliRun Default(string verb) => verb switch
    {
        "snapshot" or "tab-new" or "open" or "attach" =>
            new CliRun(0, "### Page\n- Page URL: https://www.youtube.com/@sandy/videos\n- Page Title: Sandy\n### Snapshot\n```yaml\n- link \"Upload\" [ref=e7]\n```", "", false),
        _ => new CliRun(0, "### Result\nok", "", false)
    };

    public bool Ran(string prefix) => Calls.Any(c => c.StartsWith(prefix, StringComparison.Ordinal));
}

public class BrowserToolTests
{
    private const string Cdp = "http://127.0.0.1:9222";

    private static ToolCall Call(string tool, params (string Key, string Value)[] args)
    {
        var call = new ToolCall { Tool = tool };
        foreach (var (k, v) in args) call.Args[k] = v;
        return call;
    }

    [Fact]
    public async Task AttachesToTheUsersChromeAndOpensANewTabRatherThanTakingOverTheirs()
    {
        var driver = new FakeBrowserDriver { CdpAlive = true };
        using var belt = new BrowserToolbelt(driver, Cdp) { Session = "t" };

        var result = await belt.InvokeAsync(Call("browser_open", ("url", "youtube.com/@sandy/videos")), CancellationToken.None);

        Assert.True(result.Ok, result.Text);
        Assert.Equal(BrowserMode.Attached, belt.Mode);
        Assert.Equal("attach --cdp=" + Cdp, driver.Calls[0]);
        Assert.Contains("tab-new https://youtube.com/@sandy/videos", driver.Calls);
        Assert.DoesNotContain(driver.Calls, c => c.StartsWith("open") || c.StartsWith("goto"));
        Assert.Contains("[ref=e7]", result.Text);                    // the tree comes back with the page
        Assert.Contains("DATA written by the site", result.Text);
    }

    [Fact]
    public async Task WithNoDebuggableChromeItOpensItsOwnBrowserOnTheUrl()
    {
        var driver = new FakeBrowserDriver { CdpAlive = false };
        using var belt = new BrowserToolbelt(driver, Cdp) { Session = "t" };

        var result = await belt.InvokeAsync(Call("browser_open", ("url", "https://example.com")), CancellationToken.None);

        Assert.True(result.Ok, result.Text);
        Assert.Equal(BrowserMode.Own, belt.Mode);
        Assert.StartsWith("open https://example.com", driver.Calls[0]);
        Assert.DoesNotContain(driver.Calls, c => c.StartsWith("tab-new"));    // opened ON the url already
        Assert.DoesNotContain(driver.Calls, c => c.StartsWith("attach"));
    }

    [Fact]
    public async Task OffNeverAttachesEvenWhenChromeAnswers()
    {
        var driver = new FakeBrowserDriver { CdpAlive = true };
        using var belt = new BrowserToolbelt(driver, "off") { Session = "t" };

        await belt.InvokeAsync(Call("browser_snapshot"), CancellationToken.None);

        Assert.Equal(BrowserMode.Own, belt.Mode);
        Assert.DoesNotContain(driver.Calls, c => c.StartsWith("attach"));
    }

    [Fact]
    public async Task TheFirstActionOnASiteIsPutToTheGateAndAYesHoldsForThatSite()
    {
        var driver = new FakeBrowserDriver { CdpAlive = true };
        var asked = new List<string>();
        using var belt = new BrowserToolbelt(driver, Cdp)
        {
            Session = "t",
            Gate = (what, site, _) => { asked.Add($"{what} @ {site}"); return Task.FromResult(GateVerdict.Allow()); }
        };

        await belt.InvokeAsync(Call("browser_open", ("url", "https://www.youtube.com/@sandy/videos")), CancellationToken.None);
        var first = await belt.InvokeAsync(Call("browser_click", ("ref", "e7")), CancellationToken.None);
        var second = await belt.InvokeAsync(Call("browser_press", ("key", "End")), CancellationToken.None);

        Assert.True(first.Ok && second.Ok);
        Assert.Equal(["click e7 @ www.youtube.com"], asked);
        Assert.Contains("click e7", driver.Calls);
        Assert.Contains("press End", driver.Calls);
        Assert.Contains("[ref=e7]", first.Text);                     // the page as it now is
    }

    [Fact]
    public async Task ADeclinedOrUngatedActionNeverReachesTheBrowser()
    {
        var driver = new FakeBrowserDriver { CdpAlive = true };
        using var declined = new BrowserToolbelt(driver, Cdp)
        {
            Session = "t",
            Gate = (_, _, _) => Task.FromResult(GateVerdict.Deny("the user declined"))
        };
        var refused = await declined.InvokeAsync(Call("browser_fill", ("ref", "e5"), ("text", "hi"), ("submit", "true")), CancellationToken.None);

        using var ungated = new BrowserToolbelt(driver, Cdp) { Session = "u" };
        var alsoRefused = await ungated.InvokeAsync(Call("browser_click", ("ref", "[ref=e7]")), CancellationToken.None);

        Assert.False(refused.Ok);
        Assert.Contains("declined", refused.Text);
        Assert.False(alsoRefused.Ok);
        Assert.DoesNotContain(driver.Calls, c => c.StartsWith("fill") || c.StartsWith("click"));
    }

    [Fact]
    public async Task FillPassesSubmitAndARefCopiedWholeFromTheSnapshotWorks()
    {
        var driver = new FakeBrowserDriver { CdpAlive = true };
        using var belt = new BrowserToolbelt(driver, Cdp) { Session = "t", Gate = (_, _, _) => Task.FromResult(GateVerdict.Allow()) };

        await belt.InvokeAsync(Call("browser_fill", ("ref", "[ref=e5]"), ("text", "lofi \"beats\""), ("submit", "true")), CancellationToken.None);

        Assert.Contains("fill e5 lofi \"beats\" --submit", driver.Calls);
    }

    [Fact]
    public async Task AnAttachedSessionIsOnlyEverDetachedAndAnOwnOneIsClosed()
    {
        var attachedDriver = new FakeBrowserDriver { CdpAlive = true };
        var attached = new BrowserToolbelt(attachedDriver, Cdp) { Session = "t" };
        await attached.InvokeAsync(Call("browser_tabs"), CancellationToken.None);
        attached.Dispose();

        var ownDriver = new FakeBrowserDriver { CdpAlive = false };
        var own = new BrowserToolbelt(ownDriver, Cdp) { Session = "t" };
        await own.InvokeAsync(Call("browser_tabs"), CancellationToken.None);
        own.Dispose();

        Assert.Equal("detach", attachedDriver.Calls[^1]);
        Assert.DoesNotContain("close", attachedDriver.Calls);
        Assert.Equal("close", ownDriver.Calls[^1]);
    }

    [Fact]
    public async Task ABrowserThatWentAwayIsReconnectedOnce()
    {
        var driver = new FakeBrowserDriver { CdpAlive = true };
        var gone = true;
        driver.Answer = (verb, args) =>
        {
            if (verb == "snapshot" && gone) { gone = false; return new CliRun(1, "The browser 't' is not open, please run open first", "", false); }
            return verb is "snapshot" or "attach" ? new CliRun(0, "### Snapshot\n```yaml\n- button [ref=e2]\n```", "", false) : new CliRun(0, "ok", "", false);
        };
        using var belt = new BrowserToolbelt(driver, Cdp) { Session = "t" };

        await belt.InvokeAsync(Call("browser_tabs"), CancellationToken.None);       // attaches
        var result = await belt.InvokeAsync(Call("browser_snapshot"), CancellationToken.None);

        Assert.True(result.Ok, result.Text);
        Assert.Equal(2, driver.Calls.Count(c => c.StartsWith("attach")));
        Assert.Contains("[ref=e2]", result.Text);
    }

    [Fact]
    public async Task ReadReturnsThePageTextDecodedFromTheJsonString()
    {
        var driver = new FakeBrowserDriver
        {
            CdpAlive = true,
            Answer = (verb, args) => verb == "eval"
                ? new CliRun(0, "\"Sandy - YouTube\\nhttps://www.youtube.com/@sandy\\n\\n3,700년째 답장이 없다\"", "", false)
                : new CliRun(0, "ok", "", false)
        };
        using var belt = new BrowserToolbelt(driver, Cdp) { Session = "t" };

        var result = await belt.InvokeAsync(Call("browser_read"), CancellationToken.None);

        Assert.True(result.Ok, result.Text);
        Assert.StartsWith("Sandy - YouTube\nhttps://www.youtube.com/@sandy\n\n3,700년째 답장이 없다", result.Text);
        Assert.Contains(driver.Calls, c => c.StartsWith("--raw eval"));
        Assert.Equal("https://www.youtube.com/@sandy", belt.CurrentUrl);
    }

    [Fact]
    public async Task WithoutPlaywrightCliTheModelIsToldToAskNotToInstallOrScript()
    {
        var driver = new FakeBrowserDriver { Missing = PlaywrightCliDriver.NotInstalled };
        using var belt = new BrowserToolbelt(driver, Cdp);

        var result = await belt.InvokeAsync(Call("browser_open", ("url", "https://example.com")), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("npm install -g @playwright/cli", result.Text);
        Assert.Contains("do not write a Playwright script", result.Text);
        Assert.Empty(driver.Calls);
    }

    [Fact]
    public void CleanKeepsThePageAndDropsGeneratedCodeEventsAndBanner()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ao-browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, ".playwright-cli"));
        File.WriteAllText(Path.Combine(dir, ".playwright-cli", "page-1.yml"), "- heading \"Videos\" [ref=e3]\n");
        try
        {
            var belt = new BrowserToolbelt(new FakeBrowserDriver { WorkingDirectory = dir }, Cdp);
            var cleaned = belt.Clean(string.Join('\n',
                "╔══════╗", "║ Update available ║", "╚══════╝",
                "### Ran Playwright code", "```js", "await page.goto('x');", "```",
                "### Page", "- Page URL: https://x.test/", "- Console: 17 errors, 16 warnings",
                "### Snapshot", "- [Snapshot](.playwright-cli\\page-1.yml)",
                "### Events", "- New console entries: .playwright-cli\\console.log#L1-L72"));

            Assert.DoesNotContain("Update available", cleaned);
            Assert.DoesNotContain("page.goto", cleaned);
            Assert.DoesNotContain("Console:", cleaned);
            Assert.DoesNotContain("console.log", cleaned);
            Assert.Contains("- Page URL: https://x.test/", cleaned);
            Assert.Contains("- heading \"Videos\" [ref=e3]", cleaned);   // the tree read back from its file
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ACutTreeSaysWhatIsMissingAndHowToGetIt()
    {
        // Measured (2026-11-06): a channel's video list was cut at 16 000 chars and
        // the answer silently left out the oldest history video.
        var dir = Path.Combine(Path.GetTempPath(), "ao-browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "page.yml"), new string('x', BrowserToolbelt.SnapshotChars + 500));
        try
        {
            var belt = new BrowserToolbelt(new FakeBrowserDriver { WorkingDirectory = dir }, Cdp);
            var cleaned = belt.Clean("### Snapshot\n- [Snapshot](page.yml)");

            Assert.Contains("NOT shown", cleaned);
            Assert.Contains("browser_read", cleaned);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=1", "www.youtube.com")]
    [InlineData(null, "(the current page)")]
    [InlineData("about:blank", "(the current page)")]
    public void ASiteIsItsHost(string? url, string site) => Assert.Equal(site, BrowserToolbelt.SiteOf(url));
}

public class RepeatRuleTests
{
    private static ToolCall Call(string tool, string? key = null, string? value = null)
    {
        var call = new ToolCall { Tool = tool };
        if (key is not null) call.Args[key] = value!;
        return call;
    }

    [Fact]
    public void ASecondSnapshotAfterAnActionIsANewLookNotARepeat()
    {
        var guards = new AgentLoopGuards();

        guards.Record(Call("browser_snapshot"));
        guards.Record(Call("browser_click", "ref", "e7"));

        Assert.False(guards.IsRepeat(Call("browser_snapshot")));
    }

    [Fact]
    public void TheSameSnapshotTwiceInARowIsStillARepeat()
    {
        var guards = new AgentLoopGuards();
        guards.Record(Call("browser_snapshot"));
        Assert.True(guards.IsRepeat(Call("browser_snapshot")));
    }

    [Fact]
    public void PressingEndAgainToLoadMoreIsNeverARepeat()
    {
        var guards = new AgentLoopGuards();
        guards.Record(Call("browser_press", "key", "End"));
        Assert.False(guards.IsRepeat(Call("browser_press", "key", "End")));
    }

    [Fact]
    public void FileReadsKeepTheOldRule()
    {
        var guards = new AgentLoopGuards();
        guards.Record(Call("read_file", "path", "a.md"));
        guards.Record(Call("list_files", "path", "."));
        Assert.True(guards.IsRepeat(Call("read_file", "path", "a.md")));
    }
}

public class KnownCliTests
{
    private static IReadOnlyList<KnownCliStatus> Only(params string[] installed) =>
        KnownCli.Detect(cmd => installed.Contains(cmd) ? $"C:/bin/{cmd}.exe" : null);

    [Fact]
    public void ThePromptNamesOnlyWhatIsInstalled()
    {
        var lines = KnownCli.PromptLines(Only("playwright-cli", "ffmpeg"));

        Assert.Contains("playwright-cli", lines);
        Assert.Contains("ffmpeg", lines);
        Assert.DoesNotContain("yt-dlp", lines);
        Assert.Equal("", KnownCli.PromptLines(Only()));
    }

    [Fact]
    public async Task TheGuideGivesTheCardOfAnInstalledTool()
    {
        var belt = new KnownCliToolbelt(Only("ffmpeg"));

        var result = await belt.InvokeAsync(new ToolCall { Tool = "cli_guide", Args = { ["name"] = "ffprobe" } }, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("installed (C:/bin/ffmpeg.exe)", result.Text);
        Assert.Contains("ffprobe -v error", result.Text);
    }

    [Fact]
    public async Task AMissingToolIsReportedWithItsInstallLineNotInstalled()
    {
        var belt = new KnownCliToolbelt(Only());

        var result = await belt.InvokeAsync(new ToolCall { Tool = "cli_guide", Args = { ["name"] = "yt-dlp" } }, CancellationToken.None);

        Assert.Contains("NOT installed", result.Text);
        Assert.Contains("Do not install it yourself", result.Text);
        Assert.Contains("upload_date", result.Text);                 // the card still teaches
    }

    [Fact]
    public async Task NoNameListsEveryKnownToolWithItsState()
    {
        var belt = new KnownCliToolbelt(Only("gh"));
        var result = await belt.InvokeAsync(new ToolCall { Tool = "cli_guide" }, CancellationToken.None);

        Assert.Contains("✓ gh", result.Text);
        Assert.Contains("✗ yt-dlp", result.Text);
    }

    [Fact]
    public async Task AnUnknownToolPointsAtItsOwnHelp()
    {
        var belt = new KnownCliToolbelt(Only());
        var result = await belt.InvokeAsync(new ToolCall { Tool = "cli_guide", Args = { ["name"] = "kubectl" } }, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("kubectl --help", result.Text);
    }

    [Fact]
    public void PlaywrightByAnyNameIsTheCli()
    {
        Assert.Equal("playwright-cli", KnownCli.Find("playwright")?.Name);
        Assert.Equal("playwright-cli", KnownCli.Find("@playwright/cli")?.Name);
    }

    [Fact]
    public void TheLocatorResolvesAnNpmShimThroughPathext()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ao-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var shim = OperatingSystem.IsWindows() ? "tool-x.cmd" : "tool-x";
            File.WriteAllText(Path.Combine(dir, shim), "");
            Assert.Equal(Path.Combine(dir, shim), ExecutableLocator.Find("tool-x", dir));
            Assert.Null(ExecutableLocator.Find("tool-y", dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class BrowserWiringTests
{
    [Fact]
    public void ThePromptSaysTheAgentCanUseABrowserAndMustNotHandItBack()
    {
        var prompt = SystemPrompt.Build("/workspace");

        Assert.Contains("You CAN use a real web browser", prompt);
        Assert.Contains("never ask the", prompt);
        Assert.Contains("never write a Playwright script", prompt);
    }

    [Fact]
    public void BothWorkingRoutesLetTheBrowserIn()
    {
        Assert.Contains(ToolCatalog.BrowserFamily, SmartRouter.FamiliesFor(Route.Web));
        Assert.Contains(ToolCatalog.BrowserFamily, SmartRouter.FamiliesFor(Route.Files));
        Assert.Empty(SmartRouter.FamiliesFor(Route.Answer));
        Assert.True(ToolCatalog.IsRouteExempt(ToolCatalog.GuideFamily));
    }

    [Fact]
    public void EveryBrowserActionIsFreeToRepeatAndEveryLookIsAfterChange()
    {
        foreach (var spec in ToolCatalog.All.Where(t => t.Family == ToolCatalog.BrowserFamily))
        {
            var expected = spec.Name switch
            {
                "browser_click" or "browser_fill" or "browser_press" => RepeatRule.Free,
                "browser_open" => RepeatRule.Once,
                _ => RepeatRule.AfterChange
            };
            Assert.Equal(expected, spec.Repeat);
        }
    }

    [Theory]
    [InlineData("http://127.0.0.1:9223", true)]
    [InlineData("off", true)]
    [InlineData("", true)]
    [InlineData("ws://127.0.0.1:9222", false)]
    [InlineData("9222", false)]
    public void TheCdpUrlIsAnHttpUrlOrOff(string value, bool ok)
    {
        var config = new AgentConfig();
        Assert.Equal(ok, config.TrySet("browserCdpUrl", value, out _));
        if (value.Length == 0) Assert.Equal(AgentConfig.DefaultBrowserCdpUrl, config.BrowserCdpUrl);
    }
}

/// <summary>
/// The real thing: set <c>AGENT_ONE_BROWSER_LIVE=1</c> with playwright-cli on
/// PATH (and, to test the attach path, a Chrome on the CDP port). Opens
/// example.com in a new tab, reads it, closes that tab and detaches. Does
/// nothing when unset.
/// </summary>
public class BrowserLiveTests(ITestOutputHelper output)
{
    [Fact]
    public async Task OpensReadsAndLeaves()
    {
        if (Environment.GetEnvironmentVariable("AGENT_ONE_BROWSER_LIVE") != "1") return;

        var dir = Path.Combine(Path.GetTempPath(), "ao-browser-live");
        var driver = new PlaywrightCliDriver(dir, TimeSpan.FromSeconds(60));
        Assert.Null(driver.Missing);

        var belt = new BrowserToolbelt(driver, AgentConfig.DefaultBrowserCdpUrl) { Session = "ao-live-test" };
        try
        {
            var open = await belt.InvokeAsync(new ToolCall { Tool = "browser_open", Args = { ["url"] = "https://example.com" } }, CancellationToken.None);
            output.WriteLine($"[{belt.Mode}] {open.Text}");
            Assert.True(open.Ok, open.Text);
            Assert.Contains("[ref=", open.Text);

            var read = await belt.InvokeAsync(new ToolCall { Tool = "browser_read" }, CancellationToken.None);
            output.WriteLine(read.Text);
            Assert.Contains("Example Domain", read.Text);

            var tabs = await belt.InvokeAsync(new ToolCall { Tool = "browser_tabs" }, CancellationToken.None);
            output.WriteLine(tabs.Text);
            if (belt.Mode == BrowserMode.Attached)
            {
                // Leave the person's browser as it was: close only the tab this test opened.
                var mine = tabs.Text.Split('\n').FirstOrDefault(l => l.Contains("example.com/") && l.Contains("(current)"));
                var index = mine?.TrimStart('-', ' ').Split(':')[0];
                if (index is not null) await driver.RunAsync(["-s=ao-live-test", "tab-close", index], CancellationToken.None);
            }
        }
        finally
        {
            belt.Dispose();
        }
    }
}
