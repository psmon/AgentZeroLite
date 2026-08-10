using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agent.Common.Agents;

/// <summary>
/// Pre-marks a workspace folder as "trusted" in each agent CLI's own trust
/// store (mission W2, orca-adoption) so the CLI's "Do you trust this folder?"
/// prompt never intercepts injected keystrokes. Ported from orca
/// <c>src/main/agent-trust-presets.ts</c> — the file locations/formats there are
/// a verified spec.
///
/// Pure string transforms (slug, TOML/JSON upserts) are separated from file IO
/// so they are headlessly testable; the IO methods take a <c>homeDir</c> so
/// tests can point at a temp directory instead of the real user profile.
///
/// SECURITY: writing these files auto-trusts the folder for the agent CLIs —
/// convenient but a real trust decision. Callers gate this behind explicit
/// user opt-in (never silent/auto).
/// </summary>
public static class TrustPresetWriter
{
    public sealed record TrustResult(string Agent, bool Ok, string Detail);

    /// <summary>Marks the workspace trusted for Cursor, Copilot, and Codex.</summary>
    public static IReadOnlyList<TrustResult> MarkAllTrusted(string workspacePath, string? homeDir = null)
    {
        homeDir ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var abs = Canonicalize(workspacePath);
        return new[]
        {
            Try("cursor",  () => MarkCursorTrusted(abs, homeDir)),
            Try("copilot", () => MarkCopilotTrusted(abs, homeDir)),
            Try("codex",   () => MarkCodexTrusted(abs, homeDir)),
        };
    }

    private static TrustResult Try(string agent, Func<string> action)
    {
        try { return new TrustResult(agent, true, action()); }
        catch (Exception ex) { return new TrustResult(agent, false, ex.Message); }
    }

    // ── Cursor: ~/.cursor/projects/<slug>/.workspace-trusted ────────────────

    /// <summary>
    /// Derives the Cursor project slug: strip leading separators, then replace
    /// path-illegal chars with '-' (Windows paths carry ':' etc.).
    /// </summary>
    public static string CursorSlug(string absPath)
    {
        var stripped = absPath.TrimStart('/', '\\');
        var sb = new System.Text.StringBuilder(stripped.Length);
        bool lastDash = false;
        foreach (var c in stripped)
        {
            if (c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                if (!lastDash) { sb.Append('-'); lastDash = true; }
            }
            else { sb.Append(c); lastDash = false; }
        }
        return sb.ToString();
    }

    /// <summary>Cursor trust file JSON payload.</summary>
    public static string BuildCursorPayload(string absPath, string trustedAtIso)
        => JsonSerializer.Serialize(new { trustedAt = trustedAtIso, workspacePath = absPath },
            new JsonSerializerOptions { WriteIndented = true }) + "\n";

    private static string MarkCursorTrusted(string abs, string homeDir)
    {
        var slug = CursorSlug(abs);
        var dir = Path.Combine(homeDir, ".cursor", "projects", slug);
        var file = Path.Combine(dir, ".workspace-trusted");
        if (File.Exists(file)) return "already trusted";
        Directory.CreateDirectory(dir);
        File.WriteAllText(file, BuildCursorPayload(abs, DateTime.UtcNow.ToString("o")));
        return file;
    }

    // ── Copilot: ~/.copilot/config.json → trustedFolders[] ──────────────────

    /// <summary>
    /// Adds <paramref name="absPath"/> to a Copilot config's <c>trustedFolders</c>
    /// array (creating it if absent). Returns the updated JSON and whether it
    /// changed. Foreign keys are preserved.
    /// </summary>
    public static (string Json, bool Changed) AddCopilotFolder(string existingJson, string absPath)
    {
        JsonObject obj = string.IsNullOrWhiteSpace(existingJson)
            ? new JsonObject()
            : JsonNode.Parse(existingJson) as JsonObject ?? new JsonObject();

        var arr = obj["trustedFolders"] as JsonArray ?? new JsonArray();
        foreach (var n in arr)
            if (string.Equals(n?.GetValue<string>(), absPath, PathComparison))
                return (obj.ToJsonString(Indented), false);

        arr.Add(absPath);
        obj["trustedFolders"] = arr;
        return (obj.ToJsonString(Indented), true);
    }

    private static string MarkCopilotTrusted(string abs, string homeDir)
    {
        var dir = Path.Combine(homeDir, ".copilot");
        var path = Path.Combine(dir, "config.json");
        var existing = File.Exists(path) ? File.ReadAllText(path) : "";
        var (json, changed) = AddCopilotFolder(existing, abs);
        if (!changed) return "already trusted";
        Directory.CreateDirectory(dir);
        AtomicWrite(path, json);
        return path;
    }

    // ── Codex: ~/.codex/config.toml → [projects."<path>"] trust_level ───────

    /// <summary>
    /// Upserts a Codex <c>[projects."&lt;path&gt;"]</c> section with
    /// <c>trust_level = "trusted"</c>. Idempotent: no-op if the exact section
    /// header already exists. Returns the updated TOML and whether it changed.
    /// </summary>
    public static (string Toml, bool Changed) UpsertCodexTrust(string existingToml, string absPath)
    {
        var header = $"[projects.\"{EscapeTomlKey(absPath)}\"]";
        var text = existingToml ?? "";
        if (text.Contains(header, StringComparison.Ordinal))
            return (text, false);

        var block = header + "\n" + "trust_level = \"trusted\"\n";
        var sep = text.Length == 0 ? "" : text.EndsWith("\n") ? "\n" : "\n\n";
        return (text + sep + block, true);
    }

    private static string MarkCodexTrusted(string abs, string homeDir)
    {
        var dir = Path.Combine(homeDir, ".codex");
        var path = Path.Combine(dir, "config.toml");
        var existing = File.Exists(path) ? File.ReadAllText(path) : "";
        var (toml, changed) = UpsertCodexTrust(existing, abs);
        if (!changed) return "already trusted";
        Directory.CreateDirectory(dir);
        AtomicWrite(path, toml);
        return path;
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string EscapeTomlKey(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Canonicalize(string p)
    {
        try { return Path.GetFullPath(p); } catch { return p; }
    }

    private static void AtomicWrite(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        try { File.Move(tmp, path, overwrite: true); }
        catch { File.Delete(tmp); throw; }
    }
}
