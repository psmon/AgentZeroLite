using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Agent.Common.Actors;
using Agent.Common.Llm.Tools;
using AgentZeroWpf.Module;
using AgentZeroWpf.OsControl;

namespace AgentZeroWpf.Services;

/// <summary>
/// Production <see cref="IAgentToolbelt"/> implementation for the AgentBot
/// AIMODE feature. Bridges the Tools layer (Gemma / Nemotron agent loops in
/// <c>Agent.Common.Llm.Tools</c>) to the live workspace + terminal topology
/// owned by <c>MainWindow</c>.
///
/// Mock counterpart for headless testing lives at
/// <c>Project/ZeroCommon.Tests/AgentLoopTests.cs / MockAgentToolbelt</c>.
///
/// Behavioural parity: this host reuses the **same dispatch helpers** the
/// existing WM_COPYDATA CLI handlers use — `CliTerminalIpcHelper.TryResolveSession`
/// for resolution, `BuildTerminalListJson` for the catalog, and the same
/// session method calls (`WriteAndSubmit`, `Write`, `ReadOutput`,
/// `GetConsoleText`). That guarantees a `terminal-send` over CLI and a
/// <c>send_to_terminal</c> tool call from Gemma reach the terminal in
/// the exact same way.
/// </summary>
public sealed class WorkspaceTerminalToolHost : IAgentToolbelt
{
    private readonly System.Func<IReadOnlyList<CliGroupInfo>> _groupsProvider;

    public WorkspaceTerminalToolHost(System.Func<IReadOnlyList<CliGroupInfo>> groupsProvider)
    {
        _groupsProvider = groupsProvider;
    }

    public Task<string> ListTerminalsAsync(CancellationToken ct)
    {
        var groups = _groupsProvider();
        var json = CliTerminalIpcHelper.BuildTerminalListJson(groups, EscapeJson);
        return Task.FromResult(json);
    }

    public Task<string> ReadTerminalAsync(int group, int tab, int lastN, CancellationToken ct)
    {
        if (!TryResolveOrError(group, tab, out var session, out var errorJson))
            return Task.FromResult(errorJson!);

        try
        {
            string text;
            if (lastN > 0)
            {
                int totalLen = session!.OutputLength;
                int start = System.Math.Max(0, totalLen - lastN);
                int len = totalLen - start;
                text = len > 0 ? session.ReadOutput(start, len) : "";
            }
            else
            {
                text = session!.GetConsoleText();
            }

            text = ApprovalParser.StripAnsiCodes(text);
            var resp = $"{{\"ok\":true,\"group_index\":{group},\"tab_index\":{tab},\"length\":{text.Length},\"text\":\"{EscapeJson(text)}\"}}";
            return Task.FromResult(resp);
        }
        catch (System.Exception ex)
        {
            AppLogger.Log($"[AIMODE] read_terminal FAILED [{group}:{tab}] {ex.GetType().Name}: {ex.Message}");
            return Task.FromResult($"{{\"ok\":false,\"error\":\"Read failed: {EscapeJson(ex.Message)}\"}}");
        }
    }

