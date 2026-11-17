using AgentOne.Agent;
using AgentOne.Tools;

namespace AgentOne.Tests;

/// <summary>
/// Measured (2026-11-16, music-harness, in a worktree): "곡 만들어줘" read the
/// procedure and wrote <c>songs/&lt;date&gt;-quiet-assistant/guide.md</c> in a format of
/// its own beside sixty earlier guide.md files whose front matter and three fenced
/// blocks are what the workspace's generation script parses; the genre document
/// went to <c>knowledge/genres/</c> at the root instead of <c>harness/knowledge/genres/</c>;
/// and the folder was dated a day ahead because nothing had said what day it was.
/// </summary>
public class WritePrecedentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent-one-precedent-" + Guid.NewGuid().ToString("N")[..8]);

    public WritePrecedentTests()
    {
        foreach (var song in new[] { "2026-11-01-first", "2026-11-15-guest-room", "2026-11-15-skipping-stone" })
        {
            Directory.CreateDirectory(Path.Combine(_root, "songs", song));
            File.WriteAllText(Path.Combine(_root, "songs", song, "guide.md"), "---\nvocal: male\n---\n```text\nprompt\n```\n");
        }
        Directory.CreateDirectory(Path.Combine(_root, "harness", "knowledge", "genres"));
        foreach (var genre in new[] { "folk", "metal", "trot" })
            File.WriteAllText(Path.Combine(_root, "harness", "knowledge", "genres", genre + ".md"), "# " + genre);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private AgentLoop Loop(ScriptedChatProvider provider)
    {
        var files = new LocalFileToolbelt(_root);
        var belt = new CompositeToolbelt((ToolCatalog.FilesFamily, files), (ToolCatalog.EditFamily, files));
        return new AgentLoop(provider, belt, maxSteps: 10) { Root = _root };
    }

    private static string Write(string path, string content) =>
        $"{{\"tool\":\"write_file\",\"args\":{{\"path\":\"{path}\",\"content\":\"{content}\"}}}}";

    private static string Read(string path) => $"{{\"tool\":\"read_file\",\"args\":{{\"path\":\"{path}\"}}}}";

    private static string Answer(string text) => ToolCall.Final(text).ToJson();

    [Fact]
    public void A_new_file_named_like_its_siblings_is_sent_back_with_the_newest_of_them()
    {
        var note = WritePrecedent.Check(_root, "songs/2026-11-16-quiet/guide.md", []);

        Assert.NotNull(note);
        Assert.Contains("songs/2026-11-15-skipping-stone/guide.md, songs/2026-11-15-guest-room/guide.md", note);
        Assert.Contains("keep its structure", note);
    }

    [Fact]
    public void Having_read_one_of_them_is_enough()
    {
        Assert.Null(WritePrecedent.Check(_root, "songs/2026-11-16-quiet/guide.md", ["songs/2026-11-01-first/guide.md"]));
    }

    [Fact]
    public void A_name_no_sibling_uses_an_existing_file_and_a_lone_precedent_are_left_alone()
    {
        Assert.Null(WritePrecedent.Check(_root, "songs/2026-11-16-quiet/storyboard.md", []));
        Assert.Null(WritePrecedent.Check(_root, "songs/2026-11-01-first/guide.md", []));
        Assert.Null(WritePrecedent.Check(_root, "README.md", []));

        Directory.CreateDirectory(Path.Combine(_root, "scenes", "one"));
        File.WriteAllText(Path.Combine(_root, "scenes", "one", "plan.md"), "x");
        Assert.Null(WritePrecedent.Check(_root, "scenes/two/plan.md", []));
    }

    [Fact]
    public void A_new_document_in_a_folder_of_documents_is_sent_back_too()
    {
        var note = WritePrecedent.Check(_root, "harness/knowledge/genres/city-pop.md", []);

        Assert.NotNull(note);
        Assert.Contains("the same kind of file in this folder", note);
    }

    [Fact]
    public void A_folder_the_root_lacks_but_the_tree_has_further_down_is_pointed_out()
    {
        var note = WritePrecedent.Check(_root, "knowledge/genres/city-pop.md", []);

        Assert.NotNull(note);
        Assert.Contains("'harness/knowledge/genres/city-pop.md'", note);

        // A folder nothing else ends in is simply new.
        Assert.Null(WritePrecedent.Check(_root, "drafts/notes/a.md", []));
    }

    [Fact]
    public async Task The_loop_holds_the_first_write_and_lets_the_one_after_the_read_through()
    {
        var provider = new ScriptedChatProvider(
            Write("songs/2026-11-16-quiet/guide.md", "my own format"),
            Read("songs/2026-11-15-skipping-stone/guide.md"),
            Write("songs/2026-11-16-quiet/guide.md", "front matter and blocks"),
            Answer("wrote songs/2026-11-16-quiet/guide.md"));

        var run = await Loop(provider).RunAsync("곡 만들어줘");

        Assert.Equal(StopReason.Final, run.Reason);
        Assert.Equal("front matter and blocks", File.ReadAllText(Path.Combine(_root, "songs", "2026-11-16-quiet", "guide.md")));
        var held = run.Steps.First(s => s.Tool == "write_file");
        Assert.False(held.Ok);
        Assert.Contains("Not written yet", provider.Calls[1][^1].Content);
    }

    [Fact]
    public async Task Writing_the_same_path_again_without_reading_is_insistence_and_goes_through()
    {
        var provider = new ScriptedChatProvider(
            Write("songs/2026-11-16-quiet/guide.md", "on purpose"),
            Write("songs/2026-11-16-quiet/guide.md", "on purpose"),
            Answer("wrote songs/2026-11-16-quiet/guide.md"));

        var run = await Loop(provider).RunAsync("make it my way");

        Assert.Equal(StopReason.Final, run.Reason);
        Assert.Equal("on purpose", File.ReadAllText(Path.Combine(_root, "songs", "2026-11-16-quiet", "guide.md")));
        Assert.DoesNotContain(run.Steps, s => s.Detail == "repeated call");
    }

    [Fact]
    public void The_sessions_own_files_are_not_a_precedent_for_the_next_one_beside_them()
    {
        var folder = Path.Combine(_root, "songs", "2026-11-16-quiet");
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "notes.md", "genre.md", "draft.md" }) File.WriteAllText(Path.Combine(folder, name), "x");
        string[] wrote = ["songs/2026-11-16-quiet/notes.md", "songs/2026-11-16-quiet/genre.md", "songs/2026-11-16-quiet/draft.md"];

        Assert.NotNull(WritePrecedent.Check(_root, "songs/2026-11-16-quiet/final.md", []));
        Assert.Null(WritePrecedent.Check(_root, "songs/2026-11-16-quiet/final.md", [], wrote));
    }

    [Fact]
    public void A_work_folder_short_of_what_its_siblings_hold_is_a_gap()
    {
        foreach (var song in Directory.GetDirectories(Path.Combine(_root, "songs")))
        {
            File.WriteAllText(Path.Combine(song, "lyrics.md"), "la");
            File.WriteAllText(Path.Combine(song, "notes.md"), "why");
        }
        File.WriteAllText(Path.Combine(_root, "songs", "2026-11-01-first", "only-here.txt"), "x");
        var folder = Path.Combine(_root, "songs", "2026-11-16-quiet");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "notes.md"), "plan");

        var gap = WritePrecedent.Unfinished(_root, ["songs/2026-11-16-quiet/notes.md"])!;

        Assert.Equal("songs/2026-11-16-quiet", gap.Folder);
        Assert.Equal(["guide.md", "lyrics.md"], gap.Missing);
        Assert.Equal("songs/2026-11-15-skipping-stone", gap.Example);

        File.WriteAllText(Path.Combine(folder, "guide.md"), "g");
        File.WriteAllText(Path.Combine(folder, "lyrics.md"), "l");
        Assert.Null(WritePrecedent.Unfinished(_root, ["songs/2026-11-16-quiet/notes.md"]));
        Assert.Null(WritePrecedent.Unfinished(_root, []));
    }

    [Fact]
    public async Task A_turn_that_stops_at_the_plan_is_sent_back_until_the_set_is_there()
    {
        var provider = new ScriptedChatProvider(
            Write("songs/2026-11-16-quiet/notes.md", "the plan"),
            Answer("The planning stage is complete. Next step: call the lyricist."),
            Read("songs/2026-11-15-skipping-stone/guide.md"),
            Write("songs/2026-11-16-quiet/guide.md", "front matter and blocks"),
            Answer("Wrote songs/2026-11-16-quiet/guide.md and notes.md."));

        var run = await Loop(provider).RunAsync("곡 만들어줘");

        Assert.Equal(StopReason.Final, run.Reason);
        Assert.Contains(run.Steps, s => s.Tool == "unfinished" && s.Detail.Contains("guide.md"));
        Assert.True(File.Exists(Path.Combine(_root, "songs", "2026-11-16-quiet", "guide.md")));
        Assert.StartsWith("Wrote", run.Text);
    }

    [Fact]
    public async Task The_check_speaks_once_when_nothing_comes_of_it()
    {
        var provider = new ScriptedChatProvider(
            Write("songs/2026-11-16-quiet/notes.md", "only notes were asked for"),
            Answer("Wrote the notes."),
            Answer("Only the notes were asked for; guide.md is left for later."));

        var run = await Loop(provider).RunAsync("notes only");

        Assert.Equal(StopReason.Final, run.Reason);
        Assert.Single(run.Steps, s => s.Tool == "unfinished");
        Assert.StartsWith("Only the notes", run.Text);
    }

    [Fact]
    public void A_command_that_rejects_the_sessions_own_file_is_answered_with_the_file_to_fix_and_its_model()
    {
        string[] wrote = ["songs/2026-11-16-quiet/guide.md"];
        const string command = "node tools/dv-gen.cjs songs/2026-11-16-quiet/guide.md --dry";

        var note = WritePrecedent.BlameNote(_root, command, "exit code 1\n[stderr]\nguide.md 에 text 코드블록이 3개 필요", wrote);

        Assert.NotNull(note);
        Assert.Contains("rejected songs/2026-11-16-quiet/guide.md", note);
        Assert.Contains("read_file songs/2026-11-15-skipping-stone/guide.md", note);

        // A failure that does not speak of the file, or a file somebody else wrote, is not this case.
        Assert.Null(WritePrecedent.BlameNote(_root, command, "Error: connect ECONNREFUSED 127.0.0.1:9222", wrote));
        Assert.Null(WritePrecedent.BlameNote(_root, command, "guide.md 에 text 코드블록이 3개 필요", []));
    }

    [Fact]
    public void The_prompt_says_what_day_it_is()
    {
        var before = SystemPrompt.Today;
        try
        {
            SystemPrompt.Today = () => new DateTime(2026, 11, 16, 14, 0, 0);
            Assert.Contains("Today is 2026-11-16", SystemPrompt.Build(_root));
        }
        finally { SystemPrompt.Today = before; }
    }
}
