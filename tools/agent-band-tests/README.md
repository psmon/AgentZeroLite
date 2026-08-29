# agent-band-tests — pure-JS regression suite

A **zero-dependency** unit suite (Node's built-in `node:test`) for the pure,
side-effect-free helpers in the Agent Band plugin
(`Project/Plugins/agent-band/agent-band.js`):

- `parseVideoId` — YouTube URL / bare-id parsing (mirrors the C#
  `ParseYouTubeId`, unit-tested in `Project/AgentTest/YouTubeClassifyTests.cs`).
- `labelToPerformer` — AudioSet-label → performer-sprite mapping, including the
  M0031 rock/EDM performers and the variant-before-base **ordering contract**.

## Run

Needs Node 18+ (uses `node:test`). No `npm install`.

```bash
cd tools/agent-band-tests
node --test
# or: npm test
```

## Why it lives here (not under Wasm/ or the plugin folder)

- `Project/AgentZeroWpf/Wasm/**` is copied verbatim into the app output
  (`<Content Include="Wasm\**\*">` in `AgentZeroWpf.csproj`), so a test folder
  there would ship inside the product.
- `Project/Plugins/agent-band/` is packaged as the installable plugin and is
  under a 200-file install cap — dev tooling must not bloat it.

So the suite sits in top-level `tools/`, which no project's build copies.

## The hand-alignment convention (read before editing)

`pure.mjs` is a **hand-aligned reference copy** of the plugin's helpers, not an
import — the plugin ships as a classic `<script>` inside a WebView2 sandbox and
cannot be imported as an ES module, and it is not worth refactoring a 172 KB
shipping plugin just to test it. This mirrors what the repo already does across
languages: `parseVideoId` (JS) ↔ `ParseYouTubeId` (C#).

When you change the regex/logic in `agent-band.js`, update **all three** in the
same commit:

1. `agent-band.js` (the real code),
2. `tools/agent-band-tests/pure.mjs` (this reference copy),
3. `harness/knowledge/music-curator/agent-band-mapping.md` (the mapping table).

The tests then guard the mapping semantics against silent drift. If you later
extract these helpers into a shared ES module the plugin can consume, delete
`pure.mjs` and import the real module instead.
