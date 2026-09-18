using System.Text.Json.Nodes;

namespace Agent.Common.Llm.Tools;

/// <summary>
/// The file tools over several allow-listed folders (M0032): every call resolves its
/// <c>alias/relative/path</c> through <see cref="AllowedRootResolver"/>, runs the existing
/// single-root <see cref="FileToolCore"/> against that folder, and rewrites the paths in the
/// envelope back into alias form so the model's next call can use them verbatim.
/// Writes are gated per root by <see cref="Agent.Common.Wearable.AllowedRoot.Writable"/>.
/// </summary>
public static class AllowedRootFileTools
{
    public static string ReadFile(AllowedRootResolver roots, string path, int maxBytes)
    {
        if (!roots.TryResolve(path, out var root, out var rel, out var error)) return ToolJson.Fail(error);
        return RewritePath(FileToolCore.ReadFile(root.Path, rel, maxBytes), root.Alias);
    }

    public static string WriteFile(AllowedRootResolver roots, string path, string content)
    {
        if (!roots.TryResolve(path, out var root, out var rel, out var error)) return ToolJson.Fail(error);
        if (!root.Writable) return ToolJson.Fail(ReadOnly(root.Alias));
        return RewritePath(FileToolCore.WriteFile(root.Path, rel, content), root.Alias);
    }

    public static string Edit(AllowedRootResolver roots, string path, string oldString, string newString, bool replaceAll)
    {
        if (!roots.TryResolve(path, out var root, out var rel, out var error)) return ToolJson.Fail(error);
        if (!root.Writable) return ToolJson.Fail(ReadOnly(root.Alias));
        return RewritePath(FileToolCore.Edit(root.Path, rel, oldString, newString, replaceAll), root.Alias);
    }

    /// <summary>With no <paramref name="pathFilter"/> every root is searched, sharing one result budget.</summary>
    public static string Grep(AllowedRootResolver roots, string pattern, string? pathFilter, int maxResults)
    {
        if (roots.IsEmpty) return ToolJson.Fail(AllowedRootResolver.NoRootsError);

        if (!string.IsNullOrWhiteSpace(pathFilter))
        {
            if (!roots.TryResolve(pathFilter, out var root, out var rel, out var error)) return ToolJson.Fail(error);
            return RewriteArray(FileToolCore.Grep(root.Path, pattern, rel == "." ? null : rel, maxResults),
                "matches", "file", root.Alias);
        }

        var all = new JsonArray();
        var truncated = false;
        var budget = Math.Max(1, maxResults);
        foreach (var root in roots.Roots)
        {
            if (budget <= 0)
            {
                truncated = true;
                break;
            }
            var json = FileToolCore.Grep(root.Path, pattern, null, budget);
            JsonObject node;
            try { node = JsonNode.Parse(json)!.AsObject(); }
            catch { return json; }
            if (node["ok"]?.GetValue<bool>() != true) return json;   // e.g. an invalid regex — same for every root

            if (node["matches"] is JsonArray matches)
            {
                foreach (var match in matches)
                {
                    if (match is not JsonObject m) continue;
                    var copy = m.DeepClone().AsObject();
                    copy["file"] = AllowedRootResolver.Prefix(root.Alias, m["file"]?.GetValue<string>());
                    all.Add(copy);
                    budget--;
                }
            }
            if (node["truncated"]?.GetValue<bool>() == true) truncated = true;
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["count"] = all.Count,
            ["truncated"] = truncated,
            ["matches"] = all,
        }.ToJsonString(ToolJson.Options);
    }

    /// <summary>Files each root shows in the no-path listing — enough to see what is there, not a walk.</summary>
    public const int PreviewFilesPerRoot = 30;

