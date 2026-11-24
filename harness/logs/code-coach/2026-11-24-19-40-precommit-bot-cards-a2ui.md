---
date: 2026-11-24T10:40:00Z
agent: code-coach
type: review
mode: precommit
trigger: "auto: staged diff includes *.cs"
---

# Bot transcript as components (A2UI-style cards) — auto pre-commit review

## Staged scope

```
new file:   Project/ZeroCommon/Agents/BotCards.cs
new file:   Project/ZeroCommon/Llm/Tools/TerminalReadResult.cs
new file:   Project/ZeroCommon.Tests/BotCardsTests.cs
modified:   Project/ZeroCommon/Llm/Tools/{ExternalAgentLoop,LocalAgentLoop}.cs
modified:   Project/AgentZeroAvalonia/ViewModels/AgentBotViewModel.cs
modified:   Project/AgentZeroAvalonia/Views/AgentBotView.axaml
modified:   CLAUDE.md
```

## Cross-stack lens scan

| Lens | Pass / flag | Notes |
|------|-------------|-------|
| .NET modern | Flag → fixed | Records for the node catalog, generated regex, `JsonDocument` disposed everywhere, every parser has a non-throwing fallback. Findings 1–2 were in the string handling. |
| Akka.NET | Pass | No actor code touched. `read_terminal`'s result shape changed inside the loops only; `ToolTurnPresenter.TryExtractReadTerminalText` reads the flat shape (test). |
| Avalonia | Pass | One `DataTemplate` per node type, compiled bindings; `ChatItem` builds its nodes once in the constructor; `Text` / `Detail` kept as source strings so existing tests and copy still work. WPF project untouched apart from nothing — `git diff --stat` shows no `Project/AgentZeroWpf` change. |
| LLM integration | Flag → fixed | The model now gets `{ok,text[,note]}` instead of JSON inside a JSON string. Finding 3 was the one way the unwrap could lose content. |
| Windows native | Pass | Nothing native. |

## Findings

1. **Should-fix — inline marks changed words.** `Inline` removed every `**` and `__` before looking at code
   spans, so `` `2**10` `` became `210` and `__init__.py` became `init.py`, against the type's own promise that
   words are never changed. → fixed: one regex pass, code spans first (their content is kept exactly), and `__`
   is left alone everywhere. Test `Marks_go_but_names_and_code_spans_keep_every_character`.
2. **Should-fix — a cut could split a surrogate pair.** `OneLine`, `Clip` and `BotScreen.Summary` cut with
   `text[..max]`; an emoji at the boundary leaves a lone high surrogate, drawn as a broken glyph — the very
   "sometimes garbled" this change is about. → fixed: `BotJson.Cut` steps back one. Test
   `A_cut_never_leaves_half_an_emoji`.
3. **Should-fix — `TerminalReadResult.Normalize` dropped a screen that is itself `{"ok":true,…}`.** With no
   `text` property the unwrap returned `text: ""`. Both real hosts always send `text`, but the fallback path
   (plain screen text) is documented and tested, and a terminal can show such JSON. → fixed: an `ok:true`
   object is an envelope only when it carries a string `text`; otherwise it is wrapped. Added to the
   plain-text theory.

## Not changed (noted)

- A tool result over 40,000 characters is cut by the view model before `BotJson` sees it, so it does not parse
  and is shown as clipped code. Acceptable: that is the old behaviour for an oversized result, and the model
  is unaffected.
- `BotList.Rows` allocates per read; it is bound once per item.
- WPF's bot window still prints text. Listed in CLAUDE.md as not converted.

## Evaluation

| Axis | Result |
|------|--------|
| Correctness | 3 Should-fix found, 3 fixed before the commit |
| Test coverage | 23 tests in `BotCardsTests` (markdown, JSON, screen summary, read-result unwrap) |
| Verification | ZeroCommon 1,554 pass / 37 skipped, Avalonia 140 pass, WPF Debug build 0 errors; cards checked on screenshots of the Debug GUI |

## Next steps

- Convert the WPF bot window to the same catalog when it is next touched.
- The fold toggle was not click-tested by automation.
