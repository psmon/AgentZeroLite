using System.Text.Json;

namespace Agent.Common.Agents;

/// <summary>How one completed tool turn should appear in the transcript.</summary>
public abstract record ToolTurnView;

/// <summary>Text the agent sent to, or read back from, a terminal.</summary>
/// <param name="Outgoing">True for a write to the terminal, false for a read.</param>
public sealed record TerminalExchangeView(bool Outgoing, int Group, int Tab, string Text) : ToolTurnView
{
    /// <summary>The arrow the WPF host draws: → for a write, ← for a read.</summary>
    public string Arrow => Outgoing ? "→" : "←";
}

/// <summary>A <c>wait</c> turn, collapsed to one line.</summary>
public sealed record WaitedView(int Seconds) : ToolTurnView;

/// <summary>Any other tool — shown as a compact note.</summary>
public sealed record AdministrativeView(string Tool, string ArgsJson) : ToolTurnView;

/// <summary>A tool that reported failure.</summary>
public sealed record FailedView(string Tool, string Detail) : ToolTurnView;

/// <summary>
/// Turns a finished tool turn into the shape the transcript draws (M0041). Ported from the
/// WPF host's <c>RenderToolTurnLive</c>: terminal reads and writes become exchange bubbles so
/// a conversation with another agent reads like a conversation, waits collapse to one line so
/// a long poll does not flood the chat, and everything else stays a compact note.
/// </summary>
public static class ToolTurnPresenter
{
    /// <summary>
    /// Classifies one turn. Never throws — malformed JSON from a model is expected, and a
    /// rendering helper is the wrong place to fail a run.
    /// </summary>
    public static ToolTurnView Present(string tool, string? argsJson, string? resultJson)
    {
        tool ??= "";
        argsJson ??= "{}";
        resultJson ??= "";

        switch (tool)
        {
            case "send_to_terminal":
            {
                var (g, t) = ReadTarget(argsJson);
                return new TerminalExchangeView(Outgoing: true, g, t, ReadString(argsJson, "text") ?? "");
            }

            case "read_terminal":
            {
                var (g, t) = ReadTarget(argsJson);
                var (ok, text) = TryExtractReadTerminalText(resultJson);
                return ok
                    ? new TerminalExchangeView(Outgoing: false, g, t, text)
                    : new FailedView(tool, $"read_terminal({{\"group\":{g},\"tab\":{t}}}) → error");
            }

            case "wait":
                // The result echoes the clamped value, so prefer it over what the model asked for.
                return new WaitedView(TryExtractWaitedSeconds(resultJson) ?? ReadInt(argsJson, "seconds"));

            default:
                return new AdministrativeView(tool, argsJson);
        }
    }

    private static (int Group, int Tab) ReadTarget(string argsJson)
        => (ReadInt(argsJson, "group"), ReadInt(argsJson, "tab"));

    internal static (bool Ok, string Text) TryExtractReadTerminalText(string resultJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
                && root.TryGetProperty("text", out var text))
            {
                return (true, text.GetString() ?? "");
            }
        }
        catch (JsonException) { }
        return (false, "");
    }

    internal static int? TryExtractWaitedSeconds(string resultJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("waited_seconds", out var v)
                && v.TryGetInt32(out var n))
                return n;
        }
        catch (JsonException) { }
        return null;
    }

    private static int ReadInt(string json, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(key, out var v)
                && v.TryGetInt32(out var i))
                return i;
        }
        catch (JsonException) { }
        return 0;
    }

    private static string? ReadString(string json, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(key, out var v)
                && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
        catch (JsonException) { }
        return null;
    }
}
