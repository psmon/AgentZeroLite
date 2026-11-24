using System.Text;
using System.Text.Json;

namespace Agent.Common.Agents;

// ───────────────────────────────────────────────────────────────────────────────
// The bot transcript as components, after A2UI (https://a2ui.org): what the agent
// produced is DATA — a flat list of nodes from a small fixed catalog — and the host
// draws each node with its own native control. Nothing here knows about Avalonia or
// WPF; a host maps the four node types to four templates and is done.
//
// Why: the pane used to print a tool's arguments and result as the JSON they travelled
// in. Measured 2026-11-24, a terminal read was a paragraph of
// {"ok":true,"group_index":1,…,"text":"…\n…"} with its line breaks as the two characters
// "\n", in 11 px grey. JSON is a transport; a person reads fields, lists and paragraphs.
//
// The catalog (A2UI's basic one, cut to what a transcript needs):
//   BotText    Text       heading | body | caption | code | quote
//   BotField   a labelled value (Row of caption + text)
//   BotList    List of one-line items
//   BotDivider Divider
// ───────────────────────────────────────────────────────────────────────────────

public enum BotTextVariant { Body, Heading, Caption, Code, Quote }

/// <summary>One component of a transcript card.</summary>
public abstract record BotNode;

/// <summary>A run of text in one of the catalog's variants.</summary>
public sealed record BotText(string Text, BotTextVariant Variant = BotTextVariant.Body) : BotNode
{
    public bool IsHeading => Variant == BotTextVariant.Heading;
    public bool IsCaption => Variant == BotTextVariant.Caption;
    public bool IsCode => Variant == BotTextVariant.Code;
    public bool IsQuote => Variant == BotTextVariant.Quote;
    public bool IsProse => Variant is BotTextVariant.Body or BotTextVariant.Heading or BotTextVariant.Caption or BotTextVariant.Quote;
}

/// <summary>A labelled value: "path — src/app.cs".</summary>
public sealed record BotField(string Label, string Value) : BotNode;

/// <summary>Short items, one per line.</summary>
public sealed record BotList(IReadOnlyList<string> Items, bool Ordered = false) : BotNode
{
    /// <summary>
    /// The items with their marker apart from the text, so a host can give a wrapped item a
    /// hanging indent (drawn as one string, the second line started under the bullet).
    /// </summary>
    public IReadOnlyList<BotListRow> Rows
        => Items.Select((item, i) => new BotListRow(Ordered ? $"{i + 1}." : "•", item)).ToArray();
}

/// <summary>One list item: its marker ("•", "2.") and its text.</summary>
public sealed record BotListRow(string Marker, string Text);

public sealed record BotDivider : BotNode;

