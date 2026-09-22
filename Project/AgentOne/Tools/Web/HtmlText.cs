using System.Net;
using System.Text.RegularExpressions;

namespace AgentOne.Tools.Web;

/// <summary>
/// Turns a page into the text a model can actually use. Hand-rolled rather than
/// pulling in an HTML parser: the job is "strip the furniture, keep the prose",
/// the whole surface is four regexes, and every one of them is source-generated
/// so it survives Native AOT.
/// </summary>
public static partial class HtmlText
{
    /// <summary>Whole elements whose contents are never prose.</summary>
    [GeneratedRegex(@"<(script|style|noscript|svg|head|iframe|form|template)\b[^>]*>.*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NonProseBlocks();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();

    /// <summary>Tags that end a line of text, so paragraphs do not run together.</summary>
    [GeneratedRegex(@"</?(p|div|br|li|ul|ol|tr|td|th|h[1-6]|section|article|header|footer|nav|aside|blockquote|pre|hr|table)\b[^>]*>",
        RegexOptions.IgnoreCase)]
    private static partial Regex BlockTags();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[ \t\f\v\r]+")]
    private static partial Regex HorizontalSpace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRuns();

    [GeneratedRegex(@"<title\b[^>]*>(.*?)</title\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();

    public static string Title(string html)
    {
        var match = TitleTag().Match(html);
        return match.Success ? Clean(WebUtility.HtmlDecode(match.Groups[1].Value)) : "";
    }

    /// <summary>The page's readable text, with the markup and the furniture gone.</summary>
    public static string Extract(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        var text = NonProseBlocks().Replace(html, "\n");
        text = Comments().Replace(text, "");
        text = BlockTags().Replace(text, "\n");

        // Block tags have already become line breaks, so what is left is inline
        // markup — <b>, <span>, <a>. Those must vanish without leaving a space,
        // or "Akka<b>.NET</b>" reads back as "Akka .NET".
        text = AnyTag().Replace(text, "");
        text = WebUtility.HtmlDecode(text);

        return Tidy(text);
    }

    /// <summary>Strips markup from a short fragment — a title, a search snippet.</summary>
    public static string Strip(string fragment)
    {
        var text = BlockTags().Replace(fragment, " ");
        text = AnyTag().Replace(text, "");
        return Clean(WebUtility.HtmlDecode(text));
    }

    /// <summary>Collapses runs of space, keeping paragraph breaks.</summary>
    private static string Tidy(string text)
    {
        text = HorizontalSpace().Replace(text, " ");

        var lines = text.Split('\n').Select(l => l.Trim());
        text = string.Join('\n', lines);

        return BlankRuns().Replace(text, "\n\n").Trim();
    }

    private static string Clean(string text) =>
        HorizontalSpace().Replace(text.Replace('\n', ' '), " ").Trim();

    /// <summary>
    /// Caps what goes back to the model, and says so — a silent truncation reads
    /// as "the page ended here", which is a different and wrong fact.
    /// </summary>
    public static string Cap(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        return text[..maxChars] + $"\n\n… truncated ({text.Length} characters total, {maxChars} shown)";
    }
}
