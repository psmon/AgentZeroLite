# On-Device Tool Calling — Gemma 4 vs Nemotron Nano (2026-04 survey)

> Owner: `code-coach` Mode 3 (research consult). Date: 2026-04-25.
> Stack assumed: LLamaSharp 0.26.0 + llama.cpp commit `3f7c29d` (the
> self-built bundle in `runtimes/win-x64-{cpu,vulkan}/native/`), in-process
> WPF host, no `llama-server` / HTTP layer.
>
> This survey is load-bearing for the AgentBot AIMODE feature
> (`harness/logs/code-coach/2026-04-25-1620-aimode-research.md`).

---

## Executive answer

| Model | On-device variant | Native tool-calling? | Decision |
|-------|-------------------|----------------------|----------|
| **Google Gemma 4** | E4B (forced — E2B unstable in tests) | **No** — no SFT, no tool tokens, llama.cpp Generic handler | **GBNF enforcement required** |
| **NVIDIA Nemotron Nano** | 8B-v1 / 9B-v2 | **Yes** — explicit SFT, tool-template tokens, llama.cpp Llama-3.x handler (8B) / dedicated parser (9B-v2) | **Native primary, GBNF fallback** |

> "Native works for Nemotron, GBNF for Gemma 4 — keep a runtime probe so you
> can flip to GBNF on Nemotron the moment you hit issue #22043-class breakage."

---

## Family A — Google Gemma 4 (E4B / E2B)

`Gemma 4` is the rebrand of the Gemma 3n line. E4B / E2B are the on-device
"Effective" variants. Public Gemma 3 (3-4B-it / 3-27B-it) is the closest
documented predecessor.

