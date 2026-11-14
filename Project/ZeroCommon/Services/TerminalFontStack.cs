namespace Agent.Common.Services;

/// <summary>
/// Edits the CSS font stack in <see cref="TerminalSettings.FontFamily"/> one family at a
/// time. Settings lists the installed fonts; picking one should change which font the
/// terminal uses, not throw away the fallbacks behind it — those are what catch a glyph
/// (Hangul, box drawing, an icon) the picked font does not have.
/// </summary>
public static class TerminalFontStack
{
    /// <summary>The first family of a stack, unquoted; "" for an empty stack.</summary>
    public static string Primary(string? stack)
    {
        foreach (var family in Families(stack))
            return family;
        return "";
    }

    /// <summary>
    /// <paramref name="family"/> first, then the rest of <paramref name="stack"/> minus the
    /// old first family and any repeat of the new one. A stack left with nothing behind the
    /// picked font gets <c>monospace</c>, so a missing glyph still lands in a fixed-width face.
    /// </summary>
    public static string WithPrimary(string? stack, string family)
    {
        family = Unquote(family.Trim());
        if (family.Length == 0) return string.IsNullOrWhiteSpace(stack) ? "monospace" : stack.Trim();

        var rest = Families(stack).Skip(1)
            .Where(f => !string.Equals(f, family, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (rest.Count == 0) rest.Add("monospace");
        return string.Join(", ", new[] { Quote(family) }.Concat(rest.Select(Quote)));
    }

    /// <summary>The families of a stack in order, unquoted and trimmed.</summary>
    public static IEnumerable<string> Families(string? stack)
    {
        if (string.IsNullOrWhiteSpace(stack)) yield break;
        foreach (var part in stack.Split(','))
        {
            var f = Unquote(part.Trim());
            if (f.Length > 0) yield return f;
        }
    }

    /// <summary>
    /// The family as CSS needs it: bare when every word is an identifier (how the stored
    /// default is written), quoted otherwise — a name like "3270 Nerd Font" or one with a
    /// dot is not a valid unquoted family, and the browser would drop the whole declaration.
    /// </summary>
    public static string Quote(string family)
    {
        var words = family.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 && words.All(IsIdentifier)
            ? string.Join(' ', words)
            : "\"" + family.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static string Unquote(string s) =>
        s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[^1] == s[0]
            ? s[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\").Trim()
            : s;

    // CSS ident, simplified: starts with a letter, '_' or a non-ASCII character (Hangul
    // names are fine bare), then letters, digits, '-', '_' or non-ASCII.
    private static bool IsIdentifier(string word)
    {
        if (word.Length == 0) return false;
        var c0 = word[0];
        if (!(char.IsAsciiLetter(c0) || c0 == '_' || c0 > 127)) return false;
        foreach (var c in word)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_' || c > 127)) return false;
        return true;
    }
}
