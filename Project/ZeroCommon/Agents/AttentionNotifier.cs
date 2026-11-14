using System.Text.Json;
using Agent.Common.Platform;

namespace Agent.Common.Agents;

/// <summary>Why a terminal is asking for the user.</summary>
public enum AttentionKind
{
    Blocked,
    Done,

    /// <summary>The program in the tab sent a notification itself (OSC 9 / 777 / 99).</summary>
    Notify,
}

/// <summary>
/// One "this agent needs you" moment, raised on the transition into it. <paramref name="Message"/>
/// is the program's own text for <see cref="AttentionKind.Notify"/>, null otherwise.
/// </summary>
public sealed record AttentionAlert(int Group, int Tab, string Title, AttentionKind Kind, string? Message = null);

/// <summary>
/// What the user wants to hear about when an agent starts needing them (herdr H2 — "never hunt
/// for the stuck one"). Persisted as <c>attention-settings.json</c> by <see cref="AttentionSettingsStore"/>.
/// </summary>
public sealed class AttentionSettings
{
    /// <summary>Master switch. Off means no popup and no window flash.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>An agent waits for input or an approval.</summary>
    public bool OnBlocked { get; set; } = true;

    /// <summary>An agent finished a turn the user has not looked at.</summary>
    public bool OnDone { get; set; } = true;

    /// <summary>
    /// A program in a tab asked for a notification (OSC 9 / 777 / 99) — how agents AgentZero
    /// cannot read the state of (Gemini CLI, OpenCode, a build script) still reach the user.
    /// </summary>
    public bool OnNotify { get; set; } = true;

    /// <summary>
    /// Stay quiet while the app's window is the active one — the sidebar already shows it.
    /// </summary>
    public bool OnlyWhenInactive { get; set; } = true;

    /// <summary>Flash the taskbar button (Windows) / bounce the Dock icon (macOS).</summary>
    public bool FlashWindow { get; set; } = true;

    /// <summary>Show the small popup in the corner of the screen.</summary>
    public bool Popup { get; set; } = true;

    /// <summary>How long the popup stays up, in seconds (clamped 3–60).</summary>
    public int PopupSeconds { get; set; } = 8;

    public const int MinPopupSeconds = 3;
    public const int MaxPopupSeconds = 60;

    public int ResolvePopupSeconds() => Math.Clamp(PopupSeconds, MinPopupSeconds, MaxPopupSeconds);

    public bool Wants(AttentionKind kind) => Enabled && kind switch
    {
        AttentionKind.Blocked => OnBlocked,
        AttentionKind.Done => OnDone,
        AttentionKind.Notify => OnNotify,
        _ => false,
    };
}

/// <summary>JSON persistence for <see cref="AttentionSettings"/>. Never throws on load.</summary>
public static class AttentionSettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string DefaultPath => Path.Combine(AppPaths.DataRoot, "attention-settings.json");

    public static AttentionSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new AttentionSettings();
            return JsonSerializer.Deserialize<AttentionSettings>(File.ReadAllText(path)) ?? new AttentionSettings();
        }
        catch
        {
            return new AttentionSettings();
        }
    }

    public static void Save(AttentionSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOpts));
    }
}

/// <summary>
/// Turns the tracker's per-pass picture into alerts: one per terminal that has just
/// <i>entered</i> blocked, or finished a turn not yet seen. A state that stays put raises
/// nothing again, so a pass every 1.5 s does not repeat itself, and a terminal that flaps
/// (blocked → working → blocked within the cooldown) raises once.
///
/// The first pass only records the picture: what was already waiting when the app started
/// is shown by the sidebar, not announced. Host-agnostic and clock-injected so the rules are
/// tested headlessly; call it on the thread that runs <see cref="AgentActivityTracker"/>.
/// </summary>
public sealed class AttentionNotifier
{
    /// <summary>A terminal does not alert for the same reason twice within this window.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(20);

    private readonly Dictionary<string, AttentionKind?> _previous = new();
    private readonly Dictionary<string, DateTime> _lastAlert = new();
    private bool _primed;

    /// <summary>
    /// Compare this pass with the last one. <paramref name="appActive"/> is whether the app's
    /// window has focus; with <see cref="AttentionSettings.OnlyWhenInactive"/> an active app
    /// alerts nothing (the transition is still recorded, so it is not announced later).
    /// </summary>
    public IReadOnlyList<AttentionAlert> Observe(IEnumerable<TerminalAgentState> states,
        AttentionSettings settings, bool appActive, DateTime nowUtc)
    {
        var alerts = new List<AttentionAlert>();
        var seen = new HashSet<string>();
        foreach (var s in states)
        {
            var key = KeyOf(s);
            seen.Add(key);
            var now = KindOf(s);
            _previous.TryGetValue(key, out var before);
            _previous[key] = now;

            if (!_primed || now is not { } kind || before == kind) continue;
            if (!settings.Wants(kind) || (settings.OnlyWhenInactive && appActive)) continue;

            var cooldownKey = key + "|" + kind;
            if (_lastAlert.TryGetValue(cooldownKey, out var last) && nowUtc - last < Cooldown) continue;
            _lastAlert[cooldownKey] = nowUtc;
            alerts.Add(new AttentionAlert(s.Group, s.Tab, s.Title, kind));
        }

        // Closed terminals: forget them, so a new one at the same index starts clean.
        foreach (var gone in _previous.Keys.Where(k => !seen.Contains(k)).ToList())
            _previous.Remove(gone);

        _primed = true;
        return alerts;
    }

    /// <summary>
    /// The same message from the same terminal again within this window is one alert: agents
    /// re-send on every redraw of a waiting prompt, and a loop printing BELs must not spam.
    /// </summary>
    public static readonly TimeSpan NotifyRepeatWindow = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, DateTime> _lastNotify = new();

    /// <summary>
    /// A program-sent notification (<see cref="TerminalNotification"/>): announced at once — it
    /// is an event, not a state the next pass would see — under the same switches as the rest
    /// (master, <see cref="AttentionSettings.OnNotify"/>, only-when-inactive), deduplicated per
    /// terminal and text for <see cref="NotifyRepeatWindow"/>.
    /// </summary>
    public AttentionAlert? ObserveNotification(int group, int tab, string title, TerminalNotification note,
        AttentionSettings settings, bool appActive, DateTime nowUtc)
    {
        if (!settings.Wants(AttentionKind.Notify) || (settings.OnlyWhenInactive && appActive)) return null;
        var key = $"{group}:{tab}:{title}|{note.Text}";
        if (_lastNotify.TryGetValue(key, out var last) && nowUtc - last < NotifyRepeatWindow) return null;
        _lastNotify[key] = nowUtc;
        if (_lastNotify.Count > 256)
            foreach (var stale in _lastNotify.Where(kv => nowUtc - kv.Value >= NotifyRepeatWindow).Select(kv => kv.Key).ToList())
                _lastNotify.Remove(stale);
        return new AttentionAlert(group, tab, title, AttentionKind.Notify, note.Text);
    }

    private static string KeyOf(TerminalAgentState s) => $"{s.Group}:{s.Tab}:{s.Title}";

    private static AttentionKind? KindOf(TerminalAgentState s) => s.Activity switch
    {
        AgentActivity.Blocked => AttentionKind.Blocked,
        AgentActivity.Done when !s.Seen => AttentionKind.Done,
        _ => null,
    };
}
