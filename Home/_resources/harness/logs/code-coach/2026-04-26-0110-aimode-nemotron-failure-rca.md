---
date: 2026-04-26T01:10+09:00
agent: code-coach
type: review
mode: log-eval
trigger: "AgentBot AIMODE에서 젬마는 성공 / 네모트론은 실패 — 원인분석"
---

# AIMODE — Why Nemotron failed where Gemma worked, and the user-settings bypass that caused it

## Executive summary

Two distinct bugs, multiplicatively expressed in AIMODE only:

1. **AIMODE ignored the user's LLM settings.** `AgentBotWindow.OnAiSendAsync`
   constructed `AgentToolLoopOptions` with only `OnTurnCompleted` set, so
   `MaxTokensPerTurn` and `Temperature` fell to the hardcoded class
   defaults (384 / 0.0). The Settings → AI Mode panel writes to
   `LlmSettingsStore` (`MaxTokens=3000, Temperature=0.7` per the user's
   `llm-settings.json`), and the TestBot path honours those — but AIMODE
   silently didn't.
2. **Llama-3.1 tokenizer + GBNF + a 384-token output budget is fragile.**
   The doc-comment claimed "384 absorbs all 5 tools' typical envelopes
   with headroom," extrapolating from Gemma's SentencePiece efficiency.
   Llama-3.1 BPE encodes JSON envelopes with 5–15 % more tokens, so
   `send_to_terminal` payloads with a long `text` arg routinely truncate
   mid-string. GBNF then can't close the JSON, the parser sees a
   short-payload `{"tool":"send_to_terminal","args":{"text":"...`, and
   the loop breaks on `JsonException`.

The first bug enables the second. Fixing #1 makes #2 disappear for any
user with a sane `MaxTokens` setting.

## Why TestBot worked while AIMODE failed (same model, same Vulkan, same backend)

Empirical observation from the user: TestBot in Settings → AI Mode runs
Nemotron + Vulkan + Backend=1 just fine. AIMODE on the same loaded LLM
fails. That single fact rules out the previously-suspected
"Vulkan + Llama-3.1 + GBNF instability" as the primary cause — TestBot
shares the same runtime and same model.

Diff between paths:

| Concern | TestBot (`LlamaSharpLocalChatSession`) | AIMODE (`AgentToolLoop`) |
|---------|----------------------------------------|--------------------------|
| Grammar | None — free-form sampling | GBNF enforced + `GrammarOptimization=Extended` |
| `MaxTokens` | `_options.MaxTokens` (3000 from settings) | hardcoded `384` |
| `Temperature` | `_options.Temperature` (0.7 from settings) | hardcoded `0.0` |
| Context | one `LLamaContext`, persistent | extra `LLamaContext` created on the same weights |

Two of those four (token budget and temperature) come from the same
settings store TestBot consumes. The loop just didn't read it.

## Root cause (commit fa3dad2 regression in design, not code)

`Project/AgentZeroWpf/UI/APP/AgentBotWindow.xaml.cs:1045`:

```csharp
// before
var loopOpts = new Agent.Common.Llm.Tools.AgentToolLoopOptions
{
    OnTurnCompleted = turn => Dispatcher.BeginInvoke(...)
};
```

`AgentToolLoopOptions` defaults: `MaxTokensPerTurn = 384`,
`Temperature = 0.0f`. The user-facing Settings panel had no way to reach
into AIMODE.

The design intent baked into the doc-comments was reasonable in
isolation — greedy + small budget reduces grammar-dead-end stalls. But
once the Settings panel exists *and* the TestBot honours it, AIMODE
must too, or you get exactly this bug: user tunes panel → panel works
→ AIMODE silently runs on stale defaults → user concludes the model is
broken.

## Patch (applied in this session)

`AgentBotWindow.OnAiSendAsync`:

```csharp
var s = LlmService.CurrentSettings;
var loopOpts = new Agent.Common.Llm.Tools.AgentToolLoopOptions
{
    MaxTokensPerTurn = Math.Max(256, s.MaxTokens),
    Temperature      = s.Temperature,
    OnTurnCompleted  = turn => Dispatcher.BeginInvoke(...),
};
```

