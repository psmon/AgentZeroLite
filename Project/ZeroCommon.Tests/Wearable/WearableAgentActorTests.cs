using System.IO;
using System.Text.Json;
using Akka.Actor;
using Akka.TestKit.Xunit2;
using Agent.Common.Llm.Tools;
using Agent.Common.Wearable;
using Agent.Common.Wearable.Actors;
using Agent.Common.Web;

namespace ZeroCommon.Tests.Wearable;

/// <summary>
/// The watch's agent subtree, headless (M0032): message routing between the device
/// gateway and the loop, and the two tool actors that run beside the loops. The LLM is
/// out of scope — <c>AgentLoopFactory</c> returns null, which makes <c>AgentLoopActor</c>
/// report "Backend not ready" — so what is proven here is the actor contract, not
/// generation.
/// </summary>
public sealed class WearableAgentActorTests : TestKit
{
    private static Props FilesProps(AllowedRootResolver roots, Func<string, System.Diagnostics.Process?>? launch = null,
        Func<bool>? stopFallback = null)
        => Props.Create(() => new FileToolActor(roots, launch, stopFallback, null));

    private static Props WebProps(IWebToolSurface? gui, IWebToolSurface headless, bool enabled = true)
        => Props.Create(() => new WebToolActor(gui, headless, enabled, 6000));

    private IActorRef AgentWithNoLlm(string name = "agent")
    {
        var roots = new AllowedRootResolver(null);
        var filesProps = FilesProps(roots);
        var webProps = WebProps(null, new FakeSurface("headless"));
        Func<IActorRef, IActorRef, Agent.Common.Actors.AgentLoopBindings> bindings = (files, web) => new(
            ToolbeltFactory: () => new WearableToolbelt(files, web),
            OptionsFactory: () => new AgentLoopOptions(),
            AgentLoopFactory: (_, _) => null);
        return Sys.ActorOf(Props.Create(() => new WearableAgentActor(bindings, filesProps, webProps, null)), name);
    }

    // ── WearableAgentActor ────────────────────────────────────────────────────

    [Fact]
    public void Ask_is_answered_to_the_sender_with_the_loop_result()
    {
        var agent = AgentWithNoLlm();

        agent.Tell(new WearableAgentActor.Ask("askbot-dev-1", 7, "hello", "ko"), TestActor);

        var answer = ExpectMsg<WearableAgentActor.Answer>(TimeSpan.FromSeconds(3));
        Assert.Equal("askbot-dev-1", answer.Session);
        Assert.Equal(7, answer.RequestId);
        Assert.False(answer.Success);
        Assert.Equal("Backend not ready", answer.FailureReason);
    }

    [Fact]
    public async Task Sessions_are_child_actors_and_forget_stops_them()
    {
        var agent = AgentWithNoLlm("agent-forget");
        agent.Tell(new WearableAgentActor.Ask("askbot-dev-1", 1, "a", null), TestActor);
        ExpectMsg<WearableAgentActor.Answer>(TimeSpan.FromSeconds(3));
        agent.Tell(new WearableAgentActor.Ask("askbot-other-1", 1, "b", null), TestActor);
        ExpectMsg<WearableAgentActor.Answer>(TimeSpan.FromSeconds(3));

        var loop = await Sys.ActorSelection("/user/agent-forget/session-askbot-dev-1")
            .ResolveOne(TimeSpan.FromSeconds(2));
        Watch(loop);

        agent.Tell(new WearableAgentActor.ForgetSessions("askbot-dev-"), TestActor);

        ExpectTerminated(loop, TimeSpan.FromSeconds(3));
        agent.Tell(new Agent.Common.Actors.Ping(), TestActor);
        var pong = ExpectMsg<Agent.Common.Actors.Pong>(TimeSpan.FromSeconds(2));
        Assert.Contains("sessions=1", pong.Status);
    }

