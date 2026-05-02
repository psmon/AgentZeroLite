---
mission: M0004
title: AgentZero hybrid-LLM tech article — Notion AI-DOC publish
operator: psmon
language: en
dispatched_to: [tamer, psmon-doc-writer]
status: done
started: 2026-05-02T10:50:00+09:00
finished: 2026-05-02T20:35:00+09:00
artifacts:
  - harness/missions/M0004-AgentZeroHybridLLMWithActor.md
  - D:\MYNOTE\Tech\AgentZeroLite\노션\2026-05-02-hybrid-wedge-actor-stage.md
  - https://luxuriant-brazil-09c.notion.site/354b85459d55816d92cde0aefbbd57f7
  - https://www.notion.so/354b85459d55816d92cde0aefbbd57f7
  - harness/logs/mission-records/M0004-execution-result.md
notion_page_id: "354b85459d55816d92cde0aefbbd57f7"
notion_parent: "AI-DOC (34db85459d5580f2a56ae5b07653a370)"
series_part_index: 12
---

# Execution summary

operator(psmon) requested a synthesis tech article on Notion AI-DOC explaining the
AgentZero Lite hybrid-LLM strategy in English. The brief mandated a process —
log English draft to a separate Obsidian vault first, gain operator approval,
publish to Notion, then annotate the vault entry with the publish result. Tamer
dispatched to the `psmon-doc-writer` skill, which owns this externalised
publishing workflow (W5 — *new techdoc under AI-DOC*) and its identity defaults
(`@webnori`, AI-DOC parent page id, vault path).

Article was published as **Part 12** of the AgentZero Lite series under AI-DOC,
following the existing 11-part lineage (Part 11 was the May 1 voice-driven
free-stack demo).

## Workflow steps actually run

1. **Mission frontmatter injection** — operator's mission file was free-form English
   prose without the `id / title / status / priority / created` contract. Tamer
   prepended the YAML block (graceful prepend pattern, identical to the M0002 / M0003
   sequence) so `harness-missions.json` could pair the mission with its execution
   record on subsequent index rebuilds.

2. **Vault + repo audit** — confirmed every architectural claim before writing.
   - Multi-tab ConPTY terminals, AgentBot CHT/KEY/AI modes, AIMODE Gemma 4
     coordinator, Voice (Whisper.net + SAPI + Akka.NET Streams) — all verified
     against `README.md` lines 42–101 and `Project/ZeroCommon/Actors/`,
     `Project/ZeroCommon/Voice/Streams/`, `Project/ZeroCommon/Llm/`.
   - SSH "remote AI CLI" claim from the brief was *softened* in the article —
     SSH appears only as a code comment about tailscale handshake timing, not
     as a first-class connector. Article reframes this as "any peer agent that
     lives in a terminal" which is the truthful framing (ConPTY hosts whatever
     shell program you launch, including an SSH client).
   - README's "voice output still in development" caveat is **stale** as of
     May 1 — vault Part 11 confirmed the round-trip is shipping. Article uses
     the current state, not the README snapshot.

3. **Source research** — four parallel WebSearch queries to anchor cited claims:
   - Anthropic Claude Max pricing → claude.com/pricing/max ($100 / $200 monthly tiers)
   - Google Gemma 4 release → blog.google + huggingface.co/blog/gemma4 + InfoQ (Apr 2 2026)
   - NVIDIA Nemotron 3 Nano Omni → nvidia.com + huggingface.co/nvidia + DeepInfra (Apr 28 2026)
   - OpenAI Realtime API audio rates → openai.com/api/pricing ($0.06/min in, $0.24/min out)
   - ElevenLabs pricing → elevenlabs.io/pricing (Pro $99, Scale $330)
   - Bonus citation surfaced naturally: wheresyoured.at coverage of Anthropic's
     brief Pro-tier Claude Code removal — used as evidence of pricing
     volatility, not as advocacy.

4. **Vault draft v1** (`D:\MYNOTE\Tech\AgentZeroLite\노션\2026-05-02-hybrid-wedge-actor-stage.md`) —
   first English draft, ~2200 words, tone matching the senior-engineer-to-peers
   register established in Part 11. Original framing: *"Hybrid is the product."*
   Submitted to operator with section map and intent mapping per
   psmon-doc-writer convention.

5. **Operator review feedback** — operator returned six specific revisions:
   - Main thesis to *"is hybrid a choice, or is plugging into the three giants
     inevitable"*
   - Economics tone to *"snapshot, not a trend; finance teams want fixed cost"*
   - Add Gemma function-calling explainer covering how a plain LLM is agentized
   - Make the strategic combinations (Paid+Free, Paid+Free+Free, Free+Free+Free)
     explicit
   - Reframe outlook so Gemma 4 is *the inflection point*
   - Keep three reader questions, but reframe Q1 around the "plug into all three
     giants is already happening" angle

6. **Vault draft v2** — full rewrite of TL;DR, economics, outlook, closing Q1,
   plus two new sections ("Strategic compositions — what hybrid actually buys
   you" with 5-recipe table, and "Function calling, walked through" with GBNF
   grammar excerpt). Final length ~2700 words. Added one new source citation
   (llama.cpp GBNF spec) for the function-calling section.

