---
date: 2026-04-25T16:40:00+09:00
agent: code-coach
type: review
mode: precommit-mode2
trigger: "git commit (staged: Project/ZeroCommon/Llm/LlmModelCatalog.cs)"
---

# Pre-commit review — Nemotron catalog entry

## Staged change

`Project/ZeroCommon/Llm/LlmModelCatalog.cs` — additive: one new
`LlmModelCatalogEntry` record (`NemotronNano8Bv1_UD_Q4_K_XL`) appended,
plus include in `All` array. Net diff +8 / −1.

## Review by lens

**.NET modern** — Uses existing `record` pattern, immutable `readonly`
static, `IReadOnlyList` exposure. Idiomatic. ✓

**Akka** — Not applicable to this file.

**WPF** — Not applicable. Catalog lives in `ZeroCommon` (headless). UI
binding `cbLlmModel.ItemsSource = LlmModelCatalog.All` (SettingsPanel.Llm.cs:46)
auto-picks up the new entry; no XAML or code-behind changes required. ✓

**LLM / Native**:
- File size `4_994_203_200L` matches upstream HEAD verification (HF CAS bridge,
  302 → 200, Content-Disposition filename matched).
- `DownloadUrl` to `unsloth/Llama-3.1-Nemotron-Nano-8B-v1-GGUF` — same
  ecosystem supplier as Gemma entries (Unsloth dynamic quants), most-downloaded
  Q4-class quant on HF for this model.
- Quant `UD-Q4_K_XL` matches existing project pattern exactly.
- No grammar / template specifics surface at this layer — backend selection
  (GBNF vs native) happens later in the planned `Project/ZeroCommon/Llm/Tools/`
  module per `harness/knowledge/ondevice-tool-calling-survey.md`.

**Win32** — Not applicable.

## Findings

| Severity | Count |
|----------|-------|
| Must-fix | 0 |
| Should-fix | 0 |
| Suggestion | 0 |

Clean. Ship as-is.

## Tests required by this change

None at this commit. The catalog addition is data-only; behavioral tests will
land with the AIMODE backend implementation per the planned T0–T5 sequence.

## Pre-commit decision

**Reviewed-clean** → proceed with `git commit`.

## Cross-references

- Catalog file: `Project/ZeroCommon/Llm/LlmModelCatalog.cs`
- Maintainer-confirmed variant choice: `harness/logs/code-coach/2026-04-25-1620-aimode-research.md`
  (Llama-3.1-Nemotron-Nano-8B-v1 confirmed locally runnable)
- URL verification trail: subagent HEAD probe to upstream HF, 302 → CAS xet
  bridge, Content-Disposition filename matched
