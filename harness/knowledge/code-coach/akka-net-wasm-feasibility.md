# Akka.NET in Blazor WASM — Feasibility Survey (no-WebSocket)

> Owner: `code-coach` Mode 3 (research consult). Date: 2026-05-09.
> Status: **Review-stage research** — not yet load-bearing for any AgentZero Lite
> feature. Do not promote to `_shared/` until at least one prototype validates
> the WASM payload + dispatcher behavior on .NET 10 Mono runtime.
> Question that triggered this: *"웹 WASM이 분리되어 단독 작동하는 경우 Akka.NET에서 액터 클라이언트가 탑재될 수 있나? 웹소켓 없이"*

---

## Executive answer

| Scenario | Verdict | Notes |
|----------|---------|-------|
| **Local-only `ActorSystem` inside Blazor WASM** | ✅ Works | .NET Standard 2.0 surface loads on Mono-WASM. Single-thread caveats apply. |
| **Akka.Remote / Akka.Cluster from WASM, WebSocket disallowed** | ❌ No built-in path | Browser sandbox has no raw TCP. All shipped Akka transports assume TCP or HTTP/2 streams. |
| **Custom HTTP-poll / SSE transport** | ⚠️ Theoretically possible | Requires implementing `Akka.Remote.Transport.Transport`. Non-trivial engineering — association lifecycle, serializer negotiation, backpressure. |
| **Pragmatic alternative — local actors + REST/gRPC to server** | ✅ Recommended | Same pattern AgentZero Lite already uses across its WPF↔CLI boundary (`WM_COPYDATA` + MMF). |

> "Local actors in WASM: yes. Akka.Remote in WASM without WS: not without writing a transport. Don't write one — put a REST seam at the boundary."

---

## 1) Local `ActorSystem` inside WASM

### What works

- Akka.NET 1.5.x targets .NET Standard 2.0 → loads on Mono-WASM (.NET 6/7/8/9/10).
- `ActorSystem.Create("local")`, `ActorOf<T>`, `Tell`, `Ask`, `Become`, `ReceiveActor`, FSM, supervision strategies — all in-process primitives function normally.
- Useful for client-side state machines, mailbox-driven UI coordinators, and FSM models that already exist in the codebase being reused on the web side.

### Caveats specific to WASM

