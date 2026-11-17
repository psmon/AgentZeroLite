using AgentOne.Agent;
using AgentOne.Tools;
using AgentOne.Tools.Cli;

namespace AgentOne.Tests;

/// <summary>
/// Measured (2026-11-16, music-harness): a Python compositor was written with a stray
/// quote after its last line (<c>full()"</c>). The write said ok; three runs later the
/// model was still rewriting the same text and the turn ended on the repeat guard.
/// </summary>
public class WriteCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent-one-writecheck-" + Guid.NewGuid().ToString("N")[..8]);

    public WriteCheckTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string Put(string name, string text)
    {
        File.WriteAllText(Path.Combine(_root, name), text);
        return name;
    }

    [Fact]
    public void Json_that_does_not_parse_is_reported_with_its_place()
    {
        var note = WriteCheck.Note(_root, Put("cues.json", "{\n  \"lines\": [1, 2\n}"))!;

        Assert.Contains("does NOT parse", note);
        Assert.Contains("invalid JSON at line 3", note);
        Assert.Null(WriteCheck.Note(_root, Put("ok.json", "{\"lines\": [1, 2,], /* c */ \"a\": 1}")));
    }

    [Fact]
    public void Other_files_and_good_files_are_left_alone()
    {
        Assert.Null(WriteCheck.Note(_root, Put("notes.md", "# a \"quote")));
        Assert.Null(WriteCheck.Note(_root, "missing.json"));
    }

    [Fact]
    public void Python_with_the_envelopes_quote_left_at_the_end_is_reported_when_python_is_here()
    {
        if (ExecutableLocator.Find("python") is null) return; // no interpreter, no check — by design

        var good = WriteCheck.Note(_root, Put("ok.py", "def full():\n    return 1\n\nfull()\n"));
        var note = WriteCheck.Note(_root, Put("mograph.py", "def full():\n    return 1\n\nfull()\""));
        if (good is not null || note is null) return; // a python that is only a store alias answers nothing useful

        // One quote, and the file parses without it: removed, and said so.
        Assert.StartsWith("[note]", note);
        Assert.Contains("It was removed", note);
        Assert.Equal("def full():\n    return 1\n\nfull()\n", File.ReadAllText(Path.Combine(_root, "mograph.py")));

        // A file that is wrong in some other way is reported and left exactly as written.
        const string broken = "def full(:\n    return 1\n\nfull()\"";
        var other = WriteCheck.Note(_root, Put("broken.py", broken))!;
        Assert.StartsWith("[check]", other);
        Assert.Contains("SyntaxError", other);
        Assert.Equal(broken, File.ReadAllText(Path.Combine(_root, "broken.py")));
    }

    [Fact]
    public void A_stray_quote_after_valid_json_is_removed_too()
    {
        var note = WriteCheck.Note(_root, Put("cues.json", "{\"a\": [1, 2]}\""))!;

        Assert.StartsWith("[note]", note);
        Assert.Equal("{\"a\": [1, 2]}\n", File.ReadAllText(Path.Combine(_root, "cues.json")));
    }

    [Fact]
    public void A_missing_interpreter_means_no_check_not_a_failure()
    {
        var before = WriteCheck.Locate;
        try
        {
            WriteCheck.Locate = _ => null;
            Assert.Null(WriteCheck.Note(_root, Put("broken.py", "def (:\n")));
        }
        finally { WriteCheck.Locate = before; }
    }

    [Fact]
    public async Task The_loop_says_it_with_the_write_and_takes_a_corrected_file()
    {
        var files = new LocalFileToolbelt(_root);
        var belt = new CompositeToolbelt((ToolCatalog.FilesFamily, files), (ToolCatalog.EditFamily, files));
        var provider = new ScriptedChatProvider(
            """{"tool":"write_file","args":{"path":"cues.json","content":"{ \"a\": [1, 2 }"}}""",
            """{"tool":"write_file","args":{"path":"cues.json","content":"{ \"a\": [1, 2] }"}}""",
            ToolCall.Final("wrote cues.json").ToJson());
        var loop = new AgentLoop(provider, belt, maxSteps: 6) { Root = _root };

        var run = await loop.RunAsync("write the cues");

        Assert.Equal(StopReason.Final, run.Reason);
        Assert.Contains("does NOT parse", provider.Calls[1][^1].Content);
        Assert.DoesNotContain("does NOT parse", provider.Calls[2][^1].Content);
        Assert.Equal("{ \"a\": [1, 2] }", File.ReadAllText(Path.Combine(_root, "cues.json")));
    }
}
