using System.Collections.Generic;

namespace Agent.Common.Module;

/// <summary>
/// Pure (WPF-free) parser for unified <c>git diff</c> output into a structured
/// model (mission W3, orca-adoption). Lives in ZeroCommon so the parsing is
/// headlessly testable; the WPF-side <c>GitDiffService</c> shells out to the
/// <c>git</c> binary and feeds the raw text here.
///
/// Only the subset needed for a review UI is modeled: per-file hunks with
/// old/new line numbers so comments can anchor to a concrete line + side.
/// Binary files are flagged, not diffed.
/// </summary>
public static class GitDiffReader
{
    public enum LineKind { Context, Add, Delete }

    public sealed record DiffLine(LineKind Kind, int? OldLineNo, int? NewLineNo, string Text);

    public sealed record DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, string Header, IReadOnlyList<DiffLine> Lines);

    public sealed record DiffFile(string OldPath, string NewPath, bool IsBinary, bool IsNew, bool IsDeleted, IReadOnlyList<DiffHunk> Hunks);

    /// <summary>Parses a full unified diff (possibly multi-file) into files+hunks.</summary>
    public static IReadOnlyList<DiffFile> Parse(string unifiedDiff)
    {
        var files = new List<DiffFile>();
        if (string.IsNullOrEmpty(unifiedDiff)) return files;

        var lines = unifiedDiff.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            if (!lines[i].StartsWith("diff --git ", System.StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            // New file section.
            var (oldPath, newPath) = ParseDiffGitPaths(lines[i]);
            bool isBinary = false, isNew = false, isDeleted = false;
            i++;

            var hunks = new List<DiffHunk>();
            while (i < lines.Length && !lines[i].StartsWith("diff --git ", System.StringComparison.Ordinal))
            {
                var line = lines[i];
                if (line.StartsWith("new file mode", System.StringComparison.Ordinal)) { isNew = true; i++; continue; }
                if (line.StartsWith("deleted file mode", System.StringComparison.Ordinal)) { isDeleted = true; i++; continue; }
                if (line.StartsWith("Binary files", System.StringComparison.Ordinal)) { isBinary = true; i++; continue; }
                if (line.StartsWith("--- ", System.StringComparison.Ordinal)) { var p = StripPathPrefix(line[4..]); if (p is not null) oldPath = p; i++; continue; }
                if (line.StartsWith("+++ ", System.StringComparison.Ordinal)) { var p = StripPathPrefix(line[4..]); if (p is not null) newPath = p; i++; continue; }

                if (line.StartsWith("@@", System.StringComparison.Ordinal))
                {
                    var hunk = ParseHunk(lines, ref i);
                    if (hunk is not null) hunks.Add(hunk);
                    continue;
                }
                i++; // index line, mode line, etc.
            }

            files.Add(new DiffFile(oldPath, newPath, isBinary, isNew, isDeleted, hunks));
        }

        return files;
    }

    private static DiffHunk? ParseHunk(string[] lines, ref int i)
    {
        // @@ -oldStart,oldCount +newStart,newCount @@ optional section header
        var header = lines[i];
        if (!TryParseHunkHeader(header, out int oldStart, out int oldCount, out int newStart, out int newCount))
        {
            i++;
            return null;
        }
        i++;

        var body = new List<DiffLine>();
        int oldNo = oldStart, newNo = newStart;
        while (i < lines.Length
               && !lines[i].StartsWith("@@", System.StringComparison.Ordinal)
               && !lines[i].StartsWith("diff --git ", System.StringComparison.Ordinal))
        {
            var l = lines[i];
            if (l.StartsWith("\\", System.StringComparison.Ordinal)) { i++; continue; } // "\ No newline at end of file"
            // Every hunk-body line is prefixed (' ', '+', '-'); a zero-length
            // line is not part of the hunk (e.g. the trailing-newline artifact
            // or a blank separator), so the hunk ends here.
            if (l.Length == 0)
                break;

            char c = l[0];
            var text = l[1..];
            switch (c)
            {
                case '+':
                    body.Add(new DiffLine(LineKind.Add, null, newNo, text));
                    newNo++;
                    break;
                case '-':
                    body.Add(new DiffLine(LineKind.Delete, oldNo, null, text));
                    oldNo++;
                    break;
                case ' ':
                    body.Add(new DiffLine(LineKind.Context, oldNo, newNo, text));
                    oldNo++; newNo++;
                    break;
                default:
                    // Unknown prefix — stop this hunk to be safe.
                    return new DiffHunk(oldStart, oldCount, newStart, newCount, header, body);
            }
            i++;
        }

        return new DiffHunk(oldStart, oldCount, newStart, newCount, header, body);
    }

    private static bool TryParseHunkHeader(string header, out int oldStart, out int oldCount, out int newStart, out int newCount)
    {
        oldStart = oldCount = newStart = newCount = 0;
        // format: @@ -a,b +c,d @@ ...
        int at1 = header.IndexOf("@@", System.StringComparison.Ordinal);
        int at2 = header.IndexOf("@@", at1 + 2, System.StringComparison.Ordinal);
        if (at1 < 0 || at2 < 0) return false;
        var mid = header.Substring(at1 + 2, at2 - (at1 + 2)).Trim();
        var parts = mid.Split(' ');
        if (parts.Length < 2) return false;
        if (!parts[0].StartsWith("-", System.StringComparison.Ordinal)) return false;
        if (!parts[1].StartsWith("+", System.StringComparison.Ordinal)) return false;
        if (!TryParsePair(parts[0][1..], out oldStart, out oldCount)) return false;
        if (!TryParsePair(parts[1][1..], out newStart, out newCount)) return false;
        return true;
    }

    private static bool TryParsePair(string s, out int start, out int count)
    {
        start = 0; count = 1;
        var kv = s.Split(',');
        if (!int.TryParse(kv[0], out start)) return false;
        if (kv.Length > 1 && !int.TryParse(kv[1], out count)) count = 1;
        return true;
    }

    private static (string OldPath, string NewPath) ParseDiffGitPaths(string diffGitLine)
    {
        // diff --git a/foo/bar.cs b/foo/bar.cs
        var rest = diffGitLine.Substring("diff --git ".Length);
        // Split on " b/" — but paths may contain spaces; the common case is
        // "a/<path> b/<path>". Find the " b/" that starts the second path.
        int idx = rest.IndexOf(" b/", System.StringComparison.Ordinal);
        if (idx < 0)
            return (rest.Trim(), rest.Trim());
        var a = rest[..idx].Trim();
        var b = rest[(idx + 1)..].Trim();
        return (StripPathPrefix(a) ?? a, StripPathPrefix(b) ?? b);
    }

    private static string? StripPathPrefix(string p)
    {
        p = p.Trim();
        if (p == "/dev/null") return "/dev/null";
        if (p.StartsWith("a/", System.StringComparison.Ordinal) || p.StartsWith("b/", System.StringComparison.Ordinal))
            return p[2..];
        return p;
    }
}
