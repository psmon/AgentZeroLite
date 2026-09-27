using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentOne.Dashboard;
using AgentOne.Graph;
using AgentOne.Services;

namespace AgentOne.Tests;

/// <summary>
/// `agent-one dashboard`: the memory parser, the read side over a temp
/// workspaces folder with a real Kùzu graph, the request handler's locks,
/// and one real socket round trip.
/// </summary>
public class DashboardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "agent-one-dash-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly DashboardData _data;

    public DashboardTests()
    {
        Directory.CreateDirectory(_dir);
        _data = new DashboardData(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* Kùzu may still be closing its files */ }
        GC.SuppressFinalize(this);
    }

    private const string Memory = """
        ## 2026-09-20 10:00 · 게시판 API 만들기
        - asked: 보드 api를 만들어줘
        - did: write_file: path=src/Board.cs -> ok; run_command(failed): dotnet build -> exit 1
        - outcome: stopped (MaxSteps): build failed

        ## 2026-09-21 09:30 · hello
        - asked: hi
        - did: (no tools)
        - outcome: Hello!
        a line someone added by hand
        """;

    /// <summary>A workspace folder the way WorkspaceStore lays it out, without going through AGENT_ONE_HOME.</summary>
    private string Workspace(string id, string root, string memory = Memory, bool session = true)
    {
        var dir = Path.Combine(_dir, id);
        Directory.CreateDirectory(Path.Combine(dir, "sessions"));
        File.WriteAllText(Path.Combine(dir, "workspace.json"), JsonSerializer.Serialize(new WorkspaceMeta { Root = root }, AgentOneJson.Default.WorkspaceMeta));
        File.WriteAllText(Path.Combine(dir, "memory.md"), memory);
        if (session)
        {
            var lines = new[]
            {
                new SessionEntry { Timestamp = "2026-09-20T01:00:00.000Z", Kind = "prompt", Text = "보드 api를 만들어줘", Mode = "smart" },
                new SessionEntry { Timestamp = "2026-09-20T01:00:05.000Z", Kind = "step", Tool = "write_file", Ok = true, Text = "path=src/Board.cs", ElapsedMs = 120 },
                new SessionEntry { Timestamp = "2026-09-20T01:00:09.000Z", Kind = "result", Ok = false, Text = "build failed" },
                new SessionEntry { Timestamp = "2026-09-20T01:00:10.000Z", Kind = "title", Text = "게시판 API 만들기" }
            }.Select(e => JsonSerializer.Serialize(e, AgentOneWireJson.Default.SessionEntry));
            File.WriteAllText(Path.Combine(dir, "sessions", "20260920-100000-chat.jsonl"), string.Join('\n', lines) + "\n");
        }
        return dir;
    }

    private static void Seed(string workspaceDir)
    {
        using var graph = KnowledgeGraph.Open(Path.Combine(workspaceDir, "graph"))
                          ?? throw new InvalidOperationException("Kùzu is not available in the test output");
        graph.RememberTurn("t1", "build the board api", "built");
        graph.Learn("t1", "Build command", "dotnet build src/BoardApi/BoardApi.csproj", "procedure",
            new Rationale("worth it?", "save", 0.83, "asked: x"), ["src/BoardApi/BoardApi.csproj"]);
    }

    // ------------------------------------------------------------ memory.md

    [Fact]
    public void MemoryParsesTheEntriesChatSessionWrites()
    {
        var entries = MemoryLog.Parse(Memory.Replace("\n", "\r\n"));

        Assert.Equal(2, entries.Count);
        Assert.Equal("2026-09-20 10:00", entries[0].At);
        Assert.Equal("게시판 API 만들기", entries[0].Title);
        Assert.Equal("보드 api를 만들어줘", entries[0].Asked);
        Assert.Equal(["write_file: path=src/Board.cs -> ok", "run_command(failed): dotnet build -> exit 1"], entries[0].Did);
        Assert.Equal("stopped (MaxSteps): build failed", entries[0].Outcome);

        Assert.Empty(entries[1].Did);                                      // "(no tools)"
        Assert.Equal("a line someone added by hand", entries[1].Extra);    // kept, not dropped
    }

    // ------------------------------------------------------- files, scoping

    [Fact]
    public void ListsWorkspacesAndScopesMemoryAndSessionsToOneOrAll()
    {
        var old = Workspace("board-1111111111", Path.Combine(_dir, "no-such-folder", "board"));
        foreach (var file in Directory.EnumerateFiles(old, "*", SearchOption.AllDirectories))
            File.SetLastWriteTime(file, new DateTime(2026, 9, 1, 12, 0, 0));
        Workspace("hello-2222222222", _dir, memory: "## 2026-09-25 08:00 · later\n- asked: q\n- did: (no tools)\n- outcome: a\n", session: false);

        var list = _data.Workspaces();
        Assert.Equal(2, list.Workspaces.Count);
        Assert.Equal("hello-2222222222", list.Workspaces[0].Id);          // newest activity first
        var board = list.Workspaces.Single(w => w.Id == "board-1111111111");
        Assert.Equal("board", board.Name);
        Assert.False(board.RootExists);
        Assert.Equal(2, board.MemoryEntries);
        Assert.Equal(1, board.Sessions);
        Assert.Equal("none", board.Graph);

        var all = _data.Memory(DashboardData.All)!;
        Assert.Equal(3, all.Entries.Count);
        Assert.Equal("later", all.Entries[0].Title);                      // newest first across workspaces
        Assert.Null(all.Raw);                                             // raw only for one workspace
        Assert.Equal(Memory, _data.Memory("board-1111111111")!.Raw);

        var sessions = _data.Sessions(DashboardData.All)!.Sessions;
        var s = Assert.Single(sessions);
        Assert.Equal("게시판 API 만들기", s.Title);
        Assert.Equal(4, _data.Session(s.Ws, s.Id)!.Entries.Count);
    }

    [Fact]
    public void OnlyListedNamesResolve_NoPaths()
    {
        var dir = Workspace("board-1111111111", _dir);
        File.WriteAllText(Path.Combine(_dir, "secret.jsonl"), "{\"kind\":\"prompt\",\"text\":\"x\"}");

        Assert.Null(_data.Memory("../board-1111111111"));
        Assert.Null(_data.Memory(dir));
        Assert.Null(_data.Sessions("nope"));
        Assert.Null(_data.Session("board-1111111111", "../../secret"));
        Assert.Null(_data.Session("..", "secret"));
    }

    // ---------------------------------------------------------------- graph

    [Fact]
    public void ReadsTheGraphFromTheCatalog_NodesEdgesAndCounts()
    {
        var dir = Workspace("board-1111111111", _dir);
        Seed(dir);

        var view = _data.Graph("board-1111111111")!;
        Assert.Empty(view.Notes);
        Assert.Contains(view.Nodes, n => n.Label == "Knowledge" && n.Caption == "Build command" && n.Props["kind"] == "procedure");
        Assert.Contains(view.Nodes, n => n.Label == "Path" && n.Caption == "src/BoardApi/BoardApi.csproj");
        Assert.Contains(view.Nodes, n => n.Label == "Rationale" && n.Caption == "save 0.83");
        Assert.Contains(view.Edges, e => e.Type == "LEARNED" && e.From.EndsWith("/Turn/t1", StringComparison.Ordinal));
        Assert.Contains(view.Edges, e => e.Type == "ABOUT");
        Assert.Contains(view.Edges, e => e.Type == "JUSTIFIED_BY");
        Assert.All(view.Edges, e => Assert.Contains(view.Nodes, n => n.Id == e.From));

        var info = _data.Workspaces().Workspaces.Single();
        Assert.Equal("ok", info.Graph);
        Assert.Equal(1, info.Counts!["Knowledge"]);
        Assert.Equal(1, info.Counts["Turn"]);
    }

    [Fact]
    public void GraphLimitKeepsTheNewestAndSaysSo()
    {
        var dir = Workspace("board-1111111111", _dir);
        using (var graph = KnowledgeGraph.Open(Path.Combine(dir, "graph"))!)
            for (var i = 0; i < 3; i++) graph.RememberTurn("t" + i, "q" + i, "a");

        var view = _data.Graph("board-1111111111", perTable: 2)!;
        Assert.Equal(2, view.Nodes.Count(n => n.Label == "Turn"));
        Assert.Contains("board-1111111111/Turn", view.Truncated);
    }

    [Fact]
    public void CypherAcrossWorkspacesAddsAWorkspaceColumn_AndWritesAreRefused()
    {
        Seed(Workspace("a-1111111111", _dir));
        Seed(Workspace("b-2222222222", _dir));
        Workspace("c-3333333333", _dir);                                   // no graph: skipped, not an error

        var all = _data.Cypher(DashboardData.All, "MATCH (k:Knowledge) RETURN k.title AS title")!;
        Assert.Empty(all.Errors);
        Assert.Equal(["workspace", "title"], all.Columns);
        Assert.Equal(2, all.Rows.Count);
        Assert.Equal(["a-1111111111", "b-2222222222"], all.Rows.Select(r => r[0]).Order().ToArray());

        var one = _data.Cypher("a-1111111111", "MATCH (k:Knowledge) RETURN k.title AS title")!;
        Assert.Equal(["title"], one.Columns);
        Assert.Equal("Build command", Assert.Single(one.Rows)[0]);

        // The database is opened read-only, so Kùzu itself refuses a write.
        var write = _data.Cypher("a-1111111111", "MATCH (k:Knowledge) DELETE k")!;
        Assert.NotEmpty(write.Errors);
        Assert.Equal("Build command", Assert.Single(_data.Cypher("a-1111111111", "MATCH (k:Knowledge) RETURN k.title")!.Rows)[0]);

        var bad = _data.Cypher("a-1111111111", "MATCH (x:NoSuchTable) RETURN x")!;
        Assert.NotEmpty(bad.Errors);

        // The same failure from every graph is said once, naming the workspaces.
        var badAll = _data.Cypher(DashboardData.All, "MATCH (x:NoSuchTable) RETURN x")!;
        var grouped = Assert.Single(badAll.Errors);
        Assert.StartsWith("2 workspaces:", grouped);
        Assert.Contains("a-1111111111, b-2222222222", grouped);
    }

    [Fact]
    public void DoesNotHoldTheGraphBetweenRequests()
    {
        var dir = Workspace("board-1111111111", _dir);
        Seed(dir);

        _ = _data.Graph("board-1111111111");
        _ = _data.Cypher("board-1111111111", "MATCH (k:Knowledge) RETURN k.title");

        // The next agent-one run in this workspace must still get its writable handle.
        using var writer = KnowledgeGraph.Open(Path.Combine(dir, "graph"));
        Assert.NotNull(writer);
        writer!.RememberTurn("t2", "again", "ok");
    }

    // --------------------------------------------------------------- server

    private static Dictionary<string, string> Headers(string? token, string host = "127.0.0.1:8790")
    {
        var h = new Dictionary<string, string> { ["host"] = host };
        if (token is not null) h[DashboardServer.TokenHeader] = token;
        return h;
    }

    [Fact]
    public void ServesThePage_ButTheApiOnlyWithTheTokenAndALoopbackHost()
    {
        Workspace("board-1111111111", _dir);
        using var server = new DashboardServer(_data, 0, token: "secret-token");

        var page = server.Handle("GET", "/?t=secret-token", Headers(null), []);
        Assert.Equal(200, page.Status);
        Assert.Contains("agent-one dashboard", Encoding.UTF8.GetString(page.Body));

        Assert.Equal(401, server.Handle("GET", "/api/workspaces", Headers(null), []).Status);
        Assert.Equal(401, server.Handle("GET", "/api/workspaces", Headers("wrong"), []).Status);
        Assert.Equal(421, server.Handle("GET", "/api/workspaces", Headers("secret-token", host: "evil.example:8790"), []).Status);
        Assert.Equal(200, server.Handle("GET", "/api/workspaces", Headers("secret-token", host: "localhost:8790"), []).Status);

        Assert.Equal(404, server.Handle("GET", "/api/memory?ws=..%2Fx", Headers("secret-token"), []).Status);
        var memory = server.Handle("GET", "/api/memory?ws=board-1111111111", Headers("secret-token"), []);
        Assert.Equal(200, memory.Status);
        Assert.Equal(2, JsonSerializer.Deserialize(memory.Body, DashboardJson.Default.MemoryView)!.Entries.Count);

        Assert.Equal(400, server.Handle("POST", "/api/cypher", Headers("secret-token"), "not json"u8.ToArray()).Status);
    }

    [Theory]
    [InlineData("127.0.0.1:8790", true)]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST:1", true)]
    [InlineData("[::1]:8790", true)]
    [InlineData("127.0.0.1.nip.io:8790", false)]
    [InlineData("attacker.test", false)]
    public void HostCheck(string host, bool ok) => Assert.Equal(ok, DashboardServer.IsLoopbackHost(host));

    [Fact]
    public async Task AnswersOverARealSocket()
    {
        Seed(Workspace("board-1111111111", _dir));
        using var server = new DashboardServer(_data, 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serving = server.RunAsync(cts.Token);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("api/workspaces", cts.Token)).StatusCode);

        http.DefaultRequestHeaders.Add(DashboardServer.TokenHeader, server.Token);
        var list = await http.GetFromJsonAsync("api/workspaces", DashboardJson.Default.WorkspaceList, cts.Token);
        Assert.Equal("board-1111111111", Assert.Single(list!.Workspaces).Id);

        var reply = await http.PostAsJsonAsync("api/cypher",
            new CypherRequest { Ws = "all", Query = "MATCH (p:Path) RETURN p.path AS path" }, DashboardJson.Default.CypherRequest, cts.Token);
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);                  // HttpClient sends this body chunked
        var result = await reply.Content.ReadFromJsonAsync(DashboardJson.Default.CypherResult, cts.Token);
        Assert.True(result!.Errors.Count == 0, string.Join(" | ", result.Errors));
        Assert.Equal("src/BoardApi/BoardApi.csproj", Assert.Single(result.Rows)[1]);

        var page = await http.GetAsync("/", cts.Token);
        Assert.Contains("default-src 'none'", page.Headers.GetValues("Content-Security-Policy").Single());

        cts.Cancel();
        await serving;
    }
}
