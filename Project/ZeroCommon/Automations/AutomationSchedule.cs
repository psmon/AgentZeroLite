using System;

namespace Agent.Common.Automations;

/// <summary>
/// Pure (WPF-free) schedule parsing + next-run computation for scheduled agent
/// runs (Automations). Headlessly testable. Supported spec forms (case-insensitive):
///   - "every &lt;N&gt;m" / "every &lt;N&gt;h"   — fixed interval
///   - "hourly"                     — top of every hour
///   - "daily HH:mm"                — once per day at HH:mm (UTC)
/// All times are computed in UTC for determinism; a "daily" time is interpreted
/// as UTC.
/// </summary>
public static class AutomationSchedule
{
    /// <summary>
    /// Computes the next run time strictly after <paramref name="fromUtc"/> for a
    /// schedule spec. Returns false with a reason when the spec is invalid.
    /// </summary>
    public static bool TryComputeNext(string spec, DateTime fromUtc, out DateTime nextUtc, out string error)
    {
        nextUtc = default;
        error = "";
        var s = (spec ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) { error = "empty schedule"; return false; }

        if (s == "hourly")
        {
            var top = new DateTime(fromUtc.Year, fromUtc.Month, fromUtc.Day, fromUtc.Hour, 0, 0, DateTimeKind.Utc);
            nextUtc = top <= fromUtc ? top.AddHours(1) : top;
            return true;
        }

        if (s.StartsWith("every "))
        {
            var rest = s["every ".Length..].Trim();
            if (rest.Length < 2) { error = "bad interval"; return false; }
            var unit = rest[^1];
            if (!int.TryParse(rest[..^1].Trim(), out var n) || n <= 0) { error = "bad interval number"; return false; }
            nextUtc = unit switch
            {
                'm' => fromUtc.AddMinutes(n),
                'h' => fromUtc.AddHours(n),
                _ => default,
            };
            if (nextUtc == default) { error = "interval unit must be m or h"; return false; }
            return true;
        }

        if (s.StartsWith("daily "))
        {
            var t = s["daily ".Length..].Trim();
            var parts = t.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var hh) || !int.TryParse(parts[1], out var mm)
                || hh < 0 || hh > 23 || mm < 0 || mm > 59)
            {
                error = "daily time must be HH:mm";
                return false;
            }
            var today = new DateTime(fromUtc.Year, fromUtc.Month, fromUtc.Day, hh, mm, 0, DateTimeKind.Utc);
            nextUtc = today <= fromUtc ? today.AddDays(1) : today;
            return true;
        }

        error = $"unrecognized schedule: {spec}";
        return false;
    }

    /// <summary>True if an automation with the given next-run time is due at <paramref name="nowUtc"/>.</summary>
    public static bool IsDue(DateTime nextRunUtc, DateTime nowUtc) => nowUtc >= nextRunUtc;
}
