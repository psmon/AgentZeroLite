using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent.Common.Remote;

/// <summary>A decoded client→server WS message. See <see cref="RemoteProtocol"/> for the wire shape.</summary>
public sealed record RemoteClientMessage(string Type, string? Data, int? Group, int? Tab);

/// <summary>
/// The tiny JSON envelope shared by the WS host (WPF) and <see cref="RemoteSessionActor"/>
/// (ZeroCommon), kept here so both sides agree on the format and it can be unit tested.
///
/// <para>Server→client frames: <c>{"t":"snapshot|output|info|error","d":"..."}</c> and
/// <c>{"t":"terminals","list":[...]}</c>. Client→server frames:
/// <c>{"t":"input|key","d":"..."}</c>, <c>{"t":"attach","group":G,"tab":T}</c>,
/// <c>{"t":"ping"}</c>. Terminal payloads carry raw ANSI so xterm.js renders faithfully;
/// System.Text.Json handles the string escaping.</para>
/// </summary>
public static class RemoteProtocol
{
    private sealed record ServerFrame(
        [property: JsonPropertyName("t")] string T,
        [property: JsonPropertyName("d")] string D);

    /// <summary>Streaming output delta (server→client).</summary>
    public static string Output(string data) => Frame("output", data);

    /// <summary>Initial "current screen" snapshot sent once on attach (server→client).</summary>
    public static string Snapshot(string data) => Frame("snapshot", data);

    /// <summary>Informational notice (server→client).</summary>
    public static string Info(string data) => Frame("info", data);

    /// <summary>Error notice (server→client).</summary>
    public static string Error(string data) => Frame("error", data);

    private static string Frame(string t, string d) => JsonSerializer.Serialize(new ServerFrame(t, d));

    /// <summary>Terminal catalog frame. <paramref name="listJson"/> must already be a valid
    /// JSON value (e.g. from <c>CliTerminalIpcHelper.BuildTerminalListJson</c>).</summary>
    public static string Terminals(string listJson)
        => "{\"t\":\"terminals\",\"list\":" + (string.IsNullOrWhiteSpace(listJson) ? "null" : listJson) + "}";

    /// <summary>Parse a client→server frame. Returns false on malformed JSON or a missing type.</summary>
    public static bool TryParseClient(string json, out RemoteClientMessage message)
    {
        message = null!;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String) return false;

            string type = t.GetString() ?? "";
            string? data = root.TryGetProperty("d", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() : null;
            int? group = root.TryGetProperty("group", out var g) && g.ValueKind == JsonValueKind.Number
                ? g.GetInt32() : null;
            int? tab = root.TryGetProperty("tab", out var tb) && tb.ValueKind == JsonValueKind.Number
                ? tb.GetInt32() : null;

            message = new RemoteClientMessage(type, data, group, tab);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
