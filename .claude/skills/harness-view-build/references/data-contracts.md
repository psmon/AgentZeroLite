# Data contracts — what the indexer matches

> Canonical reference for **filename + frontmatter constraints** that
> `Home/harness-view/scripts/build-indexes.js` enforces when scanning
> `harness/` and `Docs/`. If you write a file the indexer can't match,
> the viewer card silently shows null/inbox/empty even though the file
> exists on disk.
>
> This file is the **single source of truth** for indexer-side rules.
> Other skills (e.g. harness-kakashi-creator's tamer) MUST consult this
> contract before writing files into the scanned directories.

## Rule 1 — Mission records must start with `M{NNNN}`

**Indexer code**: `Home/harness-view/scripts/build-indexes.js:431`

```js
const idMatch = f.match(/^(M\d+)\b/);
if (!idMatch) continue;
```

The regex requires the mission ID to be **at the very start** of the
filename. Anything that prefixes the ID (timestamp, slug, etc.) is
silently skipped — the manifest's `recordFile` for that mission stays
`null` and the Missions card displays the mission as `inbox` even when
work was completed.

### Canonical filename — by operator language

| Operator language | Filename | Example |
|---|---|---|
| `language: ko` | `M{NNNN}-수행결과.md` | `M0001-수행결과.md` |
| `language: en` | `M{NNNN}-execution-result.md` | `M0004-execution-result.md` |
| Other | `M{NNNN}-{english-kebab-slug}.md` | `M0042-build-recovery.md` |

Slug after the ID may be in the operator's language (Unicode-safe in
filenames is fine), but the `M{NNNN}` prefix is non-negotiable.

### Anti-patterns that look right but break the indexer

| Filename | Why it fails |
|---|---|
| `2026-05-02-22-49-M0005-webdev-tab.md` | Timestamp prefix — `^M\d+` doesn't match |
| `M0005_수행결과.md` | Underscore separator — `\b` after digits doesn't catch underscore start |
| `mission-M0005-result.md` | `mission-` prefix — same issue as timestamp |
| `webdev-tab-M0005.md` | ID at end — won't match `^` anchor |

### Frontmatter shape (canonical)

`harness/knowledge/missions-protocol.md` "Completion log contract"
defines this. Mirror here for the indexer's own consumption:

```yaml
---
mission: M{NNNN}                       # required
title: ...                             # required
operator: ...                          # required
language: ko | en | ...                # required (drives output language)
dispatched_to: [tamer, code-coach]     # which specialists ran
status: done | partial | blocked | cancelled
started: ISO timestamp
finished: ISO timestamp
artifacts:                              # files touched
  - ...
---
```

Indexer reads `status / started / finished` into the manifest as
`recordStatus / recordStarted / recordFinished`. Missing any of these
just leaves the corresponding manifest field `null` — not fatal, but
the Missions card loses its "completed at" annotation.

## Rule 2 — Mission file frontmatter

**Indexer code**: `build-indexes.js:454-478`

Mission files (`harness/missions/M{NNNN}-{slug}.md`) are also matched
by `^(M\d+)\b`. They additionally need frontmatter for the Missions
card to display useful metadata:

```yaml
---
id: M{NNNN}                            # required, matches filename prefix
title: ...                             # required (operator's language OK)
operator: ...                          # required
language: ko | en | ...                # required
status: inbox | in_progress | done | cancelled
priority: low | medium | high          # optional
created: 2026-05-02                    # required, ISO date
---
```

Files without frontmatter still get listed (status falls back to
`inbox` if no record exists, `done` if a record is found), but
`title / operator / priority / created` all stay `null`.

## Rule 3 — Other indexer matchers (no special filename rule)

| Source | Match rule |
|---|---|
| `harness/agents/*.md` | Any `*.md` |
| `harness/knowledge/**/*.md` | Recursive `*.md` |
| `harness/engine/*.md` | Any `*.md` |
| `harness/logs/<subfolder>/*.md` | Any `*.md` — subfolder name = category tag |
| `harness/docs/v*.md` | Any `*.md`, semver-desc sort |
| `Docs/**/*.md` | Recursive tree |
| `Docs/harness/template/<skill>/SKILL.md` | Strictly `SKILL.md` filename |
| `Docs/design/*.{md,pen}` | Both extensions |

Only Rules 1 & 2 (missions) have strict filename anchoring; the rest
are tolerant.

## Rule 4 — `Home/_resources/` is gitignored

`Home/_resources/{Docs,harness}` is a CI-time mirror used by the
viewer when running on Pages. **Never `git add` this directory** —
`.gitignore:44` blocks it, but if a script unignores it the duplication
is multi-megabyte.

## Cross-skill consumers

Skills that write into the directories above MUST honor these
contracts:

- **harness-kakashi-creator** (plugin) — when its tamer agent
  dispatches a mission via "M{NNNN} 수행해", the completion log path
  is canonized by Rule 1. The skill reads `harness/agents/tamer.md`
  and `harness/knowledge/missions-protocol.md` for execution; both
  link back here so the rule has a single source of truth.
- **agent-zero-build** — release artifacts in `harness/docs/v*.md`
  follow Rule 3's semver convention.
- Any future skill that writes to `harness/missions/` or
  `harness/logs/mission-records/` — must read this file first.

## When this file changes

Update order to keep the system consistent:

1. Edit `Home/harness-view/scripts/build-indexes.js` (the regex/source of truth)
2. Edit this file to match the new behavior
3. Update `harness/knowledge/missions-protocol.md` if mission-specific rules change
4. Update `harness/agents/tamer.md` step 7 if execution procedure changes
5. Add a memory entry under `feedback_*.md` if the change is a guard against a past incident
