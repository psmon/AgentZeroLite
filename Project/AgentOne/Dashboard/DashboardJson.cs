using System.Text.Json.Serialization;
using AgentOne.Services;

namespace AgentOne.Dashboard;

/// <summary>One workspace folder under ~/.agent-one/workspaces, as the sidebar lists it.</summary>
public sealed class WorkspaceInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("root")] public string Root { get; set; } = "";
    [JsonPropertyName("rootExists")] public bool RootExists { get; set; }
    [JsonPropertyName("memoryChars")] public int MemoryChars { get; set; }
    [JsonPropertyName("memoryEntries")] public int MemoryEntries { get; set; }
    [JsonPropertyName("sessions")] public int Sessions { get; set; }
    /// <summary>"none" (never made), "ok", "busy" (another process holds it), "off" (no Kùzu library here).</summary>
    [JsonPropertyName("graph")] public string Graph { get; set; } = "none";
    [JsonPropertyName("graphNote")] public string? GraphNote { get; set; }
    /// <summary>Node count per table, when the graph could be read.</summary>
    [JsonPropertyName("counts")] public Dictionary<string, long>? Counts { get; set; }
    [JsonPropertyName("lastActivity")] public string LastActivity { get; set; } = "";
    /// <summary>True when the background session is attached to this root.</summary>
    [JsonPropertyName("background")] public bool Background { get; set; }
}

public sealed class WorkspaceList
{
    [JsonPropertyName("home")] public string Home { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("kuzu")] public bool Kuzu { get; set; }
    [JsonPropertyName("background")] public string? Background { get; set; }
    [JsonPropertyName("workspaces")] public List<WorkspaceInfo> Workspaces { get; set; } = [];
}

/// <summary>One "## when · title" block of memory.md.</summary>
public sealed class MemoryEntryDto
{
    [JsonPropertyName("ws")] public string Ws { get; set; } = "";
    [JsonPropertyName("at")] public string At { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("asked")] public string Asked { get; set; } = "";
    [JsonPropertyName("did")] public List<string> Did { get; set; } = [];
    [JsonPropertyName("outcome")] public string Outcome { get; set; } = "";
    /// <summary>Lines that did not fit the asked / did / outcome shape — kept, not dropped.</summary>
    [JsonPropertyName("extra")] public string? Extra { get; set; }
}

public sealed class MemoryView
{
    [JsonPropertyName("entries")] public List<MemoryEntryDto> Entries { get; set; } = [];
    /// <summary>The file as written, for a single workspace only.</summary>
    [JsonPropertyName("raw")] public string? Raw { get; set; }
}

public sealed class SessionInfo
{
    [JsonPropertyName("ws")] public string Ws { get; set; } = "";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("turns")] public int Turns { get; set; }
    [JsonPropertyName("started")] public string Started { get; set; } = "";
    [JsonPropertyName("firstPrompt")] public string FirstPrompt { get; set; } = "";
}

public sealed class SessionList
{
    [JsonPropertyName("sessions")] public List<SessionInfo> Sessions { get; set; } = [];
}

public sealed class SessionDetail
{
    [JsonPropertyName("ws")] public string Ws { get; set; } = "";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("entries")] public List<SessionEntry> Entries { get; set; } = [];
}

public sealed class GraphNode
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("caption")] public string Caption { get; set; } = "";
    [JsonPropertyName("ws")] public string Ws { get; set; } = "";
    [JsonPropertyName("props")] public Dictionary<string, string> Props { get; set; } = [];
}

public sealed class GraphEdge
{
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("to")] public string To { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("props")] public Dictionary<string, string>? Props { get; set; }
}

public sealed class GraphView
{
    [JsonPropertyName("nodes")] public List<GraphNode> Nodes { get; set; } = [];
    [JsonPropertyName("edges")] public List<GraphEdge> Edges { get; set; } = [];
    /// <summary>Tables that had more rows than the per-table limit.</summary>
    [JsonPropertyName("truncated")] public List<string> Truncated { get; set; } = [];
    [JsonPropertyName("notes")] public List<string> Notes { get; set; } = [];
}

public sealed class CypherRequest
{
    [JsonPropertyName("ws")] public string Ws { get; set; } = "";
    [JsonPropertyName("query")] public string Query { get; set; } = "";
}

public sealed class CypherResult
{
    [JsonPropertyName("columns")] public List<string> Columns { get; set; } = [];
    [JsonPropertyName("rows")] public List<string[]> Rows { get; set; } = [];
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
    [JsonPropertyName("elapsedMs")] public long ElapsedMs { get; set; }
    /// <summary>Per-workspace failures: a workspace whose graph is busy, or a query its schema rejects.</summary>
    [JsonPropertyName("errors")] public List<string> Errors { get; set; } = [];
}

public sealed class ErrorReply
{
    [JsonPropertyName("error")] public string Error { get; set; } = "";
}

/// <summary>
/// The dashboard's wire JSON. Its own context rather than a line in
/// <see cref="AgentOneWireJson"/>: these types exist for one page, and the
/// AOT rule is the same — every serialized type is declared, none reflected.
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WorkspaceList))]
[JsonSerializable(typeof(MemoryView))]
[JsonSerializable(typeof(SessionList))]
[JsonSerializable(typeof(SessionDetail))]
[JsonSerializable(typeof(GraphView))]
[JsonSerializable(typeof(CypherRequest))]
[JsonSerializable(typeof(CypherResult))]
[JsonSerializable(typeof(ErrorReply))]
public partial class DashboardJson : JsonSerializerContext;
