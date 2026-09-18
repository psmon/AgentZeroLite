using System.Text.Json;

namespace Agent.Common.Llm.Tools;

/// <summary>
/// Gemma 4 sometimes answers a tool prompt in its own native call syntax instead of the
/// JSON envelope the loops ask for:
/// <code>&lt;|tool_call&gt;call: list_terminals{}&lt;tool_call|&gt;</code>
/// (also <c>call:name{...}</c> with no space, and with or without the closing marker).
/// The envelope parser then fails on "missing 'tool' field", the format-correction budget
/// burns down, and the turn ends with zero tool calls — observed against WebnoriA2 ·
/// google/gemma-4-e4b (M0037). This converts that syntax to the envelope so the loop
/// proceeds; a reply that is not in that syntax is left alone.
/// </summary>
public static class GemmaNativeToolCall
{
    private const string OpenMarker = "<|tool_call>";
    private const string CallPrefix = "call:";

    /// <summary>Convert a native call to <c>{"tool":"name","args":{…}}</c>; false when the text is not one.</summary>
    public static bool TryConvert(string? text, out string envelopeJson)
    {
        envelopeJson = "";
        if (string.IsNullOrWhiteSpace(text)) return false;

        var at = text.IndexOf(OpenMarker, StringComparison.Ordinal);
        if (at < 0) return false;
        var i = at + OpenMarker.Length;
        SkipSpace(text, ref i);
        if (string.CompareOrdinal(text, i, CallPrefix, 0, CallPrefix.Length) == 0) i += CallPrefix.Length;
        SkipSpace(text, ref i);

        var nameStart = i;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '-' || text[i] == '.')) i++;
        var name = text.Substring(nameStart, i - nameStart);
        if (name.Length == 0) return false;
        SkipSpace(text, ref i);

        var args = "{}";
        if (i < text.Length && text[i] == '{')
        {
            var end = MatchBrace(text, i);
            if (end < 0) return false;
            args = text.Substring(i, end - i + 1);
        }
        else if (i < text.Length && text[i] == '(')
        {
            // "call: name(a=1)" — not JSON; give the loop the name and let it ask for args.
            args = "{}";
        }

        try { using var _ = JsonDocument.Parse(args); }
        catch (JsonException) { return false; }

        envelopeJson = "{\"tool\":" + JsonSerializer.Serialize(name) + ",\"args\":" + args + "}";
        return true;
    }

    private static void SkipSpace(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }

    /// <summary>Index of the brace closing the one at <paramref name="start"/>, string-aware; -1 when unterminated.</summary>
    private static int MatchBrace(string s, int start)
    {
        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (escape) { escape = false; continue; }
            if (c == '\\') { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }
        return -1;
    }
}
