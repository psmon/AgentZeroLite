using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Agent.Common.Services;
using AgentZeroWpf.Services;
using AgentZeroWpf.UI.Components;
using AvalonDock.Layout;

namespace AgentZeroWpf.Module;

public sealed class ConsoleTabInfo : IConsoleTabInfo
{
    public string Title { get; set; } = "";

    public XtermTerminalControl? XtermTerminal { get; set; }

    /// <summary>The Visual hosting this tab's terminal — used for the per-tab HWND
    /// lookup in terminal-list IPC.</summary>
    public FrameworkElement? TerminalVisual => XtermTerminal;
    public LayoutDocument Document { get; set; } = null!;
    public Grid TerminalHost { get; set; } = null!;
    public int CliDefinitionId { get; init; }
    public bool IsInitialized { get; set; }
    public bool IsTerminalStarted { get; set; }
    public string ExePath { get; init; } = "";
    public string? Arguments { get; init; }

    /// <summary>
    /// M0021: when the CliDefinition is Remote+Password, this carries the DPAPI
    /// ciphertext through to InitializeTerminal so the launcher can decrypt
    /// once and copy onto the clipboard. Plaintext never lives in
    /// ConsoleTabInfo — only the encrypted blob, which is useless without
    /// the current Windows user's DPAPI key.
    /// </summary>
    public string? EncryptedPasswordForLaunch { get; init; }
    public ITerminalSession? Session { get; set; }

    // What this tab was launched with. Kept so the terminal can be recreated in
    // place — wedge recovery and the context-menu restart both need to build the
    // same child again, and the EasyConPty control used to own that (RestartTerm)
    // where now nothing else does.
    public string? LaunchCommandLine { get; set; }
    public string? LaunchWorkingDir { get; set; }

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

    // Redock strip — sits in row 0 of TerminalHost. Visible only while
    // the tab's LayoutDocument is in a floating window; hidden otherwise.
    // Travels with the doc, so it appears inside the floating window
    // automatically (the floating window hosts Document.Content).
    public Border? RedockStrip { get; set; }

    // ── Link strip (terminal hyperlink detection) ──
    // Row 0 of TerminalHost is a StackPanel holding every top strip (redock,
    // link) so they never overlap and consume 0 px when collapsed. Lives
    // ABOVE the terminal cell rather than over it, so the EasyConPty HwndHost
    // can't punch through it (airspace).
    public StackPanel? StripHost { get; set; }
    // Watches this tab's session output for URLs (OAuth / device-login
    // links). Rebuilt whenever the session is replaced (restart).
    public TerminalLinkDetector? LinkDetector { get; set; }
    public Border? LinkStrip { get; set; }
    public TextBlock? LinkStripText { get; set; }
    public string? LinkStripUrl { get; set; }
    public System.Windows.Threading.DispatcherTimer? LinkStripHideTimer { get; set; }
}

public sealed class CliGroupInfo : ICliGroupInfo
{
    public string DirectoryPath { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public List<ConsoleTabInfo> Tabs { get; } = [];
    public Border SidebarButton { get; set; } = null!;
    public int ActiveTabIndex { get; set; } = -1;

    /// <summary>
    /// How this workspace's tabs were split when it was last left, so returning
    /// to it puts them back rather than collapsing everything into one pane.
    /// Null until the workspace has been left once. Reconciled with the tabs that
    /// actually exist by <see cref="Agent.Common.Services.DockPaneLayout"/> before
    /// it is replayed — it goes stale as soon as a tab is opened or closed.
    /// </summary>
    public Agent.Common.Services.DockPaneNode? DockLayout { get; set; }

    /// <summary>The same thing as stored — see <see cref="ICliGroupInfo.DockLayoutJson"/>.</summary>
    string? ICliGroupInfo.DockLayoutJson => Agent.Common.Services.DockPaneLayout.ToJson(DockLayout);

    IReadOnlyList<IConsoleTabInfo> ICliGroupInfo.TabsView => Tabs;
}
