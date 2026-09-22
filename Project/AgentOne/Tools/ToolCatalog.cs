namespace AgentOne.Tools;

/// <param name="Name">Verb the model writes into the envelope's "tool" field.</param>
/// <param name="Args">Argument names, in the order the prompt lists them.</param>
public sealed record ToolSpec(string Name, string Summary, string[] Args, string Example);

/// <summary>
/// The catalog is the single source of truth for what the model is told it can
/// do, what `agent-one tools list` prints, and what the toolbelt accepts. A verb
/// added to a toolbelt but not here is invisible to the model; a verb here but
/// not in the toolbelt is answered with "unknown tool". A test keeps them equal.
/// </summary>
public static class ToolCatalog
{
    public static readonly ToolSpec[] All =
    [
        new("list_files",
            "List files and folders under a path inside the workspace root.",
            ["path"],
            """{"tool":"list_files","args":{"path":"src"}}"""),

        new("read_file",
            "Read a UTF-8 text file inside the workspace root. Truncated at 64 KB.",
            ["path"],
            """{"tool":"read_file","args":{"path":"README.md"}}"""),

        new("final",
            "Answer the user and end the run. Use it as soon as you can answer.",
            ["text"],
            """{"tool":"final","args":{"text":"The project is a .NET CLI."}}"""),
    ];

    public static ToolSpec? Find(string name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
}
