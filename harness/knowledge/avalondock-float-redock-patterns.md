# AvalonDock — Multi-View Terminal Float / Redock Patterns

> Owner: **`code-coach`** (primary) — flag during pre-commit review of any code
> touching `dockManager`, `LayoutDocument`, or `LayoutFloatingWindow`.
> Cross-reference: `wpf-xaml-resource-and-window-pitfalls.md` for sibling traps.

This is a record of what works and what silently breaks when using **AvalonDock
4.72** (Vs2013Dark theme) to host multiple HWND-based terminals with
detach-to-floating-window + redock-to-main UX. Discovered during M0002
(2026-05-02) over three iterations. Every claim below is backed by a real
build and a real GUI verification.

---

## TL;DR

| Operation | Use this | Don't use this |
|---|---|---|
| Float a tab | `doc.Float()` | (works fine) |
| Redock a tab | `parent.RemoveChild(doc)` + `target.Children.Add(doc)` | `doc.Dock()` |
| Close empty floating window | `Window.Close()` + remove model from `Layout.FloatingWindows` | `Close()` alone |
| REDOCK button location | Inside the floating window (travels with `Document.Content`) | On the main window's toolbar |
| Overlay UI on HWND terminal | 2-row `Grid` (row 0 = Auto, row 1 = `*`) | Same-cell overlay (airspace flicker) |

---

## Pitfall 1 — `LayoutContent.Dock()` silently no-ops

**Symptom**

User clicks "Redock" → tab still shows in floating window. No exception, no log
entry. The `Dock()` call returns normally.

**Root cause**

`LayoutContent.Dock()` requires `PreviousContainer` to still be a live, attached
pane. In a typical AvalonDock host, that's true. But this codebase's
`OnLayoutRootUpdated` handler intentionally re-adds an orphaned `terminalDocPane`
when AvalonDock auto-prunes empty panes:

```csharp
if (terminalDocPane.Parent is null)
{
    var root = dockManager.Layout;
    root.RootPanel.Children.Add(terminalDocPane);
}
```

Sequence of events:

1. User has 1 tab. `terminalDocPane.Children = [doc]`. `doc.PreviousContainer = null`.
2. User clicks DETACH → `doc.Float()` is called. AvalonDock sets
   `doc.PreviousContainer = terminalDocPane` and moves `doc` into a
   `LayoutDocumentFloatingWindow`.
3. `terminalDocPane` is now empty. AvalonDock auto-prunes empty panes: it sets
   `terminalDocPane.Parent = null`.
4. `OnLayoutRootUpdated` fires. Sees orphan, re-adds `terminalDocPane` to the
   layout root. **But `doc.PreviousContainer` still points to the same
   instance** — which is now an "alive but reference-only orphan" from the
   docking machinery's perspective.
5. User clicks REDOCK → `doc.Dock()` walks `PreviousContainer`, finds it isn't
   "currently active" in the dock manager's bookkeeping, and silently bails.

**Fix**

Move the doc explicitly. The pattern was already in this codebase for
group-switch (line ~1645) — bypass `Dock()` entirely:

```csharp
private void RedockOneTab(ConsoleTabInfo tab)
{
    var doc = tab.Document;
    if (doc is null || !doc.IsFloating) return;
    if (doc.Parent is AvalonDock.Layout.ILayoutContainer parent)
        parent.RemoveChild(doc);
    GetActiveDocumentPane().Children.Add(doc);
    doc.IsActive = true;
    doc.IsSelected = true;
}
```

`GetActiveDocumentPane()` already exists and is orphan-safe (falls back through
4 levels: active doc's pane → `terminalDocPane` if attached → any DocumentPane
in the layout → recreate). `Float()` does work and stays canonical for the
detach direction; only redock has to be manual.

---

## Pitfall 2 — `Window.Close()` on an empty floating window is ignored

**Symptom**

After redock, the now-empty floating window shell stays on screen. No close,
no flicker. Calling `fw.Close()` returns normally; `fw.IsVisible` is still
true on the next line.

**Root cause**

In AvalonDock 4.72, `LayoutFloatingWindowControl.Close()` (the WPF Window
subclass) is intercepted by AvalonDock's own close handling. If the `Model`
(a `LayoutFloatingWindow`) is still in `dockManager.Layout.FloatingWindows`,
AvalonDock interprets the Close as a "user wants to dock all contents back"
event — but since contents are *already gone* (we redocked them ourselves),
there's nothing to dock and the close is a no-op.

The control and its model are tied: removing the model is what cues the control
to dispose.

**Fix**

Remove the model from `Layout.FloatingWindows` first, then `Close()`:

```csharp
if (fw.Model is LayoutFloatingWindow lfw
    && lfw.Parent is ILayoutContainer parent)
{
    parent.RemoveChild(lfw);   // model → gone
}
fw.Close();                     // control → disposes
```

Equivalent strategy that also worked in testing: clearing references via the
`ILayoutContainer.Children` API, then `Close()`. Either is fine; pick the one
that fits your traversal style.

A 3-tier fallback ladder (Close → Layout.RemoveChild + Close → Hide as last
resort) is what M0002 ships with — it logs which strategy succeeded so any
future AvalonDock upgrade can be diagnosed by reading the log line
`[Dock-DIAG] FW[i] result=closed-via-LayoutRemove`.

---

## Pitfall 3 — REDOCK button on the main window is the wrong UX location

**Symptom**

