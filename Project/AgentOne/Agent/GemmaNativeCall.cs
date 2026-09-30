using System.Text;
using System.Text.Json;

namespace AgentOne.Agent;

/// <summary>
/// Gemma 4's own tool-call syntax, turned into the JSON envelope. Asked for one
/// JSON object, gemma-4 sometimes answers in the format it was trained on:
/// <code>&lt;|tool_call&gt;call:write_file{args:{content:&lt;|"|&gt;# Title…&lt;|"|&gt;,path:"a/b.md"}}&lt;tool_call|&gt;</code>
/// — bare keys, strings either JSON-quoted or fenced in <c>&lt;|"|&gt;</c> with
/// real newlines inside, the arguments sometimes under an <c>args</c> key and
/// sometimes not. Measured (2026-09-30): two such write_file calls were taken
/// for prose and shown to the user as the answer, nothing was written, and the
/// claim check called the turn a false report.
///
/// ZeroCommon has a converter for the same marker (<c>GemmaNativeToolCall</c>),
/// but it accepts JSON arguments only, and agent-one references nothing in the
/// solution — so this is the small part it needs, written again.
/// </summary>
public static class GemmaNativeCall
{
    public const string OpenMarker = "<|tool_call>";
    private const string CloseMarker = "<tool_call|>";
    private const string StringFence = "<|\"|>";
    private const string CallPrefix = "call:";

    /// <summary>True when the text carries the native marker, parsed or not.</summary>
    public static bool Contains(string text) => text.Contains(OpenMarker, StringComparison.Ordinal);

    /// <summary>The first native call in <paramref name="text"/> as <c>{"tool":…,"args":{…}}</c>; false when there is none or it does not parse.</summary>
    public static bool TryConvert(string text, out string envelopeJson)
    {
        envelopeJson = "";
        var at = text.IndexOf(OpenMarker, StringComparison.Ordinal);
        if (at < 0) return false;

        var i = at + OpenMarker.Length;
        SkipSpace(text, ref i);
        if (string.CompareOrdinal(text, i, CallPrefix, 0, CallPrefix.Length) == 0) i += CallPrefix.Length;
        SkipSpace(text, ref i);

        var nameStart = i;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '-' or '.')) i++;
        var name = text[nameStart..i];
        if (name.Length == 0) return false;
        SkipSpace(text, ref i);

        Dictionary<string, object?> args = [];
        if (i < text.Length && text[i] == '{')
        {
            if (ReadObject(text, ref i) is not { } obj) return false;
            // {args:{…}} and {…} both occur; the first is the envelope's own shape.
            args = obj.Count == 1 && obj.TryGetValue("args", out var inner) && inner is Dictionary<string, object?> nested ? nested : obj;
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("tool", name);
            writer.WritePropertyName("args");
            WriteValue(writer, args);
            writer.WriteEndObject();
        }
        envelopeJson = Encoding.UTF8.GetString(buffer.ToArray());
        return true;
    }

    // ------------------------------------------------------------- reader

    private static Dictionary<string, object?>? ReadObject(string s, ref int i)
    {
        if (i >= s.Length || s[i] != '{') return null;
        i++;
        var obj = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            SkipSpace(s, ref i);
            if (i >= s.Length) return null;
            if (s[i] == '}') { i++; return obj; }

            var key = ReadKey(s, ref i);
            if (key is null) return null;
            SkipSpace(s, ref i);
            if (i >= s.Length || s[i] != ':') return null;
            i++;
            SkipSpace(s, ref i);

            if (!TryReadValue(s, ref i, out var value)) return null;
            obj[key] = value;

            SkipSpace(s, ref i);
            if (i < s.Length && s[i] == ',') i++;
        }
    }

    private static string? ReadKey(string s, ref int i)
    {
        if (s[i] == '"') return ReadJsonString(s, ref i);
        if (string.CompareOrdinal(s, i, StringFence, 0, StringFence.Length) == 0) return ReadFenced(s, ref i);

        var start = i;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '-')) i++;
        return i > start ? s[start..i] : null;
    }

    private static bool TryReadValue(string s, ref int i, out object? value)
    {
        value = null;
        if (i >= s.Length) return false;

        if (string.CompareOrdinal(s, i, StringFence, 0, StringFence.Length) == 0)
            return (value = ReadFenced(s, ref i)) is not null;
        if (s[i] == '"')
            return (value = ReadJsonString(s, ref i)) is not null;
        if (s[i] == '{')
            return (value = ReadObject(s, ref i)) is not null;

        // A number, true/false/null, or a bare word: up to the next separator.
        var start = i;
        while (i < s.Length && s[i] is not (',' or '}')) i++;
        var bare = s[start..i].Trim();
        value = bare == "null" ? null : bare;
        return bare.Length > 0;
    }

    /// <summary><c>&lt;|"|&gt;…&lt;|"|&gt;</c>: taken verbatim, newlines and quotes included.</summary>
    private static string? ReadFenced(string s, ref int i)
    {
        var start = i + StringFence.Length;
        var end = s.IndexOf(StringFence, start, StringComparison.Ordinal);
        if (end < 0) return null;
        i = end + StringFence.Length;
        return s[start..end];
    }

    /// <summary>A JSON string literal starting at <paramref name="i"/>, decoded; raw control characters tolerated.</summary>
    private static string? ReadJsonString(string s, ref int i)
    {
        var start = i;
        i++;
        var escape = false;
        for (; i < s.Length; i++)
        {
            if (escape) { escape = false; continue; }
            if (s[i] == '\\') { escape = true; continue; }
            if (s[i] != '"') continue;

            var literal = ToolCall.Repair(s[start..(i + 1)]);
            i++;
            try { using var doc = JsonDocument.Parse(literal); return doc.RootElement.GetString(); }
            catch (JsonException) { return null; }
        }
        return null;
    }

    private static void SkipSpace(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        // The closing marker is not part of any value.
        if (string.CompareOrdinal(s, i, CloseMarker, 0, CloseMarker.Length) == 0) i += CloseMarker.Length;
    }

    // ------------------------------------------------------------- writer

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case Dictionary<string, object?> obj:
                writer.WriteStartObject();
                foreach (var (key, inner) in obj)
                {
                    writer.WritePropertyName(key);
                    WriteValue(writer, inner);
                }
                writer.WriteEndObject();
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }
}