    [Fact]
    public async Task Tool_actors_live_under_the_agent()
    {
        var agent = AgentWithNoLlm("agent-tree");
        // A Pong proves PreStart ran, i.e. the children exist, before we look them up.
        agent.Tell(new Agent.Common.Actors.Ping(), TestActor);
        ExpectMsg<Agent.Common.Actors.Pong>(TimeSpan.FromSeconds(2));

        var files = await Sys.ActorSelection("/user/agent-tree/files").ResolveOne(TimeSpan.FromSeconds(2));
        var web = await Sys.ActorSelection("/user/agent-tree/web").ResolveOne(TimeSpan.FromSeconds(2));
        Assert.EndsWith("/files", files.Path.ToString());
        Assert.EndsWith("/web", web.Path.ToString());
    }

    [Fact]
    public void Tool_turns_reach_the_sender_as_Acting_progress_before_the_answer()
    {
        var roots = new AllowedRootResolver(null);
        var filesProps = FilesProps(roots);
        var webProps = WebProps(null, new FakeSurface("headless"));
        Func<IActorRef, IActorRef, Agent.Common.Actors.AgentLoopBindings> bindings = (files, web) => new(
            ToolbeltFactory: () => new WearableToolbelt(files, web),
            OptionsFactory: () => new AgentLoopOptions(),
            AgentLoopFactory: (opts, _) => new FakeLoop(opts));
        var agent = Sys.ActorOf(Props.Create(() => new WearableAgentActor(bindings, filesProps, webProps, null)), "agent-progress");

        agent.Tell(new WearableAgentActor.Ask("s", 3, "play something", null), TestActor);

        var seen = new List<object>();
        for (var i = 0; i < 6; i++)
        {
            var msg = ReceiveOne(TimeSpan.FromSeconds(3));
            if (msg is null) break;
            seen.Add(msg);
            if (msg is WearableAgentActor.Answer) break;
        }
        var acting = seen.OfType<WearableAgentActor.Progress>().FirstOrDefault(p => p.Phase == Agent.Common.Actors.AgentLoopPhase.Acting);
        Assert.True(acting is not null, "no Acting progress; saw: " + string.Join(", ",
            seen.Select(m => m is WearableAgentActor.Progress p ? $"Progress({p.Phase}:{p.Text})" : m.GetType().Name)));
        Assert.Equal("open_file", acting!.Text);
        Assert.Equal(3, acting.RequestId);
        var answer = Assert.IsType<WearableAgentActor.Answer>(seen.Last());
        Assert.True(answer.Success);
        Assert.Equal("done!", answer.Text);
    }

    /// <summary>One tool turn, one done — enough to see the progress callbacks travel.</summary>
    private sealed class FakeLoop(AgentLoopOptions opts) : IAgentLoop
    {
        public int UserSendCount => 1;

        public Task<AgentLoopRun> RunAsync(string userRequest, CancellationToken ct = default)
        {
            var call = new ToolCall("open_file", new System.Text.Json.Nodes.JsonObject { ["path"] = "music/x.mp3" });
            var turn = new ToolTurn(call, """{"ok":true}""");
            opts.OnTurnCompleted?.Invoke(turn);
            return Task.FromResult(new AgentLoopRun([turn], "done!", true, null));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ── FileToolActor ─────────────────────────────────────────────────────────

    [Fact]
    public void FileToolActor_reads_through_the_allow_list_and_answers_alias_paths()
    {
        using var temp = new TempRoot();
        File.WriteAllText(Path.Combine(temp.Path, "hello.txt"), "hi from the docs folder");
        var roots = new AllowedRootResolver([new AllowedRoot { Alias = "docs", Path = temp.Path }]);
        var files = Sys.ActorOf(FilesProps(roots), "files-read");

        files.Tell(new FileToolActor.Read("docs/hello.txt", 1000), TestActor);
        var json = ExpectMsg<string>(TimeSpan.FromSeconds(3));

        var root = JsonDocument.Parse(json).RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean(), json);
        Assert.Equal("docs/hello.txt", root.GetProperty("path").GetString());
        Assert.DoesNotContain(temp.Path, json);
    }

