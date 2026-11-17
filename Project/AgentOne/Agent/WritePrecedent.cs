namespace AgentOne.Agent;

/// <summary>
/// What the workspace already holds that a new file should be modelled on — asked
/// once, just before <c>write_file</c> creates it.
///
/// Measured (2026-11-16, music-harness): "곡 만들어줘" ended with a
/// <c>songs/&lt;date&gt;-quiet-assistant/guide.md</c> in a format of the model's own —
/// headings and a bracketed tag list — beside sixty earlier <c>guide.md</c> files
/// that all carry front matter and three fenced blocks, which is what the
/// workspace's generation script parses. It had read the procedure and never one
/// earlier output. The same turn wrote <c>knowledge/genres/city-pop.md</c> at the
/// root, when the genre documents live in <c>harness/knowledge/genres/</c>.
///
/// Both are answered from the file tree alone, so there is no model call and no
/// judgement here: a new file whose name its sibling folders already use, or
/// whose folder does not exist at the root but does further down, is sent back
/// once with what was found. Writing the same path again goes through — the
/// model may know better, and a refusal that cannot be overruled is a dead end.
/// </summary>
public static class WritePrecedent
{
    /// <summary>Sibling folders (or same-folder peers) needed before the tree counts as a convention.</summary>
    private const int MinPeers = 2;

    private const int MaxListed = 400;

