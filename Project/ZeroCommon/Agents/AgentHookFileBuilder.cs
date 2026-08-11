using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Agent.Common.Agents;

/// <summary>
/// Builds/merges hook entries into an agent CLI's <c>hooks.json</c> for Codex and
/// Cursor (herdr-adoption H4). The hooks call back
/// <c>AgentZeroLite.exe -cli agent-hook --event &lt;E&gt; --state &lt;S&gt;</c> so those CLIs
/// report lifecycle state to AgentZero (extending the Claude-only W1 installer to
/// more CLIs). Pure JSON graph work; the WPF-side <c>AgentHookInstaller</c> does
/// the file IO. Shape: <c>{ "hooks": { "&lt;Event&gt;": [ { "command": "..." } ] } }</c>.
/// </summary>
public static class AgentHookFileBuilder
{
    /// <summary>Substring identifying an AgentZero-installed hook command.</summary>
    public const string Marker = "-cli agent-hook";

    /// <summary>Codex hook events → reported state (herdr codex integration).</summary>
    public static readonly (string Event, string State)[] CodexEvents =
    {
        ("SessionStart", "idle"),
        ("UserPromptSubmit", "working"),
        ("PreToolUse", "working"),
        ("PermissionRequest", "blocked"),
        ("Stop", "idle"),
    };

    /// <summary>Cursor hook events → reported state (herdr cursor integration).</summary>
    public static readonly (string Event, string State)[] CursorEvents =
    {
        ("sessionStart", "idle"),
        ("beforeSubmitPrompt", "working"),
        ("beforeShellExecution", "working"),
        ("stop", "idle"),
        ("sessionEnd", "idle"),
    };

    /// <summary>Adds AgentZero hook entries for the given events. Idempotent (removes ours first).</summary>
    public static string AddHooks(string? existingJson, string exePath, (string Event, string State)[] events)
    {
        var root = ParseObject(existingJson);
        RemoveOurHooks(root);

        var hooks = GetOrCreateObject(root, "hooks");
        foreach (var (ev, state) in events)
        {
            var arr = GetOrCreateArray(hooks, ev);
            arr.Add(new JsonObject
            {
                ["command"] = $"\"{exePath}\" -cli agent-hook --event {ev} --state {state} --no-wait",
            });
        }
        return root.ToJsonString(Indented);
    }

    /// <summary>Removes AgentZero hook entries (marker-matched), pruning empty arrays.</summary>
    public static (string Json, bool Changed) RemoveHooks(string? existingJson)
    {
        var root = ParseObject(existingJson);
        bool changed = RemoveOurHooks(root);
        return (root.ToJsonString(Indented), changed);
    }

    /// <summary>True if the graph already carries an AgentZero hook.</summary>
    public static bool HasOurHooks(string? existingJson)
    {
        var root = ParseObject(existingJson);
        if (root["hooks"] is not JsonObject hooks) return false;
        foreach (var kv in hooks)
            if (kv.Value is JsonArray arr)
                foreach (var e in arr)
                    if (IsOurs(e)) return true;
        return false;
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static readonly System.Text.Json.JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static JsonObject ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try { return JsonNode.Parse(json!) as JsonObject ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }

    private static bool RemoveOurHooks(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return false;
        bool changed = false;
        foreach (var ev in new List<string>(Keys(hooks)))
        {
            if (hooks[ev] is not JsonArray arr) continue;
            for (int i = arr.Count - 1; i >= 0; i--)
                if (IsOurs(arr[i])) { arr.RemoveAt(i); changed = true; }
            if (arr.Count == 0) hooks.Remove(ev);
        }
        if (hooks.Count == 0) root.Remove("hooks");
        return changed;
    }

    private static bool IsOurs(JsonNode? entry)
        => entry is JsonObject o
           && o["command"]?.GetValue<string>() is string cmd
           && cmd.Contains(Marker, StringComparison.OrdinalIgnoreCase);

    private static JsonObject GetOrCreateObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject o) return o;
        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    private static JsonArray GetOrCreateArray(JsonObject parent, string key)
    {
        if (parent[key] is JsonArray a) return a;
        var created = new JsonArray();
        parent[key] = created;
        return created;
    }

    private static IEnumerable<string> Keys(JsonObject o)
    {
        foreach (var kv in o) yield return kv.Key;
    }
}
