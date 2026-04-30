using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AgentZeroWpf.Services;
using AvalonDock.Layout;
using EasyWindowsTerminalControl;

namespace AgentZeroWpf.Module;

public sealed class ConsoleTabInfo : IConsoleTabInfo
{
    public string Title { get; set; } = "";
    public EasyTerminalControl? Terminal { get; set; }
    public LayoutDocument Document { get; set; } = null!;
    public Grid TerminalHost { get; set; } = null!;
    public int CliDefinitionId { get; init; }
    public bool IsInitialized { get; set; }
    public bool IsTerminalStarted { get; set; }
    public string ExePath { get; init; } = "";
    public string? Arguments { get; init; }
    public ConPtyTerminalSession? Session { get; set; }

    /// <summary>
    /// Last time this session was touched (created, activated, or terminal init).
    /// Used by the SESSIONS panel to show a "2m ago" hint for quick orientation.
    /// </summary>
    public DateTime LastActivityAt { get; set; } = DateTime.Now;
    // Last values pushed to actor system — used to dedup redundant rebind events from
    // repeat Loaded firing (tab re-activation, airspace toggle).
    public string? LastBoundSessionId { get; set; }
    public nint LastBoundHwnd { get; set; }
    // Active retry timer when EnsureSession is waiting for ConPTY output log —
    // guarded so only one timer runs per tab at a time.
    public System.Windows.Threading.DispatcherTimer? SessionPendingRetry { get; set; }

    // ── Wedge-recovery UI state ──
    // HealthChanged subscription is wired exactly once per session. New sessions
    // (after RestartWedgedTerminal) reset this flag so the next session re-wires.
    public bool HealthWired { get; set; }
    // Banner overlay shown when HealthState transitions to Dead. Stored on the
    // tab so Show/Hide are idempotent without a global lookup table.
    public Border? WedgeBanner { get; set; }
}

public sealed class CliGroupInfo : ICliGroupInfo
{
    public string DirectoryPath { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public List<ConsoleTabInfo> Tabs { get; } = [];
    public Border SidebarButton { get; set; } = null!;
    public int ActiveTabIndex { get; set; } = -1;

    IReadOnlyList<IConsoleTabInfo> ICliGroupInfo.TabsView => Tabs;
}
