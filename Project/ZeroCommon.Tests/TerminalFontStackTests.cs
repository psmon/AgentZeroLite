using Agent.Common.Services;

namespace ZeroCommon.Tests;

/// <summary>
/// Picking an installed font in Settings rewrites the head of the CSS stack and keeps the
/// fallbacks — those catch the glyphs (Hangul, box drawing) the picked font lacks.
/// </summary>
public class TerminalFontStackTests
{
    private const string Default = "JetBrains Mono, Cascadia Mono, Consolas, monospace";

    [Theory]
    [InlineData(null, "")]
    [InlineData("  ", "")]
    [InlineData(Default, "JetBrains Mono")]
    [InlineData("\"3270 Nerd Font\", monospace", "3270 Nerd Font")]
    [InlineData("'D2Coding' , monospace", "D2Coding")]
    public void Primary_is_the_first_family_unquoted(string? stack, string expected) =>
        Assert.Equal(expected, TerminalFontStack.Primary(stack));

    [Fact]
    public void Picking_a_font_replaces_the_head_and_keeps_the_fallbacks() =>
        Assert.Equal("D2Coding, Cascadia Mono, Consolas, monospace", TerminalFontStack.WithPrimary(Default, "D2Coding"));

    [Fact]
    public void A_picked_font_already_in_the_fallbacks_is_not_listed_twice() =>
        Assert.Equal("Consolas, Cascadia Mono, monospace", TerminalFontStack.WithPrimary(Default, "Consolas"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Fira Code")]
    public void A_stack_with_nothing_behind_the_pick_gets_monospace(string? stack) =>
        Assert.Equal("Cascadia Code, monospace", TerminalFontStack.WithPrimary(stack, "Cascadia Code"));

    [Theory]
    [InlineData("Cascadia Mono", "Cascadia Mono")]
    [InlineData("나눔고딕코딩", "나눔고딕코딩")]                       // non-ASCII is a valid bare identifier
    [InlineData("3270 Nerd Font", "\"3270 Nerd Font\"")]              // a word starting with a digit is not
    [InlineData("Iosevka Term SS04.1", "\"Iosevka Term SS04.1\"")]  // nor one with a dot
    public void Names_that_are_not_css_identifiers_are_quoted(string family, string expected) =>
        Assert.Equal(expected, TerminalFontStack.Quote(family));

    [Fact]
    public void A_quoted_pick_reads_back_as_the_same_primary()
    {
        var stack = TerminalFontStack.WithPrimary(Default, "3270 Nerd Font");
        Assert.StartsWith("\"3270 Nerd Font\", ", stack);
        Assert.Equal("3270 Nerd Font", TerminalFontStack.Primary(stack));
    }
}
