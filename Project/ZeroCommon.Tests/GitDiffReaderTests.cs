using System.Linq;
using Agent.Common.Module;

namespace ZeroCommon.Tests;

/// <summary>
/// Headless tests for the unified-diff parser (mission W3, orca-adoption).
/// </summary>
[Trait("Category", "GitDiff")]
public sealed class GitDiffReaderTests
{
    private const string SingleFile =
        "diff --git a/src/app.cs b/src/app.cs\n" +
        "index 111..222 100644\n" +
        "--- a/src/app.cs\n" +
        "+++ b/src/app.cs\n" +
        "@@ -1,4 +1,4 @@\n" +
        " using System;\n" +
        "-var x = 1;\n" +
        "+var x = 2;\n" +
        " Console.WriteLine(x);\n" +
        " // end\n";

    [Fact]
    public void Parse_SingleFile_ExtractsPathsAndHunk()
    {
        var files = GitDiffReader.Parse(SingleFile);
        Assert.Single(files);
        Assert.Equal("src/app.cs", files[0].NewPath);
        Assert.Single(files[0].Hunks);
        var hunk = files[0].Hunks[0];
        Assert.Equal(1, hunk.OldStart);
        Assert.Equal(1, hunk.NewStart);
    }

    [Fact]
    public void Parse_AssignsLineNumbersAndKinds()
    {
        var hunk = GitDiffReader.Parse(SingleFile)[0].Hunks[0];

        var del = hunk.Lines.Single(l => l.Kind == GitDiffReader.LineKind.Delete);
        Assert.Equal("var x = 1;", del.Text);
        Assert.Equal(2, del.OldLineNo);
        Assert.Null(del.NewLineNo);

        var add = hunk.Lines.Single(l => l.Kind == GitDiffReader.LineKind.Add);
        Assert.Equal("var x = 2;", add.Text);
        Assert.Equal(2, add.NewLineNo);
        Assert.Null(add.OldLineNo);

        // Context after the change keeps advancing both counters.
        var ctxLast = hunk.Lines.Last(l => l.Kind == GitDiffReader.LineKind.Context);
        Assert.Equal("// end", ctxLast.Text);
        Assert.Equal(4, ctxLast.NewLineNo);
    }

    [Fact]
    public void Parse_MultipleFiles()
    {
        var diff = SingleFile +
            "diff --git a/b.txt b/b.txt\n" +
            "--- a/b.txt\n" +
            "+++ b/b.txt\n" +
            "@@ -0,0 +1,1 @@\n" +
            "+hello\n";
        var files = GitDiffReader.Parse(diff);
        Assert.Equal(2, files.Count);
        Assert.Equal("b.txt", files[1].NewPath);
        Assert.Equal("hello", files[1].Hunks[0].Lines.Single().Text);
    }

    [Fact]
    public void Parse_NewFile_IsFlagged()
    {
        var diff =
            "diff --git a/new.txt b/new.txt\n" +
            "new file mode 100644\n" +
            "index 000..abc\n" +
            "--- /dev/null\n" +
            "+++ b/new.txt\n" +
            "@@ -0,0 +1,2 @@\n" +
            "+line1\n" +
            "+line2\n";
        var f = GitDiffReader.Parse(diff).Single();
        Assert.True(f.IsNew);
        Assert.Equal("new.txt", f.NewPath);
        Assert.Equal(2, f.Hunks[0].Lines.Count);
    }

    [Fact]
    public void Parse_BinaryFile_IsFlagged_NoHunks()
    {
        var diff =
            "diff --git a/img.png b/img.png\n" +
            "index 000..abc 100644\n" +
            "Binary files a/img.png and b/img.png differ\n";
        var f = GitDiffReader.Parse(diff).Single();
        Assert.True(f.IsBinary);
        Assert.Empty(f.Hunks);
    }

    [Fact]
    public void Parse_Empty_ReturnsNoFiles()
    {
        Assert.Empty(GitDiffReader.Parse(""));
        Assert.Empty(GitDiffReader.Parse("not a diff\njust text\n"));
    }

    [Fact]
    public void Parse_MultiHunk_TracksLineNumbersAcrossHunks()
    {
        var diff =
            "diff --git a/f.cs b/f.cs\n" +
            "--- a/f.cs\n" +
            "+++ b/f.cs\n" +
            "@@ -1,2 +1,2 @@\n" +
            " a\n" +
            "-b\n" +
            "+B\n" +
            "@@ -10,2 +10,3 @@\n" +
            " x\n" +
            "+Y\n" +
            " z\n";
        var f = GitDiffReader.Parse(diff).Single();
        Assert.Equal(2, f.Hunks.Count);
        Assert.Equal(10, f.Hunks[1].NewStart);
        var added = f.Hunks[1].Lines.Single(l => l.Kind == GitDiffReader.LineKind.Add);
        Assert.Equal(11, added.NewLineNo);
    }
}
