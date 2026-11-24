using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Akka.Actor;
using Agent.Common;
using Agent.Common.Actors;
using Agent.Common.Llm.Tools;
using Agent.Common.Mcp;
using Agent.Common.Module;
using Agent.Common.Services;
using Agent.Common.Web;
using Agent.Common.Wearable;
using AgentZeroAvalonia.Actors;

namespace AgentZeroAvalonia.Services;

/// <summary>
/// The agent loop's side-effect surface in this host (M0037): a port of the WPF
/// <c>WorkspaceTerminalToolHost</c>. Terminal verbs go through the same catalog the CLI
/// uses (<see cref="TerminalCatalogJson"/>) with the first-contact handshake the
/// <c>AgentBotActor</c> tracks; files through <see cref="FileToolCore"/> under the active
/// workspace root; <c>find_files/open_file/stop_media</c> through <see cref="FileOpenPolicy"/>
/// and the OS's default program; web through the headless surface (no browser page in
/// this host yet); MCP tools (<c>server.tool</c>) through <see cref="McpHub.Shared"/>. The
/// OS-control verbs keep the interface's "not available" defaults.
/// </summary>
internal sealed class WorkspaceToolHost : IAgentToolbelt
{
    /// <summary>One headless web surface for the whole host: the agent's web tools and <c>-cli web</c> share its tabs.</summary>
    internal static HeadlessWebToolSurface SharedWeb { get; } = new();
    private static HeadlessWebToolSurface Web => SharedWeb;
    private static readonly MediaPlaybackTracker MediaTracker = new();

    /// <summary>
    /// What <c>write_file</c> / <c>edit_file</c> changed this run — the code panel's DIFF tab shows it
    /// ("AI changes") and can put the originals back. One for the host, like the web surface.
    /// </summary>
    internal static Agent.Common.Workspace.AiChangeJournal AiChanges { get; } = new();

    private readonly Func<IReadOnlyList<ICliGroupInfo>> _groups;
    private readonly Func<string?> _workspaceRoot;

    public WorkspaceToolHost(Func<IReadOnlyList<ICliGroupInfo>> groups, Func<string?> workspaceRoot)
    {
        _groups = groups;
        _workspaceRoot = workspaceRoot;
    }

    // ── terminals ────────────────────────────────────────────────────────────

    public Task<string> ListTerminalsAsync(CancellationToken ct)
        => Task.FromResult(TerminalCatalogJson.BuildTerminalListJson(_groups(), null));

    public Task<string> ReadTerminalAsync(int group, int tab, int lastN, CancellationToken ct)
    {
        if (!TryResolve(group, tab, out var session, out var errorJson)) return Task.FromResult(errorJson!);
        try
        {
            // What is on the screen, not the last N characters the pipe produced. A TUI
            // redraws: measured on a Codex tab, the raw tail was the same reply three times
            // over with its spaces gone ("안녕하세요!세계평화는국가간전쟁을막는일뿐") while the
            // screen read cleanly — and the bot, unable to find the answer in it, greeted
            // Codex four times. The raw tail is only the fallback for a session whose
            // renderer has not pushed a snapshot yet.
            var text = TerminalScreenText.Tidy(session!.GetConsoleText());
            if (text.Length == 0)
            {
                var total = session.OutputLength;
                var start = Math.Max(0, total - Math.Max(lastN, 2000));
                var len = total - start;
                text = len > 0 ? ApprovalParser.StripAnsiCodes(session.ReadOutput(start, len)) : "";
            }
            else if (lastN > 0 && text.Length > lastN)
            {
                text = text[^lastN..];
            }
            // A peer whose agent cannot run commands cannot call bot-chat either; say so, or the
            // bot waits for a callback that will never come (Agents/PeerCliFault).
            var note = "";
            if (Agent.Common.Agents.PeerCliFault.NoteFor(text) is { } fault)
            {
                note = $",\"note\":\"{Agent.Common.Platform.CliIpcProtocol.Escape(fault)}\"";
                AppLogger.Log($"[AIMODE] read_terminal [{group}:{tab}] peer cannot run its CLI — {fault[..Math.Min(fault.Length, 160)]}");
            }
            return Task.FromResult($"{{\"ok\":true,\"group_index\":{group},\"tab_index\":{tab},\"length\":{text.Length},\"text\":\"{Agent.Common.Platform.CliIpcProtocol.Escape(text)}\"{note}}}");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[AIMODE] read_terminal FAILED [{group}:{tab}] {ex.GetType().Name}: {ex.Message}");
            return Task.FromResult(ToolJson.Fail("Read failed: " + ex.Message));
        }
    }