    /// <summary>
    /// Search every root by kind and name words; results carry alias paths and are
    /// merged best-score-first across roots (M0032 follow-up #2).
    /// </summary>
    public static string FindFiles(AllowedRootResolver roots, string? query, string? kind, int maxResults)
    {
        if (roots.IsEmpty) return ToolJson.Fail(AllowedRootResolver.NoRootsError);
        maxResults = Math.Clamp(maxResults, 1, 500);

        var all = new List<(int Score, JsonObject Entry)>();
        var total = 0;
        foreach (var root in roots.Roots)
        {
            var json = FileToolCore.FindFiles(root.Path, query, kind, maxResults);
            JsonObject node;
            try { node = JsonNode.Parse(json)!.AsObject(); }
            catch { continue; }
            if (node["ok"]?.GetValue<bool>() != true) continue;
            total += node["total"]?.GetValue<int>() ?? 0;
            if (node["entries"] is not JsonArray entries) continue;
            foreach (var item in entries)
            {
                if (item is not JsonObject e) continue;
                var copy = e.DeepClone().AsObject();
                copy["path"] = AllowedRootResolver.Prefix(root.Alias, e["path"]?.GetValue<string>());
                all.Add((e["score"]?.GetValue<int>() ?? 0, copy));
            }
        }

        var picked = new JsonArray();
        foreach (var hit in all.OrderByDescending(h => h.Score).ThenBy(h => h.Entry["path"]?.GetValue<string>(), StringComparer.OrdinalIgnoreCase).Take(maxResults))
            picked.Add(hit.Entry);

        return new JsonObject
        {
            ["ok"] = true,
            ["count"] = picked.Count,
            ["total"] = total,
            ["truncated"] = total > picked.Count,
            ["kind"] = FileToolCore.NormalizeKind(kind),
            ["query"] = query ?? "",
            ["entries"] = picked,
            ["hint"] = picked.Count == 0
                ? "nothing matched; try fewer words, another kind, or an empty query to list everything of that kind"
                : "open_file plays/opens a path; read_file reads a document",
        }.ToJsonString(ToolJson.Options);
    }

    /// <summary>
    /// With no <paramref name="pathFilter"/> the answer is the list of roots, each with a
    /// preview of its files — so a single-root setup shows its songs at once instead of
    /// making the model take a second step it tends not to take.
    /// </summary>
    public static string ListFiles(AllowedRootResolver roots, string? pathFilter, int maxEntries)
    {
        if (roots.IsEmpty) return ToolJson.Fail(AllowedRootResolver.NoRootsError);
        if (string.IsNullOrWhiteSpace(pathFilter)) return RootsWithPreview(roots);

        if (!roots.TryResolve(pathFilter, out var root, out var rel, out var error)) return ToolJson.Fail(error);
        return RewriteArray(FileToolCore.ListFiles(root.Path, rel == "." ? null : rel, maxEntries),
            "entries", "path", root.Alias);
    }

    private static string RootsWithPreview(AllowedRootResolver roots)
    {
        var list = new JsonArray();
        foreach (var root in roots.Roots)
        {
            var entry = new JsonObject { ["alias"] = root.Alias, ["writable"] = root.Writable };
            var files = new JsonArray();
            var more = false;
            try
            {
                var node = JsonNode.Parse(FileToolCore.ListFiles(root.Path, null, 400))!.AsObject();
                if (node["entries"] is JsonArray entries)
                {
                    foreach (var item in entries)
                    {
                        if (item is not JsonObject e || e["dir"]?.GetValue<bool>() == true) continue;
                        if (files.Count >= PreviewFilesPerRoot) { more = true; break; }
                        files.Add(AllowedRootResolver.Prefix(root.Alias, e["path"]?.GetValue<string>()));
                    }
                    if (node["truncated"]?.GetValue<bool>() == true) more = true;
                }
            }
            catch { }
            entry["files"] = files;
            entry["more"] = more;
            list.Add(entry);
        }
        return new JsonObject
        {
            ["ok"] = true,
            ["roots"] = list,
            ["hint"] = "address files as <alias>/relative/path; find_files searches all roots by kind and name",
        }.ToJsonString(ToolJson.Options);
    }

    private static string ReadOnly(string alias)
        => $"folder '{alias}' is read-only for this device (write access is granted per folder in the Wearable settings)";

    private static string RewritePath(string json, string alias)
    {
        try
        {
            var node = JsonNode.Parse(json)!.AsObject();
            if (node["path"] is JsonValue v)
                node["path"] = AllowedRootResolver.Prefix(alias, v.GetValue<string>());
            return node.ToJsonString(ToolJson.Options);
        }
        catch
        {
            return json;
        }
    }

    private static string RewriteArray(string json, string arrayName, string field, string alias)
    {
        try
        {
            var node = JsonNode.Parse(json)!.AsObject();
            if (node[arrayName] is JsonArray items)
            {
                foreach (var item in items)
                {
                    if (item is JsonObject o && o[field] is JsonValue v)
                        o[field] = AllowedRootResolver.Prefix(alias, v.GetValue<string>());
                }
            }
            return node.ToJsonString(ToolJson.Options);
        }
        catch
        {
            return json;
        }
    }
}
