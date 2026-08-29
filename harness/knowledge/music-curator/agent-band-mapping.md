# Agent Band — AudioSet label → performer sprite mapping

**Owner**: music-curator
**Lifecycle**: convention — binding for any change to `Project/Plugins/agent-band/agent-band.js` `labelToPerformer()` / `labelToDance()` or to the classifier model that produces the labels
**Last updated**: 2026-08-29 (v0.24 — added the 7 rock/EDM performers + genre fallbacks + the variant-before-base ordering contract that M0031 introduced but the doc had missed)
**Related**: [ast-audioset-model-serving.md](ast-audioset-model-serving.md) (the upstream model whose top-K we consume) · [agent-band-youtube-stage.md](agent-band-youtube-stage.md) (the sibling YouTube-stage host-op contract)

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

First-match-wins regex against `label.toLowerCase()` in
`labelToPerformer()`. **Order is a contract, not a convenience** — the
electric/genre variants are matched *before* the base-instrument regex
they would otherwise be swallowed by. Change the order and a "Bass
guitar" label silently becomes an acoustic `guitar` again.

### Ordering contract (why the rows are grouped this way)

1. **Electric variants before their acoustic base.** `bass guitar` →
   `elec-bass` and `electric guitar` / `tapping (guitar` → `elec-guitar`
   must precede `\bguitar\b` → `guitar`. Likewise `electric piano` /
   `electronic organ` / `hammond organ` → `keytar` precede
   `\bpiano\b\|\borgan\b` → `piano`.
2. **Percussion variants + genre pre-empt before `\bdrum\b`.** `drum
   machine` / `beatbox` → `drum-machine`, and the genre pre-empt
   `dubstep` / `drum and bass` → `edrum`, both precede `\bdrum\b` →
   `drum` — otherwise "Drum and bass" would spawn an acoustic drummer.
3. **The `synth` lookbehind.** `(?<!speech )synthesizer` deliberately
   excludes AudioSet's "Speech synthesizer" (a TTS label) so narration
   doesn't summon a synth performer.

### Rock / EDM performers (M0031, matched first)

| Sprite | regex | Matching AudioSet labels today |
|---|---|---|
| `elec-bass` | `bass guitar` | "Bass guitar" |
| `elec-guitar` | `electric guitar\|tapping \(guitar` | "Electric guitar" / "Tapping (guitar technique)" |
| `drum-machine` | `drum machine\|beatbox` | "Drum machine" / "Beatbox" |
| `edrum` | `dubstep\|drum and bass` | "Dubstep" / "Drum and bass" (genre pre-empt — routes an electronic beat to the e-drummer before `\bdrum\b` grabs it) |
| `synth` | `(?<!speech )synthesizer\|\bsampler\b\|theremin` | "Synthesizer" / "Sampler" / "Theremin" (NOT "Speech synthesizer") |
| `keytar` | `electric piano\|electronic organ\|hammond organ` | "Electric piano" / "Electronic organ" / "Hammond organ" |
| `dj-deck` | `scratch(ing)? \(performance\|turntable` | "Scratching (performance technique)" / "Turntable" |

### Acoustic instruments

Bundled-but-currently-unmatched sprites (viola / oboe / contrabass /
tuba) have Tier 1 entries too. AudioSet at AST's `0.4593` build does
not emit those labels, but the moment any upstream model (MERT,
CLAP-music, AudioSet-v2) starts producing them the bundled sprite
takes over automatically — without those entries the Tier 2 fallback
would keep stealing the spawn.

| Sprite | regex | Matching AudioSet labels today | Activates with future model? |
|---|---|---|---|
| `cello` | `\bcello\b` | "Cello" | — |
| `viola` | `\bviola\b` | (none — AudioSet has no Viola label) | yes — any model emitting "Viola" |
| `violin` | `\bviolin\b\|\bfiddle\b` | "Violin, fiddle" | — |
| `contrabass` | `\bcontrabass\b\|\bdouble bass\b` | (none — AudioSet has no Contrabass / Double bass label) | yes — any model emitting "Contrabass" or "Double bass" |
| `harp` | `\bharp\b` AND NOT `harpsichord` | "Harp" (Harpsichord guarded out) | — |
| `guitar` | `\bguitar\b` | "Guitar" / "Acoustic guitar" / "Steel guitar, slide guitar" (electric + bass variants are pre-empted above) | — |
| `flute` | `\bflute\b` | "Flute" | — |
| `clarinet` | `\bclarinet\b` | "Clarinet" | — |
| `oboe` | `\boboe\b` | (none — AudioSet has no Oboe label) | yes — any model emitting "Oboe" |
| `horn` | `french horn\|\bhorn\b` | "French horn" | — |
| `trumpet` | `\btrumpet\b` | "Trumpet" | — |
| `trombone` | `\btrombone\b` | "Trombone" | — |
| `tuba` | `\btuba\b` | (none — AudioSet has no Tuba label) | yes — any model emitting "Tuba" |
| `piano` | `\bpiano\b\|\borgan\b` | "Piano" / "Organ" (electric piano / electronic organ are pre-empted to `keytar` above) | — |
| `drum` | `\bdrum\b\|cymbal\|tom-tom\|hi-hat\|tabla\|\bgong\b` | "Drum" / "Drum kit" / "Snare drum" / "Bass drum" / "Drum roll" / "Gong" (drum machine → `drum-machine`, drum-and-bass → `edrum`) | — |