    public async Task<bool> SendToTerminalAsync(int group, int tab, string text, CancellationToken ct)
    {
        if (!TryResolve(group, tab, out var session, out _)) return false;

        // First-contact protocol: the AgentBotActor holds the per-session "introduced"
        // state; the first send to a (group, tab) gets the handshake header prepended.
        try
        {
            var bot = await ActorSystemManager.System.ActorSelection("/user/stage/bot").ResolveOne(TimeSpan.FromSeconds(1));
            var reply = await bot.Ask<IntroduceTerminalReply>(new IntroduceTerminalIfFirst(group, tab), TimeSpan.FromSeconds(2));
            var peerName = ResolvePeerName(group, tab);
            if (reply.WasFirstContact)
            {
                text = BuildFirstContactHeader(peerName) + "\n\n" + text;
                AppLogger.Log($"[AIMODE] prepended first-contact intro for peer=\"{peerName}\" [{group}:{tab}]");
                try { bot.Tell(new MarkHandshakeSent(peerName)); } catch { }
            }
            try { bot.Tell(new MarkConversationActive(peerName)); } catch { }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[AIMODE] introduction Ask failed (sending without intro): {ex.Message}");
        }

        try
        {
            session!.WriteAndSubmit(text);
            AppLogger.Log($"[AIMODE] send_to_terminal [{group}:{tab}] len={text.Length} preview=\"{text[..Math.Min(text.Length, 30)]}\"");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[AIMODE] send_to_terminal FAILED [{group}:{tab}] {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public Task<bool> SendKeyAsync(int group, int tab, string key, CancellationToken ct)
    {
        if (!TryResolve(group, tab, out var session, out _)) return Task.FromResult(false);
        var seq = Cli.CliCommandRouter.KeySequence(key.ToLowerInvariant());
        if (seq.Length == 0)
        {
            AppLogger.Log($"[AIMODE] send_key REJECTED [{group}:{tab}] unknown key=\"{key}\"");
            return Task.FromResult(false);
        }
        try
        {
            session!.NoteInputAttempt("agent");
            session.Write(seq.AsSpan());
            AppLogger.Log($"[AIMODE] send_key [{group}:{tab}] key={key} seq_bytes={seq.Length}");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[AIMODE] send_key FAILED [{group}:{tab}] key={key} {ex.GetType().Name}: {ex.Message}");
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// The handshake text (ZeroCommon <see cref="PeerHandshake"/>, one for both hosts), naming this
    /// GUI's own exe so a peer's reply cannot land in another AgentZero window.
    /// </summary>
    internal static string BuildFirstContactHeader(string peerName)
        => PeerHandshake.BuildFirstContactHeader(peerName, Environment.ProcessPath);

    private string ResolvePeerName(int group, int tab)
    {
        try
        {
            var groups = _groups();
            if (group >= 0 && group < groups.Count)
            {
                var tabs = groups[group].TabsView;
                if (tab >= 0 && tab < tabs.Count && !string.IsNullOrWhiteSpace(tabs[tab].Title)) return tabs[tab].Title;
            }
        }
        catch { }
        return $"Term-{group}-{tab}";
    }

    private bool TryResolve(int group, int tab, out ITerminalSession? session, out string? errorJson)
        => TerminalCatalogJson.TryResolveSession(_groups(), group, tab, out _, out _, out session, out errorJson,
            $"Invalid group_index {group}.", $"Invalid tab_index {tab} in group {group}.", $"Terminal [{group}:{tab}] is not started.");

    // ── files (FileToolCore under the workspace root) ────────────────────────

    private string? Root() => _workspaceRoot();

    public Task<string> ReadFileAsync(string path, int maxBytes, CancellationToken ct)
    {
        var root = Root();
        AppLogger.Log($"[AIMODE] read_file root=\"{root ?? "<none>"}\" path=\"{path}\"");
        return Task.FromResult(FileToolCore.ReadFile(root, path, maxBytes));
    }

    public Task<string> WriteFileAsync(string path, string content, CancellationToken ct)
    {
        var root = Root();
        AppLogger.Log($"[AIMODE] write_file root=\"{root ?? "<none>"}\" path=\"{path}\" len={content.Length}");
        Journal(root, path);
        return Task.FromResult(FileToolCore.WriteFile(root, path, content));
    }

    public Task<string> EditFileAsync(string path, string oldString, string newString, bool replaceAll, CancellationToken ct)
    {
        var root = Root();
        AppLogger.Log($"[AIMODE] edit_file root=\"{root ?? "<none>"}\" path=\"{path}\" replace_all={replaceAll}");
        Journal(root, path);
        return Task.FromResult(FileToolCore.Edit(root, path, oldString, newString, replaceAll));
    }

    /// <summary>Remember the file's original before the tool touches it; a journal fault never stops the write.</summary>
    private static void Journal(string? root, string path)
    {
        try { AiChanges.BeforeWrite(root, path); }
        catch (Exception ex) { AppLogger.Log($"[AiChanges] could not record {path}: {ex.Message}"); }
    }

    public Task<string> GrepAsync(string pattern, string? pathFilter, int maxResults, CancellationToken ct)
    {
        var root = Root();
        AppLogger.Log($"[AIMODE] grep root=\"{root ?? "<none>"}\" pattern=\"{pattern}\" path=\"{pathFilter}\"");
        return Task.FromResult(FileToolCore.Grep(root, pattern, pathFilter, maxResults));
    }

    public Task<string> ListFilesAsync(string? pathFilter, int maxEntries, CancellationToken ct)
    {
        var root = Root();
        AppLogger.Log($"[AIMODE] list_files root=\"{root ?? "<none>"}\" path=\"{pathFilter}\"");
        return Task.FromResult(FileToolCore.ListFiles(root, pathFilter, maxEntries));
    }

    public Task<string> FindFilesAsync(string? query, string? kind, int maxResults, CancellationToken ct)
        => Task.FromResult(FileToolCore.FindFiles(Root(), query, kind, maxResults));

    public Task<string> OpenFileAsync(string path, CancellationToken ct)
    {
        var root = Root();
        if (!FileOpenPolicy.TryClassify(path, out var kind, out var error)) return Task.FromResult(ToolJson.Fail(error));
        if (!FileToolCore.TryResolveInsideRoot(root, path, out var full, out error)) return Task.FromResult(ToolJson.Fail(error));
        if (!File.Exists(full)) return Task.FromResult(ToolJson.Fail("file not found"));
        try
        {
            // UseShellExecute opens with the OS default program on Windows (ShellExecute)
            // and macOS (`open`) alike.
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(full) { UseShellExecute = true });
            if (kind == FileOpenPolicy.KindMedia) MediaTracker.Record(process, path);
            else process?.Dispose();
            AppLogger.Log($"[AIMODE] open_file {full} ({kind})");
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                ok = true, opened = true, path, kind,
                note = kind == FileOpenPolicy.KindMedia ? "now playing on the PC (stop_media stops it)" : "shown on the PC",
            }, ToolJson.Options));
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[AIMODE] open_file FAILED {full}: {ex.GetType().Name}: {ex.Message}");
            return Task.FromResult(ToolJson.Fail(ex.Message));
        }
    }

    public Task<string> StopMediaAsync(CancellationToken ct)
    {
        var was = MediaTracker.LastPath;
        var (stopped, how) = MediaTracker.Stop(OperatingSystem.IsWindows() ? SendMediaStopKeyWindows : null);
        AppLogger.Log($"[AIMODE] stop_media: {how} ({was ?? "-"})");
        return Task.FromResult(stopped
            ? JsonSerializer.Serialize(new { ok = true, stopped = true, path = was, detail = how }, ToolJson.Options)
            : ToolJson.Fail(how));
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

    private static bool SendMediaStopKeyWindows()
    {
        const byte VK_MEDIA_STOP = 0xB2;
        const uint KEYEVENTF_KEYUP = 0x0002;
        try
        {
            keybd_event(VK_MEDIA_STOP, 0, 0, IntPtr.Zero);
            keybd_event(VK_MEDIA_STOP, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
            return true;
        }
        catch { return false; }
    }

    // ── web (headless surface; the Browser page arrives with a later mission) ──

    public Task<string> WebSearchAsync(string query, int maxResults, CancellationToken ct) => Web.SearchAsync(query, maxResults, ct);
    public Task<string> WebOpenAsync(string url, int tab, CancellationToken ct) => Web.OpenAsync(url, tab, ct);
    public Task<string> WebReadAsync(int tab, string? mode, string? find, int maxChars, CancellationToken ct) => Web.ReadAsync(tab, mode, find, maxChars, ct);

    // ── MCP servers (Settings → MCP) ─────────────────────────────────────────

    public Task<string> CallDynamicToolAsync(string name, JsonObject args, CancellationToken ct)
    {
        AppLogger.Log($"[AIMODE] mcp {name} args={McpWire.Serialize(args)}");
        return McpHub.Shared.CallAsync(name, args, ct);
    }
}
