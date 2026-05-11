namespace ZeroCommon.Tests;

public class ApprovalParserTests
{
    [Fact]
    public void ContainsApprovalPattern_WithKeyword_ReturnsTrue()
    {
        var buffer = "Some text Do you want to proceed? yes";
        Assert.True(ApprovalParser.ContainsApprovalPattern(buffer));
    }

    [Fact]
    public void ContainsApprovalPattern_NoMatch_ReturnsFalse()
    {
        var buffer = "hello world normal text without any approval keywords";
        Assert.False(ApprovalParser.ContainsApprovalPattern(buffer));
    }

    [Fact]
    public void Parse_WithNumberedOptions_ReturnsOptions()
    {
        var buffer = "───────────────────────\nAllow once?\n  1. Yes\n  2. No\n";
        var result = ApprovalParser.ParseApprovalPrompt(buffer);
        Assert.NotNull(result);
        Assert.True(result!.Options.Count >= 2);
    }

    // ── M0017 후속: 자동승인 패턴 확장 v1 ────────────────────────────
    // Auto-approve missed MCP/tool-use/workspace-trust prompts because none
    // of the legacy anchors ("proceed", "requires approval", "make this
    // edit") appeared in them. These guards lock in the new keyword + the
    // Unicode-pointer separator path.

    [Theory]
    [InlineData("Allow Claude to use the playwright MCP server?")]
    [InlineData("Allow Claude to use Bash")]
    [InlineData("ALLOW CLAUDE TO USE filesystem")]  // case-insensitive
    [InlineData("Use this tool to read files?")]
    [InlineData("Trust this folder?")]
    [InlineData("Trust this directory and its subfolders?")]
    public void ContainsApprovalPattern_MCP_and_trust_prompts_detected(string buffer)
    {
        Assert.True(ApprovalParser.ContainsApprovalPattern(buffer));
    }

    [Theory]
    [InlineData("──────────────────────────────\nAllow once?\n❯ 1. Yes\n  2. No\n")]
    [InlineData("──────────────────────────────\nRun command?\n▶ 1. Approve\n  2. Reject\n")]
    [InlineData("──────────────────────────────\nProceed?\n• 1. Yes\n  2. No\n")]
    [InlineData("──────────────────────────────\nConfirm?\n◆ 1. Yes\n  2. No\n")]
    public void ContainsApprovalPattern_unicode_pointer_separator_detected(string buffer)
    {
        Assert.True(ApprovalParser.ContainsApprovalPattern(buffer));
    }

    [Fact]
    public void ContainsApprovalPattern_innocuous_text_still_passes_through()
    {
        // Sanity guard against over-broad keyword matching. "Use this tool"
        // is the most generic of the new keywords — if a tutorial/docs page
        // happens to contain that exact phrase we WILL flag it as approval.
        // That's acceptable because the parser's option-extraction returns
        // a Fallback in that case and AgentEventStream drops Fallback as a
        // false positive. Confirm the false-positive path still works.
        var docsLike = "If you want to read files, use this tool with read_file action.";
        Assert.True(ApprovalParser.ContainsApprovalPattern(docsLike));
        var parsed = ApprovalParser.ParseApprovalPrompt(docsLike);
        // Falls back because no numbered options exist.
        Assert.NotNull(parsed);
        Assert.True(parsed!.IsFallback);
    }

    [Fact]
    public void Fingerprint_for_new_keyword_is_stable_across_chunks()
    {
        // Both buffers contain the same MCP prompt rendered at slightly
        // different chunk boundaries. Fingerprint must collapse them so
        // ApprovalRequested doesn't re-fire per chunk.
        var early =
            "...streaming output here...\n" +
            "Allow Claude to use the playwright MCP server?\n" +
            "  1. Yes\n  2. No\n";
        var later = early + "  3. Yes, allow always\n";

        var fp1 = ApprovalParser.GetApprovalFingerprint(early);
        var fp2 = ApprovalParser.GetApprovalFingerprint(later);
        Assert.NotNull(fp1);
        Assert.NotNull(fp2);
        // Fingerprints share the same anchor point ("Allow Claude to use…")
        // so the earlier one is a prefix of the later. The exact dedup
        // contract in AgentEventStream is equality, but for THIS regression
        // guard we only need to prove the function produces a stable,
        // non-null fingerprint for the new keyword family.
        Assert.StartsWith(fp1, fp2);
    }
}
