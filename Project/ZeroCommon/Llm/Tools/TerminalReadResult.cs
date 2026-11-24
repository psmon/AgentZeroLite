using System.Text.Encodings.Web;
using System.Text.Json;

namespace Agent.Common.Llm.Tools;

/// <summary>
/// The <c>read_terminal</c> result the model and the transcript get: one flat object,
/// <c>{"ok":true,"text":"…"}</c>.
///
/// <para>Both loops used to wrap whatever the host returned as the <c>text</c> of a new object.
/// The hosts already answer with an envelope of their own, so the model received JSON inside a
/// JSON string — <c>{"ok":true,"text":"{\"ok\":true,\"group_index\":1,…,\"text\":\"…\\n…\"}"}</c>
/// — every newline of the screen a literal <c>\n</c>, every quote escaped twice, and the bot
/// pane drew exactly that (measured 2026-11-24: the "← Codex" bubble was a line of JSON). A
/// host envelope is unwrapped here; a host that returns plain text is wrapped as before.</para>
/// </summary>
public static class TerminalReadResult
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Normalize(string? hostReply)
    {
        hostReply ??= "";
        var trimmed = hostReply.AsSpan().TrimStart();
        if (trimmed.Length > 0 && trimmed[0] == '{')
        {
            try
            {
                using var doc = JsonDocument.Parse(hostReply);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ok", out var ok)
                    && ok.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    if (ok.ValueKind == JsonValueKind.False)
                    {
                        var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                            ? e.GetString() ?? "read failed"
                            : "read failed";
                        return JsonSerializer.Serialize(new { ok = false, error }, Json);
                    }
                    // An envelope carries its screen as "text". Without one this is a screen that
                    // happens to show {"ok":true,…} — wrap it, or its content would be dropped.
                    if (root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    {
                        var text = t.GetString() ?? "";
                        var note = root.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                        return note is null
                            ? JsonSerializer.Serialize(new { ok = true, text }, Json)
                            : JsonSerializer.Serialize(new { ok = true, text, note }, Json);
                    }
                }
            }
            catch (JsonException)
            {
                // Screen text that merely starts with a brace — fall through and wrap it.
            }
        }
        return JsonSerializer.Serialize(new { ok = true, text = hostReply }, Json);
    }
}
