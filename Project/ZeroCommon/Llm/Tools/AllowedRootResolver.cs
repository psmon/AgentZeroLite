using System.Text.Json;
using Agent.Common.Wearable;

namespace Agent.Common.Llm.Tools;

/// <summary>
/// Maps the <c>alias/relative/path</c> form a model uses onto one of several allow-listed
/// folders (M0032). Pure and WPF-free so <c>ZeroCommon.Tests</c> can prove the boundary:
/// unknown aliases, absolute paths, <c>..</c> segments and jumps between roots are all
/// refused here, and <see cref="FileToolCore"/> re-checks the resolved path against the
/// root it is handed, so an escape has to defeat both.
///
/// <para>The real folder path is deliberately absent from anything returned to the model
/// (<see cref="ListRootsJson"/> lists aliases only): a device across the room learns what
/// it may read, not how this PC's disk is laid out.</para>
/// </summary>
public sealed class AllowedRootResolver
{
    private readonly List<AllowedRoot> _roots = new();

    public IReadOnlyList<AllowedRoot> Roots => _roots;

    public bool IsEmpty => _roots.Count == 0;

    public const string NoRootsError = "no allowed folders are configured on this host";

    public AllowedRootResolver(IEnumerable<AllowedRoot>? roots)
    {
        if (roots is null) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (root is null || string.IsNullOrWhiteSpace(root.Path)) continue;
            var alias = AllowedRoot.NormalizeAlias(root.Alias);
            if (alias.Length == 0 || !seen.Add(alias)) continue;
            string full;
            try { full = Path.GetFullPath(root.Path.Trim()); }
            catch { continue; }
            _roots.Add(new AllowedRoot { Alias = alias, Path = full, Writable = root.Writable });
        }
    }

    public AllowedRoot? Find(string alias)
        => _roots.FirstOrDefault(r => string.Equals(r.Alias, alias, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Splits <paramref name="aliasPath"/> into the root it names and the path inside it.
    /// <paramref name="relative"/> is <c>"."</c> when only the alias was given.
    /// </summary>
    public bool TryResolve(string? aliasPath, out AllowedRoot root, out string relative, out string error)
    {
        root = null!;
        relative = ".";
        error = "";

        if (IsEmpty)
        {
            error = NoRootsError;
            return false;
        }
        if (string.IsNullOrWhiteSpace(aliasPath))
        {
            error = "path must not be empty";
            return false;
        }

        var p = aliasPath.Trim().Replace('\\', '/');
        if (p.StartsWith('/') || p.Contains(':') || Path.IsPathRooted(p))
        {
            error = "absolute paths are not allowed; use <alias>/relative/path (list_files with no path shows the aliases)";
            return false;
        }

        var slash = p.IndexOf('/');
        var alias = slash < 0 ? p : p[..slash];
        var rest = slash < 0 ? "" : p[(slash + 1)..].Trim('/');

        var found = Find(alias);
        if (found is null)
        {
            error = $"unknown root alias '{alias}'; call list_files with no path to see the allowed roots";
            return false;
        }
        if (rest.Split('/').Any(segment => segment == ".."))
        {
            error = "path escapes the allowed folder";
            return false;
        }

        root = found;
        relative = rest.Length == 0 ? "." : rest;
        return true;
    }

    /// <summary>What the model sees when it lists with no path: aliases and write grants only.</summary>
    public string ListRootsJson() => JsonSerializer.Serialize(new
    {
        ok = true,
        roots = _roots.Select(r => new { alias = r.Alias, writable = r.Writable }).ToArray(),
        hint = "address files as <alias>/relative/path",
    }, ToolJson.Options);

    /// <summary>Turns a root-relative path back into the alias form the model can call with.</summary>
    public static string Prefix(string alias, string? relativePath)
    {
        var rel = (relativePath ?? "").Replace('\\', '/').TrimStart('/');
        return rel.Length == 0 || rel == "." ? alias : alias + "/" + rel;
    }
}
