namespace Agent.Common.Agents;

/// <summary>
/// Suppresses repeat URL bubbles (M0041). A TUI repaints its screen constantly, so the same
/// link is detected over and over; the WPF host keeps a per-URL cooldown and the bot only
/// announces a link once per window. Ported so both hosts agree and so it is testable.
/// </summary>
public sealed class UrlNoticeThrottle
{
    /// <summary>How long the same URL stays suppressed after it is shown.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    /// <summary>Above this many tracked URLs, expired entries are dropped.</summary>
    public const int EvictAbove = 100;

    private readonly Dictionary<string, DateTimeOffset> _lastShown = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when this URL should be announced now. Records the decision, so two calls inside
    /// the cooldown return true then false.
    /// </summary>
    public bool ShouldShow(string url, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(url)) return false;

        if (_lastShown.TryGetValue(url, out var last) && now - last < Cooldown)
            return false;

        _lastShown[url] = now;

        if (_lastShown.Count > EvictAbove)
        {
            var cutoff = now - Cooldown;
            foreach (var key in _lastShown.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                _lastShown.Remove(key);
        }

        return true;
    }

    /// <summary>Tracked URL count — for tests and diagnostics.</summary>
    public int TrackedCount => _lastShown.Count;
}
