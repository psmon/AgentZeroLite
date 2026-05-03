using System.IO;
using System.Net.Http;
using System.Text.Json;
using Agent.Common;

namespace AgentZeroWpf.Services.Browser;

/// <summary>
/// One entry in the "Official plugins" list shown at the top of the install
/// dialog. Discovered at runtime from <c>psmon/AgentZeroLite</c>'s
/// <c>Project/Plugins/</c> directory via the GitHub Trees + raw content
/// APIs — never hardcoded — so newly-shipped plugins appear immediately
/// without an app rebuild.
/// </summary>
public sealed record OfficialPluginEntry(
    string Id,
    string Name,
    string? Version,
    string? Description,
    string? Icon,
    string GitFolderUrl);

/// <summary>
/// Fetches the official-plugin catalogue by walking
/// <c>github.com/psmon/AgentZeroLite/tree/main/Project/Plugins</c>:
///   1. <c>git/refs/heads/main</c> → branch SHA
///   2. <c>git/trees/{sha}?recursive=1</c> → every blob path
///   3. for each top-level dir under <c>Project/Plugins/</c> that contains
///      a <c>manifest.json</c>, fetch the raw manifest, parse it, and emit
///      an <see cref="OfficialPluginEntry"/>.
///
/// Network only — no on-disk cache. The dialog calls this every time it
/// opens; failures degrade gracefully (combo stays empty, user can still
/// paste a custom URL below).
/// </summary>
public static class OfficialPluginCatalog
{
    private const string Owner = "psmon";
    private const string Repo  = "AgentZeroLite";
    private const string Branch = "main";
    private const string PluginsPath = "Project/Plugins";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("AgentZeroLite-OfficialCatalog/1.0");
        return c;
    }

    public static async Task<IReadOnlyList<OfficialPluginEntry>> DiscoverAsync(CancellationToken ct = default)
    {
        try
        {
            // Step 1: branch SHA.
            var refUrl  = $"https://api.github.com/repos/{Owner}/{Repo}/git/refs/heads/{Branch}";
            var refJson = await Http.GetStringAsync(refUrl, ct);
            using var refDoc = JsonDocument.Parse(refJson);
            var sha = refDoc.RootElement.GetProperty("object").GetProperty("sha").GetString()
                ?? throw new InvalidDataException("branch SHA missing");

            // Step 2: recursive tree of the whole repo at that SHA.
            var treeUrl  = $"https://api.github.com/repos/{Owner}/{Repo}/git/trees/{sha}?recursive=1";
            var treeJson = await Http.GetStringAsync(treeUrl, ct);
            using var treeDoc = JsonDocument.Parse(treeJson);

            // Step 3: pluck every "Project/Plugins/<id>/manifest.json" blob.
            var manifestPrefix = PluginsPath + "/";
            var ids = new List<string>();
            foreach (var entry in treeDoc.RootElement.GetProperty("tree").EnumerateArray())
            {
                if (entry.GetProperty("type").GetString() != "blob") continue;
                var path = entry.GetProperty("path").GetString();
                if (string.IsNullOrEmpty(path)) continue;
                if (!path.StartsWith(manifestPrefix, StringComparison.Ordinal)) continue;
                if (!path.EndsWith("/manifest.json", StringComparison.Ordinal)) continue;

                var rest = path.Substring(manifestPrefix.Length);          // e.g. "voice-note/manifest.json"
                var slash = rest.IndexOf('/');
                if (slash <= 0) continue;
                var id = rest.Substring(0, slash);
                if (rest.Length != slash + "/manifest.json".Length) continue; // skip nested manifests
                ids.Add(id);
            }

            // Step 4: fetch each manifest in parallel for metadata.
            var manifestFetches = ids
                .Distinct(StringComparer.Ordinal)
                .Select(id => FetchEntryAsync(id, ct))
                .ToArray();
            var entries = await Task.WhenAll(manifestFetches);

            return entries
                .Where(e => e is not null)
                .Cast<OfficialPluginEntry>()
                .OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[OfficialPluginCatalog] discover failed: {ex.GetType().Name}: {ex.Message}");
            return Array.Empty<OfficialPluginEntry>();
        }
    }

    private static async Task<OfficialPluginEntry?> FetchEntryAsync(string id, CancellationToken ct)
    {
        try
        {
            var rawUrl = $"https://raw.githubusercontent.com/{Owner}/{Repo}/{Branch}/{PluginsPath}/{id}/manifest.json";
            var json = await Http.GetStringAsync(rawUrl, ct);
            // Reuse the strict validator so we match the install path's contract.
            var m = PluginManifest.Parse(json);

            // Tolerate id mismatch — folder name wins for the URL the user
            // ultimately installs from. Log if they disagree.
            if (!string.Equals(m.Id, id, StringComparison.Ordinal))
                AppLogger.Log($"[OfficialPluginCatalog] folder '{id}' disagrees with manifest.id '{m.Id}'");

            var folderUrl = $"https://github.com/{Owner}/{Repo}/tree/{Branch}/{PluginsPath}/{id}";
            return new OfficialPluginEntry(id, m.Name, m.Version, m.Description, m.Icon, folderUrl);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[OfficialPluginCatalog] manifest fetch failed for '{id}': {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
