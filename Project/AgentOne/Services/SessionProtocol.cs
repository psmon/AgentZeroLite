using System.Text.Json.Serialization;

namespace AgentOne.Services;

/// <summary>
/// What a client sends the background session, one JSON object per line.
/// "ask" runs a turn (slash commands included), "wait" attaches to the running
/// turn (or returns the last result), "status" reads the progress and stats,
/// "stop" ends the session, "answer" replies to an "ask" or "choose" event
/// the server raised mid-turn.
/// </summary>
public sealed class PipeRequest
{
    [JsonPropertyName("op")]
    public string Op { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    /// <summary>ask: approve every command the gate would have asked about, without asking back.</summary>
    [JsonPropertyName("yes")]
    public bool Yes { get; set; }

    /// <summary>ask: answer "Accepted" at once and let the turn run with nobody attached — `wait` collects it.</summary>
    [JsonPropertyName("detach")]
    public bool Detach { get; set; }
}

/// <summary>
/// What the background session sends back, one JSON object per line: the
/// same events the chat window sees, then one "result". The client prints
/// them the way the REPL would; another agent reads them as data.
/// </summary>
public sealed class PipeEvent
{
    /// <summary>activity · step · delta · note · decided · title · design · ask · choose · attached · result · error</summary>
    [JsonPropertyName("event")]
    public string Event { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("tool")]
    public string? Tool { get; set; }

    [JsonPropertyName("ok")]
    public bool? Ok { get; set; }

    [JsonPropertyName("elapsedMs")]
    public long? ElapsedMs { get; set; }

    /// <summary>decided: route · scope · safety · escalation. result: the stop reason.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    /// <summary>ask: why the command was not run unasked. choose: the question.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>choose: the options; design: the design's first lines.</summary>
    [JsonPropertyName("options")]
    public List<string>? Options { get; set; }

    [JsonPropertyName("recommended")]
    public int? Recommended { get; set; }

    /// <summary>result: how many characters of Text were already sent as deltas.</summary>
    [JsonPropertyName("streamed")]
    public int? Streamed { get; set; }

    /// <summary>result / attached: the tool steps the turn has taken.</summary>
    [JsonPropertyName("steps")]
    public int? Steps { get; set; }

    /// <summary>result: which turn of the session this was — 2 and up means the conversation carried on.</summary>
    [JsonPropertyName("turn")]
    public int? Turn { get; set; }

    /// <summary>result: the request the turn answered, so `wait` and a detached caller can tell which one.</summary>
    [JsonPropertyName("request")]
    public string? Request { get; set; }
}

/// <summary>The one background session's whereabouts, in ~/.agent-one/session.json.</summary>
public sealed class SessionRecord
{
    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    [JsonPropertyName("pipe")]
    public string Pipe { get; set; } = "";

    [JsonPropertyName("root")]
    public string Root { get; set; } = "";

    [JsonPropertyName("started")]
    public string Started { get; set; } = "";

    [JsonPropertyName("smart")]
    public bool Smart { get; set; }
}
