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
}
