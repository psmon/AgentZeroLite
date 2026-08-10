using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agent.Common.Llm.Tools;

/// <summary>
/// Pure (WPF/Win32-free) implementation of the agent's file tools —
/// <c>read_file</c>, <c>write_file</c>, <c>edit_file</c>, <c>grep</c> (mission
/// W8, orca-adoption). Lives in ZeroCommon so it is headlessly testable
/// (<c>ZeroCommon.Tests</c>) and reused verbatim by the WPF host
/// (<see cref="IAgentToolbelt"/> impl <c>WorkspaceTerminalToolHost</c>).
///
/// SANDBOX: every operation is scoped to a <paramref name="root"/> directory
/// (the active workspace folder). A null/empty root ⇒ "no workspace" envelope,
/// which is the default-deny gate: the on-device model cannot touch the disk
/// until a workspace root is bound. Any resolved path that escapes the root
/// (via <c>..</c> or an absolute path outside it) is rejected. This is the
/// primary safety boundary; write/edit callers may layer an approval gate on
/// top at the host level.
///
/// Every method returns a compact JSON envelope <c>{"ok":bool, ...}</c> that
/// the agent loop forwards to the model verbatim (same contract as the OS
/// tools). Serialization goes through <see cref="JsonSerializer"/> so string
/// escaping is always correct.
/// </summary>
public static class FileToolCore
{
    /// <summary>Cap on bytes returned by <see cref="ReadFile"/> (defensive default).</summary>
    public const int DefaultMaxReadBytes = 200_000;