| Concern | Impact | Mitigation |
|---------|--------|------------|
| **Single-threaded WASM (default)** | `ThreadPoolDispatcher` falls through to the same thread; no true parallelism. | Use `CallingThreadDispatcher` or a custom single-thread dispatcher. Avoid blocking `.Wait()` / `.Result` in actor message handlers. |
| **HOCON config + reflection** | Adds ~5–10 MB to the WASM publish output (Akka.Configuration's HOCON parser, type lookup). | Trim with `<TrimMode>partial</TrimMode>` + explicit `[DynamicDependency]` hints on actor types. Verify with `wasm-tools`. |
| **AOT (`PublishAot` / Wasm AOT)** | Akka resolves dispatcher / serializer types via `Type.GetType(string)`. AOT trim can drop them. | Add `DynamicDependency` attributes for every actor + Props-referenced type, or keep linker off for Akka assemblies. |
| **`Akka.Persistence` SQL plugins** | `System.Data.SqlClient`, SQLite, etc. don't run in browser sandbox. | Use `Akka.Persistence.MemoryJournal` only. For durability, persist via REST → server. |
| **`Scheduler` (HashedWheelTimer)** | Functions, but precision is bounded by browser timer throttling (especially on backgrounded tabs — minimum ~1 s). | Don't rely on sub-100ms Akka timers in WASM. Use them for high-level coordination only. |
| **WASM bundle size** | Akka.Core alone is ~3 MB compressed; with serializer + DI it climbs. | Profile with `dotnet publish -c Release` + Brotli. Decide per-feature whether actor framework justifies the payload vs. a hand-rolled state machine. |

### Verification snippet

```csharp
// Blazor WASM — Program.cs or component
var hocon = """
akka {
  actor {
    default-dispatcher {
      type = "Akka.Dispatch.CallingThreadDispatcher, Akka"
    }
  }
}
""";
var system = ActorSystem.Create("wasm-local", ConfigurationFactory.ParseString(hocon));
var coord = system.ActorOf<CoordinatorActor>("coord");
coord.Tell(new SomeCommand());
```

---

## 2) Akka.Remote / Cluster from WASM without WebSocket

### Why it doesn't work out-of-the-box

| Built-in transport | Works in browser? | Why not |
|--------------------|:-----------------:|---------|
| `Akka.Remote` (DotNetty TCP) | ❌ | Browsers expose no raw TCP; sandbox limits to HTTP/WS/WebRTC. |
| `Akka.gRPC` over HTTP/2 | ⚠️ partial | Akka.gRPC's gRPC-Web mode can do unary + server-streaming over HTTP/2-grpc-web, but bidirectional streaming historically falls back to WebSocket. Excluding WS removes the streaming half. |
| `Akka.Cluster` (gossip) | ❌ | Sits on top of Akka.Remote; same TCP problem. |

### Browser network primitives, ranked

1. **WebSocket** — *excluded by the question*.
2. **HTTP fetch / XHR** — request-response only; no native streaming push.
3. **Server-Sent Events (SSE)** — server→client unidirectional; one TCP per stream.
4. **WebRTC DataChannel** — peer-to-peer; STUN/TURN required; latency-sensitive but heavy setup.
5. **WebTransport (HTTP/3)** — newer; not supported by Akka.NET; uneven browser availability.
6. **`fetch` over HTTP/2 with streaming response** — possible for one-way push.

None of these have an off-the-shelf Akka transport plugin shipped today.

### What writing a custom transport entails

If you genuinely need actor-style RPC from WASM without WS, you'd implement `Akka.Remote.Transport.Transport`:

- `Listen()` returning an inbound association handler (mostly a no-op on the WASM side — clients don't accept).
- `Associate(remoteAddress)` opening an HTTP long-poll or SSE pair as a logical "connection".
- An `AssociationHandle` that maps `Send(byteString)` to HTTP POST and inbound bytes via the SSE stream.
- Heartbeat / disassociation semantics that survive browser tab suspend.
- Serializer negotiation (Akka assumes both sides agree on a serializer set).

**Estimated effort**: 2–4 engineer-weeks of focused work plus integration testing on browser quirks (tab visibility, network change events, mid-stream disconnect). The carrying cost (debugging, version drift against Akka.Remote internals) is significant. **Avoid unless the actor model is the explicit user-facing contract.**

---

## 3) Recommended architecture for AgentZero Lite extensions

If a future browser/WASM surface (e.g., a Blazor evolution of `Home/harness-view/`) needs to interact with the WPF actor system, prefer:

```
[Blazor WASM]                              [WPF host]
 ┌────────────────────┐                    ┌────────────────────────┐
 │ Local ActorSystem   │  ── HTTP/REST ──→  │ Web API shim           │
 │  (UI FSMs, caches)  │                    │   ↓ Tell                │
 │                    │  ←── SSE/poll ───   │ StageActor / Workspace  │
 └────────────────────┘                    └────────────────────────┘
```

- WASM side runs **local actors only**; no Akka.Remote.
- WPF side keeps `StageActor → AgentBotActor → AgentLoopActor` as-is.
- Boundary is plain JSON over HTTP. Mirrors today's `WM_COPYDATA` + MMF design (`CliHandler` ↔ `CliTerminalIpcHelper`) — same "REST-shaped seam at the process edge" pattern, just over HTTP instead of Win32 messages.
- **No coupling on Akka semantics across the wire.** Messages on the wire are domain commands / events (e.g., `StartMission`, `MissionLogged`), not internal actor protocol envelopes.

This keeps the actor system testable headlessly (current `ZeroCommon.Tests` suite still applies) and avoids subjecting the browser to Akka.Remote's TCP assumption.

---

## 4) Decision aid

Use this when someone proposes "let's run Akka.Remote into the browser":

1. **Do you actually need cluster membership / location-transparent ActorRef in the browser?** If the answer is "no, we just want to call backend logic", the answer is REST. Stop here.
2. **Do you need server→client push?** SSE handles it. No actor framework required for the channel itself.
3. **Are messages strictly request/response with idempotent retry semantics?** HTTP. Stop here.
4. **Do you need stateful client-side coordinator with mailbox semantics?** Local-only `ActorSystem` in WASM. Stop here.
5. **Do you need symmetric, location-transparent actor-to-actor messaging across the network boundary AND can't use WebSocket?** Now you're writing a custom transport — confirm twice that requirements (1)–(4) really don't cover the case before spending the weeks.

---

## References (load-bearing)

- Akka.NET targeting matrix — `akka.net` 1.5.x docs, `Akka` and `Akka.Remote` package metadata on NuGet.
- Blazor WebAssembly runtime model — `learn.microsoft.com/aspnet/core/blazor/hosting-models#blazor-webassembly`.
- WASM threading limitations — `learn.microsoft.com/dotnet/core/wasm-build`.
- gRPC-Web limits in browsers — `grpc.io/docs/platforms/web/`.
- Browser timer throttling — `developer.mozilla.org/docs/Web/API/setTimeout#reasons_for_delays_longer_than_specified`.

---

## Open follow-ups

- [ ] Build a minimal Blazor WASM sample (`Akka.Core` + `MemoryJournal`) and measure publish size + cold-start time on .NET 10 preview, before committing to actor-style client code.
- [ ] Confirm AOT trim behavior: does `dotnet publish -c Release` against Akka 1.5.x retain reflection-resolved actor types? Reproduce on the project's current `net10.0-windows` baseline.
- [ ] Map this against the AgentZero Lite roadmap — at the moment there is no WASM client planned; if `harness-view` migrates to Blazor, revisit.
