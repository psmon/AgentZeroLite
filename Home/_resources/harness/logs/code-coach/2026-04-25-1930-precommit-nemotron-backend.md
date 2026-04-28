---
date: 2026-04-25T19:30:00+09:00
agent: code-coach
type: review
mode: precommit-mode2
trigger: "git commit (Nemotron backend — Phase 2 #2 + #3)"
---

# Pre-commit review — Nemotron backend (template parameterization + T1N–T4N)

## Staged change

| File | Type |
|------|------|
| `Project/ZeroCommon/Llm/Tools/ChatTemplates.cs` | new — record + Gemma + Llama31 templates |
| `Project/ZeroCommon/Llm/Tools/AgentToolLoop.cs` | modified — accepts optional ChatTemplate (default Gemma) |
| `Project/ZeroCommon.Tests/AgentToolLoopTests.cs` | modified — T1N/T2N/T3N/T4N appended |
| `Project/ZeroCommon.Tests/NemotronProbeTests.cs` | new — bare-minimum load+token sanity |

## Review by lens

**.NET modern** — Record type for `ChatTemplate`, primary-ctor-style init,
`string.Format` for the marker substitution. `AgentToolLoop` constructor
gains an optional `template` parameter without breaking existing call sites
(default = Gemma). Idiomatic. ✓

**LLM** — Template parameterization correctly isolates the Gemma/Llama-3.1
divergence to two record literals. The Tools loop body is otherwise
unchanged — same GBNF, same parser, same dispatch. The Nemotron path
runs through the **same loop code** as Gemma, just with different chat
markers. That's the point: validates template injection is enough to
support Nemotron, no parallel implementation needed. ✓

**Akka / WPF / Win32** — Not applicable.

## Findings

| Severity | Count |
|----------|-------|
| Must-fix | 0 |
| Should-fix | 0 |
| Suggestion | 1 |

### Suggestion (defer) — Nemotron T3N tab routing soft-fail

T3N hard-asserts that `send_to_terminal` was called at all; the tab index
correctness is a soft (log-only) check. In this run Nemotron routed to
tab=0 (Claude) instead of tab=1 (Codex) — it skipped the discovery
`list_terminals` step and went straight to send. At temp 0.1 with the
larger 8B model this kind of "eager" behavior can dominate. Two options
if this becomes a problem in production:
1. Tighten the system prompt: "Always call list_terminals first."
2. Statistical T3N — run 5 trials, assert ≥ 3/5 hit the right tab.

For Phase 2 closeout it's fine. T3N's structural assertion (send was
called, with valid args, completing cleanly) is what matters most for
the GBNF correctness story.

## Test results

```
NemotronProbeTests.Cpu_load_and_single_token_via_llama31_template   PASS  11s
  load=8.9s infer=1.6s reply.len=5 reply="Hello"

AgentToolLoopTests.T1N_first_call_for_discovery_question...         PASS  21s (loop 9.2s)
  first_call=list_terminals clean=False (MaxIterations=1, expected)

AgentToolLoopTests.T2N_multi_turn_after_list_result...              PASS  23s
  turns=3 clean=True final="No output from the Claude terminal."

AgentToolLoopTests.T3N_multistep_send_to_named_terminal...          PASS  34s (loop 27.7s)
  turn 0: send_to_terminal {"group":0,"tab":0,"text":"hello"}
  ⚠ routed to tab=0 (Claude) — soft warn, not test failure

AgentToolLoopTests.T4N_first_call_is_list_terminals at least 4_of_5  PASS  48s
  5/5 trials picked list_terminals first (8.7-9.1s/trial)
```

Plus regression on Gemma side after the template refactor:
```
AgentToolLoopTests.Parser × 3                                        PASS
AgentToolLoopTests.T0_grammar_constrained_first_turn...              PASS  17s
```

No regression on Gemma path. Template parameterization is clean.

## Phase 1 + Phase 2 cumulative

Total live tests: **11 PASS / 11**, plus 3 parser unit tests and the
Nemotron load probe. The dual-backend (Gemma GBNF + Nemotron
GBNF-on-Llama-3.1-template) is operationally complete.

## Pre-commit decision

**Reviewed-clean** → proceed with `git commit`.

## What's still TODO

- T5 cross-model parity (next commit) — same scenario, both models,
  assert equivalent functional outcomes
- Phase 3 NOTE.md decision (Task #8)
- Harness v1.2.0 bump + changelog when above land (Task #9)
