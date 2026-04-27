using System.Text.RegularExpressions;

namespace Agent.Common.Voice;

/// <summary>
/// Strip Markdown formatting from text before sending to TTS so the
/// synthesizer reads natural prose instead of "asterisk asterisk bold".
/// Pure function — no LLM calls, no side effects. Ported from origin
/// (covered by 9 xUnit cases there; we'll re-add the test fixture once
/// concrete TTS providers ship).
/// </summary>
public static partial class TtsTextCleaner
{
    public static string StripMarkdown(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return markdown;

        var s = markdown;

        s = FencedCodeBlock().Replace(s, "");
        s = InlineCode().Replace(s, "$1");
        s = Image().Replace(s, "$1");
        s = Link().Replace(s, "$1");
        s = Heading().Replace(s, "$1");
        s = BoldItalic3Star().Replace(s, "$1");
        s = Bold2Star().Replace(s, "$1");
        s = Italic1Star().Replace(s, "$1");
        s = BoldItalic3Under().Replace(s, "$1");
        s = Bold2Under().Replace(s, "$1");
        s = Italic1Under().Replace(s, "$1");
        s = Strikethrough().Replace(s, "$1");
        s = Blockquote().Replace(s, "$1");
        s = UnorderedList().Replace(s, "");
        s = OrderedList().Replace(s, "");
        s = HorizontalRule().Replace(s, "");
        s = TableSeparator().Replace(s, "");
        s = TableRow().Replace(s, m =>
        {
            var inner = m.Value.Trim().Trim('|').Trim();
            return inner.Replace("|", ",");
        });
        s = HtmlTag().Replace(s, "");
        s = MultipleBlankLines().Replace(s, "\n");

        return s.Trim();
    }

    [GeneratedRegex(@"```[\s\S]*?```", RegexOptions.Compiled)]
    private static partial Regex FencedCodeBlock();

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.Compiled)]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^\)]+\)", RegexOptions.Compiled)]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]+)\]\([^\)]+\)", RegexOptions.Compiled)]
    private static partial Regex Link();

    [GeneratedRegex(@"^#{1,6}\s+(.+)$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex Heading();

    [GeneratedRegex(@"\*{3}(.+?)\*{3}", RegexOptions.Compiled)]
    private static partial Regex BoldItalic3Star();

    [GeneratedRegex(@"\*{2}(.+?)\*{2}", RegexOptions.Compiled)]
    private static partial Regex Bold2Star();

    [GeneratedRegex(@"\*(.+?)\*", RegexOptions.Compiled)]
    private static partial Regex Italic1Star();

    [GeneratedRegex(@"_{3}(.+?)_{3}", RegexOptions.Compiled)]
    private static partial Regex BoldItalic3Under();

    [GeneratedRegex(@"_{2}(.+?)_{2}", RegexOptions.Compiled)]
    private static partial Regex Bold2Under();

    [GeneratedRegex(@"(?<!\w)_(.+?)_(?!\w)", RegexOptions.Compiled)]
    private static partial Regex Italic1Under();

    [GeneratedRegex(@"~~(.+?)~~", RegexOptions.Compiled)]
    private static partial Regex Strikethrough();

    [GeneratedRegex(@"^>\s?(.*)$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex Blockquote();

    [GeneratedRegex(@"^[\s]*[-*+]\s+", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex UnorderedList();

    [GeneratedRegex(@"^[\s]*\d+\.\s+", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex OrderedList();

    [GeneratedRegex(@"^[-*_]{3,}\s*$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex HorizontalRule();

    [GeneratedRegex(@"^\|[\s:\-]+(\|[\s:?\-]+)+\|?\s*$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"^\|.+\|?\s*$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex TableRow();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\n{3,}", RegexOptions.Compiled)]
    private static partial Regex MultipleBlankLines();
}