    /// <summary>
    /// A note to send back instead of writing, or null to write.
    /// </summary>
    /// <param name="relativePath">The path as the model gave it.</param>
    /// <param name="read">Workspace-relative paths (forward slashes) read so far in this session.</param>
    /// <param name="wrote">Workspace-relative paths written so far in this session — its own files are nobody's precedent.</param>
    public static string? Check(string root, string relativePath, IReadOnlyCollection<string> read, IReadOnlyCollection<string>? wrote = null)
    {
        wrote ??= [];
        try
        {
            var rel = relativePath.Replace('\\', '/').Trim().TrimStart('.', '/');
            if (rel.Length == 0 || Path.IsPathRooted(relativePath) || rel.Contains("..")) return null;

            var full = Path.GetFullPath(Path.Combine(root, rel));
            if (File.Exists(full)) return null; // an update of a file that is there is nobody's precedent question

            return Misplaced(root, rel) ?? Unmodelled(root, rel, full, read, wrote);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The path opens a folder the root does not have, while exactly one folder
    /// further down ends in the same folder path.
    /// </summary>
    private static string? Misplaced(string root, string rel)
    {
        var parts = rel.Split('/');
        if (parts.Length < 2 || Directory.Exists(Path.Combine(root, parts[0]))) return null;

        var folder = string.Join('/', parts[..^1]);
        var matches = new List<string>();
        foreach (var top in SafeDirectories(root))
        {
            if (LocalNoise(Path.GetFileName(top))) continue;
            Collect(top, depth: 1);
        }
        if (matches.Count != 1) return null;

        var found = Path.GetRelativePath(root, matches[0]).Replace('\\', '/');
        return $"Not written yet: there is no '{parts[0]}/' folder at the workspace root, but '{found}/' exists " +
               $"({SafeFiles(matches[0]).Count()} files). If this file belongs with those, write it as " +
               $"'{found}/{parts[^1]}'. If a new top-level folder really is intended, call write_file with the same path again.";

        void Collect(string dir, int depth)
        {
            if (matches.Count > 1) return;
            var here = Path.GetRelativePath(root, dir).Replace('\\', '/');
            if (here.EndsWith("/" + folder, StringComparison.OrdinalIgnoreCase)) matches.Add(dir);
            if (depth >= 3) return;
            foreach (var sub in SafeDirectories(dir))
                if (!LocalNoise(Path.GetFileName(sub))) Collect(sub, depth + 1);
        }
    }

    /// <summary>
    /// The file is new, files like it exist — the same name in sibling folders, or the
    /// same extension in its own folder — and none of them has been read.
    /// </summary>
    private static string? Unmodelled(string root, string rel, string full, IReadOnlyCollection<string> read, IReadOnlyCollection<string> wrote)
    {
        var name = Path.GetFileName(full);
        var folder = Path.GetDirectoryName(full);
        if (folder is null) return null;

        var peers = new List<string>();

        // songs/<new>/guide.md beside songs/<earlier>/guide.md
        var parent = Path.GetDirectoryName(folder);
        if (parent is not null && !SamePath(folder, root) && Directory.Exists(parent))
        {
            foreach (var sibling in SafeDirectories(parent).Take(MaxListed))
            {
                if (SamePath(sibling, folder)) continue;
                var candidate = Path.Combine(sibling, name);
                if (File.Exists(candidate)) peers.Add(candidate);
            }
        }

        // harness/knowledge/genres/<new>.md beside the other genre documents
        var how = "the same name in sibling folders";
        // ...unless this session has been writing in that folder: measured, the rule held
        // lyricist_final.md back for the three files the same turn had just put beside it.
        var ownFolder = wrote.Any(w => SamePath(Path.GetDirectoryName(Path.GetFullPath(Path.Combine(root, w))) ?? "", folder));
        if (peers.Count < MinPeers && Directory.Exists(folder) && !SamePath(folder, root) && !ownFolder)
        {
            var ext = Path.GetExtension(name);
            var same = ext.Length == 0 ? [] : SafeFiles(folder).Take(MaxListed)
                .Where(f => string.Equals(Path.GetExtension(f), ext, StringComparison.OrdinalIgnoreCase)
                            && !Path.GetFileName(f).StartsWith("README", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (same.Count >= MinPeers + 1) { peers = same; how = "the same kind of file in this folder"; }
        }

        if (peers.Count < MinPeers) return null;

        var relPeers = peers.Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).ToList();
        if (relPeers.Any(p => read.Contains(p, StringComparer.OrdinalIgnoreCase))) return null;

        // Newest first: by write time, and by name when a fresh checkout gave them all the same time
        // (dated folder names then put the latest work on top).
        var newest = peers
            .OrderByDescending(p => File.GetLastWriteTimeUtc(p).Ticks / TimeSpan.TicksPerMinute)
            .ThenByDescending(p => Path.GetRelativePath(root, p), StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .ToList();

        return $"Not written yet: '{rel}' would be new, and this workspace already has {peers.Count} of {how} — " +
               $"newest: {string.Join(", ", newest)}. None of them has been read in this session. read_file the newest one " +
               "first and keep its structure exactly — front matter keys, headings, fenced blocks and their order — " +
               "because the workspace's scripts and the next steps read these files by that structure. Then call write_file again. " +
               "(Calling write_file with the same path again without reading writes it as it is.)";
    }

    /// <summary>The newest file of the same name in a sibling folder, relative to the root, or null.</summary>
    public static string? NewestSibling(string root, string relativePath)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(root, relativePath));
            var folder = Path.GetDirectoryName(full);
            var parent = folder is null ? null : Path.GetDirectoryName(folder);
            if (folder is null || parent is null || SamePath(folder, root)) return null;

            return SafeDirectories(parent).Take(MaxListed)
                .Where(d => !SamePath(d, folder))
                .Select(d => Path.Combine(d, Path.GetFileName(full)))
                .Where(File.Exists)
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f).Ticks / TimeSpan.TicksPerMinute)
                .ThenByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f => Slash(Path.GetRelativePath(root, f)))
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>
    /// A note for a command that failed on a file this session wrote, or null.
    ///
    /// Measured (2026-11-16, music-harness): the workspace's generation script refused the
    /// session's own guide.md — "guide.md needs three text code blocks (prompt / lyrics /
    /// style)". The model reran the script with another option, got the same line, and
    /// spent ten minutes of reasoning each time without touching the file. The message was
    /// about the file; the file was its own; sixty accepted examples lay beside it.
    /// </summary>
    /// <param name="wrote">Workspace-relative paths written in this session.</param>
    public static string? BlameNote(string root, string command, string output, IReadOnlyCollection<string> wrote)
    {
        foreach (var path in wrote)
        {
            var name = Path.GetFileName(path);
            if (name.Length < 4) continue;
            var inCommand = command.Replace(BS_CHAR, '/').Contains(path, StringComparison.OrdinalIgnoreCase);
            var inOutput = output.Contains(name, StringComparison.OrdinalIgnoreCase);
            if (!inCommand || !inOutput) continue;

            var sibling = NewestSibling(root, path);
            return $"[note] The command did not fail by itself: it rejected {path}, a file YOU wrote in this session, and its " +
                   "message says what that file must contain. Running the command again — with other options or not — fails " +
                   "the same way. Fix the file: " +
                   (sibling is null
                       ? $"read the script's header to see the format it parses, then rewrite {path} with write_file."
                       : $"read_file {sibling} (the same kind of file, one this command has accepted), copy its structure " +
                         $"exactly — front matter, headings, fenced blocks with the same info string, in the same order — and " +
                         $"rewrite {path} with write_file with this work's own content.") +
                   " Then run the command again.";
        }
        return null;
    }

