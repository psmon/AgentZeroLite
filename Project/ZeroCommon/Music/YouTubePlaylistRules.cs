using System.Text.RegularExpressions;

namespace Agent.Common.Music;

/// <summary>
/// Pure rules for the Agent Band YouTube playlist, shared by the host
/// persistence path and unit tests (mirrors how <c>Mp3InstrumentSet</c> keeps
/// the plugin's contract testable without WPF/DB). Kept in sync with the
/// plugin's constants in <c>agent-band.js</c>.
/// </summary>
public static class YouTubePlaylistRules
{
    /// <summary>Max rows kept (matches the plugin's <c>YT_STORE_MAX</c>). Oldest beyond this are pruned.</summary>
    public const int MaxItems = 60;

    /// <summary>Allowed genre buckets (matches the plugin's <c>YT_CATEGORIES</c>).</summary>
    public static readonly string[] Categories =
        { "재즈", "K-Pop", "클래식", "힙합", "EDM", "발라드", "록", "OST", "기타" };

    private static readonly Regex ValidId = new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

    /// <summary>True when <paramref name="videoId"/> is a well-formed 11-char YouTube id.</summary>
    public static bool IsValidVideoId(string? videoId)
        => !string.IsNullOrEmpty(videoId) && ValidId.IsMatch(videoId);

    /// <summary>Clamp a category to the allowed set, defaulting to "기타".</summary>
    public static string NormalizeCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return "기타";
        foreach (var c in Categories)
            if (c == category) return category;
        return "기타";
    }

    /// <summary>Provenance is either "llm" or "keyword"; anything else falls back to "keyword".</summary>
    public static string NormalizeCategoryBy(string? by)
        => by == "llm" ? "llm" : "keyword";
}
