using System.Runtime.InteropServices;
using System.Text.Json;
using Akka.Actor;
using Agent.Common;
using Agent.Common.Actors;
using Agent.Common.Llm.Tools;
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
/// this host yet). The OS-control verbs keep the interface's "not available" defaults.
/// </summary>
internal sealed class WorkspaceToolHost : IAgentToolbelt
{
    private static readonly HeadlessWebToolSurface Web = new();
    private static readonly MediaPlaybackTracker MediaTracker = new();

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
            string text;
            if (lastN > 0)
            {
                var total = session!.OutputLength;
                var start = Math.Max(0, total - lastN);
                var len = total - start;
                text = len > 0 ? session.ReadOutput(start, len) : "";
            }
            else
            {
                text = session!.GetConsoleText();
            }
            text = ApprovalParser.StripAnsiCodes(text);
            return Task.FromResult($"{{\"ok\":true,\"group_index\":{group},\"tab_index\":{tab},\"length\":{text.Length},\"text\":\"{Agent.Common.Platform.CliIpcProtocol.Escape(text)}\"}}");
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

    /// <summary>The WPF host's handshake text, verbatim — the peer's <c>--from</c> contract depends on it.</summary>
    internal static string BuildFirstContactHeader(string peerName) =>
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
        return Task.FromResult(FileToolCore.WriteFile(root, path, content));
    }

    public Task<string> EditFileAsync(string path, string oldString, string newString, bool replaceAll, CancellationToken ct)
    {
        var root = Root();
        AppLogger.Log($"[AIMODE] edit_file root=\"{root ?? "<none>"}\" path=\"{path}\" replace_all={replaceAll}");
        return Task.FromResult(FileToolCore.Edit(root, path, oldString, newString, replaceAll));
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
}
