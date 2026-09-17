using Agent.Common.Llm.Tools;

namespace ZeroWearable.Agent;

/// <summary>
/// What the watch's agent is allowed to touch on this PC.
///
/// <see cref="IAgentToolbelt"/> is AgentZero's side-effect surface, and every method it
/// declares has a default that returns a "not available" envelope. That default is the
/// point here: the wearable host implements only the <b>file</b> surface — sandboxed by
/// <see cref="FileToolCore"/> to one configured root — and leaves terminal control, mouse,
/// keyboard and screenshots unimplemented, so a device across the room cannot drive the
/// machine. The terminal methods have no default (they predate the pattern), so they are
/// answered here with the same shape.
///
/// <para>File tools are default-deny too: with no <c>WorkspaceRoot</c> configured,
/// <see cref="FileToolCore"/> receives a null root and refuses. Nobody gets the whole disk
/// because a setting was left blank.</para>
/// </summary>
public sealed class WearableToolbelt : IAgentToolbelt
{
    private const string NoTerminals =
        """{"ok":false,"error":"the wearable host has no terminals — it answers the watch directly"}""";

    private readonly string? _root;
    private readonly Action<string, string> _log;

    /// <param name="workspaceRoot">Folder the file tools are confined to. Null/empty = no file tools.</param>
    public WearableToolbelt(string? workspaceRoot, Action<string, string> log)
    {
        _root = string.IsNullOrWhiteSpace(workspaceRoot) ? null : workspaceRoot;
        _log = log;
    }

    public string? Root => _root;

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

    // ── File surface: sandboxed to one root ──────────────────────────────────

    public Task<string> ReadFileAsync(string path, int maxBytes, CancellationToken ct)
        => Run("read_file", path, () => FileToolCore.ReadFile(_root, path, maxBytes));

    public Task<string> WriteFileAsync(string path, string content, CancellationToken ct)
        => Run("write_file", path, () => FileToolCore.WriteFile(_root, path, content));

    public Task<string> EditFileAsync(string path, string oldString, string newString, bool replaceAll,
        CancellationToken ct)
        => Run("edit_file", path, () => FileToolCore.Edit(_root, path, oldString, newString, replaceAll));

    public Task<string> GrepAsync(string pattern, string? pathFilter, int maxResults, CancellationToken ct)
        => Run("grep", pattern, () => FileToolCore.Grep(_root, pattern, pathFilter, maxResults));

    public Task<string> ListFilesAsync(string? pathFilter, int maxEntries, CancellationToken ct)
        => Run("list_files", pathFilter ?? ".", () => FileToolCore.ListFiles(_root, pathFilter, maxEntries));

    private Task<string> Run(string tool, string subject, Func<string> body)
    {
        if (_root is null)
        {
            return Task.FromResult(
                """{"ok":false,"error":"no workspace root configured for the wearable host"}""");
        }
        try
        {
            var result = body();
            _log("debug", $"tool {tool}({Head(subject, 60)}) -> {result.Length} chars");
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            // A failing tool is information the model can act on, not a failed request.
            _log("warn", $"tool {tool} threw: {ex.Message}");
            return Task.FromResult($$"""{"ok":false,"error":{{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}""");
        }
    }

    private static string Head(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