    [Fact]
    public void FileToolActor_opens_media_but_refuses_executables()
    {
        using var temp = new TempRoot();
        File.WriteAllBytes(Path.Combine(temp.Path, "song.mp3"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(temp.Path, "evil.exe"), new byte[] { 1, 2, 3 });
        var launched = new List<string>();
        var roots = new AllowedRootResolver([new AllowedRoot { Alias = "music", Path = temp.Path }]);
        Func<string, System.Diagnostics.Process?> recorder = p => { launched.Add(p); return null; };
        var files = Sys.ActorOf(FilesProps(roots, recorder), "files-open");

        files.Tell(new FileToolActor.Open("music/song.mp3"), TestActor);
        var ok = JsonDocument.Parse(ExpectMsg<string>(TimeSpan.FromSeconds(3))).RootElement;
        Assert.True(ok.GetProperty("ok").GetBoolean());
        Assert.Equal("media", ok.GetProperty("kind").GetString());
        Assert.Equal("music/song.mp3", ok.GetProperty("path").GetString());

        files.Tell(new FileToolActor.Open("music/evil.exe"), TestActor);
        var denied = JsonDocument.Parse(ExpectMsg<string>(TimeSpan.FromSeconds(3))).RootElement;
        Assert.False(denied.GetProperty("ok").GetBoolean());

        files.Tell(new FileToolActor.Open("music/missing.mp3"), TestActor);
        Assert.False(JsonDocument.Parse(ExpectMsg<string>(TimeSpan.FromSeconds(3))).RootElement.GetProperty("ok").GetBoolean());

        Assert.Equal(new[] { Path.Combine(temp.Path, "song.mp3") }, launched);   // the .exe never reached the shell
    }

    [Fact]
    public void FileToolActor_stop_media_uses_the_fallback_key_and_refuses_when_nothing_played()
    {
        using var temp = new TempRoot();
        File.WriteAllBytes(Path.Combine(temp.Path, "song.mp3"), new byte[] { 1, 2, 3 });
        var roots = new AllowedRootResolver([new AllowedRoot { Alias = "music", Path = temp.Path }]);
        var stops = 0;
        Func<string, System.Diagnostics.Process?> noProcess = _ => null;   // the shell activated an existing player
        Func<bool> stopKey = () => { stops++; return true; };
        var files = Sys.ActorOf(FilesProps(roots, noProcess, stopKey), "files-stop");

        // Nothing started yet → honest refusal, no key sent.
        files.Tell(new FileToolActor.StopMedia(), TestActor);
        var idle = JsonDocument.Parse(ExpectMsg<string>(TimeSpan.FromSeconds(3))).RootElement;
        Assert.False(idle.GetProperty("ok").GetBoolean());
        Assert.Equal(0, stops);

        files.Tell(new FileToolActor.Open("music/song.mp3"), TestActor);
        var opened = JsonDocument.Parse(ExpectMsg<string>(TimeSpan.FromSeconds(3))).RootElement;
        Assert.Contains("playing", opened.GetProperty("note").GetString());

        files.Tell(new FileToolActor.StopMedia(), TestActor);
        var stopped = JsonDocument.Parse(ExpectMsg<string>(TimeSpan.FromSeconds(3))).RootElement;
        Assert.True(stopped.GetProperty("ok").GetBoolean());
        Assert.Equal("music/song.mp3", stopped.GetProperty("path").GetString());
        Assert.Equal(1, stops);

        // Second stop: the tracker was cleared, so this is a refusal again — the model
        // must not be told it stopped something twice.
        files.Tell(new FileToolActor.StopMedia(), TestActor);
        Assert.False(JsonDocument.Parse(ExpectMsg<string>(TimeSpan.FromSeconds(3))).RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, stops);
    }

    // ── WebToolActor ──────────────────────────────────────────────────────────

    [Fact]
    public void WebToolActor_uses_the_gui_when_it_answers()
    {
        var web = Sys.ActorOf(WebProps(new FakeSurface("gui"), new FakeSurface("headless")), "web-gui");
        web.Tell(new WebToolActor.Search("watch", 3), TestActor);
        var root = JsonDocument.Parse(ExpectMsg<string>(TimeSpan.FromSeconds(3))).RootElement;
        Assert.Equal("gui", root.GetProperty("via").GetString());
        Assert.Equal("gui:search:watch", root.GetProperty("echo").GetString());
    }

    [Fact]
    public void WebToolActor_falls_back_to_headless_when_the_gui_is_unreachable()
    {
        var web = Sys.ActorOf(WebProps(new FakeSurface("gui", unavailable: true), new FakeSurface("headless")), "web-fallback");
        web.Tell(new WebToolActor.Open("https://example.org", 0), TestActor);
        var root = JsonDocument.Parse(ExpectMsg<string>(TimeSpan.FromSeconds(3))).RootElement;
        Assert.Equal("headless", root.GetProperty("via").GetString());
        Assert.Equal("headless:open:https://example.org", root.GetProperty("echo").GetString());
    }

    [Fact]
    public void WebToolActor_refuses_when_disabled()
    {
        var web = Sys.ActorOf(WebProps(new FakeSurface("gui"), new FakeSurface("headless"), enabled: false), "web-off");
        web.Tell(new WebToolActor.Read(0, "summary", null, 0), TestActor);
        var root = JsonDocument.Parse(ExpectMsg<string>(TimeSpan.FromSeconds(3))).RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Contains("turned off", root.GetProperty("error").GetString());
    }

    // ── WearableToolbelt over the actors ──────────────────────────────────────

    [Fact]
    public async Task Toolbelt_routes_to_the_actors_and_denies_terminals()
    {
        using var temp = new TempRoot();
        File.WriteAllText(Path.Combine(temp.Path, "a.txt"), "alpha");
        var roots = new AllowedRootResolver([new AllowedRoot { Alias = "docs", Path = temp.Path }]);
        var files = Sys.ActorOf(FilesProps(roots), "belt-files");
        var web = Sys.ActorOf(WebProps(null, new FakeSurface("headless")), "belt-web");
        var belt = new WearableToolbelt(files, web);

        var read = JsonDocument.Parse(await belt.ReadFileAsync("docs/a.txt", 100, CancellationToken.None)).RootElement;
        Assert.Equal("alpha", read.GetProperty("text").GetString());

        var search = JsonDocument.Parse(await belt.WebSearchAsync("q", 2, CancellationToken.None)).RootElement;
        Assert.Equal("headless", search.GetProperty("via").GetString());

        Assert.False(await belt.SendToTerminalAsync(0, 0, "rm -rf", CancellationToken.None));
        Assert.Equal("""{"groups":[]}""", await belt.ListTerminalsAsync(CancellationToken.None));
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private sealed class FakeSurface(string name, bool unavailable = false) : IWebToolSurface
    {
        private Task<string> Reply(string what)
            => unavailable
                ? throw new WebSurfaceUnavailableException("not running")
                : Task.FromResult(JsonSerializer.Serialize(new { ok = true, echo = $"{name}:{what}" }));

        public Task<string> SearchAsync(string query, int maxResults, CancellationToken ct) => Reply($"search:{query}");
        public Task<string> OpenAsync(string url, int tab, CancellationToken ct) => Reply($"open:{url}");
        public Task<string> ReadAsync(int tab, string? mode, string? find, int maxChars, CancellationToken ct) => Reply($"read:{tab}");
        public Task<string> ListTabsAsync(CancellationToken ct) => Reply("tabs");
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aztest-actor-" + Guid.NewGuid().ToString("n"));
        public TempRoot() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
