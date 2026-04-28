---
date: 2026-04-25T17:20:00+09:00
agent: code-coach
type: review
mode: precommit-mode2
trigger: "git commit (staged: Tools layer + tests + InternalsVisibleTo wiring)"
---

# Pre-commit review — AgentToolLoop GBNF backend (Phase 1, Gemma)

## Staged change

| File | Lines | Type |
|------|-------|------|
| `Project/ZeroCommon/ZeroCommon.csproj` | +5 | csproj — InternalsVisibleTo for tests |
| `Project/ZeroCommon/Llm/LlamaSharpLocalLlm.cs` | +7 | internal `GetInternals()` accessor |
| `Project/ZeroCommon/Llm/Tools/IAgentToolHost.cs` | new ~60 | interface + tool-call records |
| `Project/ZeroCommon/Llm/Tools/AgentToolGrammar.cs` | new ~70 | GBNF + system prompt + 5-tool catalog |
| `Project/ZeroCommon/Llm/Tools/AgentToolLoop.cs` | new ~210 | grammar-constrained tool loop |
| `Project/ZeroCommon.Tests/AgentToolLoopTests.cs` | new ~200 | 3 parser + 3 live Gemma tests |

## Review by lens

**.NET modern** — `record` types for tool-call payloads, JsonNode/JsonObject
for arg manipulation, `await using` for `LLamaContext` lifetime, async iterator
pattern from `InteractiveExecutor.InferAsync`. Idiomatic. ✓

**Akka** — Not applicable. The Tools layer is below the actor topology;
WPF integration in Phase 3 will route between AgentBotActor and the loop
via existing message types.

**WPF** — Not applicable. Code lives in `Project/ZeroCommon/` and is
headless-testable. Boundary preserved.

**LLM / native** —
- GBNF wiring uses `LLama.Sampling.Grammar(gbnfText, "root")` with
  `DefaultSamplingPipeline.Grammar` and
  `DefaultSamplingPipeline.GrammarOptimization = .Extended` — confirmed via
  reflection of the actual LLamaSharp 0.26 DLL and verified end-to-end by
  the live Gemma tests passing.
- `Grammar` does NOT implement `IDisposable` (verified via reflection); the
  Loop only disposes its `LLamaContext`. ✓
- Multi-turn pattern reuses the existing `LlamaSharpLocalChatSession`'s
  prompt-formatting convention (`<start_of_turn>user/<end_of_turn>` markers,
  `_firstTurn` separator) — this is the Gemma chat template, not invented here.
- `internal (LLamaWeights, ModelParams) GetInternals()` is the minimum
  surface needed for the Loop to build its own grammar-aware executor without
  going through the (intentionally simple) `ILocalChatSession` Q&A surface.

**Win32** — Not applicable.

## Findings

| Severity | Count |
|----------|-------|
| Must-fix | 0 |
| Should-fix | 0 |
| Suggestion | 1 |

### Suggestion (defer)

`AgentToolLoop.ReadInt` / `ReadString` use `JsonValue.TryGetValue<int>` /
`<string>`. `JsonValue` from JsonNode parsing can hold the value as
`long` / `double` even when integer-shaped, and the typed accessor may
return false for those. Tests pass (the GBNF rule constrains numbers to
`-?[0-9]+` so the parsed value is integer-shaped), but if we later
broaden the grammar to allow doubles or large ints, the accessor will
silently fall back to the default. Re-visit when expanding tool args.

## Tests run for this commit

```
Project/ZeroCommon.Tests/AgentToolLoopTests.cs
  Parser_extracts_tool_and_args_from_grammar_output    PASS  (4 ms)
  Parser_handles_empty_args_object_for_zero_arg_tool   PASS  (11 ms)
  Parser_extracts_string_args_with_done_message        PASS  (<1 ms)
  T0_grammar_constrained_first_turn...                 PASS  (16 s)  Gemma E4B CPU
  T1G_first_call_for_discovery_question...             PASS  (11 s)  Gemma E4B CPU
  T2G_multi_turn_after_list_result...                  PASS  (19 s)  Gemma E4B CPU
```

6/6 pass. The 3 live tests prove:
- T0 — GBNF wiring + LLamaSharp 0.26 sampler integration (no malformed JSON)
- T1G — Gemma picks a known tool from the 5-tool surface for a discovery query
- T2G — Multi-turn KV cache works under grammar constraint; tool result
  feeding round-trips correctly

## Pre-commit decision

**Reviewed-clean** → proceed with `git commit`.

## Cross-references

- Research log: `harness/logs/code-coach/2026-04-25-1620-aimode-research.md`
  (Phase 1 plan: Gemma GBNF first, Nemotron native second)
- Knowledge: `harness/knowledge/ondevice-tool-calling-survey.md` (why Gemma
  needs GBNF — primary sources)
- Memory pin: `memory/project_aimode_dual_backend.md` (cross-session
  invariant)
