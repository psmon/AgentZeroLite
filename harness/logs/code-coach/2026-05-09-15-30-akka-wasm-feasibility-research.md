---
date: 2026-05-09T15:30:00+09:00
agent: code-coach
type: research
mode: log-eval
trigger: "웹 WASM이 분리되어 단독 작동하는 경우 Akka.NET에서 액터 클라이언트가 탑재될 수 있나? 웹소켓 없이"
---

# Akka.NET in Blazor WASM (no-WebSocket) — Feasibility Research

## Execution summary

User asked whether an Akka.NET actor client can be embedded in a standalone Blazor WASM app without WebSocket transport. No matching agent trigger existed for the topic, so this entered as `code-coach` Mode 3 (research consult).

Survey scope:
- Akka.NET 1.5.x runtime model on Mono-WASM
- Browser sandbox network primitives (HTTP, SSE, WebRTC, WebTransport) vs. Akka.Remote's TCP/HTTP-2 assumption
- Practical engineering cost of a custom HTTP-poll / SSE transport
- Recommended architecture for AgentZero Lite if a Blazor WASM client surface is ever added

Initially proposed `harness/knowledge/_shared/akka-wasm-feasibility.md` as the storage location; user redirected to `code-coach/` with the rationale that `_shared/` should hold *confirmed, all-experts-must-know* knowledge, while review-stage research belongs to the agent that owns the consult. Adopted that convention.

## Result

- New knowledge doc: `harness/knowledge/code-coach/akka-net-wasm-feasibility.md`
  - Executive verdict table (4 scenarios)
  - Local `ActorSystem` in WASM caveats (single-thread, AOT trim, persistence, scheduler precision, bundle size)
  - Why no built-in Akka transport works without WebSocket
  - Custom transport effort estimate (2–4 engineer-weeks)
  - Recommended REST-seam pattern that mirrors the existing `CliHandler` ↔ `CliTerminalIpcHelper` IPC design
  - 5-step decision aid for future "let's run Akka.Remote in the browser" proposals
  - Open follow-ups: prototype publish-size measurement on .NET 10 preview, AOT trim verification

No code changes. No `harness.config.json` version bump (knowledge addition, not structural change to the harness).

## Evaluation (code-coach rubric)

| Axis | Result | Notes |
|------|--------|-------|
| Cross-stack judgment | **A** | Touches .NET modern (Mono-WASM, AOT), Akka.NET (transport, dispatcher, persistence), and Windows-native parallel (mirrors the project's existing IPC seam). 3 of 4 lenses engaged; WPF lens not applicable. |
| Actionability | **A** | Concrete: HOCON snippet for `CallingThreadDispatcher`, ranked browser primitives, explicit estimate for custom-transport cost, 5-step decision tree. |
| Research depth (Mode 3) | **A** | Three options presented (local-only / custom transport / REST seam) with one-line tradeoffs each + explicit recommendation. |
| Knowledge capture | **Pass** | Long-shelf-life finding landed in `harness/knowledge/code-coach/`, not lost in chat. |
| Issue handoff | **N/A** | No code change reviewed → no findings ≥ Suggestion → GitHub issue not required by the rubric. |

## Suggestions for next steps

- If a Blazor WASM surface for `harness-view` is ever scoped, treat the "Open follow-ups" section as the entry checklist before committing to actor code on the client.
- Consider a future `_shared/` promotion **only after** at least one prototype confirms publish-size and AOT trim assumptions for .NET 10 preview. Until then, keep this in `code-coach/`.
- If the same question recurs ("can we put X actor framework in the browser"), point at this doc rather than re-researching.
