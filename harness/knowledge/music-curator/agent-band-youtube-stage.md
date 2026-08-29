# Agent Band — YouTube stage host-op contract (oEmbed + classify)

**Owner**: music-curator
**Lifecycle**: convention — binding for any change to the Agent Band YouTube
stage: `youtube.oembed` / `llm.classify` host ops, `parseVideoId` /
`ParseYouTubeId`, `YT_CATEGORIES`, or the classify fallback chain.
**Last updated**: 2026-08-29 (M0026 follow-up — first capture of this contract)
**Related**: [agent-band-mapping.md](agent-band-mapping.md) (the sibling AudioSet→sprite stage)

## Why this doc exists

M0026 added a **YouTube stage** to the Agent Band plugin: paste a YouTube
URL → the plugin plays it in an iframe and asks the host to (a) fetch title/
channel via YouTube **oEmbed** and (b) **classify** the track into a Korean
genre bucket via the local LLM. Two host ops (`youtube.oembed`, `llm.classify`)
cross the WebView2 trust boundary into `WebDevHost`, so the contract — the
SSRF guard, the `llm-not-ready` behavior, the category-match clamp, and the
keyword fallback — needs to live somewhere other than a 172 KB plugin IIFE.
This is that place.

## Flow (paste → stage)

```
onPasteYoutube(raw)                         agent-band.js
  → parseVideoId(raw)  ─ null → reject      (bare 11-char id or v=/youtu.be/embed/shorts/live)
  → loadVideoFrame(id) immediately          iframe ?autoplay=1&rel=0&modestbranding=1
  → host youtube.oembed { videoId }         → title / author / thumbnail
  → host llm.classify { title, channel, categories: YT_CATEGORIES }
        ├ ok            → category (by:'llm')
        ├ 'llm-not-ready' or error → keywordCategory(title, channel) (by:'keyword')
```

The iframe plays **before** classification returns — classification is
metadata enrichment, never a gate on playback.

## Host-op surface

Both ops are dispatched by `WebDevBridge` (`case "youtube.oembed"` /
`case "llm.classify"`) to the note host, and exposed to plugin JS through the
typed `zero-bridge.js` surface (`zero.youtube.oembed(id)` / `zero.youtube.classify(opts)`).

| Host op | Input | Output (record) | Contract |
|---|---|---|---|
| `youtube.oembed` | `{ videoId }` | `OEmbedResult(Ok, VideoId, Title, Author, Thumbnail, Error)` | **SSRF-guarded** — see below. 8 s HTTP timeout (`_http`). |
| `llm.classify` | `{ title, channel, categories[] }` | `ClassifyResult(Ok, Category, Raw, Error)` | Returns `Error="llm-not-ready"` when no active LLM; otherwise a throwaway LLM session + category clamp. |

## SSRF defense (the important one)

`YouTubeOEmbedAsync` **ignores any caller-supplied URL**. It validates the id
with `IsValidVideoId` (length 8–16, `[A-Za-z0-9_-]` only) and then rebuilds a
canonical URL server-side:

```
https://www.youtube.com/watch?v={id}         ← rebuilt, never caller-provided
→ https://www.youtube.com/oembed?url=...&format=json
```

A plugin can therefore never steer the host's HTTP client at an arbitrary
host. Keep this invariant: if a future op needs a different provider, add a
new op with its own allow-list — do not accept a full URL from JS.

## `llm.classify` contract

- **No active LLM → `"llm-not-ready"`.** `ClassifyAsync` returns early
  (now logged — M0026 후속 #1) when `!LlmGateway.IsActiveAvailable()`. The
  plugin treats this as a signal to fall back, not an error to surface.
- **Throwaway session.** Uses `await using var session = LlmGateway.OpenSession()`
  so classification never pollutes the operator's chat history. `_chatLock`
  serializes LLM access with the rest of the note pipeline.
- **Category clamp — `MatchCategory(reply, allowed)`.** The model's free-text
  reply is clamped to the allowed set: forward contains-hit (an allowed label
  appears in the reply), then reverse (the reply is a substring of an allowed
  label, len ≥ 2). No match → `null` → caller applies the fallback = the **last**
  category (`기타`). The model can never invent a category outside the set.
- **`YT_CATEGORIES`** (the allowed set, JS-side): `재즈 · K-Pop · 클래식 ·
  힙합 · EDM · 발라드 · 록 · OST · 기타`. Changing this list is a contract
  change — update both the JS constant and any host-side few-shot examples.

## Fallback chain (provenance is recorded)

Every classified item carries a `by` field so the UI/telemetry can tell an
LLM verdict from a heuristic one:

1. `by:'llm'` — `llm.classify` returned a clamped category.
2. `by:'keyword'` — LLM not ready or failed → `keywordCategory(title, channel)`
   keyword heuristic in the plugin.

Never silently drop to keyword without setting `by` — the distinction is what
lets an operator trust (or discount) the bucket.

## `parseVideoId` ↔ `ParseYouTubeId` mirror

The id parser exists **twice by design** (the repo's hand-aligned mirror
convention): `parseVideoId` in JS (`agent-band.js`) and `ParseYouTubeId` in C#
(`WebDevHost.cs`). Both accept a bare 11-char id or extract from
`v=` / `youtu.be/` / `/embed/` / `/shorts/` / `/live/`, tolerate trailing
`&list=…`, and return null otherwise. When you change one, change the other.

- C# side is unit-tested: `Project/AgentTest/YouTubeClassifyTests.cs`.
- JS side: `tools/agent-band-tests/parseVideoId.test.mjs` (see
  agent-band-mapping.md "Source of truth" for the harness).

## Cross-references

- Plugin JS: `Project/Plugins/agent-band/agent-band.js`
  (`parseVideoId`, `onPasteYoutube`, `classifyVideo`, `keywordCategory`, `YT_CATEGORIES`)
- Host ops: `Project/AgentZeroWpf/Services/Browser/WebDevHost.cs`
  (`YouTubeOEmbedAsync`, `ClassifyAsync`, `MatchCategory`, `ParseYouTubeId`, `IsValidVideoId`)
- Bridge dispatch: `Project/AgentZeroWpf/Services/Browser/WebDevBridge.cs`
  (`youtube.oembed`, `llm.classify`)
- Typed JS surface: `Project/AgentZeroWpf/Wasm/common/zero-bridge.js`
- C# tests: `Project/AgentTest/YouTubeClassifyTests.cs`
- Mission record: `harness/logs/mission-records/M0026-수행결과.md`
