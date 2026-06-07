# Agent Band — AudioSet label → performer sprite mapping

**Owner**: music-curator
**Lifecycle**: convention — binding for any change to `Project/Plugins/agent-band/agent-band.js` `labelToPerformer()` or to the classifier model that produces the labels
**Last updated**: 2026-06-07
**Related**: [ast-audioset-model-serving.md](ast-audioset-model-serving.md) (the upstream model whose top-K we consume)

## Why this doc exists

The Agent Band plugin (M0025) turns AST AudioSet's top-K classification
output into a live stage of performer sprites. Two layers — the score
gating and the regex mapping — together decide *which sprite shows up*,
*how long it stays*, and *whether it animates "playing" or just "idling"*.
Tuning either layer in isolation breaks the other. This doc is the
single source of truth for both, plus a "what's not mapped" section that
tells music-curator exactly which sprites become reachable when the
upstream model gets upgraded.

## Live pipeline (one tick ≈ 1.5 s)

```
AST AudioSet top-K labels (sigmoid scored)
        ↓
labelToPerformer(label) → sprite id        ← Tier 1 specific, Tier 2 category, vocal fan-out
        ↓
collapse: same sprite id → max score wins  ← "Guitar 0.4" + "Acoustic guitar 0.3" = guitar @ 0.4
        ↓
score gate (hysteresis)                    ← entry vs stay thresholds differ
        ↓
performer registry (max 8 on stage)
        ↓
state → play (score ≥ 0.12) | idle (score ≥ 0.05)
        ↓
~12 s unseen → fade out → evict
```

## Score thresholds (3-tier hysteresis)

| Constant | Value | Role |
|---|---|---|
| `SCORE_PRESENT` | **0.05** | First-time spawn threshold |
| `SCORE_KEEP` | **0.025** | Already-on-stage retention threshold (hysteresis lower bar) |
| `SCORE_ACTIVE` | **0.12** | At/above → 4-frame `play` animation; below → `idle` |
| `PERSIST_TICKS` | **8** (≈12 s) | Tick count of "not seen in top-K" before fade-out begins |
| `FADE_MS` | **700** | Fade-out duration once fading flag flips |
| `MAX_PERFORMERS` | **8** | Stage population cap (evict a fading performer to admit a new one) |

> AST AudioSet sigmoid scores typically hover **0.05–0.25** even for
> clean hits because the model is multi-label across 527 classes — gates
> tuned for "winner-takes-all" softmax would never spawn anything. Don't
> raise these without re-measuring against the active model.

## Tier 1 — specific instruments

First-match-wins regex against `label.toLowerCase()`. Order matters
where labels can co-occur (e.g. `\bcello\b` is tested before
`\bviolin\b` would otherwise be — they're orthogonal here but the
principle generalizes).

| Sprite | regex | Matching AudioSet labels |
|---|---|---|
| `cello` | `\bcello\b` | "Cello" |
| `violin` | `\bviolin\b\|\bfiddle\b` | "Violin, fiddle" |
| `harp` | `\bharp\b` AND NOT `harpsichord` | "Harp" (Harpsichord guarded out) |
| `guitar` | `\bguitar\b` | "Guitar" / "Electric guitar" / "Acoustic guitar" / "Bass guitar" / "Steel guitar, slide guitar" / "Tapping (guitar technique)" |
| `flute` | `\bflute\b` | "Flute" |
| `clarinet` | `\bclarinet\b` | "Clarinet" |
| `horn` | `french horn\|\bhorn\b` | "French horn" |
| `trumpet` | `\btrumpet\b` | "Trumpet" |
| `trombone` | `\btrombone\b` | "Trombone" |
| `piano` | `\bpiano\b` | "Piano" / "Electric piano" |
| `drum` | `\bdrum\b\|cymbal\|tom-tom\|hi-hat\|tabla` | "Drum" / "Drum kit" / "Drum machine" / "Snare drum" / "Bass drum" / "Drum roll" / "Drum and bass" |

### Collapse rule

When multiple labels in one tick map to the same sprite id, the
**max** score wins — the resulting score drives both the gate decision
and the play/idle state. Example: `"Guitar" 0.40` + `"Acoustic guitar"
0.30` co-occurring in top-K → one `guitar` performer at score 0.40.

## Vocals — round-robin fan-out

```regex
/sing(ing)?|choir|vocal|chant|yodel|rapping|hum/
```

Matching AudioSet labels: "Singing" / "Choir" / "Male singing" /
"Female singing" / "Child singing" / "Synthetic singing" / "Vocal
music" / "Chant" / "Yodeling" / "Rapping" / "Humming".

Vocals are mapped through a per-tick **round-robin cursor** across
`vocal-1 → vocal-2 → vocal-3 → vocal-4` instead of collapsing onto a
single id. Within a tick, each successive matching label gets the next
vocal slot.

