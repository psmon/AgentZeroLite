using System.Text.RegularExpressions;

namespace AgentOne.Tools;

/// <summary>
/// Finds the absolute paths a person names in a request. Naming a folder
/// outside the workspace is taken as permission to <em>read</em> it for the
/// rest of the session — the "temporary approval" — and never as permission to
/// change it: the workspace root is the only place anything is ever written.
/// </summary>
public static partial class PathGrants
{
    /// <summary>
    /// Existing directories named in <paramref name="text"/> that lie outside
    /// <paramref name="root"/>. A file path grants its directory. Nothing under
    /// the root is returned: that needs no grant.
    /// </summary>
    public static IReadOnlyList<string> FindOutside(string text, string root)
    {
        var found = new List<string>();
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (Match match in AbsolutePath().Matches(text))
        {
            var raw = match.Value.TrimEnd('.', ',', ';', ':', ')', ']', '"', '\'');
            if (raw.StartsWith('~')) raw = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + raw[1..];

            string full;
            try { full = Path.GetFullPath(raw); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }

            string dir;
            if (Directory.Exists(full)) dir = full;
            else if (File.Exists(full)) dir = Path.GetDirectoryName(full) ?? full;
            else continue;

            dir = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // A drive or the filesystem root is not a grant: "C:\" or "/" would
            // open the whole machine to reading on the strength of a mention.
            var top = (Path.GetPathRoot(full) ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (dir.Length == 0 || string.Equals(dir, top, StringComparison.OrdinalIgnoreCase)) continue;
            if (IsUnder(dir, rootFull)) continue;
            if (!found.Contains(dir, PathComparer)) found.Add(dir);
        }

        return found;
    }

    public static bool IsUnder(string candidate, string root)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(candidate, root, comparison)) return true;
        return candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison)
               || candidate.StartsWith(root + Path.AltDirectorySeparatorChar, comparison);
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // C:\x\y  ·  C:/x/y  ·  /x/y  ·  ~/x — up to the next whitespace or quote.
    [GeneratedRegex(@"(?<![\w.])(?:[A-Za-z]:[\\/][^\s""'<>|]*|~[\\/][^\s""'<>|]*|/(?:[^\s""'<>|/]+/?)+)")]
    private static partial Regex AbsolutePath();
}