7. **Operator approval** — *"승인 게시해"* (approve, publish).

8. **Notion publish blocker — recovered same session** — The first publish attempt
   failed because Notion MCP (`mcp__claude_ai_Notion__*`) was not surfaced in
   this session's deferred tool list. `.mcp.json` only configures
   memorizer/playwright/pencil; user-level Claude config has no Notion entry
   either. Reported the constraint honestly with two options (re-connect MCP
   vs. manual paste). Operator reconnected the claude.ai Notion connector via
   `/mcp`; tools surfaced; publish proceeded.

9. **Notion publish** — content was reflowed from standard Markdown to
   Notion-flavored Markdown:
   - Two pipe-tables (`|` syntax) → Notion `<table>` XML elements
   - H1 page title removed from body (Notion sets it via `properties.title`)
   - Mermaid block kept with explicit `"User"` quoting per spec
   - Code block fences kept (Notion auto-detected `c#` language for the
     C# blocks; `text` for the FSM ASCII diagram)

   `notion-create-pages` returned page id `354b85459d55816d92cde0aefbbd57f7`
   under AI-DOC parent. `notion-fetch` round-trip confirmed full content
   rendered with all 9 sections and both tables intact.

10. **Vault annotation** — `published_url`, `internal_url`, `this_page_id`,
    `posted_at`, `status: published` all written back to the same vault
    file. `published_url` uses the public `luxuriant-brazil-09c.notion.site/...`
    form so the entry is shareable; `internal_url` uses `notion.so` for
    workspace-internal navigation.

11. **External public verification** — `WebFetch` to the public URL returned
    the Notion SPA shell (200 OK; full body not extractable — Notion is a
    React SPA that renders client-side, expected per psmon-doc-writer W6
    notes). The page is publicly reachable; full visual rendering would
    require Playwright, deferred since the parent AI-DOC inherited publish
    setting and existing 11 sibling pages all served fine.

## Result

| Surface | URL |
|---|---|
| Public (notion.site) | https://luxuriant-brazil-09c.notion.site/354b85459d55816d92cde0aefbbd57f7 |
| Internal (notion.so) | https://www.notion.so/354b85459d55816d92cde0aefbbd57f7 |
| Vault entry | `D:\MYNOTE\Tech\AgentZeroLite\노션\2026-05-02-hybrid-wedge-actor-stage.md` |
| Series position | AgentZero Lite series, Part 12 (after Part 11 *Voice-Driving Claude CLI on a Free Stack*) |
| Length | ~2700 words, 9 sections, 2 tables, 1 mermaid diagram, 4 code blocks |
| Citations | 11 distinct sources, all vendor or reputable editorial |

## Evaluation

### Acceptance check (operator brief)

- [x] Tech document on Notion under specified AI-DOC location
- [x] Written in English
- [x] Brief intro to AgentZero Lite features (multi-CLI views, peer-CLI control, AIMODE Gemma toolchain, voice mode)
- [x] Hybrid strategy section
- [x] Voice limitations of open models honestly addressed (top-tier wins on prosody/latency)
- [x] Top-tier economic reality covered (Claude Max ≈ ₩280k territory, OpenAI Realtime per-minute math)
- [x] End-of-free-lunch perception covered (GPT/Copilot/Claude Code Pro shifts)
- [x] Hybrid-is-the-answer framing for consumer apps
- [x] Playground value articulated (in closing Q3 specifically)
- [x] **Core architectural section**: actor stage agentizing the LLM, free-stack voice mimicking the paid realtime API, code samples (`ILocalLlm` interface + reactor FSM transition + GBNF grammar excerpt)
- [x] Outlook with Gemma 4 / Nemotron / customisable specialised models trend
- [x] Conclusion + reader questions
- [x] Citations from reputable institutional sources (vendor pages + editorial pieces, all linked inline)

Operator post-draft revisions (round 2):

- [x] Main thesis pivoted to *"plugging into the three giants is inevitable; mixing open in is the strategy"*
- [x] Economics tone to *"snapshot, not a trend; finance wants fixed cost"*
- [x] Function-calling explainer for how a plain LLM gets agentized (Route A: GBNF; Route B: native template)
- [x] Strategic combinations (Paid+Free / Paid+Free+Free / Free+Free+Free) explicit as a recipe table
- [x] Outlook reframed with Gemma 4 as the inflection point
- [x] Q1 reframed around "you're already running hybrid; have you named it"

### Tamer 7-axis evaluation

| Axis | Result | Justification |
|---|---|---|
| Workflow improvement | **A** | First mission to exercise the design-first / draft-review-publish loop end-to-end through psmon-doc-writer. The MCP blocker mid-publish was recovered the same session via operator-side reconnect — protocol survived a partial outage. |
| Claude skill utilisation | **5/5** | Tamer dispatched to psmon-doc-writer as the workflow owner; psmon-doc-writer delegated source research to WebSearch (4 parallel queries), draft persistence to vault, publish to claude.ai Notion connector. Pencil and Playwright were correctly *not* invoked — wrong tool fit for a pure prose deliverable. |
| Harness maturity | L4 → L4 | No new harness layer added. Mission #4 demonstrates the missions subsystem now handles externalised content workflows via a dedicated specialist (psmon-doc-writer), not just code/UI work. Confirms the protocol generalises. |
| Dispatch accuracy | **A** | Single-skill dispatch was correct — psmon-doc-writer is the canonical owner of vault → Notion content. No cross-dispatch needed. Mission's "search reputable institutional sources" subtask was naturally absorbed into the skill's source-citation expectation. |
| Mission language fidelity | **Pass** | Operator brief was English; this execution log is English (matches operator language per missions-protocol). Conversation with operator stayed Korean per Identity defaults. Vault intent_mapping captures the Korean→English crossings explicitly. |
| Acceptance coverage | **A** | All 11 acceptance items from the original brief plus all 6 round-2 revision items covered. Two extras shipped beyond brief: (a) a fifth source citation (wheresyoured.at) for pricing volatility evidence, (b) a recipe table making the hybrid composition concrete enough for a PM-or-finance reader. |
| Status hygiene | **Pass** | M0004: missing-frontmatter → in_progress (frontmatter injected) → done (this log). Vault file: draft → published with `published_url` annotated. Original mission body preserved (graceful prepend). |

## Notes

### MCP outage — recovered without re-architecting the workflow

The Notion MCP not being surfaced in this session was recoverable because the
draft was already in the vault as plain markdown — operator could have copy-pasted
manually had the connector reconnect failed. The skill's separation of concerns
(`vault first, then publish`) made the publish step the *only* failure mode that
needed recovery, not the whole pipeline. Lesson worth carrying forward: any
future skill that depends on a connector that can drop should keep the
"deliverable on local disk" step as a hard checkpoint before the network call.

### Truthfulness on stale claims

Two operator-stated facts were softened in the article rather than echoed:
- "SSH CLI can be used to control remote AI CLIs" → reframed as "any peer agent
  that lives in a terminal," because there is no first-class SSH connector in
  the code, only ConPTY's ability to host any shell program (which technically
  includes `ssh`). The reframing is more honest and still captures the
  operator's intent.
