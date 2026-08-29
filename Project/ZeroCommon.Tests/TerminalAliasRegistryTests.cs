using System;
using System.IO;
using Agent.Common.Agents;
using Xunit;

namespace ZeroCommon.Tests;

[Trait("Category", "TerminalAlias")]
public sealed class TerminalAliasRegistryTests
{
    [Fact]
    public void Set_then_Resolve_returns_stable_target()
    {
        var r = new TerminalAliasRegistry();
        Assert.True(r.Set("build", "myrepo", "Claude"));
        var t = r.Resolve("build");
        Assert.NotNull(t);
        Assert.Equal("myrepo", t!.GroupName);
        Assert.Equal("Claude", t.Title);
    }

    [Fact]
    public void Resolve_is_case_insensitive()
    {
        var r = new TerminalAliasRegistry();
        r.Set("Build", "g", "t");
        Assert.NotNull(r.Resolve("build"));
        Assert.NotNull(r.Resolve("BUILD"));
    }

    [Fact]
    public void Set_rejects_invalid_alias_and_empty_target()
    {
        var r = new TerminalAliasRegistry();
        Assert.False(r.Set("has space", "g", "t"));
        Assert.False(r.Set("bad/slash", "g", "t"));
        Assert.False(r.Set("", "g", "t"));
        Assert.False(r.Set("ok", "", "t"));
        Assert.False(r.Set("ok", "g", "  "));
        Assert.Null(r.Resolve("ok"));
    }

    [Theory]
    [InlineData("build", true)]
    [InlineData("agent-1", true)]
    [InlineData("A_b-2", true)]
    [InlineData("has space", false)]
    [InlineData("slash/x", false)]
    [InlineData("", false)]
    public void IsValidAlias(string alias, bool expected)
        => Assert.Equal(expected, TerminalAliasRegistry.IsValidAlias(alias));

    [Fact]
    public void Set_reassigns_and_Remove_drops()
    {
        var r = new TerminalAliasRegistry();
        r.Set("x", "g1", "t1");
        r.Set("x", "g2", "t2"); // reassign
        Assert.Equal("g2", r.Resolve("x")!.GroupName);
        Assert.True(r.Remove("x"));
        Assert.Null(r.Resolve("x"));
        Assert.False(r.Remove("x")); // already gone
    }

    [Fact]
    public void Prune_drops_aliases_whose_target_is_gone()
    {
        var r = new TerminalAliasRegistry();
        r.Set("live", "g", "Alive");
        r.Set("dead", "g", "Gone");
        var pruned = r.Prune(new[] { new TerminalAliasRegistry.AliasTarget("g", "Alive") });
        Assert.Equal(1, pruned);
        Assert.NotNull(r.Resolve("live"));
        Assert.Null(r.Resolve("dead"));
    }

    [Fact]
    public void Json_roundtrip()
    {
        var r = new TerminalAliasRegistry();
        r.Set("build", "myrepo", "Claude");
        r.Set("test", "myrepo", "PW7");
        var restored = TerminalAliasRegistry.FromJson(r.ToJson());
        Assert.Equal("Claude", restored.Resolve("build")!.Title);
        Assert.Equal("PW7", restored.Resolve("test")!.Title);
    }

    [Fact]
    public void FromJson_tolerates_null_and_garbage()
    {
        Assert.Empty(TerminalAliasRegistry.FromJson(null).Entries);
        Assert.Empty(TerminalAliasRegistry.FromJson("{ not json").Entries);
    }

    [Fact]
    public void File_Load_Save_roundtrip_and_missing_defaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "az-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "terminal-aliases.json");
        try
        {
            Assert.Empty(TerminalAliasRegistry.Load(path).Entries); // missing → empty
            var r = new TerminalAliasRegistry();
            r.Set("build", "myrepo", "Claude");
            r.Save(path);
            Assert.Equal("Claude", TerminalAliasRegistry.Load(path).Resolve("build")!.Title);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
