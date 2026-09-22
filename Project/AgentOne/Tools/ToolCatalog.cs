namespace AgentOne.Tools;

/// <param name="Name">Verb the model writes into the envelope's "tool" field.</param>
/// <param name="Family">Which toolbelt owns it — "files", "web", or "loop" for final.</param>
/// <param name="Args">Argument names, in the order the prompt lists them.</param>
public sealed record ToolSpec(string Name, string Family, string Summary, string[] Args, string Example);

/// <summary>
/// The catalog is the single source of truth for what the model is told it can
/// do, what `agent-one tools list` prints, and which belt a call is routed to.
/// A verb added to a toolbelt but not here is invisible to the model; a verb
/// here but not in a belt is answered with "unknown tool". Tests keep them equal.
///
/// Everything here is read-only. Adding a verb that changes something — writing
/// a file, running a command — is the point at which an approval gate has to
/// exist first.
/// </summary>
public static class ToolCatalog
{
    public const string FilesFamily = "files";
    public const string WebFamily = "web";
    public const string LoopFamily = "loop";

    /// <summary>Creates and changes files — inside the workspace root, never anywhere else.</summary>
    public const string EditFamily = "edit";

    /// <summary>Runs a shell command in the workspace root, through the approval gate.</summary>
    public const string ExecFamily = "exec";

    /// <summary>
    /// The families that change something. Every verb in one of these goes
    /// through a gate — the path sandbox for edits, the command gate for exec —
    /// and a test asserts that stays true.
    /// </summary>
    public static readonly string[] GuardedFamilies = [EditFamily, ExecFamily];

    /// <summary>The family a verb belongs to, or null for a verb the catalog does not know.</summary>
    public static string? FamilyOf(string tool)
    {
        foreach (var spec in All)
            if (string.Equals(spec.Name, tool, StringComparison.OrdinalIgnoreCase)) return spec.Family;
        return null;
    }

    public static readonly ToolSpec[] All =
    [
        new("list_files", FilesFamily,
            "List files and folders under a path inside the workspace root.",
            ["path"],
            """{"tool":"list_files","args":{"path":"src"}}"""),

        new("read_file", FilesFamily,
            "Read a UTF-8 text file inside the workspace root. Truncated at 64 KB.",
            ["path"],
            """{"tool":"read_file","args":{"path":"README.md"}}"""),

        new("find_files", FilesFamily,
            "Find files by name pattern (e.g. *.cs) anywhere under a path.",
            ["pattern", "path"],
            """{"tool":"find_files","args":{"pattern":"*.csproj","path":"."}}"""),

        new("grep", FilesFamily,
            "Find which files contain a piece of text. Case-insensitive, plain text, not a regex.",
            ["text", "path", "glob"],
            """{"tool":"grep","args":{"text":"ConfigureServices","glob":"*.cs"}}"""),

        new("web_search", WebFamily,
            "Search the web. Returns titles, URLs and snippets — not the pages themselves.",
            ["query", "count"],
            """{"tool":"web_search","args":{"query":"akka.net actor supervision"}}"""),

        new("web_read", WebFamily,
            "Fetch one web page and return its readable text. Truncated at 24 000 characters.",
            ["url"],
            """{"tool":"web_read","args":{"url":"https://getakka.net/articles/concepts/actors.html"}}"""),

        new("write_file", EditFamily,
            "Create or overwrite a UTF-8 text file inside the workspace root, creating folders as needed. Always the whole file.",
            ["path", "content"],
            """{"tool":"write_file","args":{"path":"src/app.py","content":"print('hi')\n"}}"""),

        new("run_command", ExecFamily,
            "Run ONE shell command in the workspace root (PowerShell on Windows, bash elsewhere) and get its output and exit code. " +
            "A risky command is put to the user first and may be declined.",
            ["command"],
            """{"tool":"run_command","args":{"command":"dotnet build"}}"""),

        new("final", LoopFamily,
            "Answer the user and end the run. Use it as soon as you can answer.",
            ["text"],
            """{"tool":"final","args":{"text":"The project is a .NET CLI."}}"""),
    ];

    public static ToolSpec? Find(string name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The families a session must provide a belt for (everything but the loop's own verb).</summary>
    public static IEnumerable<string> Families =>
        All.Select(t => t.Family).Where(f => f != LoopFamily).Distinct(StringComparer.Ordinal);
}
