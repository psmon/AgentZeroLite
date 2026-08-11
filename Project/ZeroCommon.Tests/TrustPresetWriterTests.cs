using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Agent.Common.Agents;

namespace ZeroCommon.Tests;

/// <summary>
/// Headless tests for trust-preset writing (mission W2, orca-adoption): pure
/// slug/TOML/JSON transforms plus a full IO round-trip against a temp home dir.
/// </summary>
[Trait("Category", "TrustPreset")]
public sealed class TrustPresetWriterTests : IDisposable
{
    private readonly string _home;

    public TrustPresetWriterTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "aztest-trust-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    // ── cursor slug ──────────────────────────────────────────────────────────

    [Fact]
    public void CursorSlug_ReplacesIllegalChars_AndStripsLeadingSep()
    {
        var slug = TrustPresetWriter.CursorSlug(@"C:\code\my proj");
        Assert.DoesNotContain(":", slug);
        Assert.DoesNotContain("\\", slug);
        Assert.StartsWith("C-", slug); // "C:" → "C-"
    }

    [Fact]
    public void CursorSlug_CollapsesConsecutiveSeparators()
    {
        var slug = TrustPresetWriter.CursorSlug("/home//user/proj");
        Assert.DoesNotContain("--", slug);
    }

    // ── codex TOML upsert ────────────────────────────────────────────────────

    [Fact]
    public void UpsertCodexTrust_AppendsSection()
    {
        var (toml, changed) = TrustPresetWriter.UpsertCodexTrust("", @"C:\code\proj");
        Assert.True(changed);
        Assert.Contains("[projects.\"C:\\\\code\\\\proj\"]", toml);
        Assert.Contains("trust_level = \"trusted\"", toml);
    }

    [Fact]
    public void UpsertCodexTrust_Idempotent()
    {
        var (toml1, _) = TrustPresetWriter.UpsertCodexTrust("", @"C:\code\proj");
        var (toml2, changed) = TrustPresetWriter.UpsertCodexTrust(toml1, @"C:\code\proj");
        Assert.False(changed);
        Assert.Equal(toml1, toml2);
    }

    [Fact]
    public void UpsertCodexTrust_PreservesExistingContent()
    {
        var existing = "[foo]\nbar = 1\n";
        var (toml, changed) = TrustPresetWriter.UpsertCodexTrust(existing, "/home/p");
        Assert.True(changed);
        Assert.Contains("[foo]", toml);
        Assert.Contains("[projects.\"/home/p\"]", toml);
    }

    // ── copilot JSON merge ───────────────────────────────────────────────────

    [Fact]
    public void AddCopilotFolder_CreatesArray()
    {
        var (json, changed) = TrustPresetWriter.AddCopilotFolder("", "/home/p");
        Assert.True(changed);
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.GetProperty("trustedFolders").EnumerateArray().Select(e => e.GetString());
        Assert.Contains("/home/p", arr);
    }

    [Fact]
    public void AddCopilotFolder_Idempotent()
    {
        var (json1, _) = TrustPresetWriter.AddCopilotFolder("", "/home/p");
        var (_, changed) = TrustPresetWriter.AddCopilotFolder(json1, "/home/p");
        Assert.False(changed);
    }

    [Fact]
    public void AddCopilotFolder_PreservesForeignKeys()
    {
        var existing = "{\"theme\":\"dark\",\"trustedFolders\":[\"/a\"]}";
        var (json, changed) = TrustPresetWriter.AddCopilotFolder(existing, "/b");
        Assert.True(changed);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("dark", doc.RootElement.GetProperty("theme").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("trustedFolders").GetArrayLength());
    }

    // ── full IO round-trip ───────────────────────────────────────────────────

    [Fact]
    public void MarkAllTrusted_WritesAllThreeStores()
    {
        var ws = Path.Combine(_home, "workspace");
        Directory.CreateDirectory(ws);

        var results = TrustPresetWriter.MarkAllTrusted(ws, _home);
        Assert.All(results, r => Assert.True(r.Ok, $"{r.Agent}: {r.Detail}"));

        Assert.True(File.Exists(Path.Combine(_home, ".copilot", "config.json")));
        Assert.True(File.Exists(Path.Combine(_home, ".codex", "config.toml")));
        // cursor slug dir exists under ~/.cursor/projects
        Assert.True(Directory.Exists(Path.Combine(_home, ".cursor", "projects")));
    }

    [Fact]
    public void MarkAllTrusted_SecondRun_IsIdempotent()
    {
        var ws = Path.Combine(_home, "workspace");
        Directory.CreateDirectory(ws);
        TrustPresetWriter.MarkAllTrusted(ws, _home);
        var second = TrustPresetWriter.MarkAllTrusted(ws, _home);
        Assert.All(second, r => Assert.True(r.Ok));
        Assert.Contains(second, r => r.Detail == "already trusted");
    }
}
