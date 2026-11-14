using System.Text;
using System.Text.RegularExpressions;

namespace AgentOne.Agent;

/// <summary>
/// How earlier work like the current one looks in this workspace: the
/// current work's folder is one of a collection (songs/2026-11-07-x among
/// songs/2026-11-05-y, songs/2026-11-02-z …), and the newest siblings that got
/// further hold what the next steps produce — and the logs named after them say
/// how. Measured (2026-11-07, music-harness): asked to review generated takes,
/// both models answered from the plan document; the sibling song folders each
/// held out/*.mp3 and mv/judge-takes.py, and a log named "…-work-log-generated.md"
/// described the download, transcription and measuring step by step. A search on
/// the request's words ("검토", "추천") never reaches those ("측정", "판정").
///
/// Structure only, no guessing at meaning: folder shapes, file names, log names.
/// </summary>
public static partial class WorkspacePrecedent
{
    private const int MaxSiblings = 2, MaxLogs = 8, MaxNamesPerFolder = 8, MaxWalkFiles = 20_000;

    private static readonly HashSet<string> Media = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".mp3", ".wav", ".m4a", ".flac", ".ogg", ".mp4", ".mov", ".webm", ".npz"
    };

    /// <summary>
    /// The folder a written path's work lives in: the nearest ancestor whose
    /// parent holds three or more folders (a collection). Null for files at the
    /// root or outside any collection.
    /// </summary>
    public static string? WorkFolderOf(string root, string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var depth = parts.Length - 1; depth >= 1; depth--)
        {
            var folder = string.Join('/', parts.Take(depth));
            var parent = Path.Combine(root, string.Join('/', parts.Take(depth - 1)));
            try
            {
                if (depth >= 2 && Directory.Exists(parent) && Directory.GetDirectories(parent).Length >= 3)
                    return folder;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
        return null;
    }

    /// <summary>The newest siblings of <paramref name="workFolder"/> that got further, as material; empty when there are none.</summary>
    public static string Material(string root, string? workFolder)
    {
        if (workFolder is null) return "";
        var full = Path.Combine(root, workFolder);
        var parent = Path.GetDirectoryName(full);
        if (parent is null || !Directory.Exists(full)) return "";

        string[] siblings;
        try { siblings = Directory.GetDirectories(parent); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }

        var self = CountFiles(full);
        var dated = siblings.All(s => DatePrefix().IsMatch(Path.GetFileName(s)));
        var chosen = siblings
            .Where(s => !string.Equals(Path.GetFullPath(s), Path.GetFullPath(full), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => dated ? Path.GetFileName(s) : Directory.GetLastWriteTimeUtc(s).ToString("o"), StringComparer.Ordinal)
            .Where(s => CountFiles(s) > self)
            .Take(MaxSiblings)
            .ToList();
        if (chosen.Count == 0) return "";

        var sb = new StringBuilder($"[precedent] Earlier work like this one ({workFolder}) in the workspace, newest first — " +
                                   "what its next steps produced and the logs that say how. Reuse its scripts and steps:");
        foreach (var sibling in chosen)
        {
            var rel = Path.GetRelativePath(root, sibling).Replace('\\', '/');
            sb.Append("\n- ").Append(rel).Append("/: ").Append(Describe(sibling));
            var slug = DatePrefix().Replace(Path.GetFileName(sibling), "");
            var logs = slug.Length >= 3 ? NamedAfter(root, slug, sibling) : [];
            if (logs.Count > 0) sb.Append("\n  logs: ").Append(string.Join(", ", logs));
            foreach (var line in ScriptLines(root, sibling, logs))
                sb.Append("\n  how: ").Append(line);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The lines in a sibling's own notes and its logs that name a script — how
    /// its steps were run, in the words of whoever ran them. Measured (2026-11-07):
    /// the material named notes.md and the model never opened it; line 107 said
    /// "받기: dv-fetch.cjs out 4 --match …", line 108 "측정: mv/transcribe.py →
    /// mv/judge-takes.py". One line per script, first mention, the sibling's own
    /// notes before its logs.
    /// </summary>
    internal static List<string> ScriptLines(string root, string sibling, IReadOnlyList<string> logs)
    {
        var files = new List<string>();
        try { files.AddRange(Directory.GetFiles(sibling, "*.md").OrderBy(f => f, StringComparer.Ordinal)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* none */ }
        files.AddRange(logs.Select(l => Path.Combine(root, l)));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        foreach (var file in files)
        {
            string[] text;
            try { text = File.ReadAllLines(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var raw in text)
            {
                var named = ScriptName().Matches(raw).Select(m => Path.GetFileName(m.Value)).Where(n => !seen.Contains(n)).ToList();
                if (named.Count == 0) continue;
                foreach (var n in named) seen.Add(n);
                var line = raw.Trim().TrimStart('-', '*', ' ');
                lines.Add(line.Length > MaxHowChars ? line[..MaxHowChars] + "…" : line);
                if (lines.Count >= MaxHowLines) return lines;
            }
        }
        return lines;
    }

    private const int MaxHowLines = 6, MaxHowChars = 280;

    [GeneratedRegex(@"[\w./-]+\.(?:cjs|mjs|js|py|sh|ps1)\b")]
    private static partial Regex ScriptName();

    /// <summary>Top-level files by name, then each subfolder with its non-media files by name and its media by count.</summary>
    private static string Describe(string dir)
    {
        var parts = new List<string>();
        try
        {
            var files = Directory.GetFiles(dir).Select(Path.GetFileName).OfType<string>().OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (files.Count > 0) parts.Add(string.Join(", ", files.Take(MaxNamesPerFolder)) + (files.Count > MaxNamesPerFolder ? ", …" : ""));

            foreach (var sub in Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
            {
                var all = Directory.EnumerateFiles(sub, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
                    .Take(500).ToList();
                // Scripts first: they are how the step was done.
                var named = all.Where(f => !Media.Contains(Path.GetExtension(f)))
                    .Select(f => Path.GetRelativePath(sub, f).Replace('\\', '/'))
                    .OrderBy(n => Path.GetExtension(n) is ".py" or ".sh" or ".cjs" or ".js" or ".mjs" or ".ps1" or ".cs" ? 0 : 1)
                    .ThenBy(n => n, StringComparer.Ordinal).ToList();
                var media = all.Where(f => Media.Contains(Path.GetExtension(f)))
                    .GroupBy(f => Path.GetExtension(f).ToLowerInvariant()).Select(g => $"{g.Count()} {g.Key}").ToList();
                var mediaNames = all.Where(f => Path.GetExtension(f) is ".mp3" or ".wav" or ".mp4")
                    .Select(Path.GetFileName).OfType<string>().Take(6).ToList();

                var desc = new List<string>();
                if (named.Count > 0) desc.Add(string.Join(", ", named.Take(MaxNamesPerFolder)) + (named.Count > MaxNamesPerFolder ? ", …" : ""));
                if (mediaNames.Count > 0) desc.Add(string.Join(", ", mediaNames));
                else if (media.Count > 0) desc.Add(string.Join(", ", media));
                parts.Add($"{Path.GetFileName(sub)}/ ({string.Join("; ", desc)})");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* what was read stands */ }
        return string.Join(" · ", parts);
    }

    /// <summary>Text files elsewhere in the workspace whose name carries the sibling's slug — its logs, newest first.</summary>
    private static List<string> NamedAfter(string root, string slug, string sibling)
    {
        var found = new List<string>();
        var seen = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0 && seen < MaxWalkFiles)
        {
            var dir = pending.Pop();
            if (string.Equals(dir, sibling, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.md"))
                {
                    seen++;
                    if (Path.GetFileName(file).Contains(slug, StringComparison.OrdinalIgnoreCase))
                        found.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
                }
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    if (!WorkspaceProbe.IsExcluded(Path.GetFileName(sub))) pending.Push(sub);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* skip */ }
        }
        return found.OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).Take(MaxLogs).ToList();
    }

    private static int CountFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true })
                .Take(2_000).Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}-")]
    private static partial Regex DatePrefix();
}
