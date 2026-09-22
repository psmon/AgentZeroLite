using AgentOne.Agent;
using AgentOne.Tools;

namespace AgentOne.Tests;

/// <summary>
/// The sandbox is the whole security story of v0, so its boundary gets the
/// tests: what is inside the root is readable, everything else is refused —
/// and a refusal is a ToolResult, never an exception the loop has to catch.
/// </summary>
public class LocalFileToolbeltTests : IDisposable
{
    private readonly string _root;

    public LocalFileToolbeltTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agent-one-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
        File.WriteAllText(Path.Combine(_root, "README.md"), "# hello\n");
        File.WriteAllText(Path.Combine(_root, "src", "main.cs"), "class C {}\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static ToolResult Invoke(IToolbelt belt, string tool, string path)
    {
        var call = new ToolCall { Tool = tool, Args = { ["path"] = path } };
        return belt.InvokeAsync(call, CancellationToken.None).GetAwaiter().GetResult();
    }

    [Fact]
    public void ReadsFileInsideRoot()
    {
        var result = Invoke(new LocalFileToolbelt(_root), "read_file", "README.md");
        Assert.True(result.Ok);
        Assert.Contains("# hello", result.Text);
    }

    [Fact]
    public void ListsDirectoryAndHidesNoiseFolders()
    {
        var result = Invoke(new LocalFileToolbelt(_root), "list_files", ".");
        Assert.True(result.Ok);
        Assert.Contains("src/", result.Text);
        Assert.Contains("README.md", result.Text);
        Assert.DoesNotContain("node_modules", result.Text);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("src/../../outside.txt")]
    [InlineData("./src/./../../etc/passwd")]
    public void RefusesRelativeEscape(string path)
    {
        var result = Invoke(new LocalFileToolbelt(_root), "read_file", path);
        Assert.False(result.Ok);
        Assert.Contains("escapes the workspace root", result.Text);
    }

    [Fact]
    public void RefusesAbsolutePathOutsideRoot()
    {
        var outside = Path.Combine(Path.GetTempPath(), "agent-one-outside.txt");
        File.WriteAllText(outside, "secret");
        try
        {
            var result = Invoke(new LocalFileToolbelt(_root), "read_file", outside);
            Assert.False(result.Ok);
            Assert.DoesNotContain("secret", result.Text);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void AcceptsAbsolutePathInsideRoot()
    {
        var result = Invoke(new LocalFileToolbelt(_root), "read_file", Path.Combine(_root, "README.md"));
        Assert.True(result.Ok);
        Assert.Contains("# hello", result.Text);
    }

    [Fact]
    public void ReportsMissingFileWithoutThrowing()
    {
        var result = Invoke(new LocalFileToolbelt(_root), "read_file", "nope.txt");
        Assert.False(result.Ok);
        Assert.Contains("no such file", result.Text);
    }

    [Fact]
    public void ReportsUnknownToolWithTheCatalog()
    {
        var result = Invoke(new LocalFileToolbelt(_root), "delete_everything", ".");
        Assert.False(result.Ok);
        Assert.Contains("unknown tool", result.Text);
        Assert.Contains("read_file", result.Text);
    }

    // --- find_files / grep ------------------------------------------------

    [Fact]
    public void FindFilesMatchesByNameAnywhereUnderThePath()
    {
        var belt = new LocalFileToolbelt(_root);
        var call = new ToolCall { Tool = "find_files", Args = { ["pattern"] = "*.cs" } };

        var result = belt.InvokeAsync(call, CancellationToken.None).GetAwaiter().GetResult();

        Assert.True(result.Ok);
        Assert.Contains("main.cs", result.Text);           // nested under src/
        Assert.DoesNotContain("README.md", result.Text);
    }

    [Fact]
    public void FindFilesSkipsTheNoiseFolders()
    {
        File.WriteAllText(Path.Combine(_root, "node_modules", "junk.cs"), "x");
        var belt = new LocalFileToolbelt(_root);

        var result = belt.InvokeAsync(
            new ToolCall { Tool = "find_files", Args = { ["pattern"] = "*.cs" } },
            CancellationToken.None).GetAwaiter().GetResult();

        Assert.DoesNotContain("junk.cs", result.Text);
    }

    [Fact]
    public void FindFilesWithNoMatchSaysSoAndSucceeds()
    {
        var belt = new LocalFileToolbelt(_root);

        var result = belt.InvokeAsync(
            new ToolCall { Tool = "find_files", Args = { ["pattern"] = "*.nope" } },
            CancellationToken.None).GetAwaiter().GetResult();

        Assert.True(result.Ok);
        Assert.Contains("no files matching", result.Text);
    }

    [Fact]
    public void FindFilesNeedsAPattern()
    {
        var belt = new LocalFileToolbelt(_root);

        var result = belt.InvokeAsync(new ToolCall { Tool = "find_files" }, CancellationToken.None)
                         .GetAwaiter().GetResult();

        Assert.False(result.Ok);
        Assert.Contains("pattern", result.Text);
    }

    [Fact]
    public void GrepReportsFileAndLineNumber()
    {
        var belt = new LocalFileToolbelt(_root);

        var result = belt.InvokeAsync(
            new ToolCall { Tool = "grep", Args = { ["text"] = "class C" } },
            CancellationToken.None).GetAwaiter().GetResult();

        Assert.True(result.Ok);
        Assert.Contains("main.cs:1:", result.Text);
    }

    [Fact]
    public void GrepIsCaseInsensitive()
    {
        var belt = new LocalFileToolbelt(_root);

        var result = belt.InvokeAsync(
            new ToolCall { Tool = "grep", Args = { ["text"] = "HELLO" } },
            CancellationToken.None).GetAwaiter().GetResult();

        Assert.Contains("README.md", result.Text);
    }

    [Fact]
    public void GrepCanBeNarrowedByGlob()
    {
        var belt = new LocalFileToolbelt(_root);

        var result = belt.InvokeAsync(
            new ToolCall { Tool = "grep", Args = { ["text"] = "hello", ["glob"] = "*.cs" } },
            CancellationToken.None).GetAwaiter().GetResult();

        Assert.DoesNotContain("README.md", result.Text);
    }

    [Fact]
    public void GrepWithNoHitsSaysHowManyFilesItLookedAt()
    {
        var belt = new LocalFileToolbelt(_root);

        var result = belt.InvokeAsync(
            new ToolCall { Tool = "grep", Args = { ["text"] = "zzz-not-present-zzz" } },
            CancellationToken.None).GetAwaiter().GetResult();

        Assert.True(result.Ok);
        Assert.Contains("not found", result.Text);
    }

    [Fact]
    public void GrepSkipsBinaryFiles()
    {
        File.WriteAllBytes(Path.Combine(_root, "blob.bin"), [0x00, 0x01, 0x02, 0x00]);
        var belt = new LocalFileToolbelt(_root);

        var result = belt.InvokeAsync(
            new ToolCall { Tool = "grep", Args = { ["text"] = "hello" } },
            CancellationToken.None).GetAwaiter().GetResult();

        Assert.DoesNotContain("blob.bin", result.Text);
    }

    [Theory]
    [InlineData("find_files")]
    [InlineData("grep")]
    public void TheNewVerbsRespectTheSandbox(string tool)
    {
        var belt = new LocalFileToolbelt(_root);
        var call = new ToolCall
        {
            Tool = tool,
            Args = { ["pattern"] = "*", ["text"] = "x", ["path"] = "../.." }
        };

        var result = belt.InvokeAsync(call, CancellationToken.None).GetAwaiter().GetResult();

        Assert.False(result.Ok);
        Assert.Contains("escapes the workspace root", result.Text);
    }

    [Fact]
    public void TruncatesFilesOverTheReadCap()
    {
        var big = Path.Combine(_root, "big.txt");
        File.WriteAllText(big, new string('x', LocalFileToolbelt.MaxReadBytes + 5_000));

        var result = Invoke(new LocalFileToolbelt(_root), "read_file", "big.txt");
        Assert.True(result.Ok);
        Assert.Contains("truncated", result.Text);
        Assert.True(result.Text.Length < LocalFileToolbelt.MaxReadBytes + 500);
    }
}
