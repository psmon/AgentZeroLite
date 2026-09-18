using System.IO;
using System.Text.Json;
using Agent.Common.Llm.Tools;
using Agent.Common.Wearable;

namespace ZeroCommon.Tests;

/// <summary>
/// The allow-list boundary of the wearable file tools (M0032). Every way a model could
/// reach outside a granted folder — an unknown alias, an absolute path, a <c>..</c>, a
/// jump from one root into another — has to come back as a refusal, and nothing the
/// resolver returns may leak the real folder path.
/// </summary>
[Trait("Category", "FileTools")]
public sealed class AllowedRootResolverTests
{
    private static AllowedRootResolver TwoRoots() => new(new[]
    {
        new AllowedRoot { Alias = "Docs", Path = Path.Combine(Path.GetTempPath(), "az-docs"), Writable = false },
        new AllowedRoot { Alias = "music", Path = Path.Combine(Path.GetTempPath(), "az-music"), Writable = true },
    });

    [Fact]
    public void Aliases_are_normalised_and_deduplicated()
    {
        var r = new AllowedRootResolver(new[]
        {
            new AllowedRoot { Alias = "My Docs!", Path = @"C:\a" },
            new AllowedRoot { Alias = "my-docs", Path = @"C:\b" },     // same alias after normalisation → dropped
            new AllowedRoot { Alias = "", Path = @"C:\c" },            // no alias → dropped
            new AllowedRoot { Alias = "x", Path = "" },                // no path → dropped
        });

        Assert.Single(r.Roots);
        Assert.Equal("my-docs", r.Roots[0].Alias);
        Assert.Equal(Path.GetFullPath(@"C:\a"), r.Roots[0].Path);
    }

    [Fact]
    public void Alias_only_resolves_to_the_root_itself()
    {
        Assert.True(TwoRoots().TryResolve("docs", out var root, out var rel, out _));
        Assert.Equal("docs", root.Alias);
        Assert.Equal(".", rel);
    }

    [Theory]
    [InlineData("docs/notes/today.md", "docs", "notes/today.md")]
    [InlineData("DOCS\\notes\\today.md", "docs", "notes/today.md")]
    [InlineData("music/", "music", ".")]
    public void Nested_paths_resolve_case_insensitively(string input, string alias, string expectedRel)
    {
        Assert.True(TwoRoots().TryResolve(input, out var root, out var rel, out var error), error);
        Assert.Equal(alias, root.Alias);
        Assert.Equal(expectedRel, rel);
    }

    [Theory]
    [InlineData("photos/a.png", "unknown root alias")]
    [InlineData(@"C:\Windows\system32\cmd.exe", "absolute paths")]
    [InlineData("/etc/passwd", "absolute paths")]
    [InlineData("docs/../music/song.mp3", "escapes")]
    [InlineData("docs/..", "escapes")]
    [InlineData("", "must not be empty")]
    public void Escapes_and_unknowns_are_refused(string input, string expectedFragment)
    {
        Assert.False(TwoRoots().TryResolve(input, out _, out _, out var error));
        Assert.Contains(expectedFragment, error);
    }

    [Fact]
    public void Empty_resolver_is_default_deny()
    {
        var r = new AllowedRootResolver(null);
        Assert.True(r.IsEmpty);
        Assert.False(r.TryResolve("docs/a.txt", out _, out _, out var error));
        Assert.Equal(AllowedRootResolver.NoRootsError, error);
    }

    [Fact]
    public void Root_listing_shows_aliases_and_grants_but_never_paths()
    {
        var json = TwoRoots().ListRootsJson();
        using var doc = JsonDocument.Parse(json);
        var roots = doc.RootElement.GetProperty("roots");
        Assert.Equal(2, roots.GetArrayLength());
        Assert.Equal("docs", roots[0].GetProperty("alias").GetString());
        Assert.False(roots[0].GetProperty("writable").GetBoolean());
        Assert.True(roots[1].GetProperty("writable").GetBoolean());
        Assert.DoesNotContain("az-docs", json);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd('\\'), json);
    }

    [Theory]
    [InlineData("docs", ".", "docs")]
    [InlineData("docs", "", "docs")]
    [InlineData("docs", "a\\b.txt", "docs/a/b.txt")]
    public void Prefix_rebuilds_the_alias_form(string alias, string rel, string expected)
        => Assert.Equal(expected, AllowedRootResolver.Prefix(alias, rel));
}