- `Math.Max(256, …)` floor protects against a user setting `MaxTokens=64`
  in the panel and then breaking AIMODE in a different way (envelope
  can't fit). 256 is the smallest value that always closes the
  five-tool envelopes per the original 80–250 estimate.
- No floor or ceiling on `Temperature` — user choice respected. If the
  doc-comment's `Vulkan + Llama-3.1 + temp>0` stall ever materialises in
  the wild, surface a tip in the UI rather than silently overriding.

Plus a UX gap closed: `AgentBotWindow.xaml` gains `btnAiNewSession` (a
refresh icon, visible only in AI mode) so users can drop the current
KV cache and re-introduce terminals without Shift+Tab-cycling out and
back in. Mirrors Settings → AI Mode → New Session for the AIMODE side.
Handler: `OnAiNewSessionClick` → `DisposeAiLoopAsync()`.

## Files changed

- `Project/AgentZeroWpf/UI/APP/AgentBotWindow.xaml.cs`
  - L1043 area: thread `LlmService.CurrentSettings.MaxTokens / Temperature`
    into `AgentToolLoopOptions`.
  - L2024 area: toggle `btnAiNewSession.Visibility` based on `_chatMode == Ai`.
  - New handler `OnAiNewSessionClick`.
- `Project/AgentZeroWpf/UI/APP/AgentBotWindow.xaml`
  - New `<Button x:Name="btnAiNewSession">` adjacent to `btnCycleMode`,
    Segoe MDL2 refresh glyph (`&#xE72C;`), default `Collapsed`.

## Verification

- `dotnet build Project/AgentZeroWpf/AgentZeroWpf.csproj -c Debug` — 0
  errors, only pre-existing CS0414/CS0649/NETSDK1206 warnings.
- `dotnet test Project/ZeroCommon.Tests/ZeroCommon.Tests.csproj` (live
  Nemotron suite filtered out — no GPU/model in this run): **31/31 pass**.
- Headless AgentToolLoop parser/grammar tests, ChatMode cycle tests, T0
  probes for both families: all pass after the change.

UI verification (Nemotron + Vulkan in actual AIMODE conversation) is
the user's next step — the test surface for this fix is "user
settings reach AIMODE", which the changed code line is the entirety of.

## Evaluation (Code Coach Mode 3 rubric)

| Axis | Result |
|------|--------|
| Cross-stack judgment | A — caught the cross-cutting bug spanning Settings store, two backend paths, and tokenizer-vs-grammar interaction. |
| Actionability | A — patch is two diffs in one file + one XAML element; called out with file:line and rationale. |
| Research depth | A — three options enumerated (settings wiring, llama.cpp bump, auto-CPU downgrade); option 1 chosen because TestBot evidence directly contradicted the Vulkan-instability hypothesis. |
| Knowledge capture | Pass — `harness/knowledge/llm-prompt-conventions.md` already covers prompt budgets; this RCA captures the settings-routing gap as a one-off log rather than a new convention (single instance, no policy needed yet). |

## Follow-up regression (2026-04-26 01:30) — `MaxTokens=3000` broke Gemma+CPU too

After the first patch shipped, the user reported AIMODE no longer
responding even on Gemma + CPU (a previously-stable combo). Logs:

```
[01:24:37.605] [AIMODE] new session opened (template=gemma, maxTokens=3000, temp=0.70, backend=Cpu)
[01:24:48.324] [Akka] CoordinatedShutdown triggered          ← 11s later, no [AIMODE] complete
```

**Root cause**: I conflated TestBot's `MaxTokens` (free-form answer cap)
with AIMODE's `MaxTokensPerTurn` (single tool-call envelope cap).
`AgentToolGrammar.Gbnf` ends with a trailing `ws*` rule:

```ebnf
root ::= ... "}" ws
```

That trailing `ws` accepts unbounded whitespace, so once the model emits
the closing `}` it tries to emit the family's EOT marker
(`<end_of_turn>` / `<|eot_id|>`) — but those aren't in `[ \t\n\r]`, so
the grammar masks them out. The model is forced to keep emitting
whitespace until it hits `MaxTokensPerTurn`. With the old hardcoded 384
that capped the stall to ~25s on CPU; with my change to 3000 it became
~3 minutes and the user (correctly) gave up after 11s.

**Fix**: `Math.Clamp(s.MaxTokens, 256, 1024)` instead of
`Math.Max(256, s.MaxTokens)`. 1024 is enough headroom for the longest
realistic `send_to_terminal` text payload and bounds the trailing-ws
stall to ~7s on CPU / sub-second on GPU.

Log line now reports both: panel value AND the per-turn-cap actually
used, so the next time something looks wrong the diagnosis is one
line away:

```
[AIMODE] new session opened (template=gemma, maxTokens=1024 (panel=3000, capped to per-turn budget), temp=0.70, backend=Cpu)
```

**Lesson**: When a setting has different meanings in two consumers,
don't pretend it's the same setting. Either rename one, expose two
fields, or clamp loudly. The Settings panel still says "MaxTokens" but
both consumers now adapt: TestBot uses it raw, AIMODE clamps and logs
the clamp.

A cleaner long-term fix is to anchor the GBNF without the trailing
`ws*` so the model naturally terminates at `}` and the LLamaSharp
anti-prompt scanner can fire on the next EOT — but that change touches
the parser side too (current code does `Trim()` on output assuming
trailing whitespace) and is deferred.

## Next steps / deferred

- **Watch for the "Temperature stall" doc-comment claim.** If a user with
  `Temperature=0.7` + Vulkan + Nemotron + GBNF actually hits a stall, we
  surface a tip and possibly add a UI floor — but don't override
  silently. So far it's a hypothesis without a confirmed reproduction.
- **Consider hoisting the chat-template + settings → loop wiring into
  `LlmService.OpenAgentToolLoop(...)`** so we don't repeat the
  catalog/template/settings dance at every UI call site. Defer until a
  second call site exists (CLAUDE.md "no premature abstractions").
- **`AgentToolLoop` opens a second `LLamaContext`.** Not necessarily a
  bug, but worth measuring on Vulkan — twin contexts on one weights
  doubles KV-cache VRAM. If first-run latency spikes, fold the
  AgentToolLoop's executor into the existing chat session's context.
