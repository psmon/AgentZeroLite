using System.IO;
using System.Text.Json;
using Agent.Common.Llm.Tools;

namespace ZeroCommon.Tests;

/// <summary>
/// Headless tests for the pure file-tool logic (mission W8, orca-adoption).
/// Covers the sandbox boundary (path-escape denial, unbound root),
/// read/write/edit round-trips, edit ambiguity handling, and grep.
/// </summary>
[Trait("Category", "FileTools")]
public sealed class FileToolCoreTests : IDisposable
{
    private readonly string _root;

    public FileToolCoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aztest-filetools-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;
    private static bool Ok(string json) => Parse(json).GetProperty("ok").GetBoolean();
    private static string Err(string json) => Parse(json).GetProperty("error").GetString() ?? "";

    // ---------------------------------------------------------------- sandbox

    [Fact]
    public void NullRoot_IsDefaultDeny()
    {
        Assert.False(Ok(FileToolCore.ReadFile(null, "a.txt")));
        Assert.False(Ok(FileToolCore.WriteFile(null, "a.txt", "x")));
        Assert.False(Ok(FileToolCore.Edit(null, "a.txt", "x", "y")));
        Assert.False(Ok(FileToolCore.Grep(null, "x")));
    }

    [Fact]
    public void PathEscapingRoot_IsRejected()
    {
        var res = FileToolCore.ReadFile(_root, "../../etc/passwd");
        Assert.False(Ok(res));
        Assert.Contains("escapes", Err(res));
    }

    [Fact]
    public void AbsolutePathOutsideRoot_IsRejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("n") + ".txt");
        File.WriteAllText(outside, "secret");
        try
        {
            var res = FileToolCore.ReadFile(_root, outside);
            Assert.False(Ok(res));
        }
        finally { File.Delete(outside); }
    }

    // ---------------------------------------------------------------- read/write

    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        var w = FileToolCore.WriteFile(_root, "sub/hello.txt", "hi there");
        Assert.True(Ok(w));
        Assert.True(Parse(w).GetProperty("created").GetBoolean());

        var r = FileToolCore.ReadFile(_root, "sub/hello.txt");
        Assert.True(Ok(r));
        Assert.Equal("hi there", Parse(r).GetProperty("text").GetString());
    }

    [Fact]
    public void Read_MissingFile_Fails()
    {
        Assert.False(Ok(FileToolCore.ReadFile(_root, "nope.txt")));
    }

    [Fact]
    public void Read_BinaryFile_IsRejected()
    {
        File.WriteAllBytes(Path.Combine(_root, "b.bin"), new byte[] { 1, 2, 0, 3, 4 });
        var res = FileToolCore.ReadFile(_root, "b.bin");
        Assert.False(Ok(res));
        Assert.Contains("binary", Err(res));
    }

    [Fact]
    public void Read_Truncates_AtMaxBytes()
    {
        FileToolCore.WriteFile(_root, "big.txt", new string('a', 1000));
        var res = FileToolCore.ReadFile(_root, "big.txt", maxBytes: 100);
        Assert.True(Ok(res));
        Assert.True(Parse(res).GetProperty("truncated").GetBoolean());
        Assert.Equal(100, Parse(res).GetProperty("text").GetString()!.Length);
    }

    // ---------------------------------------------------------------- edit

    [Fact]
    public void Edit_UniqueOccurrence_Replaces()
    {
        FileToolCore.WriteFile(_root, "e.txt", "alpha beta gamma");
        var res = FileToolCore.Edit(_root, "e.txt", "beta", "BETA");
        Assert.True(Ok(res));
        Assert.Equal("alpha BETA gamma", Parse(FileToolCore.ReadFile(_root, "e.txt")).GetProperty("text").GetString());
    }

    [Fact]
    public void Edit_AmbiguousWithoutReplaceAll_Fails()
    {
        FileToolCore.WriteFile(_root, "e.txt", "x x x");
        var res = FileToolCore.Edit(_root, "e.txt", "x", "y");
        Assert.False(Ok(res));
        Assert.Contains("ambiguous", Err(res));
    }

    [Fact]
    public void Edit_ReplaceAll_ReplacesEvery()
    {
        FileToolCore.WriteFile(_root, "e.txt", "x x x");
        var res = FileToolCore.Edit(_root, "e.txt", "x", "y", replaceAll: true);
        Assert.True(Ok(res));
        Assert.Equal(3, Parse(res).GetProperty("replaced").GetInt32());
        Assert.Equal("y y y", Parse(FileToolCore.ReadFile(_root, "e.txt")).GetProperty("text").GetString());
    }

    [Fact]
    public void Edit_MissingTarget_Fails()
    {
        FileToolCore.WriteFile(_root, "e.txt", "abc");
        Assert.False(Ok(FileToolCore.Edit(_root, "e.txt", "zzz", "y")));
    }

    // ---------------------------------------------------------------- grep

    [Fact]
    public void Grep_FindsMatches_AndSkipsIgnoredDirs()
    {
        FileToolCore.WriteFile(_root, "src/a.cs", "public void Foo() {}\nvar x = 1;");
        FileToolCore.WriteFile(_root, "src/b.cs", "nothing here");
        // A match inside an ignored dir must NOT surface.
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        File.WriteAllText(Path.Combine(_root, "bin", "gen.cs"), "public void Foo() {}");

        var res = FileToolCore.Grep(_root, @"Foo\(\)");
        Assert.True(Ok(res));
        var matches = Parse(res).GetProperty("matches");
        Assert.Equal(1, Parse(res).GetProperty("count").GetInt32());
        Assert.Equal("src/a.cs", matches[0].GetProperty("file").GetString());
        Assert.Equal(1, matches[0].GetProperty("line").GetInt32());
    }

    [Fact]
    public void Grep_InvalidRegex_Fails()
    {
        Assert.False(Ok(FileToolCore.Grep(_root, "(")));
    }

    [Fact]
    public void Grep_RespectsMaxResults()
    {
        FileToolCore.WriteFile(_root, "m.txt", "hit\nhit\nhit\nhit");
        var res = FileToolCore.Grep(_root, "hit", maxResults: 2);
        Assert.True(Ok(res));
        Assert.Equal(2, Parse(res).GetProperty("count").GetInt32());
        Assert.True(Parse(res).GetProperty("truncated").GetBoolean());
    }

    // -------------------------------------------------- grammar lockstep guard

    [Theory]
    [InlineData("read_file")]
    [InlineData("write_file")]
    [InlineData("edit_file")]
    [InlineData("grep")]
    public void FileTools_AreRegistered_InGrammarAndCatalog(string tool)
    {
        // The tool name must appear in all three lockstep locations, else the
        // grammar/validation/prompt drift and the model can't call the tool.
        Assert.Contains(tool, AgentToolGrammar.KnownTools);
        Assert.Contains($"\\\"{tool}\\\"", AgentToolGrammar.Gbnf);
        Assert.Contains(tool, AgentToolGrammar.SystemPrompt);
    }
}