### Collapse rule

When multiple labels in one tick map to the same sprite id, the
**max** score wins — the resulting score drives both the gate decision
and the play/idle state. Example: `"Guitar" 0.40` + `"Acoustic guitar"
0.30` co-occurring in top-K → one `guitar` performer at score 0.40.

## Vocals — gender-aware round-robin

The four vocal sprites are visually distinguishable as two male / two
female characters. v0.3.0 splits the pool accordingly so AST's
"Male singing" / "Female singing" labels show up as the matching
silhouette, and gender-neutral labels default to the female pool (the
operator-picked default; matches the more common pop / OST register).

### Sprite gender (visually verified)

| Sprite | Identity |
|---|---|
| `vocal-1` | **Female** — blonde hair, blue dress, white headpiece |
| `vocal-2` | **Male** — dark hair, red coat, suit |
| `vocal-3` | **Female** — pink hair, red dress with rose accessory |
| `vocal-4` | **Male** — silver hair, purple coat |

### Pools + cursors

| Pool | Sprites | Cursor variable | Resets when |
|---|---|---|---|
| Female | `vocal-1`, `vocal-3` | `femaleCursor` | Every tick (start of `upsertPerformersFromLabels`) |
| Male | `vocal-2`, `vocal-4` | `maleCursor` | Every tick |

Both cursors advance only within their own pool. A "Male singing +
Singing" co-occurrence in one tick produces vocal-2 (male, from male
label) and vocal-1 (female, from neutral default) — not both pulling
from the same cursor.

### Routing rules (in match order)

| AudioSet labels | Pool | Notes |
|---|---|---|
| Anything matching `/male sing\|\bman sing\b/` AND NOT containing `female` | **Male** (vocal-2 → vocal-4 round-robin) | The `!female` guard prevents "Female singing" from sliding into the male pool because "female" contains "male" |
| Anything matching `/female sing\|\bwoman sing\b/` | **Female** (vocal-1 → vocal-3 round-robin) | — |
| Anything matching `/sing(ing)?\|choir\|vocal\|chant\|yodel\|rapping\|hum/` (gender-neutral) | **Female** (vocal-1 → vocal-3 round-robin) | Operator-picked default. Switch the default by flipping the fallback pool here. |

### Why fan-out instead of single sticky vocal

"Singing" + "Choir" + "Vocal music" co-occurring in one top-K tick is
the model saying "I'm seeing multiple vocal characters" — fan-out
makes the stage read as an ensemble rather than one confused performer.
Stable label order across ticks → stable cursor advancement → stable
sprite assignment → no flicker between vocal-1 and vocal-3.

Vocals are always rendered in **stage center** (separate from
instrument L/R wings); instruments don't push vocals around.

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

### Genre fallbacks (M0031) — route a *style* to a performer

Tested after the instrument-category fallbacks. When the top-K carries
a genre/style label but no instrument, these keep the rock/EDM
performers reachable on music where AudioSet never emits a specific
instrument (common for produced electronic / rock tracks).

| Sprite | regex | Matching AudioSet labels |
|---|---|---|
| `dj-deck` | `house music\|techno\|electronic dance\|dance music\|\bdisco\b\|hip hop` | "House music" / "Techno" / "Electronic dance music" / "Dance music" / "Disco" / "Hip hop music" |
| `synth` | `electronica\|electronic music\|ambient music\|trance music\|new-age music` | "Electronica" / "Electronic music" / "Ambient music" / "Trance music" / "New-age music" |
| `elec-bass` | `\bfunk\b` | "Funk" |
| `trumpet` | `\bska\b` | "Ska" |
| `elec-guitar` | `\brock\b\|heavy metal\|grunge` | "Rock music" / "Heavy metal" / "Grunge" |

