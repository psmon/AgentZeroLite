using System.Text.Json;
using System.Text.Json.Serialization;
using AgentOne.Services;

namespace AgentOne.Agent;

/// <summary>
/// The model's move for one turn: either a tool to run, or "final" carrying the
/// answer. One envelope shape for both means the loop has exactly one thing to
/// parse, and a small model only has to learn one output format.
/// </summary>
public sealed class ToolCall
{
    public const string FinalTool = "final";

    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "";

    [JsonPropertyName("args")]
    public Dictionary<string, string> Args { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsFinal => string.Equals(Tool, FinalTool, StringComparison.OrdinalIgnoreCase);

    public string Arg(string name, string fallback = "") =>
        Args.TryGetValue(name, out var v) ? v : fallback;

    public static ToolCall Final(string text) =>
        new() { Tool = FinalTool, Args = new(StringComparer.OrdinalIgnoreCase) { ["text"] = text } };

    public string ToJson() => JsonSerializer.Serialize(this, AgentOneWireJson.Default.ToolCall);

    /// <summary>Identity for the repeat guard: same tool with the same arguments.</summary>
    public string Signature()
    {
        var parts = Args.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(kv => $"{kv.Key}={kv.Value}");
        return Tool.ToLowerInvariant() + "(" + string.Join(",", parts) + ")";
    }

    /// <summary>
    /// Tolerant parse. Models fence their JSON, prepend "Sure!", or emit numbers
    /// where the schema says string — none of that is worth a failed run, so we
    /// scan for the first balanced object and coerce scalar args to text.
    /// </summary>
    public static bool TryParse(string raw, out ToolCall call, out string error)
    {
        call = new ToolCall();
        error = "";

        if (string.IsNullOrWhiteSpace(raw)) { error = "empty model reply"; return false; }

        var json = ExtractFirstJsonObject(raw);
        if (json is null) { error = "no JSON object found in model reply"; return false; }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "top-level JSON is not an object"; return false; }

            if (!root.TryGetProperty("tool", out var toolEl) || toolEl.ValueKind != JsonValueKind.String)
            {
                error = "missing string property 'tool'";
                return false;
            }

            call.Tool = toolEl.GetString() ?? "";
            if (call.Tool.Length == 0) { error = "'tool' is empty"; return false; }

            if (root.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in argsEl.EnumerateObject())
                    call.Args[prop.Name] = ScalarToString(prop.Value);
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = "malformed JSON: " + ex.Message;
            return false;
        }
    }

    private static string ScalarToString(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => el.GetRawText()
    };

    /// <summary>Scans for the first balanced {...}, ignoring braces inside strings.</summary>
    internal static string? ExtractFirstJsonObject(string text)
    {
        int start = text.IndexOf('{');
        if (start < 0) return null;

        int depth = 0;
        bool inString = false, escaped = false;

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return text[start..(i + 1)];
                    break;
            }
        }

        return null;
    }
}