| Question | Answer | Source |
|----------|--------|--------|
| Native tool-calling SFT? | **No.** Gemma 3 / 3n model cards do not advertise a tool-calling fine-tune; tool calling shown only as a prompt-engineering pattern. | `huggingface.co/google/gemma-3-4b-it`, `huggingface.co/google/gemma-3n-E4B-it`, `ai.google.dev/gemma/docs/capabilities/function-calling` |
| Chat-template tool tokens? | **No.** Template uses only `<start_of_turn>user / model / <end_of_turn>`; no `<tool_call>`, no `<\|tool\|>`, no `tool` role. Confirmed in tokenizer_config of every public Gemma 3/3n GGUF. | (tokenizer config in each Gemma 3/3n GGUF) |
| llama.cpp template support? | **Generic handler.** `function-calling.md` lists `google-gemma-2-*.jinja` and `google-gemma-7b-it.jinja` as Generic. Gemma 3/4 inherits the same shape. NOT in native-handler list (Llama 3.x, Hermes 2 Pro, Functionary, Mistral Nemo, Command R7B, DeepSeek R1). | `github.com/ggml-org/llama.cpp/blob/master/docs/function-calling.md` |
| Recommended approach? | Google's own cookbook uses **prompted JSON**, not a special token. llama.cpp Generic handler does the same. For in-process LLamaSharp app with no jinja server, **GBNF-constrained JSON** is the safe equivalent. | `github.com/ggml-org/llama.cpp/pull/9639` |
| Community benchmarks? | None I could cite for Gemma 4 tool reliability specifically. No BFCL number on the cards. | (open issue #21381 is a Vulkan kernel crash, not a tool-quality report) |

### Implication for our app

GBNF must enforce structure. Without it, Gemma 4 will sometimes return
free-form prose mixed with JSON — and our parser will fail intermittently. The
GBNF route guarantees JSON validity at the **sampler** level (not at output
parsing), so the only remaining quality dimension is *whether Gemma chose the
right tool*, which we measure with statistical assertions in tests.

---

## Family B — NVIDIA Nemotron Nano (recently opened)

Recently opened on-device-realistic checkpoints:
- **Llama-3.1-Nemotron-Nano-8B-v1** (Mar 2025) — Llama 3.1 backbone + Nemotron post-training.
- **NVIDIA-Nemotron-Nano-9B-v2** (Aug 2025) — Mamba-2 hybrid, current flagship small model, fits a single consumer GPU.
- The older `Nemotron-4-Mini-Instruct-4B` exists; superseded by the above.

| Question | Answer | Source |
|----------|--------|--------|
| Native tool-calling SFT? | **Yes, explicit.** Nano-8B card: "post trained for reasoning, human chat preferences, and tasks, such as **RAG and tool calling**." Nano-9B-v2 publishes BFCL v3 = **66.9%**. | `huggingface.co/nvidia/Llama-3.1-Nemotron-Nano-8B-v1`, `huggingface.co/nvidia/NVIDIA-Nemotron-Nano-9B-v2` |
| Chat-template tool tokens? | **Yes.** 8B uses Llama-3.1 tool template (`<\|python_tag\|>`, `<\|eom_id\|>`). 9B-v2 uses a Nemotron-specific template with a `tool` role and `<TOOLCALL>` block. Both ship in `tokenizer_config.json`. | (tokenizer configs of each model) |
| llama.cpp template support? | **Partial-to-yes.** 8B → Llama 3.x native handler (`meta-llama-Llama-3.1-8B-Instruct.jinja`). Nemotron-Nano-3 GGUF parses with `parallel_tool_calls`. Nemotron-H / 9B-v2 needed convert-side fixes. | `github.com/ggml-org/llama.cpp/issues/22043`, `pull/21890`, `pull/21664` |
| Recommended approach? | NVIDIA cards point to OpenAI-shape `tools=` on `build.nvidia.com`; locally that maps to `llama-server --jinja` native parsing. **In-process LLamaSharp** without llama-server: native template is doable (parse `<TOOLCALL>` ourselves), GBNF still useful as safety net. | (NVIDIA model cards) |
| Community benchmarks? | **Yes** — published BFCL v3 = 66.9% (Nano-9B-v2 card). DGX Spark benches in llama.cpp issue #20652 (closed). Real `parallel_tool_calls` infinite-loop bug in #22043 — reliability is not yet bullet-proof. | (cards + issues above) |

### Implication for our app

Native template parsing is real and worth using on Nemotron — quality is
materially better than Gemma 4 per BFCL. But **infinite-loop bugs in parallel
tool calls are documented**: keep a max-iteration cap (we already plan one) and
keep GBNF as a fallback the user can flip to if native gets unstable on a
given llama.cpp commit.

---

## Variant selection (for the AIMODE module)

| Family | Recommended variant | Why |
|--------|---------------------|-----|
| Gemma 4 | **E4B** (forced — user direction) | E2B fails the tool-quality bar in user testing. E4B Q4_K_XL fits CPU mode and Vulkan. |
| Nemotron | **Llama-3.1-Nemotron-Nano-8B-v1** as Phase 1 | Proven llama.cpp Llama-3.x native handler, classic template, fewer parser surprises. |
| Nemotron | **Nano-9B-v2** as Phase 2 (after 8B works end-to-end) | Newer, BFCL v3 66.9%, Mamba-2 hybrid — needs newer llama.cpp; revisit when self-build commit moves up. |

We do *not* invest in the older `Nemotron-4-Mini-Instruct-4B` — superseded.

---

## Architectural consequence

The AIMODE module is no longer "Gemma 4 specific" — it must support **two
backend strategies**:

```
                    ┌─────────────────────────────┐
                    │       AgentToolLoop         │
                    │   (model-agnostic driver)   │
                    └───────┬─────────────┬───────┘
                            │             │
        ┌───────────────────┘             └───────────────────┐
        ▼                                                     ▼
┌──────────────────────┐                        ┌──────────────────────┐
│  GbnfBackend         │                        │  NativeToolBackend   │
│  - GBNF grammar      │                        │  - Llama-3.x parser  │
│  - prompted JSON     │                        │  - <TOOLCALL> parser │
│  - Gemma 4 default   │                        │  - Nemotron Nano     │
└──────────────────────┘                        └──────────────────────┘
```

Selection at load time per model identity. **Per CLAUDE.md** ("don't add
abstractions beyond what the task requires"): we start with the two **concrete**
backends side-by-side. We extract an `IToolBackend` interface only when a third
model arrives or when both backends share enough non-trivial code that
deduplication helps.

A **runtime probe** (see test plan) checks whether the loaded model emits valid
native tool tokens. If not, the loop transparently routes through the GBNF
backend regardless of model identity. This protects against:
- Future Gemma version that DOES gain tool tokens (no code change needed)
- Future llama.cpp regression that breaks Nemotron native parsing (auto-flip to GBNF)

---

## Cross-references

- `harness/logs/code-coach/2026-04-25-1620-aimode-research.md` — log of this
  research and the design decisions it produced.
- `memory/project_aimode_dual_backend.md` — cross-session pin of the
  Gemma-GBNF / Nemotron-native decision and the E4B-only / Nano-8B-v1-first
  variant selection.
- `Docs/llm/en/gemma4-ondevice-tutorial.md` — how Gemma 4 is loaded today.
- `Project/ZeroCommon/Llm/LlamaSharpLocalChatSession.cs:11` — current Gemma
  anti-prompt list (`<end_of_turn>`, `<eos>`); will need a Nemotron equivalent
  list (`<\|eom_id\|>` / `<TOOLCALL>` / etc.) when the second backend lands.