> These overlap the dance troupe's genre routing (see "Genre → style
> mapping" below) on purpose — the *same* "House music" label both spawns
> a `dj-deck` band performer (row 1) and a `kpop` dancer (row 2). They are
> independent registries; a genre can populate both rows at once.

## Currently unreachable sprites (forward-compat already wired)

Bundled, **Tier 1 regex entries present** as of v0.3.0, but never
spawned by AST AudioSet today because the model doesn't emit a
matching label. The regex is there so the moment a richer upstream
model arrives the sprite lights up — no code change required, just
re-test in case the new label string differs.

| Sprite | Tier 1 regex (live) | Why unreachable today | What activates it |
|---|---|---|---|
| `viola` | `\bviola\b` | AudioSet has no "Viola" label — "Violin, fiddle" is the only bowed-string-with-fingerboard category at sub-class granularity → currently `violin` sprite covers it via the Tier 1 "Violin, fiddle" match | Upstream model that distinguishes viola (MERT-large, CLAP-music, Marsyas) |
| `oboe` | `\boboe\b` | AudioSet has no "Oboe" label — "Woodwind instrument" is the only woodwind parent below "Flute" / "Clarinet" → currently `flute` sprite covers via the Tier 2 woodwind fallback | Same — any finer-grained woodwind taxonomy |
| `contrabass` | `\bcontrabass\b\|\bdouble bass\b` | AudioSet has no "Contrabass" / "Double bass" label ("Bass guitar" exists but that's a different instrument that already routes to `guitar`) → currently `violin` sprite covers via the Tier 2 bowed-string fallback | Finer-grained bowed-string taxonomy |
| `tuba` | `\btuba\b` | AudioSet has no "Tuba" label — "Brass instrument" is the only brass parent below "French horn" / "Trumpet" / "Trombone" → currently `trumpet` sprite covers via the Tier 2 brass fallback | Finer-grained brass taxonomy |

> **Tier interaction**: Tier 1 matches always win, so when the future
> label arrives the bundled sprite takes over from whatever Tier 2 was
> filling in. No "graveyard" cleanup needed — flipping a model literally
> activates four sprites at once.

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
- **Tests**: `tools/agent-band-tests/` — a zero-dependency `node --test`
  suite (`labelToPerformer.test.mjs`) pins this table's mapping. The
  regex lives inline in the plugin IIFE (not exported), so the test file
  keeps a **hand-aligned reference copy** — the same mirror convention
  the repo already uses for `parseVideoId` (JS) ↔ `ParseYouTubeId` (C#).
  When you edit the regex in `agent-band.js`, update the reference copy
  and this table together; the test then guards the mapping semantics.
- **Invariant for any future model swap**: keep this table, the reference
  copy in `tools/agent-band-tests/`, and the regex updated in lock-step.
  If you add a new sprite, add a row to the "Tier 1" table (and the
  "unreachable sprites" graveyard when it ships before the model that
  activates it lands) and a case to the test suite.

## Dance troupe (v0.4.0) — row 2

A second registry runs in parallel to the performer registry, gated on
genre + mood labels rather than instrument labels. Up to **3 dancers**
on stage (row 2) simultaneously; they're rendered **before** the band
so the band sits in front (row 1 stays the visual focus).

### Score gates

| Constant | Value | Role |
|---|---|---|
| `DANCE_PRESENT` | **0.07** | Aggregated style score required to first spawn |
| `DANCE_KEEP` | **0.03** | Hysteresis lower bar once on stage |
| `DANCE_PERSIST_TICKS` | **6** (≈9 s) | Unseen-genre ticks before fade-out begins |
| `DANCE_FADE_MS` | **800** | Fade-out duration |
| `MAX_DANCERS` | **3** | Row 2 population cap |

Multiple matching labels **stack** per style — `selectDanceStyles()`
sums scores across all labels that route to the same style. This lets
"Hip hop music 0.18" + "Rapping 0.15" + "Trap music 0.08" reinforce
the hiphop dancer at 0.41 instead of competing for the slot.

### Genre → style mapping

| Style | regex | Matching AudioSet labels |
|---|---|---|
| `hiphop` | `hip hop\|hiphop\|\brap\b\|rapping\|trap music` | "Hip hop music" / "Rapping" / "Trap music" (Rapping is technically a speech label but operator decision: rap *is* hip-hop dance) |
| `waacking` | `\bdisco\b\|\bfunk\b\|salsa\|latin america` | "Disco" / "Funk" / "Salsa music" / "Music of Latin America" (waacking descended from 70s disco-funk; salsa shares the percussive groove) |
| `jazz` | `\bjazz\b\|swing music\|\bblues\b\|soul music\|rhythm and blues\|gospel` | "Jazz" / "Swing music" / "Blues" / "Soul music" / "Rhythm and blues" / "Gospel music" |
| `ballet` | `classical\|\bopera\b\|symphony\|\borchestra\b\|chamber music\|new-age\|wedding music\|tender music\|soundtrack music` | "Classical music" / "Opera" / "Symphony" / "Orchestra" / "Chamber music" / "New-age music" / "Wedding music" / "Tender music" / "Soundtrack music" |
| `kpop` | `pop music\|electronic\|electronica\|\bedm\b\|electronic dance\|dance music\|house music\|techno\|dubstep\|trance` | "Pop music" / "Electronic music" / "Electronica" / "EDM" / "Electronic dance music" / "Dance music" / "House music" / "Techno" / "Dubstep" / "Trance music" |
| `cheer` | `cheering\|exciting music\|happy music\|christmas music` | "Cheering" / "Exciting music" / "Happy music" / "Christmas music" |

### Row 2 layout

| Constant | Value | Note |
|---|---|---|
| `DANCE_MAX_W` | **120 px** | Smaller than band's 140 to feel "behind" |
| `DANCE_MIN_W` | **60 px** | |
| `DANCE_GAP` | **16 px** | Slightly wider than band's 14 for breathing room |
| `DANCE_BASE_Y` | **0.58 × h** | Baseline higher than band's 0.94 |
| `DANCE_TARGET_H` | **0.40 × h** | Smaller than band's 0.55 |

Z-order: **background → row 2 dancers → row 1 band → spectrum overlay**.
Dancers drawn first; band drawn on top (the overlap region from y≈0.39
to y≈0.58 will show the dancers' lower bodies hidden behind the band's
heads — the "back chorus line" effect).

Each dancer carries a random `framePhase` (0..5) so 2-3 simultaneous
dancers don't sync to the same beat — they look like a troupe, not a
hivemind. 6-frame animation at 8 FPS = 750 ms loop per cycle.

### Sprite layout on disk

```
Project/Plugins/agent-band/assets/dancers/
├── ballet/    ballet-1.png .. ballet-6.png
├── cheer/     cheer-1.png .. cheer-6.png
├── hiphop/    hiphop-1.png .. hiphop-6.png
├── jazz/      jazz-1.png .. jazz-6.png
├── kpop/      kpop-1.png .. kpop-6.png
└── waacking/  waacking-1.png .. waacking-6.png
```

Same green chroma-key background as the band sprites — the existing
`chromaKey()` routine handles them at load time. **6 styles × 6 frames
= 36 PNGs, ~16 MB**. Plugin total: 195 files (under the 200 install
limit), ~89 MB.

### What if no dance label hits?

If no genre/mood label scores above `DANCE_PRESENT` in a tick, **no
dancer spawns** — the stage is band-only. Once a dancer is on stage,
`DANCE_KEEP` hysteresis keeps them while the genre stays in top-K at
any score. ~9 s of silence on the genre side triggers fade-out.

## Sprite assets — two layouts coexist (v0.5.0)

### Band (v0.5.0+) — TexturePacker sheet + JSON

```
Project/Plugins/agent-band/assets/sprites/
├── violin/
│   ├── idle.png    (sheet — 808×208, 4 × 192×192 frames horizontally)
│   ├── idle.json   (TexturePacker frame rects)
│   ├── play.png
│   └── play.json
├── viola/ … (same shape)
└── … (19 performers total)
```

**Source format** (`idle.json`):
```json
{
  "frames": {
    "violin_idle_0.png": {
      "frame": { "x": 8, "y": 8, "w": 192, "h": 192 },
      "duration": 120
    },
    "violin_idle_1.png": { "frame": { "x": 208, "y": 8, "w": 192, "h": 192 }, "duration": 120 },
    "violin_idle_2.png": { … },
    "violin_idle_3.png": { … }
  },
  "meta": { "image": "idle.png", "size": { "w": 808, "h": 208 }, "scale": "1" }
}
```

`ensureSpriteSet()` fetches the PNG and JSON in parallel, sorts frame
keys lexically (so `_0.png` < `_1.png` < … is the playback order), and
caches `{ sheet: HTMLImageElement, frames: [{x,y,w,h}, …] }`.
`drawPerformer()` uses the 9-arg form of `drawImage` to blit the sub-rect
without copying pixels:

```js
ctx.drawImage(set.sheet, fr.x, fr.y, fr.w, fr.h, dx, dy, drawW, drawH);
```

These sheets ship with proper alpha channels — **no chroma-key step**.
Frame count is derived from the JSON, not hardcoded; future performers
that ship 6 or 8 frames work without code changes.

Total band assets: 19 performers × 2 states × (PNG + JSON) =
**76 files / ≈1.5 MB** (vs the v0.4 layout's 152 PNGs / ≈65 MB —
~40× shrink). Install-limit pressure (max 200 files) is now mostly
from the dance assets.

### Dance (v0.6.0+) — single master sheet + grid index

```
Project/Plugins/agent-band/assets/dancers/
└── _master/
    ├── dance-master.png   (~5.6 MB, 4848 × 1248 — 6 rows × 6 cols)
    └── index.json         catalogue of every character with row/col + frame count
```

**Layout**:
- Rows = dance styles (fixed order in code's `DANCE_STYLE_ROW`):
  `kpop` = 0, `hiphop` = 1, `jazz` = 2, `ballet` = 3, `cheer` = 4, `waacking` = 5
- Columns = 6 distinct sub-characters per style (e.g. `hiphop-1` …
  `hiphop-6` are 6 different hip-hop dancers, all properly animated)
- Each **cell** = 808 × 208 px containing 4 animation frames of
  192 × 192 each, laid out horizontally with 8 px padding (same as the
  band-sprite cell shape — single mental model across both rows)
- Master PNG has a proper alpha channel — **no chroma-key**

**Loader** (`ensureDanceMaster()`): fires the PNG and JSON fetches in
parallel the first time any dancer needs to draw. Cached as module
singletons (`danceMaster`, `danceIndex`). Until both resolve, the
dancer is already in the registry but `drawDancer` skips its slot.

**Renderer** (`drawDancer`):
```
srcX = characterIdx * 808 + 8 + frameIdx * 200
srcY = styleRow      * 208 + 8
ctx.drawImage(danceMaster, srcX, srcY, 192, 192, dx, dy, drawW, drawH)
```

`characterIdx` is randomly picked once at spawn (0..5) — successive
re-spawns of the same style cycle through different dancers across
sessions. `framePhase` (0..3) is also randomised so simultaneous
dancers desync from the beat. 4 frames × 8 FPS = 500 ms loop.

**File count contribution**: dance assets are now just 2 files
(`dance-master.png` + `index.json`), down from 36 loose PNGs. Plugin
total: 85 files / ~10 MB — comfortably below the 200-file install
limit and dwarfed by the band's chunky old layout.

## Change-trigger list

Re-read this doc when you are about to:

- Swap the upstream classifier (AST → MERT / CLAP / wav2vec2-music)
- Add a new sprite asset to `Project/Plugins/agent-band/assets/sprites/`
- Tune `SCORE_PRESENT` / `SCORE_KEEP` / `SCORE_ACTIVE` / `PERSIST_TICKS`
- Add a regex line to `labelToPerformer()`
- Change which sprite represents a parent-category fallback
- Change vocal gender defaults (currently: gender-neutral → female pool)
- Add new vocal sprites — they need to be slotted into
  `VOCAL_FEMALE` or `VOCAL_MALE` arrays explicitly, with the visual
  gender verified against the actual sprite art
- Add a new dance style — needs a new folder under
  `assets/dancers/`, a `labelToDance()` regex line, and a row-2 layout
  consideration (3-style cap)
- Migrate dance sprites again — current master is a flat 6×6 grid.
  If a future asset bundle splits styles into separate sheets or
  adds new actions (e.g. an "idle" stance distinct from "dance"),
  generalise the loader the same way the band side does it (per-state
  sheet + JSON instead of one master)
- Add a new band performer / state — drop a folder with `idle.png`,
  `idle.json`, `play.png`, `play.json` under `assets/sprites/{id}/`;
  no code change needed (loader reads frame count from JSON)
- Change genre→style routing (e.g. send Funk to jazz instead of
  waacking) — update `labelToDance()` order and the table above
- Tune `DANCE_PRESENT` / `DANCE_KEEP` / `MAX_DANCERS`
