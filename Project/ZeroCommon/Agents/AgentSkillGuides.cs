using System;
using System.Collections.Generic;

namespace Agent.Common.Agents;

/// <summary>
/// Version-matched skill guide text served by the running binary (missions
/// W4/W5, orca-adoption). The anti-drift pattern from orca: only a tiny STUB is
/// written into an agent's skills folder (see <c>SkillStubInjector</c>); the
/// FULL guide is served here at runtime via <c>-cli help &lt;topic&gt;</c> so it can
/// never drift from the binary that actually runs the commands.
/// </summary>
public static class AgentSkillGuides
{
    /// <summary>Topic → full guide text. Keys are lower-case, stable.</summary>
    public static readonly IReadOnlyDictionary<string, string> Guides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["agentzero"] = """
        AgentZero Lite — agent control surface (served by the live binary)
        ================================================================

        You are running inside an AgentZero Lite hosted terminal. You can drive
        the shell itself through its CLI. All commands are:

            AgentZeroLite.exe -cli <command> [args]

        Terminals (other agent tabs in this workspace):
          terminal-list                       List terminal groups/tabs + indices.
          terminal-read <grp> <tab> [--last N] Read another terminal's output.
          terminal-send <grp> <tab> "<text>"  Type into another terminal.
          terminal-wait <grp> <tab> [--timeout-ms N]
                                              Block until a terminal goes idle
                                              (output stops changing) — use this
                                              instead of polling read in a loop.
          terminal-alias set <g> <t> <name>   Give a terminal a stable name, then
          terminal-send --alias <name> "..."  target it by --alias instead of the
          terminal-read --alias <name>        volatile <grp> <tab> indices
                                              (--alias works on send/key/read).
                                              terminal-alias list | rm <name>.

        Resume a crashed/closed agent conversation:
          agent-resume <grp> <tab>            Print the 'claude --resume <id>' cmd.
          agent-resume-launch <grp> <tab>     Inject that resume cmd straight into
                                              the live terminal (--alias works too).

        Peer messaging (report back to the AgentBot coordinator):
          bot-chat "DONE(<msg>)" --from <yourName>

        Workspaces (git worktrees — isolated parallel checkouts):
          worktree list                       Show this repo's worktrees.
          worktree add <path> [branch]        Create an isolated worktree.
          worktree remove <path>              Remove one.

        Cost:
          cost                                Estimated USD spend so far.

        Rules:
          * Prefer terminal-wait over a read/sleep loop.
          * Keep bot-chat replies wrapped in DONE(...) so the coordinator routes them.
          * This guide is served by the binary — it always matches the installed
            command set. Do not cache it; re-run `-cli help agentzero` if unsure.
        """,

        ["orchestrate"] = """
        AgentZero orchestration (supervised multi-agent runs)
        =====================================================

        A coordinator dispatches tasks to worker agents and advances a DAG as
        each worker reports done. See `-cli orchestrate --help` for the current
        subcommands. Workers signal completion with:

            AgentZeroLite.exe -cli bot-chat "DONE(<result>)" --from <workerName>
        """,
    };

    /// <summary>Returns the guide for a topic, or null if unknown.</summary>
    public static string? Get(string topic)
        => Guides.TryGetValue(topic ?? "", out var g) ? g : null;

    /// <summary>All known topic names (for `-cli help` listing).</summary>
    public static IEnumerable<string> Topics => Guides.Keys;

    /// <summary>Marker identifying an AgentZero-installed skill stub (for clean uninstall).</summary>
    public const string StubMarker = "agentzero-skill-stub";

    /// <summary>
    /// Builds the tiny discovery STUB written into an agent's skills folder
    /// (mission W5, orca-adoption). Deliberately carries NO command reference of
    /// its own — it points at <c>-cli help &lt;topic&gt;</c> so the real instructions
    /// are always served by the live binary and can never drift.
    /// </summary>
    public static string BuildStub(string topic = "agentzero")
        => $$"""
        ---
        name: agentzero-control
        description: Drive the AgentZero Lite shell you are running inside (terminals, worktrees, orchestration, cost). Use when the task involves other agent tabs, isolated checkouts, or reporting back to the coordinator.
        marker: {{StubMarker}}
        ---

        # AgentZero control (discovery stub)

        This file is a **discovery stub, not the usage guide**. The full, always
        up-to-date reference is served by the AgentZero binary itself — kept out
        of this file on purpose so it can never drift from the commands that will
        actually run.

        To load the real instructions, run:

            AgentZeroLite.exe -cli help {{topic}}

        Do that first whenever you need to control terminals, git worktrees,
        orchestration, or check cost from inside an AgentZero session.
        """;
}
