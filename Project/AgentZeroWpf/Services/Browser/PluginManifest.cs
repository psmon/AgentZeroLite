using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentZeroWpf.Services.Browser;

/// <summary>
/// Manifest for a WebDev plugin packaged as a .zip. Authored by plugin
/// developers as a top-level <c>manifest.json</c> next to <c>index.html</c>.
/// Only <see cref="Id"/>, <see cref="Name"/>, and <see cref="Entry"/> are
/// required; everything else is optional metadata used by the sample list.
/// </summary>
public sealed record PluginManifest
{
    [JsonPropertyName("id")]          public string Id { get; init; } = "";
    [JsonPropertyName("name")]        public string Name { get; init; } = "";
    [JsonPropertyName("entry")]       public string Entry { get; init; } = "index.html";
    [JsonPropertyName("icon")]        public string? Icon { get; init; }
    [JsonPropertyName("version")]     public string? Version { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }

    private static readonly Regex IdRegex = new("^[a-z0-9][a-z0-9-]{0,62}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static PluginManifest Parse(string json)
    {
        var m = JsonSerializer.Deserialize<PluginManifest>(json, JsonOpts)
                ?? throw new InvalidDataException("manifest.json is empty or invalid JSON");
        return m.Validated();
    }

    public PluginManifest Validated()
    {
        if (string.IsNullOrWhiteSpace(Id))
            throw new InvalidDataException("manifest.id is required");
        if (!IdRegex.IsMatch(Id))
            throw new InvalidDataException(
                "manifest.id must be lower-case kebab-case (a-z, 0-9, '-'), 1-63 chars, starting with a letter or digit");
        if (string.IsNullOrWhiteSpace(Name))
            throw new InvalidDataException("manifest.name is required");
        if (string.IsNullOrWhiteSpace(Entry))
            throw new InvalidDataException("manifest.entry is required (e.g. 'index.html')");
        if (Entry.Contains("..") || Entry.Contains('\\') || Entry.StartsWith('/'))
            throw new InvalidDataException("manifest.entry must be a relative path inside the plugin folder");
        if (!Entry.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("manifest.entry must point to an .html file");
        return this;
    }
}
