---
date: 2026-04-29T15:12:00+09:00
agent: tamer
type: improvement
mode: log-eval
trigger: "지금발견 내용은 지식을 업데이트"
---

# Knowledge Update — WPF XAML Resource & Window Pitfalls

## 실행 요약

The voice-pipeline build session today (`0a87d1b → 585f8f3`) crashed the
app four times in the same hour — every crash was a different WPF
trap, every fix was one line, and **every one of them passed
`dotnet build` cleanly**. The build-green signal was insufficient for
WPF/XAML diffs in this codebase, and the live-run feedback loop took
real user time per round.

The cleanup is a single knowledge file plus one binding rule on
`code-coach`. The intent is to catch this whole class of "crash on
first click into new UI" in pre-commit review going forward.

## 결과

### 신규 파일
- **`harness/knowledge/wpf-xaml-resource-and-window-pitfalls.md`** —
  Five pitfalls with real DispatcherUnhandled stacks, root cause,
  landed fix, and a one-grep pre-commit check each. Closes with a
  P1–P5 checklist for code-coach Mode 2.
  - **P1**: `<StackPanel.Resources>` style not visible from siblings
    (the `MiniKeyButton` crash, commit `69f93a3`).
  - **P2**: assumed-but-undefined resource key (`TextLight` crash,
    commit `f44281c`). Includes the actual key inventory of
    `AgentBotWindow.xaml`.
  - **P3**: `Window.Resources` does NOT inherit from `Owner` —
    every new Window must self-declare its keys (TestToolsWindow
    landed in `9606394` doing exactly this).
  - **P4**: `Window.Owner = this` requires `this` to have an HWND
    (commit `585f8f3` — `ResolveVisibleOwner()` helper for
    AgentBotWindow's embedded vs floating dual-mode).
  - **P5**: `System.Drawing.Brush` vs `System.Windows.Media.Brush`
    ambiguity — project has `<UseWPF>` + `<UseWindowsForms>` both
    on; global `Brushes` alias exists but `Brush` does not.

- **`harness/docs/v1.1.6.md`** — version bump record.

### 수정 파일
- **`harness/agents/code-coach.md`** — Added the new knowledge file to
  "Owned convention sets" with binding instruction: walk the P1–P5
  checklist on every Mode 2 review where staged diff touches `*.xaml`
  or a `Window` code-behind.
- **`harness/harness.config.json`** — version 1.1.5 → 1.1.6,
  lastUpdated 2026-04-29.

## 평가 (3축)

| 축 | 등급 | 근거 |
|----|------|------|
| 워크플로우 개선도 | **B+** | Knowledge addition + a binding rule on the agent that runs every commit. Cost to add: ~30 min. Cost saved per future XAML crash: 5–10 min round-trip per. Five distinct pitfalls covered, all with one-grep checks. |
| Claude 스킬 활용도 | **2 / 5** | Pure knowledge work — no external tools. The knowledge will improve `code-coach` Mode 2 quality, which is internal to the harness. |
| 하네스 성숙도 | **L3 → L3+** | Knowledge count 8 → 9. Each existing knowledge file is referenced by ≥ 1 agent's "Owned convention sets" and this one is no exception (code-coach picked it up). Engine count unchanged (2). Agent count unchanged (5). |

## 다음 단계 제안

- [ ] Code-coach next Mode 2 invocation on a XAML diff should explicitly
      run the P1–P5 checks and cite the result in the review log
      (verifies the rule is operational, not theoretical).
- [ ] Consider promoting the per-window resource key inventories to a
      generated reference if more new Windows arrive — the current
      "AgentBotWindow.xaml has TextPrimary/TextDim only" table will
      drift if more brushes are added. For now manual maintenance is
      fine since changes are rare.
- [ ] If a sixth WPF pitfall surfaces, consider extracting the
      checklist into its own `harness/engine/xaml-review.md` workflow
      that code-coach mode 2 calls explicitly. Two pitfalls' worth of
      growth doesn't yet justify the indirection.
