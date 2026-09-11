using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Agent.Common.Services;
using AgentZeroWpf.Services;
using AgentZeroWpf.UI.Components;
using AvalonDock.Layout;
using EasyWindowsTerminalControl;

namespace AgentZeroWpf.Module;

public sealed class ConsoleTabInfo : IConsoleTabInfo
{
    public string Title { get; set; } = "";

    // ── Terminal backend controls (exactly one is non-null per tab) ──
    // EasyConPty backend → Terminal; WebViewXterm backend → XtermTerminal.
    // TerminalVisual gives backend-agnostic access to the hosting Visual
    // (used for the per-tab HWND lookup in terminal-list IPC).
    public EasyTerminalControl? Terminal { get; set; }
    public XtermTerminalControl? XtermTerminal { get; set; }
    public FrameworkElement? TerminalVisual => (FrameworkElement?)Terminal ?? XtermTerminal;
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

    IReadOnlyList<IConsoleTabInfo> ICliGroupInfo.TabsView => Tabs;
}