Functional but feels off. User reports: "I'm in the floating window, where do I
click to come back?"

**Root cause**

A REDOCK button in the main window's toolbar requires the user to:

1. Look away from the floating window
2. alt-tab back to the main window
3. Locate the toolbar button
4. Click

Four friction steps for a one-action operation. The mental model is "go back
*from here*", and "here" is the floating window — not the main one.

**Fix — put the button in the doc's content**

`LayoutDocument.Content` *travels with the doc*. When AvalonDock floats the doc,
the floating window hosts that same Content. So a button placed in the
Content's visual tree appears in the floating window automatically — no
visual-tree walking, no theme override, no AvalonDock internals.

Implementation: structure the doc's Content as a 2-row Grid:

```csharp
var termHost = new Grid();
termHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // row 0
termHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // row 1

var redockStrip = BuildRedockStrip(tab); // a Border containing the button
Grid.SetRow(redockStrip, 0);
redockStrip.Visibility = Visibility.Collapsed;   // only show when floating
termHost.Children.Add(redockStrip);

Grid.SetRow(terminal, 1);
termHost.Children.Add(terminal);
```

Toggle `redockStrip.Visibility` from the existing `dockManager.Layout.Updated`
callback based on `tab.Document.IsFloating`. Strip becomes visible the moment
the tab enters a floating window, hides the moment it docks back.

---

## Pitfall 4 — HWND airspace forbids same-cell WPF overlays

**Symptom**

Adding a WPF `Border` overlay to the same Grid cell as `EasyTerminalControl`
(which embeds a native HWND) causes flicker, mis-clipping, or the overlay
disappearing behind the terminal.

**Root cause**

WPF's HWND-host (`HwndHost` / `WindowsFormsHost`) opts out of WPF compositing.
The native window paints *over* WPF content within its rect — "airspace
violation".

**Fix**

Use a multi-row Grid where rows are non-overlapping. Row 0 (the REDOCK strip)
and row 1 (the terminal) occupy disjoint vertical bands, so there's no
airspace conflict. When the strip is `Visibility.Collapsed`, row 0 has 0px
height and the terminal has the full cell — no compromise in docked mode.

The pre-existing `WedgeBanner` in this codebase used `VerticalAlignment=Top`
in a single-cell Grid, which sort-of-works (top portion of the terminal HWND
is occluded), but flickers on resize. The 2-row pattern is strictly better
and that's why this mission migrated WedgeBanner into row 1 alongside the
terminal — banner and terminal still overlap (banner at top), but they share
a cell that's already disjoint from the REDOCK strip.

---

## Pitfall 5 — AvalonDock chevron in floating window is cosmetic noise

**Symptom**

After detaching, the floating window shows a small ▾ chevron at the right edge
of its tab strip. Clicking it opens a context menu listing tabs in the same
pane. With one floating tab, this is one entry pointing to the only doc that's
already visible. Useless.

**Root cause**

`LayoutDocumentPaneControl` template (in Vs2013Dark theme) always renders the
chevron, regardless of tab count or context.

**Fix**

Don't fight it. Removing the chevron requires overriding the
`LayoutDocumentPaneControlStyle` resource — that key is internal to the theme
DLL and can be renamed/restructured between AvalonDock minor versions. Given
the chevron is harmless and the new REDOCK strip sits prominently inside the
content area (above the terminal, cyan accent on dark surface), users
self-discover the strip and ignore the chevron.

If a future operator insists on chevron removal: the right approach is to
inject a custom `ResourceDictionary` into `dockManager.Resources` that
overrides `LayoutDocumentPaneControlStyle`, *not* to walk the visual tree at
runtime — visual-tree walking is fragile against AvalonDock's lazy-templating.

---

## Reference: the OnLayoutRootUpdated hook

This callback is the central nervous system for floating-window lifecycle in
this codebase. It fires after every layout mutation. Three things happen here
that work together:

```csharp
// 1. Pane preservation: re-add terminalDocPane if AvalonDock auto-pruned it.
if (terminalDocPane.Parent is null) { ... }

// 2. Owner unset: detach floating windows from main so they survive main close.
foreach (var fw in dockManager.FloatingWindows) {
    if (already-processed) continue;
    fw.Owner = null;
    fw.ShowInTaskbar = true;
}

// 3. (M0002) Per-tab REDOCK strip visibility refresh.
RefreshAllRedockStripVisibility();

// 4. (M0002) Sweep empty floating windows.
CloseEmptyFloatingWindows("OnLayoutRootUpdated");
```

The order matters: pane preservation runs first so subsequent steps see a
stable layout; the sweep runs last so we catch any window emptied by the
preceding steps.

---

## Diagnostic logging

When AvalonDock float/dock behavior breaks in a future regression, enable
`[Dock-DIAG]` log lines (already wired in `RedockOneTab` and
`CloseEmptyFloatingWindows`). They report:

- doc.Parent type before/after move
- floating window count before/after
- per-FW model type, doc count, IsVisible, IsLoaded
- which close strategy succeeded (Close / Layout.RemoveChild / Hide-fallback)

Log file: `%LOCALAPPDATA%\AgentZeroWpf\logs\app-log.txt` (or fallback
`%TEMP%\AgentZeroWpf\logs\app-log.txt`).

These are *first-class* diagnostic logs — like `[CLI-Init-DIAG]` — keep them
in the codebase even after the fix is verified. They cost a few log lines per
redock and pay back the next time AvalonDock surprises us.