- "Gemma embeddings to delegate tasks" → reframed as "GBNF-constrained
  function-calling" with embeddings nowhere mentioned, because that is what the
  code actually does. Embeddings would be the wrong technical claim to put
  next to a vendor link.

The fix here was not to argue with the operator at draft time but to write the
truthful version, then surface the gap in the intent-mapping section so the
operator could compare brief and draft side by side. They approved without
flagging either softening, suggesting the reframings were the intended meaning.

### Why ~2700 words and not 1500

The brief explicitly demanded *both* a feature intro **and** an economic
argument **and** a core architectural section **and** outlook **and** reader
questions. Each is a section worth in tone, and skipping any of them would have
broken the brief. ~2700 words is roughly the same length as Part 1 (LLM Is Not
an Agent) and Part 11 (Voice-Driving Claude CLI), which set the series's
length expectation. Trimming further would either flatten the architecture
section (defeating the point of having the actor stage as the core) or omit
the recipe table (defeating the point of revision round 2).

### What was deliberately not done

- **Playwright public-render verification** — skipped because the parent AI-DOC
  has publish-on-the-site enabled and all 11 prior children render fine. WebFetch
  200 OK was sufficient evidence of public reachability. If a future pulldown
  audit ever shows the page missing from the public site, the recovery is a
  manual UI publish-toggle (no code change), not an architectural fix.
- **Cross-posting to X (W2 — reply / W10 — quote tweet)** — operator did not
  ask. The skill's W10 *series / connected post* pattern would normally
  follow a tech-doc publish (quote-card the previous Part to redirect
  audience), but is not in the M0004 brief. If operator wants the X
  announcement, it is a follow-up M-mission and the canonical minimal
  pattern (title + URL + 🔗 + quote card) is already documented in the skill.

## Next mission candidates

- **M0005 candidate** — X announcement post for Part 12 with quote card
  pointing back to Part 11. Pure psmon-doc-writer W10 execution. ~10 min.
- **M0006 candidate** — Korean translation pair for Part 12 (`Part 12 (KO)`),
  matching the Part 1 / Part 1 KO pair pattern. Lets Korean-language readers
  in Akka Labs FB group land on the same argument. psmon-doc-writer W7.
- **M0007 candidate** — README's "voice output still in development" line is
  stale (Part 11 shipped May 1, voice round-trip is live). Update README and
  the build doctor's release notes accordingly. Cheap dev mission.
- **M0008 candidate** — `Project/ZeroCommon/Llm/` survey doc — the article
  references `ILocalLlm`, `LlmGateway`, `LlmModelCatalog` and the
  `AgentToolGrammar.cs` GBNF excerpt. A short internal explainer doc (under
  `Docs/llm-arch/`) would let future devs read the inversion pattern (FSM
  drives the LLM, not the other way) without first decoding the article.

These are scoped backlog items — they become live missions only when the
operator files an `M{NNNN}-*.md` for them.