/// <summary>
/// An answer written by a model, as nodes: headings, paragraphs, bullet and numbered lists,
/// fenced code and quotes. Deliberately small — it reads the Markdown models actually write in
/// a chat answer and leaves anything else as the paragraph it is. Inline emphasis marks are
/// dropped (a transcript cell is plain text); the words are never changed.
/// </summary>
public static partial class BotMarkdown
{
    public static IReadOnlyList<BotNode> Parse(string? text)
    {
        var nodes = new List<BotNode>();
        if (string.IsNullOrWhiteSpace(text)) return nodes;

        var paragraph = new List<string>();
        var list = new List<string>();
        var listOrdered = false;
        var code = new List<string>();
        var inCode = false;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            nodes.Add(new BotText(string.Join("\n", paragraph)));
            paragraph.Clear();
        }
        void FlushList()
        {
            if (list.Count == 0) return;
            nodes.Add(new BotList(list.ToArray(), listOrdered));
            list.Clear();
        }

        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    nodes.Add(new BotText(string.Join("\n", code), BotTextVariant.Code));
                    code.Clear();
                    inCode = false;
                }
                else
                {
                    FlushParagraph();
                    FlushList();
                    inCode = true;
                }
                continue;
            }
            if (inCode) { code.Add(line); continue; }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                FlushList();
                continue;
            }

            if (TryHeading(trimmed, out var heading))
            {
                FlushParagraph();
                FlushList();
                nodes.Add(new BotText(Inline(heading), BotTextVariant.Heading));
                continue;
            }
            if (trimmed is "---" or "***" or "___")
            {
                FlushParagraph();
                FlushList();
                nodes.Add(new BotDivider());
                continue;
            }
            if (TryListItem(trimmed, out var item, out var ordered))
            {
                FlushParagraph();
                if (list.Count > 0 && ordered != listOrdered) FlushList();
                listOrdered = ordered;
                list.Add(Inline(item));
                continue;
            }
            if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                FlushParagraph();
                FlushList();
                nodes.Add(new BotText(Inline(trimmed[2..]), BotTextVariant.Quote));
                continue;
            }

            FlushList();
            paragraph.Add(Inline(trimmed));
        }

        if (inCode && code.Count > 0) nodes.Add(new BotText(string.Join("\n", code), BotTextVariant.Code));
        FlushParagraph();
        FlushList();
        return nodes;
    }

    private static bool TryHeading(string line, out string heading)
    {
        heading = "";
        var hashes = 0;
        while (hashes < line.Length && line[hashes] == '#') hashes++;
        if (hashes is 0 or > 6 || hashes >= line.Length || line[hashes] != ' ') return false;
        heading = line[(hashes + 1)..].Trim().TrimEnd('#').TrimEnd();
        return heading.Length > 0;
    }

    private static bool TryListItem(string line, out string item, out bool ordered)
    {
        item = "";
        ordered = false;
        if (line.Length > 2 && line[0] is '-' or '*' or '•' or '+' && line[1] == ' ')
        {
            item = line[2..].Trim();
            return item.Length > 0;
        }
        var digits = 0;
        while (digits < line.Length && char.IsAsciiDigit(line[digits])) digits++;
        if (digits is > 0 and <= 3 && digits + 1 < line.Length && line[digits] is '.' or ')' && line[digits + 1] == ' ')
        {
            item = line[(digits + 2)..].Trim();
            ordered = true;
            return item.Length > 0;
        }
        return false;
    }

    /// <summary>
    /// Drops the marks around words — emphasis, and the backticks of inline code — never the
    /// words. A transcript cell is plain text, and "`agent.md`, `AGENTS.md`" read as noise.
    /// </summary>
    /// <remarks>
    /// One pass, code spans first: what is inside backticks is kept exactly ("2**10" is not
    /// emphasis). "__" is left alone everywhere — models write bold as "**", and "__init__.py"
    /// is a name.
    /// </remarks>
    internal static string Inline(string text)
        => text.Contains('`') || text.Contains("**", StringComparison.Ordinal)
            ? InlineMarks().Replace(text, m => m.Groups[1].Success ? m.Groups[1].Value : "")
            : text;

    [System.Text.RegularExpressions.GeneratedRegex(@"`([^`\n]+)`|\*\*")]
    private static partial System.Text.RegularExpressions.Regex InlineMarks();
}

/// <summary>
/// A JSON value — a tool's arguments or its result — as nodes: scalars become fields, short
/// arrays lists, long strings paragraphs. Works on any tool's JSON (the built-in verbs and MCP
/// tools alike), so a new tool needs no template of its own. Text that is not JSON comes back as
/// one paragraph.
/// </summary>
public static class BotJson
{
    private const int MaxDepth = 3;
    private const int MaxListItems = 8;
    private const int MaxFields = 24;
    private const int LongText = 90;
    private const int MaxTextChars = 4000;