    private const char BS_CHAR = '\\';

    /// <summary>What a work folder lacks that the folders beside it have.</summary>
    /// <param name="Folder">The folder this session wrote into, relative to the root.</param>
    /// <param name="Missing">File names most of its siblings hold and it does not.</param>
    /// <param name="Example">The newest sibling that holds them, relative to the root.</param>
    /// <param name="Siblings">How many folders it was compared with.</param>
    public sealed record Gap(string Folder, IReadOnlyList<string> Missing, string Example, int Siblings);

    /// <summary>Share of the sibling folders that must hold a file name before it counts as part of the set.</summary>
    private const double SetShare = 0.6;

    /// <summary>
    /// A folder this session wrote into that is short of what its sibling folders hold, or null.
    ///
    /// Measured (2026-11-16, music-harness): "곡 만들어줘" wrote notes.md, a genre note and two
    /// lyric drafts into songs/&lt;date&gt;-quiet-assistant/ and ended the turn calling the planning
    /// stage complete — next step, "call the lyricist". Every one of the sixty folders beside it
    /// holds guide.md, lyrics.md and notes.md. What a finished piece of this work consists of was
    /// on disk the whole time; nobody had to judge the answer's words to see it was not there.
    /// </summary>
    public static Gap? Unfinished(string root, IReadOnlyCollection<string> wrote)
    {
        try
        {
            var folders = wrote
                .Select(w => Path.GetDirectoryName(Path.GetFullPath(Path.Combine(root, w))))
                .Where(f => f is not null && !SamePath(f, root) && Directory.Exists(f))
                .Select(f => f!)
                .GroupBy(f => f, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key);

            foreach (var folder in folders)
            {
                var parent = Path.GetDirectoryName(folder);
                if (parent is null) continue;

                var siblings = SafeDirectories(parent).Take(MaxListed)
                    .Where(d => !SamePath(d, folder) && !LocalNoise(Path.GetFileName(d)))
                    .ToList();
                if (siblings.Count < MinPeers + 1) continue;

                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var sibling in siblings)
                    foreach (var file in SafeFiles(sibling).Take(MaxListed))
                        counts[Path.GetFileName(file)] = counts.GetValueOrDefault(Path.GetFileName(file)) + 1;

                var missing = counts
                    .Where(c => c.Value >= Math.Max(MinPeers + 1, (int)Math.Ceiling(siblings.Count * SetShare)))
                    .Select(c => c.Key)
                    .Where(name => !File.Exists(Path.Combine(folder, name)))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (missing.Count == 0) continue;

                var example = siblings
                    .Where(s => missing.All(m => File.Exists(Path.Combine(s, m))))
                    .OrderByDescending(s => Directory.GetLastWriteTimeUtc(s).Ticks / TimeSpan.TicksPerMinute)
                    .ThenByDescending(s => Path.GetFileName(s), StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (example is null) continue;

                return new Gap(Slash(Path.GetRelativePath(root, folder)), missing, Slash(Path.GetRelativePath(root, example)), siblings.Count);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }

        return null;
    }

    private static string Slash(string path) => path.Replace(Path.DirectorySeparatorChar, '/');

    private static bool LocalNoise(string name) =>
        name.StartsWith('.') || name is "node_modules" or "bin" or "obj" or "Library" or "Temp" or "__pycache__";

    private static IEnumerable<string> SafeDirectories(string dir)
    {
        try { return Directory.EnumerateDirectories(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static bool SamePath(string a, string b) => Services.GitWorkspace.SamePath(a, b);
}
