using Akka.Actor;
using Agent.Common.Llm.Tools;
using Agent.Common.Wearable.Actors;

namespace Agent.Common.Wearable;

/// <summary>
/// What the watch's agent is allowed to touch on this PC (M0032: the actor edition).
///
/// <see cref="IAgentToolbelt"/> is AgentZero's side-effect surface. This implementation is a
/// thin adapter: every file call is an <c>Ask</c> to <see cref="FileToolActor"/>, every web
/// call an <c>Ask</c> to <see cref="WebToolActor"/>, every desktop call (windows, screenshot,
/// mouse, keyboard, launch) an <c>Ask</c> to <see cref="OsToolActor"/> — which answers "turned
/// off" unless the Wearable page allows it — and the terminal methods are answered "not
/// available". The tool actors own the policy (allow-listed folders with the built-in
/// <c>home</c>, openable file types, GUI-or-headless browsing, which programs may start);
/// this class owns nothing but the routing.
/// </summary>
public sealed class WearableToolbelt : IAgentToolbelt
{
    private const string NoTerminals =
        """{"ok":false,"error":"the wearable host has no terminals — it answers the watch directly"}""";

    private static readonly TimeSpan FileTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WebTimeout = WebToolActor.RequestTimeout + TimeSpan.FromSeconds(5);

    private readonly IActorRef _files;
    private readonly IActorRef _web;
    private readonly IActorRef? _os;

    public WearableToolbelt(IActorRef files, IActorRef web, IActorRef? os = null)
    {
        _files = files;
        _web = web;
        _os = os;
    }

    public WearableToolbelt(WearableToolActors tools) : this(tools.Files, tools.Web, tools.Os) { }

    // ── Terminal surface: deliberately absent ────────────────────────────────
    // Returning a truthful envelope (rather than throwing) keeps the model in Mode 1,
    // which is what a watch wants: answer the question, don't go hunting for a CLI.

    public Task<string> ListTerminalsAsync(CancellationToken ct)
        => Task.FromResult("""{"groups":[]}""");

    public Task<string> ReadTerminalAsync(int group, int tab, int lastN, CancellationToken ct)
        => Task.FromResult(NoTerminals);

    public Task<bool> SendToTerminalAsync(int group, int tab, string text, CancellationToken ct)
        => Task.FromResult(false);

    public Task<bool> SendKeyAsync(int group, int tab, string key, CancellationToken ct)
        => Task.FromResult(false);

    // ── File surface: the FileToolActor decides ──────────────────────────────

    public Task<string> ReadFileAsync(string path, int maxBytes, CancellationToken ct)
        => AskFiles(new FileToolActor.Read(path, maxBytes), ct);

    public Task<string> WriteFileAsync(string path, string content, CancellationToken ct)
        => AskFiles(new FileToolActor.Write(path, content), ct);

    public Task<string> EditFileAsync(string path, string oldString, string newString, bool replaceAll, CancellationToken ct)
        => AskFiles(new FileToolActor.Edit(path, oldString, newString, replaceAll), ct);

    public Task<string> GrepAsync(string pattern, string? pathFilter, int maxResults, CancellationToken ct)
        => AskFiles(new FileToolActor.Grep(pattern, pathFilter, maxResults), ct);

    public Task<string> ListFilesAsync(string? pathFilter, int maxEntries, CancellationToken ct)
        => AskFiles(new FileToolActor.ListFiles(pathFilter, maxEntries), ct);

    public Task<string> FindFilesAsync(string? query, string? kind, int maxResults, CancellationToken ct)
        => AskFiles(new FileToolActor.Find(query, kind, maxResults), ct);

    public Task<string> OpenFileAsync(string path, CancellationToken ct)
        => AskFiles(new FileToolActor.Open(path), ct);

    public Task<string> StopMediaAsync(CancellationToken ct)
        => AskFiles(new FileToolActor.StopMedia(), ct);

    public Task<string> DeleteFileAsync(string path, CancellationToken ct)
        => AskFiles(new FileToolActor.Delete(path), ct);

    public Task<string> NoteSaveAsync(string title, string text, bool append, CancellationToken ct)
        => AskFiles(new FileToolActor.NoteSave(title, text, append), ct);

    public Task<string> NoteReadAsync(string? title, CancellationToken ct)
        => AskFiles(new FileToolActor.NoteRead(title), ct);

    // ── Desktop surface: the OsToolActor decides ─────────────────────────────

    public Task<string> OsListWindowsAsync(string? titleFilter, CancellationToken ct)
        => AskOs(new OsToolActor.ListWindows(titleFilter), ct);

    public Task<string> OsScreenshotAsync(long hwnd, bool grayscale, CancellationToken ct)
        => AskOs(new OsToolActor.Screenshot(hwnd, grayscale), ct);

    public Task<string> OsActivateAsync(long hwnd, CancellationToken ct)
        => AskOs(new OsToolActor.Activate(hwnd), ct);

    public Task<string> OsMouseClickAsync(int x, int y, bool right, bool dbl, CancellationToken ct)
        => AskOs(new OsToolActor.Click(x, y, right, dbl), ct);

    public Task<string> OsKeyPressAsync(string keySpec, CancellationToken ct)
        => AskOs(new OsToolActor.Key(keySpec), ct);

    public Task<string> OsTypeTextAsync(string text, CancellationToken ct)
        => AskOs(new OsToolActor.TypeText(text), ct);

    public Task<string> OsLaunchAsync(string program, string? file, CancellationToken ct)
        => AskOs(new OsToolActor.Launch(program, file), ct);

    // os_element_tree stays on the interface default: UI Automation is not ported yet.

    // ── Web surface: the WebToolActor decides ────────────────────────────────

    public Task<string> WebSearchAsync(string query, int maxResults, CancellationToken ct)
        => AskWeb(new WebToolActor.Search(query, maxResults), ct);

    public Task<string> WebOpenAsync(string url, int tab, CancellationToken ct)
        => AskWeb(new WebToolActor.Open(url, tab), ct);

    public Task<string> WebReadAsync(int tab, string? mode, string? find, int maxChars, CancellationToken ct)
        => AskWeb(new WebToolActor.Read(tab, mode, find, maxChars), ct);

    private Task<string> AskFiles(object message, CancellationToken ct) => Ask(_files, message, FileTimeout, ct);
    private Task<string> AskWeb(object message, CancellationToken ct) => Ask(_web, message, WebTimeout, ct);
    private Task<string> AskOs(object message, CancellationToken ct) => _os is null
        ? Task.FromResult(ToolJson.Fail("os tools are not wired in this host"))
        : Ask(_os, message, FileTimeout, ct);

    private static async Task<string> Ask(IActorRef target, object message, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            return await target.Ask<string>(message, timeout, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolJson.Fail($"tool actor did not answer: {ex.Message}");
        }
    }
}
