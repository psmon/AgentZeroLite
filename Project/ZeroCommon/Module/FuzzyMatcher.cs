using System;
using System.Collections.Generic;
using System.Linq;

namespace Agent.Common.Module;

/// <summary>
/// Pure (WPF-free) subsequence fuzzy matcher + ranker for the command palette.
/// Case-insensitive; scores consecutive runs, word-boundary starts, and prefers
/// shorter targets. Headlessly testable.
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>
    /// Returns true if every char of <paramref name="query"/> appears in
    /// <paramref name="text"/> in order (a subsequence), with a relevance
    /// <paramref name="score"/> (higher = better). An empty query matches anything.
    /// </summary>
    public static bool TryMatch(string query, string text, out int score)
    {
        score = 0;
        if (string.IsNullOrEmpty(query)) return true;
        if (string.IsNullOrEmpty(text)) return false;

        int qi = 0, consecutive = 0, lastMatch = -2, firstMatch = -1;
        for (int ti = 0; ti < text.Length && qi < query.Length; ti++)
        {
            if (char.ToLowerInvariant(text[ti]) != char.ToLowerInvariant(query[qi])) continue;

            if (firstMatch < 0) firstMatch = ti;
            score += 10;
            if (ti == lastMatch + 1) { consecutive++; score += consecutive * 5; }
            else consecutive = 0;
            if (ti == 0 || !char.IsLetterOrDigit(text[ti - 1])) score += 15; // word boundary
            lastMatch = ti;
            qi++;
        }

        if (qi < query.Length) return false; // not all query chars matched
        // Strongly prefer matches that start at the very beginning of the target
        // (prefix), then earlier starts, then shorter targets.
        score += firstMatch == 0 ? 30 : 0;
        score -= firstMatch;
        score -= Math.Max(0, text.Length - query.Length);
        return true;
    }

    /// <summary>
    /// Filters <paramref name="items"/> to those matching <paramref name="query"/>
    /// and returns them best-first. Ties preserve input order (stable).
    /// </summary>
    public static IReadOnlyList<T> Rank<T>(string query, IEnumerable<T> items, Func<T, string> textOf)
    {
        var scored = new List<(T Item, int Score, int Index)>();
        int idx = 0;
        foreach (var item in items)
        {
            if (TryMatch(query, textOf(item) ?? "", out var s))
                scored.Add((item, s, idx));
            idx++;
        }
        return scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .Select(x => x.Item)
            .ToList();
    }
}
