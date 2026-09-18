using System.IO;
using System.Linq;
using System.Text.Json;
using Agent.Common.Llm.Tools;
using Agent.Common.Wearable;

namespace ZeroCommon.Tests;

/// <summary>
/// The file tools over two allow-listed folders (M0032): paths come back in alias form,
/// writes obey the per-root grant, a grep with no path spans every root, and a list with
/// no path is the root catalogue rather than a walk.
/// </summary>
[Trait("Category", "FileTools")]
public sealed class AllowedRootFileToolsTests : IDisposable
{
    private readonly string _base;
    private readonly AllowedRootResolver _roots;

    public AllowedRootFileToolsTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "aztest-roots-" + Guid.NewGuid().ToString("n"));
        var docs = Path.Combine(_base, "docs");
        var music = Path.Combine(_base, "music");
        Directory.CreateDirectory(Path.Combine(docs, "notes"));
        Directory.CreateDirectory(music);
        File.WriteAllText(Path.Combine(docs, "notes", "meeting.md"), "# Meeting\nDecision: ship the watch tools on Friday.\n");
        File.WriteAllText(Path.Combine(music, "playlist.txt"), "friday-mix.mp3\n");
        _roots = new AllowedRootResolver(new[]
        {
            new AllowedRoot { Alias = "docs", Path = docs, Writable = false },
            new AllowedRoot { Alias = "music", Path = music, Writable = true },
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { }
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Read_returns_alias_path_not_disk_path()
    {
        var json = AllowedRootFileTools.ReadFile(_roots, "docs/notes/meeting.md", 10_000);
        var root = Parse(json);
        Assert.True(root.GetProperty("ok").GetBoolean(), json);
        Assert.Equal("docs/notes/meeting.md", root.GetProperty("path").GetString());
        Assert.Contains("ship the watch tools", root.GetProperty("text").GetString());
        Assert.DoesNotContain(_base, json);
    }

    [Fact]
    public void Write_and_edit_are_refused_on_a_read_only_root_but_allowed_on_a_writable_one()
    {
        var denied = Parse(AllowedRootFileTools.WriteFile(_roots, "docs/new.txt", "x"));
        Assert.False(denied.GetProperty("ok").GetBoolean());
        Assert.Contains("read-only", denied.GetProperty("error").GetString());
        Assert.False(File.Exists(Path.Combine(_base, "docs", "new.txt")));

        var deniedEdit = Parse(AllowedRootFileTools.Edit(_roots, "docs/notes/meeting.md", "Friday", "Monday", false));
        Assert.False(deniedEdit.GetProperty("ok").GetBoolean());

        var ok = Parse(AllowedRootFileTools.WriteFile(_roots, "music/todo.txt", "buy strings"));
        Assert.True(ok.GetProperty("ok").GetBoolean());
        Assert.Equal("music/todo.txt", ok.GetProperty("path").GetString());
        Assert.Equal("buy strings", File.ReadAllText(Path.Combine(_base, "music", "todo.txt")));
    }

    [Fact]
    public void Grep_without_a_path_spans_every_root_and_prefixes_aliases()
    {
        var root = Parse(AllowedRootFileTools.Grep(_roots, "friday", null, 50));
        Assert.True(root.GetProperty("ok").GetBoolean());
        var files = root.GetProperty("matches").EnumerateArray().Select(m => m.GetProperty("file").GetString()).ToList();
        Assert.Contains("music/playlist.txt", files);
        // "Friday" is capitalised in the doc; the pattern is case-sensitive, so only music hits.
        Assert.DoesNotContain(files, f => f!.StartsWith("docs/"));

        var scoped = Parse(AllowedRootFileTools.Grep(_roots, "Friday", "docs", 50));
        var scopedFiles = scoped.GetProperty("matches").EnumerateArray().Select(m => m.GetProperty("file").GetString()).ToList();
        Assert.Equal(new[] { "docs/notes/meeting.md" }, scopedFiles);
    }

    [Fact]
    public void List_without_a_path_is_the_root_catalogue()
    {
        var root = Parse(AllowedRootFileTools.ListFiles(_roots, null, 100));
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(2, root.GetProperty("roots").GetArrayLength());

        var inside = Parse(AllowedRootFileTools.ListFiles(_roots, "docs", 100));
        var paths = inside.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("path").GetString()).ToList();
        Assert.Contains("docs/notes", paths);
        Assert.Contains("docs/notes/meeting.md", paths);
    }

    [Fact]
    public void Find_files_searches_every_root_by_kind_and_words_best_first()
    {
        File.WriteAllBytes(Path.Combine(_base, "music", "이문세-01-사랑은 늘 도망가.mp3"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_base, "music", "박효신-03-야생화.mp3"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_base, "docs", "cover.png"), new byte[] { 1 });

        var media = Parse(AllowedRootFileTools.FindFiles(_roots, "", "music", 20));
        Assert.True(media.GetProperty("ok").GetBoolean());
        var paths = media.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("path").GetString()!).ToList();
        Assert.Equal(2, paths.Count);
        Assert.All(paths, p => Assert.StartsWith("music/", p));
        Assert.DoesNotContain(paths, p => p.EndsWith(".txt"));

        var artist = Parse(AllowedRootFileTools.FindFiles(_roots, "이문세 노래", "media", 20));
        var best = artist.GetProperty("entries")[0];
        Assert.Equal("music/이문세-01-사랑은 늘 도망가.mp3", best.GetProperty("path").GetString());
        Assert.Equal(1, artist.GetProperty("count").GetInt32());   // 박효신 has none of the words

        var docs = Parse(AllowedRootFileTools.FindFiles(_roots, "meeting", "document", 20));
        Assert.Equal("docs/notes/meeting.md", docs.GetProperty("entries")[0].GetProperty("path").GetString());

        var any = Parse(AllowedRootFileTools.FindFiles(_roots, "", "any", 20));
        Assert.Equal(5, any.GetProperty("total").GetInt32());
        Assert.Contains("docs/cover.png", any.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("path").GetString()));

        var none = Parse(AllowedRootFileTools.FindFiles(_roots, "zzz", "media", 20));
        Assert.Equal(0, none.GetProperty("count").GetInt32());
        Assert.Contains("nothing matched", none.GetProperty("hint").GetString());
    }

    [Fact]
    public void List_without_a_path_previews_each_roots_files()
    {
        var root = Parse(AllowedRootFileTools.ListFiles(_roots, null, 100));
        var music = root.GetProperty("roots").EnumerateArray().Single(r => r.GetProperty("alias").GetString() == "music");
        var files = music.GetProperty("files").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains("music/playlist.txt", files);
        Assert.False(music.GetProperty("more").GetBoolean());
        var docs = root.GetProperty("roots").EnumerateArray().Single(r => r.GetProperty("alias").GetString() == "docs");
        Assert.Contains("docs/notes/meeting.md", docs.GetProperty("files").EnumerateArray().Select(f => f.GetString()));
    }

    [Fact]
    public void Everything_is_refused_with_no_roots()
    {
        var none = new AllowedRootResolver(null);
        Assert.False(Parse(AllowedRootFileTools.ReadFile(none, "docs/a", 100)).GetProperty("ok").GetBoolean());
        Assert.False(Parse(AllowedRootFileTools.Grep(none, "x", null, 10)).GetProperty("ok").GetBoolean());
        Assert.False(Parse(AllowedRootFileTools.ListFiles(none, null, 10)).GetProperty("ok").GetBoolean());
    }
}
