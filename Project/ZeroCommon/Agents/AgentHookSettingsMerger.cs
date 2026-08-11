using System.Text.Json.Nodes;

namespace Agent.Common.Agents;

/// <summary>
/// Pure (WPF-free) merge/unmerge of AgentZero's hook entries into a Claude Code
/// <c>settings.json</c> object graph (mission W1, orca-adoption). Lives in
/// ZeroCommon so the subtree logic is headlessly testable; the WPF-side
/// <c>AgentHookInstaller</c> handles discovery, atomic file writes, and backups.
///
/// Claude Code hook schema:
/// <code>
/// { "hooks": { "&lt;Event&gt;": [ { "matcher": "*", "hooks": [ { "type":"command", "command":"..." } ] } ] } }
/// </code>
/// Our entries are recognized (for idempotent reinstall + clean uninstall) by
/// the <see cref="Marker"/> substring inside the command string.
/// </summary>
public static class AgentHookSettingsMerger
{
    /// <summary>Substring that identifies an AgentZero-installed hook command.</summary>
    public const string Marker = "-cli agent-hook";

    /// <summary>Claude Code hook events AgentZero subscribes to for state reporting.</summary>
    public static readonly string[] DefaultEvents =
    {
        "UserPromptSubmit", "PreToolUse", "PostToolUse",
        "Notification", "Stop", "SessionStart", "SessionEnd",
    };

    /// <summary>
    /// Adds (or refreshes) AgentZero hook entries for each event, using
    /// <paramref name="commandFor"/> to build the per-event command string.
    /// Any pre-existing AgentZero entries (matched by <see cref="Marker"/>) are
    /// removed first so reinstall is idempotent. Foreign hook entries are left
    /// untouched. Returns true if the graph changed.
    /// </summary>
    public static bool AddHooks(JsonObject root, System.Func<string, string> commandFor, System.Collections.Generic.IReadOnlyList<string>? events = null)
    {
        events ??= DefaultEvents;
        // Start from a clean slate for our own entries, then add fresh ones.
        RemoveHooks(root);

        var hooks = GetOrCreateObject(root, "hooks");
        foreach (var ev in events)
        {
            var arr = GetOrCreateArray(hooks, ev);
            var entry = new JsonObject
            {
                ["matcher"] = "*",
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = commandFor(ev),
                }),
            };
            arr.Add(entry);
        }
        return true;
    }

    /// <summary>
    /// Removes every AgentZero-installed hook entry (matched by <see cref="Marker"/>)
    /// and prunes emptied event arrays / the <c>hooks</c> object. Foreign entries
    /// stay. Returns true if anything was removed.
    /// </summary>
    public static bool RemoveHooks(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return false;

        bool changed = false;
        foreach (var eventName in new System.Collections.Generic.List<string>(GetKeys(hooks)))
        {
            if (hooks[eventName] is not JsonArray arr) continue;
            for (int i = arr.Count - 1; i >= 0; i--)
            {
                if (EntryIsOurs(arr[i]))
                {
                    arr.RemoveAt(i);
                    changed = true;
                }
            }
            if (arr.Count == 0)
                hooks.Remove(eventName);
        }
        if (hooks.Count == 0)
            root.Remove("hooks");
        return changed;
    }

    /// <summary>True if the settings graph already carries an AgentZero hook entry.</summary>
    public static bool HasOurHooks(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return false;
        foreach (var kv in hooks)
            if (kv.Value is JsonArray arr)
                foreach (var e in arr)
                    if (EntryIsOurs(e))
                        return true;
        return false;
    }

    private static bool EntryIsOurs(JsonNode? entry)
    {
        if (entry is not JsonObject obj) return false;
        if (obj["hooks"] is not JsonArray inner) return false;
        foreach (var h in inner)
            if (h is JsonObject ho && ho["command"]?.GetValue<string>() is string cmd
                && cmd.Contains(Marker, System.StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static JsonObject GetOrCreateObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    private static JsonArray GetOrCreateArray(JsonObject parent, string key)
    {
        if (parent[key] is JsonArray existing) return existing;
        var created = new JsonArray();
        parent[key] = created;
        return created;
    }

    private static System.Collections.Generic.IEnumerable<string> GetKeys(JsonObject obj)
    {
        foreach (var kv in obj) yield return kv.Key;
    }
}