    public static IReadOnlyList<BotNode> ToNodes(string? json)
    {
        var nodes = new List<BotNode>();
        if (string.IsNullOrWhiteSpace(json)) return nodes;
        var trimmed = json.Trim();
        if (trimmed[0] is not ('{' or '['))
        {
            nodes.Add(new BotText(Clip(trimmed)));
            return nodes;
        }
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            Emit(nodes, null, doc.RootElement, 0);
        }
        catch (JsonException)
        {
            // Half an envelope from a model, or a result cut at a size cap: show it as it is.
            nodes.Add(new BotText(Clip(trimmed), BotTextVariant.Code));
        }
        return nodes;
    }

    /// <summary>Whether a tool result says the tool failed (<c>ok:false</c> or an <c>error</c>).</summary>
    public static bool LooksFailed(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False) return true;
            return root.TryGetProperty("error", out var error)
                && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                && !(error.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(error.GetString()));
        }
        catch (JsonException) { return false; }
    }

    /// <summary>The value a person would name the call by: its text, path, query, url… or nothing.</summary>
    public static string Headline(string? argsJson, int max = 80)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return "";
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "";
            foreach (var key in HeadlineKeys)
                if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(v.GetString()))
                    return OneLine(v.GetString()!, max);
        }
        catch (JsonException) { }
        return "";
    }

    private static readonly string[] HeadlineKeys =
        ["query", "path", "url", "title", "pattern", "text", "program", "key", "name", "message"];

    private static readonly string[] SummaryKeys =
        ["title", "name", "group_name", "path", "file", "directory", "url", "alias", "text", "snippet", "message", "id"];

    private static void Emit(List<BotNode> nodes, string? label, JsonElement value, int depth)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
            {
                if (label is not null) nodes.Add(new BotText(label, BotTextVariant.Caption));
                if (depth >= MaxDepth)
                {
                    nodes.Add(new BotText(Clip(value.GetRawText()), BotTextVariant.Code));
                    return;
                }
                var shown = 0;
                var total = 0;
                foreach (var property in value.EnumerateObject())
                {
                    // The envelope's own flag: the card says FAILED (or nothing) for it already.
                    if (depth == 0 && property.NameEquals("ok")
                        && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) continue;
                    total++;
                    if (shown >= MaxFields) continue;
                    shown++;
                    Emit(nodes, Humanize(property.Name), property.Value, depth + 1);
                }
                if (total > shown) nodes.Add(new BotText($"+{total - shown} more fields", BotTextVariant.Caption));
                return;
            }

            case JsonValueKind.Array:
            {
                var count = value.GetArrayLength();
                var title = label is null ? $"{count} item(s)" : $"{label} ({count})";
                if (count == 0)
                {
                    nodes.Add(new BotField(label ?? "items", "none"));
                    return;
                }
                nodes.Add(new BotText(title, BotTextVariant.Caption));
                var items = new List<string>();
                foreach (var element in value.EnumerateArray())
                {
                    if (items.Count >= MaxListItems) break;
                    items.Add(Summarize(element));
                }
                if (count > items.Count) items.Add($"… +{count - items.Count} more");
                nodes.Add(new BotList(items));
                return;
            }

            case JsonValueKind.String:
            {
                var text = value.GetString() ?? "";
                if (text.Length > LongText || text.Contains('\n'))
                {
                    if (label is not null) nodes.Add(new BotText(label, BotTextVariant.Caption));
                    nodes.Add(new BotText(Clip(text.Replace("\r", "").TrimEnd())));
                }
                else
                {
                    nodes.Add(new BotField(label ?? "value", text.Length == 0 ? "(empty)" : text));
                }
                return;
            }

            case JsonValueKind.True:
            case JsonValueKind.False:
                nodes.Add(new BotField(label ?? "value", value.GetBoolean() ? "yes" : "no"));
                return;

            case JsonValueKind.Number:
                nodes.Add(new BotField(label ?? "value", value.GetRawText()));
                return;

            default:
                nodes.Add(new BotField(label ?? "value", "—"));
                return;
        }
    }

    /// <summary>One array element as a line: a scalar as itself, an object by its naming fields.</summary>
    private static string Summarize(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String: return OneLine(element.GetString() ?? "", 160);
            case JsonValueKind.Object:
            {
                var parts = new List<string>();
                foreach (var key in SummaryKeys)
                {
                    if (parts.Count >= 2) break;
                    if (element.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(v.GetString()))
                        parts.Add(OneLine(v.GetString()!, 110));
                }
                if (parts.Count > 0) return string.Join(" — ", parts);
                // No naming field: the first few scalars, labelled.
                foreach (var property in element.EnumerateObject())
                {
                    if (parts.Count >= 3) break;
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) continue;
                    parts.Add($"{Humanize(property.Name)}: {OneLine(Scalar(property.Value), 60)}");
                }
                return parts.Count > 0 ? string.Join(" · ", parts) : "{…}";
            }
            case JsonValueKind.Array: return $"[{element.GetArrayLength()} item(s)]";
            default: return Scalar(element);
        }
    }

    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => "yes",
        JsonValueKind.False => "no",
        JsonValueKind.Null => "—",
        _ => value.GetRawText(),
    };

    /// <summary>"group_index" → "group index", "maxResults" → "max results".</summary>
    internal static string Humanize(string key)
    {
        var sb = new StringBuilder(key.Length + 4);
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (c is '_' or '-') { sb.Append(' '); continue; }
            if (char.IsUpper(c) && i > 0 && char.IsLower(key[i - 1])) sb.Append(' ');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString().Trim();
    }

    private static string OneLine(string text, int max)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= max ? line : Cut(line, max) + "…";
    }

    private static string Clip(string text)
        => text.Length <= MaxTextChars ? text : Cut(text, MaxTextChars) + "\n… (" + (text.Length - MaxTextChars) + " more characters)";

    /// <summary>The first <paramref name="max"/> characters, never half of a surrogate pair (an emoji cut in two draws as a broken glyph).</summary>
    internal static string Cut(string text, int max)
    {
        if (text.Length <= max) return text;
        if (max > 0 && char.IsHighSurrogate(text[max - 1])) max--;
        return text[..max];
    }
}

/// <summary>What a terminal screen read is summarised as while its card is folded.</summary>
public static class BotScreen
{
    /// <summary>"12 lines · last: › Ask Codex to do anything".</summary>
    public static string Summary(string? screen)
    {
        if (string.IsNullOrWhiteSpace(screen)) return "empty screen";
        var lines = screen.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (lines.Length == 0) return "empty screen";
        var last = lines[^1];
        if (last.Length > 70) last = BotJson.Cut(last, 70) + "…";
        return $"{lines.Length} line(s) · last: {last}";
    }
}