    public async Task<bool> SendToTerminalAsync(int group, int tab, string text, CancellationToken ct)
    {
        if (!TryResolveOrError(group, tab, out var session, out _))
            return false;

        // First-contact protocol — ask the AgentBotActor (which holds the
        // introduction state per Akka best practice) whether this (group, tab)
        // has been talked-to in the current AIMODE session. If not, prepend a
        // one-shot self-introduction so the receiving terminal AI knows what
        // it's talking to + how the channel works (read responses inline; no
        // reverse function-call channel exists, just type back into your
        // terminal and AgentBot will read via read_terminal).
        try
        {
            // AgentBotActor lives at /user/stage/bot as a child of StageActor.
            // Ask the actor (which holds the per-AIMODE-session introduction
            // state) whether this is first contact for (group, tab). Reply
            // is atomic — the actor marks "introduced" before sending the
            // reply, so a second concurrent Ask correctly gets WasFirstContact=false.
            // ResolveOne resolves the path to a concrete IActorRef so Ask
            // (which is defined as an extension on IActorRef, not ActorSelection)
            // can be used.
            var bot = await AgentZeroWpf.Actors.ActorSystemManager.System
                .ActorSelection("/user/stage/bot")
                .ResolveOne(System.TimeSpan.FromSeconds(1));
            var reply = await bot.Ask<IntroduceTerminalReply>(
                new IntroduceTerminalIfFirst(group, tab),
                System.TimeSpan.FromSeconds(2));
            // The peer name string is the contract between AgentBot's
            // first-contact handshake instructions ("call back with --from
            // <peerName>") and the bot-chat IPC routing. Use the terminal
            // tab's display title — it's stable, user-visible, and what the
            // intro tells the peer to use.
            var peerName = ResolvePeerName(group, tab);
            if (reply.WasFirstContact)
            {
                text = BuildFirstContactHeader(peerName) + "\n\n" + text;
                AppLogger.Log($"[AIMODE] prepended first-contact intro for peer=\"{peerName}\" [{group}:{tab}]");
                // First contact = handshake initiation. Tell Bot we're
                // awaiting a bot-chat callback for this peer.
                try
                {
                    bot.Tell(new MarkHandshakeSent(peerName));
                }
                catch { /* not load-bearing */ }
            }
            // Mark conversation active on EVERY send so peer signals route
            // back to the agent loop as continuations. Idempotent on the bot side.
            try { bot.Tell(new MarkConversationActive(peerName)); }
            catch { /* not load-bearing */ }
        }
        catch (System.Exception ex)
        {
            // Introduction tracking is a UX nicety, not load-bearing — log
            // and proceed without an intro if the actor query fails.
            AppLogger.Log($"[AIMODE] introduction Ask failed (sending without intro): {ex.Message}");
        }

        try
        {
            session!.WriteAndSubmit(text);
            int preview = System.Math.Min(text.Length, 30);
            AppLogger.Log($"[AIMODE] send_to_terminal [{group}:{tab}] len={text.Length} preview=\"{text.Substring(0, preview)}\"");
            return true;
        }
        catch (System.Exception ex)
        {
            AppLogger.Log($"[AIMODE] send_to_terminal FAILED [{group}:{tab}] {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // English by design — LLM prompts and prompts directed at LLM-backed
    // terminals (Claude, Codex, etc.) default to English for tokenization
    // efficiency and stability across model families. See harness/knowledge/
    // llm-prompt-conventions.md.
    //
    // Identity convention: peerName == terminal tab name (e.g. "Claude").
    // Both parties refer to the peer by this name AND use it as the
    // `--from` value when calling back via bot-chat. So the same string
    // ties together: (a) "who you are" in the handshake, (b) the
    // identity tag in bot-chat replies, (c) the bot's per-peer
    // bookkeeping (active conversation set, handshake state).
    //
    // Handshake flow: VERIFY CLI exists → ACKNOWLEDGE via that same CLI.
    // Two explicit steps so the peer commits to the channel before the
    // bot trusts it.
    private static string BuildFirstContactHeader(string peerName = "Claude") =>
        "[AgentBot Handshake — first contact, please read carefully]\n"
        + "\n"
        + "You are " + peerName + " and I am AgentBot, an on-device AI agent running inside the AgentZero Lite shell.\n"
        + "\"" + peerName + "\" is the name of YOUR terminal tab — both of us will use that name to refer to you, and you will use it as your --from identity when replying.\n"
        + "\n"
        + "Step 1 — Verify the CLI channel exists.\n"
        + "  Run this in your terminal RIGHT NOW to confirm the channel works:\n"
        + "      AgentZeroLite.exe -cli help\n"
        + "  If you see a usage list with a 'bot-chat' command, the reverse channel is available.\n"
        + "\n"
        + "Step 2 — Acknowledge using the same CLI.\n"
        + "  Once Step 1 succeeds, send your handshake reply:\n"
        + "      AgentZeroLite.exe -cli bot-chat \"DONE(handshake-ok)\" --from " + peerName + "\n"
        + "  The DONE(...) wrapper is the signal envelope. --from " + peerName + " names you so I know who answered. After this acknowledgement, the user can ask me to relay real tasks to you.\n"
        + "\n"
        + "Ongoing replies use the same shape:\n"
        + "      AgentZeroLite.exe -cli bot-chat \"DONE(your reply text)\" --from " + peerName + "\n"
        + "  PowerShell wrapper if PATH-friendly: AgentZeroLite.ps1 bot-chat \"DONE(...)\" --from " + peerName + "\n"
        + "\n"
        + "Fallback: if AgentZeroLite.exe is not reachable from your terminal, just reply normally here and I will fall back to polling read_terminal — but the CLI path is more reliable.\n"
        + "\n"
        + "You'll only see this introduction once per AIMODE session.\n"
        + "─── original message follows ───";

    public Task<bool> SendKeyAsync(int group, int tab, string key, CancellationToken ct)
    {
        if (!TryResolveOrError(group, tab, out var session, out _))
            return Task.FromResult(false);

        var seq = MapKeyName(key);
        if (string.IsNullOrEmpty(seq))
        {
            AppLogger.Log($"[AIMODE] send_key REJECTED [{group}:{tab}] unknown key=\"{key}\"");
            return Task.FromResult(false);
        }

        try
        {
            session!.Write(seq.AsSpan());
            AppLogger.Log($"[AIMODE] send_key [{group}:{tab}] key={key} seq_bytes={seq.Length}");
            return Task.FromResult(true);
        }
        catch (System.Exception ex)
        {
            AppLogger.Log($"[AIMODE] send_key FAILED [{group}:{tab}] key={key} {ex.GetType().Name}: {ex.Message}");
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Returns the user-visible tab title for (group, tab) — used as the
    /// peer-name string that ties together first-contact handshake
    /// instructions and bot-chat `--from` routing. Falls back to a
    /// synthetic name if the indices don't resolve.
    /// </summary>
    private string ResolvePeerName(int group, int tab)
    {
        try
        {
            var groups = _groupsProvider();
            if (group >= 0 && group < groups.Count)
            {
                var g = groups[group];
                if (g.Tabs is not null && tab >= 0 && tab < g.Tabs.Count)
                {
                    var title = g.Tabs[tab].Title;
                    if (!string.IsNullOrWhiteSpace(title))
                        return title;
                }
            }
        }
        catch { /* fall through */ }
        return $"Term-{group}-{tab}";
    }

    private bool TryResolveOrError(int group, int tab, out ConPtyTerminalSession? session, out string? errorJson)
    {
        var groups = _groupsProvider();
        return CliTerminalIpcHelper.TryResolveSession(
            groups,
            group,
            tab,
            $"Invalid group_index {group}.",
            $"Invalid tab_index {tab} in group {group}.",
            $"Terminal [{group}:{tab}] is not started.",
            out _,
            out _,
            out session,
            out errorJson);
    }

    // Same key map as MainWindow.HandleTerminalKey — keep them in lockstep so
    // CLI `terminal-key` and AIMODE `send_key` accept the exact same names.
    private static string MapKeyName(string key) => key switch
    {
        "cr"        => "\r",
        "lf"        => "\n",
        "crlf"      => "\r\n",
        "esc"       => "\x1B",
        "tab"       => "\t",
        // Shift+Tab — ESC[Z. "shifttab" + "backtab" alias kept in lockstep
        // with MainWindow.HandleTerminalKey so CLI `terminal-key` and AIMODE
        // `send_key` accept the same names.
        "shifttab"  => "\x1b[Z",
        "backtab"   => "\x1b[Z",
        "backspace" => "\x08",
        "del"       => "\x7F",
        "ctrlc"     => "\x03",
        "ctrld"     => "\x04",
        "up"        => "\x1B[A",
        "down"      => "\x1B[B",
        "right"     => "\x1B[C",
        "left"      => "\x1B[D",
        _           => "",
    };

    private static string EscapeJson(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"':  sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    // ====================== OS-control bridge (mission M0014) ================
    // Read-only verbs (list/screenshot/activate/element-tree) are unconditional.
    // Input-simulation verbs (mouse/key) consult OsApprovalGate before
    // touching SendInput so a hijacked LLM session can't drive the desktop
    // unless the operator opted in via env var or GUI Settings toggle.

    public Task<string> OsListWindowsAsync(string? titleFilter, CancellationToken ct)
        => Task.FromResult(OsControlService.ListWindows(titleFilter, includeHidden: false, OsAuditLog.Caller.Llm));

    public Task<string> OsScreenshotAsync(long hwnd, bool grayscale, CancellationToken ct)
        => Task.FromResult(OsControlService.Screenshot(hwnd, grayscale, fullDesktop: hwnd == 0, OsAuditLog.Caller.Llm));

    public Task<string> OsActivateAsync(long hwnd, CancellationToken ct)
        => Task.FromResult(OsControlService.Activate(hwnd, OsAuditLog.Caller.Llm));

    public Task<string> OsElementTreeAsync(long hwnd, int maxDepth, string? search, CancellationToken ct)
        => OsControlService.ElementTreeAsync(hwnd, maxDepth, search, OsAuditLog.Caller.Llm);

    public Task<string> OsMouseClickAsync(int x, int y, bool right, bool dbl, CancellationToken ct)
    {
        bool gateOk = OsApprovalGate.IsInputAllowedByEnv();
        return Task.FromResult(OsControlService.MouseClick(x, y, right, dbl, gateOk, OsAuditLog.Caller.Llm));
    }

    public Task<string> OsKeyPressAsync(string keySpec, CancellationToken ct)
    {
        bool gateOk = OsApprovalGate.IsInputAllowedByEnv();
        return Task.FromResult(OsControlService.KeyPress(keySpec, gateOk, OsAuditLog.Caller.Llm));
    }
}
