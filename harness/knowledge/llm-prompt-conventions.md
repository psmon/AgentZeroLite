# LLM Prompt Conventions

> Owner: `code-coach`. Created 2026-04-26 in response to maintainer direction
> "프롬프트 영어로 작성할것 조정 - 툴체인 프롬프트도 동일 영어로". This file is
> load-bearing for any work that adds or modifies a prompt that ends up on
> the inference path of an on-device LLM (Gemma 4, Nemotron Nano, future
> additions).

## Rule R-1 — Default language is English

All prompts that reach an LLM inference call MUST be written in English by
default. This includes:

- System prompts (e.g. `AgentToolGrammar.SystemPrompt`)
- Tool descriptions and tool-catalogue text
- First-contact / introduction messages injected before user content
  (`WorkspaceTerminalToolHost.BuildFirstContactHeader`)
- Few-shot examples embedded in any prompt
- Error / retry messages that the LLM will see in a follow-up turn
- GBNF grammar comments that the model can be expected to read indirectly

The user's free-form input (relayed via `RunAsync(userRequest)`) stays in
whatever language the user typed. The wrapper around it does not.

### Why

1. **Tokenisation efficiency** — Gemma 3/4 and Llama-3.1 tokenizers are
   tuned on English-heavy corpora. Korean strings cost roughly 1.5–3×
   the tokens English would for the same semantic content, eating the
   already-tight `ContextSize` (2048 default).
2. **Cross-family stability** — every model in the catalogue understands
   English natively as instruction language. Korean-trained vocabulary
   varies between families and between quantisations.
3. **Test repeatability** — `T1G–T4G` / `T1N–T4N` test prompts are in
   English; production prompts that match keep regression behaviour aligned
   with what tests check.
4. **Function-call schema clarity** — JSON keys / tool names are English;
   surrounding instructions in English avoid mode-switching costs in the
   model's attention.

### Out of scope (Korean is fine)

- UI labels and chat bubbles shown to the user.
- Logs (`AppLogger.Log("[AIMODE] ..."`).
- Comments in source files.
- `Docs/llm/ko/*.md` tutorials and any Korean reference material.
- The user's actual chat input — that's their content, not ours.

## Rule R-2 — Token budget for prompt rewrites

When extending a system prompt, keep the additional length under control:

- A new instruction block: aim for ≤ 100 English tokens (~ 80 words).
- The full system prompt should stay well under 800 tokens so it leaves
  room for the user request + tool results + the model's own response
  inside the 2048-token context.

Use the LlmProbe `bench` phase to verify a prompt change doesn't push
first-token latency past ~1.5× the previous baseline.

## Rule R-3 — Audit checklist for prompt PRs

Before merging any change that touches an LLM prompt, verify:

- [ ] All wrapper text is English (R-1).
- [ ] System prompt total token count comparable to baseline (R-2).
- [ ] If the prompt teaches a multi-step pattern (e.g. send → read → decide),
      that pattern is also exercised by at least one test in
      `AgentToolLoopTests` (`T2G/T2N` or higher).
- [ ] Hard rules ("do NOT", "ALWAYS") appear at top of the rules section,
      not buried mid-prompt.
- [ ] No pretty-printed JSON examples that the grammar would forbid (the
      sampler has to reproduce the example to satisfy the grammar; mismatch
      causes silent stalls).

## Concrete touch-points to keep in sync with this rule

| File | Role |
|------|------|
| `Project/ZeroCommon/Llm/Tools/AgentToolGrammar.cs` (`SystemPrompt`) | Tool-loop system prompt |
| `Project/ZeroCommon/Llm/Tools/T0Probe.cs` (`BuildPrompt`) | Native-token probe prompts |
| `Project/AgentZeroWpf/Services/WorkspaceTerminalToolHost.cs` (`BuildFirstContactHeader`) | First-contact intro injected into terminal AIs |
| Future: any new `*Prompt`, `*Header`, `*Instructions` constants under `Project/ZeroCommon/Llm/` | Anything that becomes part of the LLM input |

## Cross-references

- `harness/knowledge/ondevice-tool-calling-survey.md` — backend choice that
  motivated the GBNF + system-prompt design.
- `harness/agents/code-coach.md` — owns enforcement of these conventions
  during pre-commit reviews of LLM-prompt-touching changes.
- `Docs/llm/en/gemma4-ondevice-tutorial.md` §5 — the original prompt /
  template walkthrough that future prompts should remain consistent with.