    /// <summary>Directory names skipped by <see cref="Grep"/> (noise / VCS / build output).</summary>
    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".idea", "packages", "dist", "out",
    };

    private static readonly StringComparison PathCmp =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Reads a UTF-8 text file within the workspace root.</summary>
    public static string ReadFile(string? root, string path, int maxBytes = DefaultMaxReadBytes)
    {
        if (!TryResolve(root, path, out var full, out var err))
            return Envelope(false, error: err);
        try
        {
            if (!File.Exists(full))
                return Envelope(false, error: "file not found");
            var bytes = File.ReadAllBytes(full);
            if (LooksBinary(bytes))
                return Envelope(false, error: "file appears to be binary");
            bool truncated = bytes.Length > maxBytes;
            var slice = truncated ? bytes.AsSpan(0, maxBytes).ToArray() : bytes;
            var text = Encoding.UTF8.GetString(slice);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                path = Rel(root, full),
                bytes = bytes.Length,
                truncated,
                text,
            });
        }
        catch (Exception ex) { return Envelope(false, error: ex.Message); }
    }

    /// <summary>Creates or overwrites a text file within the workspace root.</summary>
    public static string WriteFile(string? root, string path, string content)
    {
        if (!TryResolve(root, path, out var full, out var err))
            return Envelope(false, error: err);
        try
        {
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var existed = File.Exists(full);
            File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return JsonSerializer.Serialize(new
            {
                ok = true,
                path = Rel(root, full),
                created = !existed,
                bytes = Encoding.UTF8.GetByteCount(content),
            });
        }
        catch (Exception ex) { return Envelope(false, error: ex.Message); }
    }

    /// <summary>
    /// Replaces <paramref name="oldString"/> with <paramref name="newString"/> in
    /// a file. Default replaces a single, unique occurrence (fails if the target
    /// is absent or ambiguous); <paramref name="replaceAll"/> replaces every match.
    /// </summary>
    public static string Edit(string? root, string path, string oldString, string newString, bool replaceAll = false)
    {
        if (!TryResolve(root, path, out var full, out var err))
            return Envelope(false, error: err);
        if (string.IsNullOrEmpty(oldString))
            return Envelope(false, error: "old string must not be empty");
        try
        {
            if (!File.Exists(full))
                return Envelope(false, error: "file not found");
            var original = File.ReadAllText(full);
            int count = CountOccurrences(original, oldString);
            if (count == 0)
                return Envelope(false, error: "old string not found");
            if (count > 1 && !replaceAll)
                return Envelope(false, error: $"old string is ambiguous ({count} occurrences); pass replace_all or add more context");

            string updated = replaceAll
                ? original.Replace(oldString, newString)
                : ReplaceFirst(original, oldString, newString);
            File.WriteAllText(full, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return JsonSerializer.Serialize(new
            {
                ok = true,
                path = Rel(root, full),
                replaced = replaceAll ? count : 1,
            });
        }
        catch (Exception ex) { return Envelope(false, error: ex.Message); }
    }

    /// <summary>
    /// Regex search across text files under the workspace root (optionally scoped
    /// to a <paramref name="pathFilter"/> subdirectory or file). Returns up to
    /// <paramref name="maxResults"/> matches as file/line/text triples.
    /// </summary>
    public static string Grep(string? root, string pattern, string? pathFilter = null, int maxResults = 200)
    {
        if (string.IsNullOrEmpty(root))
            return Envelope(false, error: "no workspace root bound");
        if (string.IsNullOrEmpty(pattern))
            return Envelope(false, error: "pattern must not be empty");

        Regex rx;
        try { rx = new Regex(pattern, RegexOptions.Compiled); }
        catch (Exception ex) { return Envelope(false, error: $"invalid regex: {ex.Message}"); }

        string searchRoot;
        if (string.IsNullOrEmpty(pathFilter))
            searchRoot = Path.GetFullPath(root);
        else if (!TryResolve(root, pathFilter, out searchRoot!, out var perr))
            return Envelope(false, error: perr);

        var matches = new List<object>();
        bool truncated = false;
        try
        {
            foreach (var file in EnumerateTextFiles(searchRoot))
            {
                if (matches.Count >= maxResults) { truncated = true; break; }
                int lineNo = 0;
                foreach (var line in File.ReadLines(file))
                {
                    lineNo++;
                    if (rx.IsMatch(line))
                    {
                        matches.Add(new { file = Rel(root, file), line = lineNo, text = Trim(line, 300) });
                        if (matches.Count >= maxResults) { truncated = true; break; }
                    }
                }
            }
        }
        catch (Exception ex) { return Envelope(false, error: ex.Message); }

        return JsonSerializer.Serialize(new { ok = true, count = matches.Count, truncated, matches });
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Resolves <paramref name="path"/> against <paramref name="root"/> and
    /// verifies the result stays inside the root. Returns false with an error
    /// envelope reason when the root is unbound or the path escapes.
    /// </summary>
    private static bool TryResolve(string? root, string path, out string full, out string error)
    {
        full = "";
        error = "";
        if (string.IsNullOrEmpty(root))
        {
            error = "no workspace root bound";
            return false;
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "path must not be empty";
            return false;
        }
        try
        {
            var rootFull = Path.GetFullPath(root);
            var combined = Path.IsPathRooted(path) ? path : Path.Combine(rootFull, path);
            full = Path.GetFullPath(combined);
            var rootWithSep = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (string.Equals(full, rootFull, PathCmp) || full.StartsWith(rootWithSep, PathCmp))
                return true;
            error = "path escapes workspace root";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static IEnumerable<string> EnumerateTextFiles(string start)
    {
        // Manual recursive walk so we can prune ignored directories cheaply.
        var stack = new Stack<string>();
        if (Directory.Exists(start)) stack.Push(start);
        else if (File.Exists(start)) { yield return start; yield break; }

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] subDirs;
            try { subDirs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var sub in subDirs)
            {
                var name = Path.GetFileName(sub);
                if (!IgnoredDirs.Contains(name))
                    stack.Push(sub);
            }
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { continue; }
            foreach (var f in files)
                yield return f;
        }
    }

    private static bool LooksBinary(byte[] bytes)
    {
        int scan = Math.Min(bytes.Length, 8000);
        for (int i = 0; i < scan; i++)
            if (bytes[i] == 0) return true;
        return false;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    private static string ReplaceFirst(string haystack, string needle, string replacement)
    {
        int idx = haystack.IndexOf(needle, StringComparison.Ordinal);
        return idx < 0 ? haystack : haystack[..idx] + replacement + haystack[(idx + needle.Length)..];
    }

    private static string Rel(string? root, string full)
    {
        if (string.IsNullOrEmpty(root)) return full;
        try { return Path.GetRelativePath(Path.GetFullPath(root), full).Replace('\\', '/'); }
        catch { return full; }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string Envelope(bool ok, string error)
        => JsonSerializer.Serialize(new { ok, error });
}