| Behaviour | Why |
|---|---|
| Fan-out across 4 sprites instead of one sticky vocal | "Singing" + "Choir" + "Vocal music" co-occurring is the model saying "I'm seeing multiple vocal characters" — fan-out makes the stage read as an ensemble rather than one confused performer |
| Cursor resets at the start of every tick | Stable label order across ticks → stable vocal-N assignment → no slot churn → no flicker |
| Vocals always rendered in stage center (separate from instrument L/R wings) | Mirrors how a real band stands; instruments don't push vocals around |

## Tier 2 — parent-category fallbacks

Tested only when no Tier 1 specific instrument matched. AST's top-K
often features the **parent category** higher than the specific
sub-class on mixed audio (e.g. an orchestral piece returns "Bowed
string instrument" at 0.18 while "Cello" sits at 0.07).

| Sprite | regex | Matching AudioSet parent labels |
|---|---|---|
| `violin` | `bowed string\|orchestra\|symphony\|chamber music` | "Bowed string instrument" / "Orchestra" / "Symphony" / "Chamber music" |
| `guitar` | `plucked string` | "Plucked string instrument" |
| `flute` | `woodwind\|wind instrument` | "Woodwind instrument" / "Wind instrument" |
| `trumpet` | `\bbrass\b` | "Brass instrument" |
| `piano` | `keyboard \(musical\)` | "Keyboard (musical)" |
| `drum` | `percussion` | "Percussion" |

> The Tier 2 sprite is a *representative* — violin stands in for all
> bowed strings, guitar for all plucked. When the upstream model gets
> upgraded to one with proper viola/oboe/contrabass/tuba sub-classes,
> the fallback gracefully steps aside in favour of the Tier 1 match.

## Currently unreachable sprites

Bundled but never spawned by the current model:

| Sprite | Why unreachable | What needs to change |
|---|---|---|
| `viola` | AudioSet has no "Viola" label — "Violin, fiddle" is the only bowed-string-with-fingerboard category at sub-class granularity | Upstream model that distinguishes viola from violin (MERT-large, CLAP-music) |
| `oboe` | AudioSet has no "Oboe" label — "Woodwind instrument" is the only woodwind parent below "Flute" / "Clarinet" | Same — finer-grained woodwind taxonomy |
| `contrabass` | AudioSet has no "Contrabass" / "Double bass" label at this granularity ("Bass guitar" exists but that's a different instrument that goes to `guitar` already) | Finer-grained bowed-string taxonomy |
| `tuba` | AudioSet has no "Tuba" label — "Brass instrument" is the only brass parent below "French horn" / "Trumpet" / "Trombone" | Finer-grained brass taxonomy |

When the AST model gets replaced, add the regex line to Tier 1 (with
the new specific label) — the sprite then takes over from the Tier 2
fallback automatically.

## Stage layout (informs *where* the mapped sprite renders)

```
[outer-left]            ← strings (low ORDER_RANK)
   [inner-left]         ← plucked
      [center]          ← vocals (always reserved)
   [inner-right]        ← woodwinds / brass / keys
[outer-right]           ← percussion (highest ORDER_RANK)
```

`ORDER_RANK`: violin/viola/cello/contrabass = 10s; guitar/harp = 20s;
flute/clarinet/oboe = 30s; horn/trumpet/trombone/tuba = 40s; piano =
50; drum = 60. Vocals are filtered out and placed in the center
regardless of instrument count.

Sprite width is capped at **140 px** (min 70 px) with **≥14 px gap**
so a single performer never blows up to fill the screen and crowded
stages never overlap.

## Source of truth + invariant

- **Code**: `Project/Plugins/agent-band/agent-band.js` →
  `labelToPerformer(label)` (the regex tiers) and the score/persist
  constants at the top of the IIFE.
- **Tests**: none yet — the regex tiers are pure functions and would
  benefit from a small JS unit suite the next time the plugin folder
  grows tooling (see follow-ups in M0025 mission log).
- **Invariant for any future model swap**: keep this table updated in
  lock-step with the regex. If you add a new sprite, add a row to both
  the "Tier 1" table and the "unreachable sprites" graveyard once it
  ships but before the model that activates it lands.

## Change-trigger list

Re-read this doc when you are about to:

- Swap the upstream classifier (AST → MERT / CLAP / wav2vec2-music)
- Add a new sprite asset to `Project/Plugins/agent-band/assets/sprites/`
- Tune `SCORE_PRESENT` / `SCORE_KEEP` / `SCORE_ACTIVE` / `PERSIST_TICKS`
- Add a regex line to `labelToPerformer()`
- Change which sprite represents a parent-category fallback
