---
date: 2026-04-25T18:30:00+09:00
agent: code-coach
type: review
mode: precommit-mode2
trigger: "git commit (ChatMode.Ai entry — Phase 3 #5)"
---

# Pre-commit review — ChatMode.Ai cycle entry

## Staged change

`Project/AgentZeroWpf/UI/APP/AgentBotWindow.xaml.cs` only — small WPF
addition: AI as a third mode in the Shift+Tab cycle, badge styling, and a
defensive downgrade if persisted state lands in Ai with no LLM loaded.

## Review by lens

**.NET modern** — `switch` expression on enum cycle is idiomatic.
Pattern match `LlmService.Llm is not null` reads clean. ✓

**WPF** — Reuses existing `ShowModeToast` for user-visible feedback.
`btnCycleMode.Foreground` swap follows the same pattern as Chat/Key
modes. Color picked at `0xC586C0` (violet) — distinct from CHT (gray)
and KEY (blue). ✓

**LLM / native** — `using Agent.Common.Llm;` added. `LlmService.Llm`
null-check is the gate (matches the SettingsPanel `EnsureSessionAsync`
pattern). No new LLM dependencies introduced. ✓

**Akka** — Not applicable.
**Win32** — Not applicable.

## Findings

| Severity | Count |
|----------|-------|
| Must-fix | 0 |
| Should-fix | 0 |
| Suggestion | 0 |

## Behavioral notes

- Entering AI mode requires an LLM to be loaded (`LlmService.Llm != null`).
  When no LLM is loaded, Shift+Tab cycles `Chat → Key → Chat` (skipping
  Ai). When transitioning into Ai, the toast announces "AI : On-device
  agent mode". When a previously-Ai persisted state is restored without
  a loaded LLM, the badge defensively drops to Chat with an explanatory
  toast.
- This commit only changes mode *entry*; send-time routing is Phase 3
  #7 territory. So pressing Enter while in Ai mode currently routes
  through the same path as Chat/Key (until #7 lands). That's intentional
  — keeps each commit small and reviewable.

## Pre-commit decision

**Reviewed-clean** → proceed with `git commit`.

## Cross-references

- Task #5 in the in-session task list (now completed).
- Source comment "CHT → KEY → AI → CHT" on line 1621 was already in the
  codebase as a forward-looking hint; this commit makes the comment
  match reality.
- Phase 3 #6 (real IAgentToolHost) and #7 (AgentBotWindow wiring) are
  the remaining WPF pieces; both can land while download #1 finishes
  Nemotron in the background.
