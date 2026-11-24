namespace Agent.Common.Actors;

/// <summary>
/// The first-contact message AgentBot puts in front of its first send to a terminal peer —
/// one text for both hosts (each used to carry its own copy).
///
/// <para>It names the CLI by <b>absolute path</b>, the exe of the GUI that is speaking. A bare
/// <c>AgentZeroLite.exe</c> is resolved by whoever runs the command, and that is not always the
/// tab: a tab's PATH starts with its own GUI's folder, but Codex hands commands to a shared
/// <c>codex app-server</c> daemon whose PATH is that of whatever started the daemon first. With
/// two AgentZero windows up (a Debug build beside the installed one — each has its own CLI
/// pipe) the reply would then reach the other window, where no conversation is waiting for it
/// and it is dropped as an inactive peer. The exe decides the pipe, so the path decides the
/// window.</para>
/// </summary>
public static class PeerHandshake
{
    /// <param name="peerName">The peer's tab title — its <c>--from</c> identity.</param>
    /// <param name="cliPath">This GUI's exe (<see cref="Environment.ProcessPath"/>); null falls back to the bare name.</param>
    public static string BuildFirstContactHeader(string peerName, string? cliPath, bool? windows = null)
    {
        var exe = string.IsNullOrWhiteSpace(cliPath) ? null : cliPath;
        var quoted = exe is null ? "AgentZeroLite.exe" : "\"" + exe + "\"";
        // PowerShell needs the call operator in front of a quoted path; cmd and POSIX shells do not.
        var powershell = exe is not null && (windows ?? OperatingSystem.IsWindows());

        string Call(string args) => powershell
            ? "      PowerShell:  & " + quoted + " " + args + "\n"
            + "      cmd / bash:  " + quoted + " " + args + "\n"
            : "      " + quoted + " " + args + "\n";

        return
            "[AgentBot Handshake — first contact, please read carefully]\n"
            + "\n"
            + "You are " + peerName + " and I am AgentBot, an on-device AI agent running inside the AgentZero Lite shell.\n"
            + "\"" + peerName + "\" is the name of YOUR terminal tab — both of us will use that name to refer to you, and you will use it as your --from identity when replying.\n"
            + "\n"
            + (exe is null ? "" :
                "The CLI of THIS AgentZero window is the file below. Always call exactly this path: another AgentZeroLite.exe on PATH can belong to a different AgentZero window, and your reply would be delivered there instead of to me.\n"
                + "      " + exe + "\n"
                + "\n")
            + "Step 1 — Verify the CLI channel exists.\n"
            + "  Run this in your terminal RIGHT NOW to confirm the channel works:\n"
            + Call("-cli help")
            + "  If you see a usage list with a 'bot-chat' command, the reverse channel is available.\n"
            + "\n"
            + "Step 2 — Acknowledge using the same CLI.\n"
            + "  Once Step 1 succeeds, send your handshake reply:\n"
            + Call("-cli bot-chat \"DONE(handshake-ok)\" --from " + peerName)
            + "  The DONE(...) wrapper is the signal envelope. --from " + peerName + " names you so I know who answered. After this acknowledgement, the user can ask me to relay real tasks to you.\n"
            + "\n"
            + "Ongoing replies use the same shape:\n"
            + Call("-cli bot-chat \"DONE(your reply text)\" --from " + peerName)
            + "  Put ONLY your reply text inside DONE(...). No names in there: --from says who you are, and I know who the reply is for.\n"
            + "\n"
            + "Fallback: if that CLI cannot be run from your terminal, just reply normally here and I will fall back to polling read_terminal — but the CLI path is more reliable.\n"
            + "\n"
            + "You'll only see this introduction once per AIMODE session.\n"
            + "─── original message follows ───";
    }
}
