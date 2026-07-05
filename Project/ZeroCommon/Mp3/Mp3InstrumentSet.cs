using System.Text.RegularExpressions;

namespace Agent.Common.Mp3;

/// <summary>
/// Normalization/merge for the <c>Mp3Track.Instruments</c> CSV (M0029 확장).
/// The plugin accumulates canonical performer keys ("piano", "violin",
/// "vocal") from the live AST tick while a track plays and posts them over
/// the bridge; this clamp keeps the stored set clean no matter what JS
/// sends — lowercase key charset only, deduped, sorted, capped.
/// </summary>
public static class Mp3InstrumentSet
{
    private static readonly Regex KeyRe = new("^[a-z0-9][a-z0-9-]{0,23}$", RegexOptions.Compiled);
    private const int MaxKeys = 24;

    public static IReadOnlyList<string> Parse(string? csv)
        => (csv ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .Where(s => KeyRe.IsMatch(s))
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .Take(MaxKeys)
            .ToList();

    /// <summary>Union of the stored CSV and newly heard keys → normalized CSV.</summary>
    public static string Merge(string? existingCsv, IEnumerable<string>? add)
    {
        var joined = (existingCsv ?? "") + "," + string.Join(",", add ?? Array.Empty<string>());
        return string.Join(",", Parse(joined));
    }
}
