---
date: 2026-04-25T16:20:00+09:00
agent: code-coach
type: research
mode: research-consult
trigger: "AgentBot AIMODE 탑재 — 젬마4 + 네모트론 듀얼 백엔드 설계"
---

# AIMODE Research — Dual-Backend (Gemma 4 + Nemotron Nano)

## Problem

User wants to extend `AgentBot` chat panel with an AIMODE (toggled by a
keyboard shortcut, conditional on Gemma being loaded) where the on-device LLM
itself participates in conversations with terminal-side AIs (claude / codex /
etc.) via function calling — not just text forwarding. After initial design,
user added Nemotron as a co-equal target ("최근 오픈한 네몬트론 엔비디아도
함께 조사").

Maintainer constraints:
- **GBNF enforcement** as baseline ("GBNF 강제를 기본으로")
- **E4B forced** for Gemma — E2B unstable
- **Native** if the model supports it ("네이티브로 가능한지 체크해보고") —
  fallback to GBNF if not
- **Module is Gemma 4 specialized** — but now extended to Nemotron, so the
  module is "on-device tool-using LLM" not "Gemma-only"
- WPF integration timing → after unit tests pass

## Investigation

Two-step investigation:

1. **Existing infrastructure map** (delegated to Explore agent): ~650-word
   report mapping the on-device tab in SettingsPanel, LlmService /
   LlamaSharpLocalLlm / LlamaSharpLocalChatSession interfaces, current
   AgentBot ↔ terminal flow (currently no-op in AgentBotActor — only logs),
   LlmProbe + LlmProbeTests pattern, and Shift+Tab keyboard handler that
   already cycles modes (with an unfinished comment "CHT → KEY → AI → CHT").

2. **Tool-calling capability survey** (delegated to general-purpose agent
   with web research): primary-source findings for both model families.
   Saved to `harness/knowledge/ondevice-tool-calling-survey.md`.

## Key findings

| Family | Native tool-calling | Decision |
|--------|---------------------|----------|
| Google Gemma 4 (E4B) | **No.** No SFT, no tool tokens, llama.cpp Generic handler | **GBNF required** |
| NVIDIA Nemotron Nano (8B-v1, 9B-v2) | **Yes.** Explicit SFT, tool template, llama.cpp native handler. BFCL v3 = 66.9% on 9B-v2. | **Native primary, GBNF fallback** |

Architectural consequence: the AIMODE module is **dual-backend** from the
start. Per CLAUDE.md ("no premature abstractions") we ship two concrete
backends side-by-side; extract an `IToolBackend` interface only when a third
model lands.

## Design decisions (committed)

1. **Backend selection at LLM load time per model identity.** A runtime probe
   step verifies that native tool-template parsing actually works for the
   loaded model; fails-soft to GBNF if not.
2. **Variant selection**:
   - Gemma 4 → E4B only (forced; E2B excluded)
   - Nemotron → Llama-3.1-Nemotron-Nano-8B-v1 first (proven llama.cpp Llama
     3.x handler), Nano-9B-v2 in Phase 2 after self-built llama.cpp commit
     advances.
3. **5-tool surface** (unchanged from initial proposal):
   `list_terminals`, `read_terminal`, `send_to_terminal`, `send_key`, `done`.
4. **Test plan** (gated by Skip-if-no-model, same pattern as `LlmProbeTests`):
   - **T0** — runtime probe — does the loaded model emit valid tool tokens?
     (Gemma → expect "no, fall back to GBNF"; Nemotron → expect "yes,
     native works".)
   - **T1G/T1N** — single-call: ask Gemma/Nemotron to call
     `list_terminals` with mock host. Assert valid tool call + correct name.
   - **T2G/T2N** — multi-turn: list → result → done. Assert clean
     termination via `done`.
   - **T3G/T3N** — multi-step: "send 'hello' to other tab" → list →
     send_to_terminal → done. Assert mock host saw correct args.
   - **T4G/T4N** — output stability: 8 trials × ambiguous prompt; assert
     ≥ N/8 produce parseable structure. (Statistical assertion — LLMs are
     non-deterministic.)
   - **T5** — cross-model parity (only if both models available locally):
     same scenario, both backends, expect functionally equivalent traces.
5. **Code layout**:
   ```
   Project/ZeroCommon/Llm/Tools/
     ├── IAgentToolHost.cs          ← mockable host interface
     ├── AgentToolDescriptors.cs    ← tool schemas + system prompts
     ├── GbnfBackend.cs             ← Gemma 4 path
     ├── NativeToolBackend.cs       ← Nemotron path
     ├── AgentToolLoop.cs           ← model-agnostic driver
     └── AgentToolResult.cs         ← result records
   Project/ZeroCommon.Tests/
     └── AgentToolLoopTests.cs      ← T0-T5 (Skip-if-no-model)
   ```
6. **WPF integration deferred**: AIMODE entry on Shift+Tab cycle, badge
   styling, etc. — only after unit tests pass for at least Gemma 4 GBNF
   backend.

## Open question for maintainer

**Nemotron variant**: Llama-3.1-Nemotron-Nano-8B-v1 as Phase 1 (recommended)
is proven on llama.cpp's Llama-3.x handler — minimal parser surprises. The
maintainer hasn't confirmed which Nemotron variant to target. Recommendation
saved to `memory/project_aimode_dual_backend.md` but a "yes / different"
from maintainer is needed before test prototype work begins.

## Self-evaluation (rubric)

| Axis | Result |
|------|--------|
| Cross-stack judgment | **A** — investigation crossed .NET (LLamaSharp / WPF), ML (model card claims), C++ (llama.cpp parser support), and protocol design (function-call schemas) |
| Actionability | **A** — every conclusion ties to a file path or specific URL; tests have concrete I/O scenarios |
| Research depth | **A** — Option A/B/C laid out per family with primary-source URLs (not blog noise); recommendation explains why (BFCL benchmark vs lack thereof) and acknowledges fragility (issue #22043) |
| Knowledge capture | **Pass** — wrote `harness/knowledge/ondevice-tool-calling-survey.md` for shelf-life beyond this session, plus cross-session memory pin |

## Next step (gated on maintainer sign-off)

1. Maintainer confirms (or amends) Nemotron variant choice.
2. Write T0 probe test first — settles native vs GBNF empirically per model
   actually loaded.
3. Then T1G-T4G (Gemma GBNF path) — proves the fallback path works for the
   model that *needs* GBNF.
4. Then T1N-T4N (Nemotron native path) — proves the primary path works for
   the model that *can* use native.
5. Then T5 cross-model parity (if both models available).
6. Only then: WPF integration.
