using System.Text;
using System.Text.Json;

namespace AgentZeroAvalonia.Terminal;

/// <summary>
/// The wire between the host and the xterm.js renderer (M0035), kept free of the
/// control so it can be tested. Vocabulary is the WPF host's plus two words:
/// <list type="bullet">
/// <item>host → JS: <c>out</c>/<c>out64</c>/<c>clear</c>/<c>focus</c>/<c>config</c> — delivered by
/// <c>InvokeScript("window.zeroHost.recv({…})")</c>, because <c>NativeWebView</c> has no
/// post-message channel in that direction.</item>
/// <item>JS → host: <c>ready</c>/<c>in</c>/<c>resize</c>/<c>screen</c>/<c>renderer</c>/<c>fontstatus</c>/
/// <c>activate</c>/<c>link</c>/<c>hotkey</c> — one JSON string through <c>invokeCSharpAction</c>,
/// which some engines hand over double-encoded; <see cref="TryParseInbound"/> unwraps.</item>
/// </list>
/// Output travels as <c>out64</c> (base64 of UTF-8) so a script literal never has to escape
/// VT bytes, and xterm.js takes the bytes straight into its own UTF-8 decoder.
/// </summary>
internal static class XtermMessages
{
    /// <summary>One <c>out64</c> per this many UTF-8 bytes — well inside every engine's script size comfort zone.</summary>
    public const int MaxOutChunkBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // The default encoder escapes U+2028/U+2029 and every non-ASCII char, which is
        // exactly what makes the JSON safe to drop into a script literal unchanged.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
    };

    /// <summary>The script that hands <paramref name="message"/> to the renderer.</summary>
    public static string BuildRecvScript(object message)
        => "window.zeroHost&&window.zeroHost.recv(" + JsonSerializer.Serialize(message, JsonOptions) + ");";

    /// <summary>Base64 chunks of <paramref name="text"/>'s UTF-8, never splitting a code point.</summary>
    public static IEnumerable<string> ChunkUtf8Base64(string text, int maxBytes = MaxOutChunkBytes)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        if (maxBytes < 4) maxBytes = 4;

        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes)
        {
            yield return Convert.ToBase64String(bytes);
            yield break;
        }

        var start = 0;
        while (start < bytes.Length)
        {
            var end = Math.Min(start + maxBytes, bytes.Length);
            if (end < bytes.Length)
            {
                // Step back to a code-point boundary: a continuation byte is 10xxxxxx.
                while (end > start && (bytes[end] & 0xC0) == 0x80) end--;
                if (end == start) end = Math.Min(start + maxBytes, bytes.Length);
            }
            yield return Convert.ToBase64String(bytes, start, end - start);
            start = end;
        }
    }

    /// <summary>
    /// Parse what <c>WebMessageReceived</c> delivered. The renderer always sends a JSON
    /// object serialised to a string; an engine may wrap that string in JSON once more.
    /// </summary>
    public static bool TryParseInbound(string? body, out JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var el = doc.RootElement;
            if (el.ValueKind == JsonValueKind.String)
            {
                var inner = el.GetString();
                if (string.IsNullOrWhiteSpace(inner)) return false;
                using var innerDoc = JsonDocument.Parse(inner);
                if (innerDoc.RootElement.ValueKind != JsonValueKind.Object) return false;
                root = innerDoc.RootElement.Clone();
                return true;
            }
            if (el.ValueKind != JsonValueKind.Object) return false;
            root = el.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string? Type(JsonElement root)
        => root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

    public static string? Str(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int Int(JsonElement root, string name, int fallback)
        => root.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : fallback;
}
