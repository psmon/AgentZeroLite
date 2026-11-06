namespace AgentOne.Tools;

/// <param name="Name">Verb the model writes into the envelope's "tool" field.</param>
/// <param name="Family">Which toolbelt owns it — "files", "web", or "loop" for final.</param>
/// <param name="Args">Argument names, in the order the prompt lists them.</param>
/// <param name="Repeat">When the same call (same verb, same arguments) counts as repetition — see <see cref="RepeatRule"/>.</param>
public sealed record ToolSpec(string Name, string Family, string Summary, string[] Args, string Example, RepeatRule Repeat = RepeatRule.Once);

/// <summary>How the loop's repeat guard treats a verb called again with the same arguments.</summary>
public enum RepeatRule
{
    /// <summary>The result cannot change within a turn: a second identical call is repetition.</summary>
    Once,

    /// <summary>
    /// It LOOKS at something that changes under it — a page, a running process. Only the same call twice IN A ROW is
    /// repetition; with anything in between it is a new look. Measured trap: the guard would end a browsing turn on the
    /// second browser_snapshot of a page the model had just clicked through.
    /// </summary>
    AfterChange,

    /// <summary>
    /// An action that is legitimately repeated — pressing End to load more of a list, clicking "next". Never a
    /// repetition; the step budget bounds it.
    /// </summary>
    Free
}

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

    /// <summary>Asks the decision engine (Jev) to judge between options. Touches nothing.</summary>
    public const string DecideFamily = "decide";

    /// <summary>
    /// A real browser driven through playwright-cli — the person's own Chrome
    /// when it listens for remote debugging, otherwise one of the agent's own.
    /// Reading verbs run freely; acting verbs (click, fill, press) go through
    /// the browser gate, once per site per session.
    /// </summary>
    public const string BrowserFamily = "browser";

    /// <summary>The built-in usage cards for well-known command-line tools. Reads nothing on disk.</summary>
    public const string GuideFamily = "guide";

    /// <summary>
    /// Families a smart-mode route never rules out: a judgment call reads and
    /// changes nothing, so refusing it on a web or answer-directly turn would
    /// only take away the model's way of checking itself.
    /// </summary>
    public static bool IsRouteExempt(string family) =>
        string.Equals(family, DecideFamily, StringComparison.OrdinalIgnoreCase)
        || string.Equals(family, GuideFamily, StringComparison.OrdinalIgnoreCase);

    /// <summary>How the repeat guard treats <paramref name="tool"/>; an unknown verb is <see cref="RepeatRule.Once"/>.</summary>
    public static RepeatRule RepeatOf(string tool) => Find(tool)?.Repeat ?? RepeatRule.Once;

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
            "List files and folders under a path inside the workspace root. At the root it also names the Claude skills (project .claude, enabled plugins) found for the workspace.",
            ["path"],
            """{"tool":"list_files","args":{"path":"src"}}"""),

        new("read_file", FilesFamily,
            "Read a UTF-8 text file inside the workspace root. Truncated at 64 KB.",
            ["path"],
            """{"tool":"read_file","args":{"path":"README.md"}}"""),

        new("find_files", FilesFamily,
            "Find files by name pattern (e.g. *.cs) anywhere under a path. From the root, Claude skills and the files they point to are searched first.",
            ["pattern", "path"],
            """{"tool":"find_files","args":{"pattern":"*.csproj","path":"."}}"""),

        new("grep", FilesFamily,
            "Find which files contain a piece of text. Case-insensitive, plain text, not a regex. From the root, Claude skills and the files they point to are searched first.",
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
            "Run ONE shell command in the workspace root (the shell named in the rules above) and get its output and exit code. " +
            "A risky command is put to the user first and may be declined. " +
            "Anything that keeps running — a dev server, a watcher — needs \"background\":\"true\" (it is also detected): " +
            "it starts in the background, the step returns once it is up, and process_status / process_stop manage it.",
            ["command", "background"],
            """{"tool":"run_command","args":{"command":"dotnet build"}}"""),

        new("process_status", ExecFamily,
            "List the processes this session started (no id), or one process's state, address and newest output.",
            ["id"],
            """{"tool":"process_status","args":{"id":"p1"}}""",
            Repeat: RepeatRule.AfterChange),

        new("process_stop", ExecFamily,
            "Stop a process this session started, and everything it started.",
            ["id"],
            """{"tool":"process_stop","args":{"id":"p1"}}"""),

        new("cli_guide", GuideFamily,
            "How to use a well-known command-line tool (playwright-cli, gh, git, ffmpeg, yt-dlp, node, python, dotnet, docker…): " +
            "whether it is installed here and its key commands. No name lists them all. Read it instead of searching the web " +
            "for a tool's usage; then run the tool with run_command.",
            ["name"],
            """{"tool":"cli_guide","args":{"name":"ffmpeg"}}"""),

        new("browser_open", BrowserFamily,
            "Open a URL in a real browser (a new tab in the user's own Chrome when it runs with remote debugging, " +
            "otherwise a browser of your own) and return the page: title, URL and its element tree with [ref=eN] refs. " +
            "Use it for pages that need a browser — logged-in sites, YouTube, pages built by JavaScript.",
            ["url"],
            """{"tool":"browser_open","args":{"url":"https://www.youtube.com/@channel/videos"}}"""),

        new("browser_snapshot", BrowserFamily,
            "The current tab's element tree with [ref=eN] refs (what to click or fill). Take a new one after acting on the page.",
            [],
            """{"tool":"browser_snapshot","args":{}}""",
            Repeat: RepeatRule.AfterChange),

        new("browser_read", BrowserFamily,
            "The current tab's visible text (title, URL, body text). Truncated at 24 000 characters.",
            [],
            """{"tool":"browser_read","args":{}}""",
            Repeat: RepeatRule.AfterChange),

        new("browser_find", BrowserFamily,
            "Search the current tab for a piece of text; returns the matching elements with their refs.",
            ["text"],
            """{"tool":"browser_find","args":{"text":"Upload date"}}""",
            Repeat: RepeatRule.AfterChange),

        new("browser_click", BrowserFamily,
            "Click an element by its ref from the latest snapshot (e.g. e21). The first action on a site is put to the user.",
            ["ref"],
            """{"tool":"browser_click","args":{"ref":"e21"}}""",
            Repeat: RepeatRule.Free),

        new("browser_fill", BrowserFamily,
            "Type text into an input by its ref; submit \"true\" presses Enter after it.",
            ["ref", "text", "submit"],
            """{"tool":"browser_fill","args":{"ref":"e5","text":"lofi hip hop","submit":"true"}}""",
            Repeat: RepeatRule.Free),

        new("browser_press", BrowserFamily,
            "Press a key in the current tab: Enter, Escape, PageDown, ArrowDown, End…",
            ["key"],
            """{"tool":"browser_press","args":{"key":"PageDown"}}""",
            Repeat: RepeatRule.Free),

        new("browser_tabs", BrowserFamily,
            "List the browser's tabs (no index), or switch to tab number 'index'.",
            ["index"],
            """{"tool":"browser_tabs","args":{}}""",
            Repeat: RepeatRule.AfterChange),

        new("decide", DecideFamily,
            "Put a judgment call to the decision engine (Jev): it picks one of 2-12 options and says how sure it is. " +
            "Use it when a choice between options you already have is genuinely unclear — which approach, which file, " +
            "whether the work is done. It judges what you give it in 'context'; it looks nothing up. " +
            "options: one per line, or separated by ';', each name: description.",
            ["question", "options", "context"],
            """{"tool":"decide","args":{"question":"Which fix should be applied?","options":"patch: change the one failing call; rewrite: replace the module","context":"The test fails only on Windows paths."}}"""),

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
