using System.Collections;

namespace Agent.Common.Services;

/// <summary>
/// The environment a terminal tab's child process starts with.
///
/// <para><b>Why this is not just "inherit".</b> AgentZeroLite is a GUI process that
/// someone launched from somewhere — a shell, an IDE's terminal, another agent —
/// and it inherits that launcher's environment wholesale. Handing that straight to
/// a terminal child makes every tab pretend to be a continuation of whatever
/// happened to start the app. The symptom that found this: every terminal rendered
/// in one colour, because the app had been launched from a session with
/// <c>NO_COLOR=1</c> set and every CLI in every tab dutifully obeyed it.</para>
///
/// <para>This is the same thing terminal emulators all end up doing. VS Code has
/// <c>terminal.integrated.inheritEnv</c> and its own sanitizer; node-pty takes an
/// explicit <c>env</c> rather than defaulting to the parent's; projects embedding a
/// PTY strip host-specific variables like <c>NODE_CHANNEL_FD</c> for exactly this
/// reason — a stale IPC handle makes a child believe in a channel that is not
/// there.</para>
///
/// <para>So: inherit, then remove what described the <i>host</i> rather than the
/// child, then state what this terminal actually is.</para>
/// </summary>
public static class TerminalEnvironment
{
    /// <summary>What this terminal reports itself as, for CLIs that look.</summary>
    public const string TermProgram = "AgentZeroLite";

    /// <summary>
    /// Variables that describe the process that launched us and are actively
    /// misleading inside a fresh terminal.
    /// </summary>
    private static readonly string[] Drop =
    {
        // The monochrome bug. Honoured by chalk, Ink, and essentially every modern
        // CLI: set to anything non-empty and colour is off. Inheriting it from
        // whoever launched the GUI silenced colour in every tab.
        "NO_COLOR",

        // A stale IPC descriptor makes Node tools act as though a channel exists.
        "NODE_CHANNEL_FD",
        "NODE_OPTIONS",
        "BUN_WATCH_PID",

        // Another emulator's identity. A tab is this terminal, not the one the app
        // was started from.
        "TERM_SESSION_ID",
        "TERM_PROGRAM",
        "TERM_PROGRAM_VERSION",
        "TERMINAL_EMULATOR",
        "FIG_TERM",
    };

    /// <summary>
    /// Prefixes dropped wholesale. An agent CLI marks its own children so they know
    /// they are nested; a terminal the user opened is not a nested anything, and
    /// inheriting the marker makes it behave like one — the visible tell was tabs
    /// reporting that transcript saving was off because of an inherited session
    /// marker.
    /// </summary>
    private static readonly string[] DropPrefixes =
    {
        "CLAUDE_CODE_",
        "CLAUDECODE",
        "CLAUDE_PID",
    };

    /// <summary>
    /// Build the child environment from <paramref name="source"/> (the current
    /// process's environment when null).
    /// </summary>
    public static SortedDictionary<string, string> Build(IDictionary? source = null)
    {
        // Windows environment blocks are case-insensitive and must be sorted by
        // name for CreateProcess to accept them.
        var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (DictionaryEntry entry in source ?? Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key || key.Length == 0) continue;
            if (ShouldDrop(key)) continue;
            env[key] = entry.Value as string ?? "";
        }

        // xterm.js renders 24-bit colour, so say so. This is the convention the
        // termstandard/colors project documents and what kitty, Konsole and the
        // libvte emulators advertise.
        env["COLORTERM"] = "truecolor";
        env["TERM_PROGRAM"] = TermProgram;

        return env;
    }

    public static bool ShouldDrop(string name)
    {
        foreach (var d in Drop)
            if (string.Equals(name, d, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var p in DropPrefixes)
            if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// The block CreateProcess wants: <c>NAME=VALUE\0</c> repeated, then a final
    /// <c>\0</c>. Entries with an empty name, or the <c>=C:</c> drive-current-
    /// directory pseudo-variables, are skipped — an empty name terminates the block
    /// early and truncates everything after it.
    /// </summary>
    public static string ToBlock(IEnumerable<KeyValuePair<string, string>> env)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (key, value) in env)
        {
            if (string.IsNullOrEmpty(key) || key[0] == '=') continue;
            sb.Append(key).Append('=').Append(value).Append('\0');
        }
        sb.Append('\0');
        return sb.ToString();
    }
}
