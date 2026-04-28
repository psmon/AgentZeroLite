---
date: 2026-04-27T22:00:00+09:00
agent: code-coach
type: review
mode: log-eval
trigger: "설정창 갔다 오면 분할 layout reset 발견 → 한 줄 fix"
target: "OnSidebarSettingsClick: drop ActivateGroup on settings-close return"
---

# Pre-commit Review — Settings panel layout-preservation fix

## Verdict

**OK to commit.** 0 must-fix, 0 should-fix, 1 advisory suggestion
(`psmon/AgentZeroLite#3`).

## Diagnosis

User report: building a split / multi-pane terminal layout via AvalonDock
drag-drop, then opening settings and returning, snaps every document
back into a single `LayoutDocumentPane` (single tab strip).

Trace:

```
OnSidebarSettingsClick (settings → CLI)
  → SwitchToCliPanel()  (Visibility flip)
  → ActivateGroup(_activeGroupIndex)
  → RebuildDocumentPane()
  → terminalDocPane.Children.Clear()   ← layout gone
  → re-add Documents into single pane  ← single-pane regression
```

`SwitchToCliPanel` alone preserves AvalonDock state (a `Visibility`
flip does not destroy child controls). The trailing `ActivateGroup`
call rebuilds the document pane unconditionally. Removed.

## Change set

| File | Type | Concern |
|---|---|---|
| `Project/AgentZeroWpf/UI/APP/MainWindow.xaml.cs:1155-1170` | edit | Drop `ActivateGroup(_activeGroupIndex)` from the settings-close branch; comment explains the rationale. |

## 4-lens cross-stack judgment

| Lens | Issues |
|---|---|
| .NET modern | None — single-line removal + clarifying comment |
| Akka.NET | Untouched — actor topology never observes settings round-trip |
| WPF / AvalonDock | The whole point of the fix; `Visibility=Collapsed` does not dispose children, so the `LayoutRoot` survives intact across the round-trip |
| LLM integration | Untouched |
| Windows native | Untouched |

## Findings

### S-1 (Suggestion) — defensive group-index validation

Today the settings panel only mutates `CliDefinition` rows, never
`CliGroup` rows, so `_activeGroupIndex` is guaranteed valid on return.
If a future settings extension lets the panel mutate group structure,
this branch could find the index stale. The removed `ActivateGroup`
call accidentally masked that case by always re-running activation
logic.

A small defensive snap-to-valid-index would survive a future scope
shift without bringing back the layout-clobber. Not blocking; tracked
in #3 with a `Closes-when`.

## Owned-convention check

- `harness/knowledge/llm-prompt-conventions.md`: ✅ no LLM prompts touched
- `harness/knowledge/agent-origin-reference.md`: ✅ this is a Lite-side UX
  bug, not an Origin adoption (Origin's WPF surface is much larger and
  has its own dock lifecycle considerations not relevant here)

## Test verification

- ZeroCommon + AgentZeroWpf builds clean (0 errors)
- Headless suite untouched (this is a UI-only path with no actor or LLM
  effect)
- Manual verification deferred to user — empirical re-test of the
  symptom (split layout → settings → CLI → still split) is the only
  meaningful test for an AvalonDock interaction

## Recommendation

Single-file commit. References issue #3 for the suggestion follow-up.

## Related

- Issue handoff: psmon/AgentZeroLite#3
- The diagnosis and trace shown to the user before the fix went in
