using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Agent.Common.Actors;
using Agent.Common.Llm.Tools;
using AgentZeroWpf.Module;

namespace AgentZeroWpf.Services;

/// <summary>
/// Production <see cref="IAgentToolHost"/> implementation for the AgentBot
/// AIMODE feature. Bridges the Tools layer (Gemma / Nemotron tool loops in
/// <c>Agent.Common.Llm.Tools</c>) to the live workspace + terminal topology
/// owned by <c>MainWindow</c>.
///
/// Mock counterpart for headless testing lives at
/// <c>Project/ZeroCommon.Tests/AgentToolLoopTests.cs / MockAgentToolHost</c>.
///
/// Behavioural parity: this host reuses the **same dispatch helpers** the
/// existing WM_COPYDATA CLI handlers use — `CliTerminalIpcHelper.TryResolveSession`
/// for resolution, `BuildTerminalListJson` for the catalog, and the same
/// session method calls (`WriteAndSubmit`, `Write`, `ReadOutput`,
/// `GetConsoleText`). That guarantees a `terminal-send` over CLI and a
/// <c>send_to_terminal</c> tool call from Gemma reach the terminal in
/// the exact same way.
/// </summary>
public sealed class WorkspaceTerminalToolHost : IAgentToolHost
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
            if (reply.WasFirstContact)
            {
                text = BuildFirstContactHeader() + "\n\n" + text;
                AppLogger.Log($"[AIMODE] prepended first-contact intro for [{group}:{tab}]");
            }
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
    private static string BuildFirstContactHeader() =>
        "[AgentBot self-introduction]\n"
        + "Hi! I'm AgentBot — an on-device AI agent running inside the AgentZero Lite shell. "
        + "The user asked me to relay a message to this terminal, so I connected here to talk with you.\n"
        + "How to talk back: just type your reply normally into this terminal. "
        + "I read your output via a read_terminal function call and use it to decide what to do next.\n"
        + "If you ever need to message a DIFFERENT terminal tab, you can use: "
        + "`AgentZeroLite.ps1 terminal-send <group> <tab> \"text\"`. "
        + "There is no reverse function-call channel back to me — just answer here and I will see it.\n"
        + "You will only see this introduction once per AIMODE session.\n"
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
}
