using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using Agent.Common;
using Agent.Common.Agents;
using AgentZeroWpf.Module;

namespace AgentZeroWpf.Services;

/// <summary>
/// Live per-terminal agent-state monitor (herdr-adoption H1/H2 runtime). On a
/// timer it snapshots each started terminal's on-screen text, classifies it via
/// the pure <see cref="AgentStateDetector"/> + a manifest picked by tab title,
/// and tracks whether the user has "seen" a finished agent. Exposes a snapshot
/// (state + seen per tab) and fires a change event so the UI / CLI can surface
/// "which agent needs me".
/// </summary>
public sealed class AgentStateMonitor
{
    public sealed record TabState(int Group, int Tab, string Title, AgentActivity Activity, bool Seen, string? Rule);

    private readonly Func<IReadOnlyList<CliGroupInfo>> _groups;
    private readonly Func<(int g, int t)?> _active;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, TabState> _states = new();

    /// <summary>Raised (on the UI thread) after each detection pass.</summary>
    public event Action? Changed;

    public AgentStateMonitor(
        Func<IReadOnlyList<CliGroupInfo>> groupsProvider,
        Func<(int g, int t)?> activeProvider,
        TimeSpan? interval = null)
    {
        _groups = groupsProvider;
        _active = activeProvider;
        _timer = new DispatcherTimer { Interval = interval ?? TimeSpan.FromMilliseconds(1500) };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    /// <summary>Current per-tab states, most-urgent first (blocked/unseen-done bubble up).</summary>
    public IReadOnlyList<TabState> Snapshot()
        => _states.Values
            .OrderByDescending(s => Urgency(s))
            .ThenBy(s => s.Group).ThenBy(s => s.Tab)
            .ToList();

    /// <summary>How many tabs need the user right now (blocked or unseen-done).</summary>
    public int AttentionCount()
        => _states.Values.Count(s => s.Activity == AgentActivity.Blocked
                                  || (s.Activity == AgentActivity.Done && !s.Seen));

    private static int Urgency(TabState s) => s.Activity switch
    {
        AgentActivity.Blocked => 4,
        AgentActivity.Done when !s.Seen => 3,
        AgentActivity.Working => 2,
        AgentActivity.Idle => 1,
        _ => 0,
    };

    private void Tick()
    {
        try
        {
            var groups = _groups();
            var active = _active();
            var live = new HashSet<string>();

            for (int gi = 0; gi < groups.Count; gi++)
            {
                var g = groups[gi];
                for (int ti = 0; ti < g.Tabs.Count; ti++)
                {
                    var tab = g.Tabs[ti];
                    if (!tab.IsTerminalStarted || tab.Session is null) continue;

                    string key = (tab.Session as ConPtyTerminalSession)?.InternalId ?? $"{gi}:{ti}";
                    live.Add(key);

                    string text;
                    try { text = ApprovalParser.StripAnsiCodes(tab.Session.GetConsoleText()); }
                    catch { continue; }

                    var lines = text.Replace("\r\n", "\n").Split('\n');
                    var manifest = AgentManifestCatalog.ForAgent(tab.Title);
                    var res = AgentStateDetector.Detect(manifest, new ScreenSnapshot(lines));

                    _states.TryGetValue(key, out var prev);
                    var activity = res.StateChanged ? res.State : (prev?.Activity ?? AgentActivity.Unknown);

                    bool isActive = active is { } a && a.g == gi && a.t == ti;
                    bool seen;
                    if (isActive) seen = true;                                   // focusing marks it seen
                    else if (activity == AgentActivity.Done && prev?.Activity != AgentActivity.Done)
                        seen = false;                                             // newly done, unseen
                    else seen = prev?.Seen ?? (activity != AgentActivity.Done);

                    _states[key] = new TabState(gi, ti, tab.Title, activity, seen, res.MatchedRuleId);
                }
            }

            // Drop tabs that no longer exist.
            foreach (var stale in _states.Keys.Where(k => !live.Contains(k)).ToList())
                _states.Remove(stale);

            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[AgentState] monitor tick failed: {ex.Message}");
        }
    }
}
