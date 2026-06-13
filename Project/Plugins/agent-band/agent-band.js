// agent-band.js — M0025 Agent Band plugin.
//
// v0.11 changelog:
//   • climax staging by LOUDNESS — the idol group's play/dance vs idle is
//     now gated on volume (mean spectrum energy), not the AST label score:
//       - normal volume → "일반노래": only the main vocal performs; the
//         backup idols hold their idle (normal-singing) sheet.
//       - loud volume   → climax: the whole group performs together.
//     Hysteresis (CLIMAX_VOL_ENTER > EXIT) plus a slightly-high ENTER keeps
//     the calmer "일반노래" state held a bit longer / more often. The
//     decision is per-frame in performState(); both sheets are preloaded so
//     the idle↔play flip is instant.
//
// v0.10 changelog:
//   • main vocal is `vocal-ex` — the idol group's lead is now a dedicated
//     singer (idle+play sheets: she SINGS, doesn't dance). She holds slot 0
//     of VOCAL_FEMALE so she's always dead-center; vox7-1..7 are the
//     backup dancers fanning out around her. Group pool is now 8.
//   • row-2 dance troupe DISABLED (DANCE_TROUPE_ENABLED=false) pending a
//     quality pass. The troupe code and the dance-master assets are kept
//     intact — flip the flag to bring it back once the sprites are re-tuned.
//
// v0.9 changelog:
//   • idol group staging — the female pool is no longer a soloist that
//     fans out only when multiple distinct vocal labels co-occur. It's now
//     a GROUP that builds around a fixed lead:
//       - first female recognition → main vocal (vox7-1), dead-center.
//       - while the voice keeps coming, the group grows one member per
//         tick, fanning out alternately right/left around the lead. The
//         richer the signal (sustained presence + co-occurring genres +
//         explicit "Female singing"), the larger the target group.
//       - when the voice stops, it shrinks one member per tick until only
//         the (then fading) main vocal remains.
//     New: femaleVocalSignal() / countGenres() / upsertIdolGroup() drive
//     the size; centerVocals() keeps the lead centered while members
//     alternate L/R. MAX_PERFORMERS raised to fit a full group.
//
// v0.8 changelog:
//   • idol vocal roster — the female vocal pool is replaced by seven new
//     higher-quality idol singers (vox7-1 … vox7-7). Unlike the old
//     band sprites (idle + play), these idols sing AND dance: their
//     resting sheet is `idle`, and when a vocal label clears
//     SCORE_ACTIVE their active sheet is `dance` (not `play`). The
//     play→dance file remap is centralised in stateSheet(); everything
//     else (fps, glow, bob) keeps the existing 'play' logical state.
//   • idols are first-class vocals — isVocal() recognises them so the
//     stage layout keeps them in the center main-vocal area alongside
//     any remaining vocal-* singers, never the instrument wings.
//   • the old female sprites (vocal-1 / vocal-3) are retired; the male
//     pool (vocal-2 / vocal-4) is unchanged.
//
// v0.7 changelog:
//   • pitch-driven note particle effect — soft vertical streaks
//     rise from the stage baseline and fade out as they ascend.
//     X position maps to a spectrum bar (low pitch ← left, high ← right,
//     piano-keyboard style), color picks up the same cyan→magenta
//     gradient as the spectrum bars below. Tuned for "은은한" feel —
//     per-bar cooldown + probabilistic emission so even loud passages
//     stay sparse rather than turning into a particle storm.
//
// v0.6 changelog:
//   • dance: migrated to a single 5.6 MB master sheet (assets/dancers/
//     _master/dance-master.png) + index.json. The master is a 6×6 grid
//     where rows are styles (kpop / hiphop / jazz / ballet / cheer /
//     waacking) and columns are 6 distinct sub-characters per style.
//     Each cell is 808×208 holding 4 × 192×192 frames laid out
//     horizontally with 8 px padding — same shape as the band cells.
//     One image fetch covers every dancer; the per-style PNGs from v0.5
//     (36 loose files) are gone.
//   • Frame cycling is back — these are proper animation sheets.
//     Each new dancer picks a random characterIdx 0..5 and a random
//     framePhase 0..3 at spawn for visual variety; subsequent ticks
//     keep that character but advance frames at DANCE_FPS.
//
// v0.5 changelog:
//   • new band sprite system — TexturePacker-style sheet+JSON per
//     performer (assets/sprites/{id}/{idle|play}.{png,json}). One ~50KB
//     sheet per state replaces the old 4 loose PNGs. Sheets ship with a
//     proper alpha channel, so the runtime chroma-key step is skipped
//     for band sprites (still used for dance sprites until they're
//     migrated too).
//   • dance: each spawned dancer now picks a single static frame at
//     spawn and stays on it for its lifetime — the current dance
//     assets are 6 distinct characters per style, not animation
//     frames, so cycling made the character appear to morph. Once
//     proper dance animation sheets arrive, restore the cycle.
//
// v0.4 changelog:
//   • dance troupe (back row) — AudioSet genre + mood labels drive a
//     second row of dancers behind the band. Six styles bundled
//     (ballet / cheer / hiphop / jazz / kpop / waacking), each a
//     6-frame animation loop. Genre→style mapping:
//       hiphop   ← Hip hop music / Rapping / Trap
//       waacking ← Disco / Funk / Salsa / Latin music
//       jazz     ← Jazz / Blues / R&B / Soul / Swing / Gospel
//       ballet   ← Classical / Opera / Symphony / Orchestra / New-age /
//                  Wedding music / Tender music / Soundtrack music
//       kpop     ← Pop / Electronic / EDM / Dance music / House /
//                  Techno / Dubstep / Trance / Electronica
//       cheer    ← Cheering / Exciting music / Happy music / Christmas
//   • Up to 3 dancers on stage simultaneously, picked by aggregated
//     score per style (multiple matching labels stack).
//
// v0.3 changelog:
//   • gender-aware vocal pools — Male singing → vocal-2/vocal-4, Female
//     singing → vocal-1/vocal-3, ambiguous labels (Singing / Choir /
//     Vocal music) default to the female pool
//   • Tier 1 gains explicit regex for viola / oboe / contrabass /
//     double-bass / tuba so the bundled sprites for those instruments
//     light up the moment any upstream model emits a matching label
//   • drum regex picks up gong (AudioSet's "Gong" label)
//
// v0.2 (chroma-key + realtime spectrum + hysteresis):
//   • runtime chroma-key on sprite load (assets/sprites/*.png have a
//     uniform bright-green background; keyed onto an off-screen canvas
//     once, drawImage'd zero-cost thereafter)
//   • host emits 30 Hz `music.spectrum` events independent of the slow
//     1.5 s AST tick — bars feel real-time
//   • SCORE_PRESENT + SCORE_KEEP hysteresis so AudioSet's low sigmoid
//     scores still spawn performers, and once a performer is on stage
//     it stays unless silence dominates
//   • parent-category fallbacks ("Plucked string instrument" / "Brass"
//     / …) when AST emits the parent above the specific sub-class
//   • stage layout: vocals reserved in canvas center, instruments split
//     into L/R wings sorted by ORDER_RANK, sprite width capped so a
//     single performer doesn't fill the screen
//   • asymmetric attack/release lerp on the bars (fast snap up, smooth
//     decay) so even between 1.5 s ticks the visualizer stays alive

(function () {
  'use strict';

  // ── Tunables ─────────────────────────────────────────────────────────
  const SPRITE_BASE     = 'assets/sprites/';
  const STAGE_BASE      = 'assets/stages/';
  const DANCER_BASE     = 'assets/dancers/';
  const DANCE_MASTER_PNG  = `${DANCER_BASE}_master/dance-master.png`;
  const DANCE_MASTER_JSON = `${DANCER_BASE}_master/index.json`;
  const PLAY_FPS        = 9;
  const IDLE_FPS        = 4;
  // Band sprites: actual frame count is read from each sheet's JSON so
  // the loader doesn't care if a future performer ships 4, 6, or 8
  // frames.
  //
  // Dance master sheet layout (v0.6+):
  //   • single PNG, 6 rows × 6 cols of 808×208 cells
  //   • each cell = 4 frames horizontally, 192×192, 8 px padding
  //   • rows index: kpop 0 / hiphop 1 / jazz 2 / ballet 3 / cheer 4 / waacking 5
  //
  // v0.10: the row-2 dance troupe is DISABLED pending a quality pass. The
  // code + the dance-master assets are kept intact (not deleted) so the
  // troupe can be re-enabled by flipping this single flag once the sprites
  // are re-tuned. While false: no dancers spawn and row 2 isn't rendered.
  const DANCE_TROUPE_ENABLED = false;
  const DANCE_FRAMES         = 4;
  const DANCE_FPS            = 8;
  const DANCE_CHARS_PER_STYLE = 6;
  const DANCE_CELL_W         = 808;
  const DANCE_CELL_H         = 208;
  const DANCE_FRAME_W        = 192;
  const DANCE_FRAME_H        = 192;
  const DANCE_FRAME_PAD      = 8;
  const DANCE_FRAME_STRIDE   = DANCE_FRAME_W + DANCE_FRAME_PAD; // 200
  const DANCE_STYLE_ROW = {
    kpop: 0, hiphop: 1, jazz: 2, ballet: 3, cheer: 4, waacking: 5,
  };

  // Dance gating — genre labels in AudioSet typically score 0.10–0.30
  // even for confident hits (and labels in the same family stack via
  // selectDanceStyles), so the entry threshold is a touch higher than
  // the instrument gate. Hysteresis works the same way.
  const DANCE_PRESENT   = 0.07;
  const DANCE_KEEP      = 0.03;
  const DANCE_PERSIST_TICKS = 6;   // ~9 s of unseen labels before fade out
  const DANCE_FADE_MS   = 800;
  const MAX_DANCERS     = 3;

  // Score gating — tuned against AST AudioSet sigmoid distributions which
  // typically hover 0.05–0.25 even for clean hits (multilabel + 527 classes).
  // PRESENT = first-time spawn threshold. KEEP = stays-on-stage threshold
  // (hysteresis — once a performer is up, it takes less to keep them).
  // ACTIVE = play-animation threshold; below that they idle.
  const SCORE_ACTIVE    = 0.12;
  const SCORE_PRESENT   = 0.05;
  const SCORE_KEEP      = 0.025;
  const PERSIST_TICKS   = 8;   // ~12 s of unseen labels before fade out
  const FADE_MS         = 700;
  const MAX_PERFORMERS  = 12;  // room for the full 8-member idol group + a few instruments

  // Layout — sprite width is capped so 1 performer doesn't blow up to fill
  // the whole canvas; the unused space stays as empty stage on the sides.
  const MAX_SPRITE_W    = 140;
  const MIN_SPRITE_W    = 70;
  const MIN_GAP         = 14;
  const STAGE_BASE_Y    = 0.94;  // ground line as fraction of canvas height
  const SPRITE_TARGET_H = 0.55;  // sprite height as fraction of canvas height

  // Spectrum visual config.
  const SPEC_GRADIENT_FROM = [0x00, 0xe5, 0xff];
  const SPEC_GRADIENT_TO   = [0xff, 0x2d, 0x95];
  const SPEC_ATTACK  = 0.40;   // bars going UP — fast snap
  const SPEC_RELEASE = 0.10;   // bars going DOWN — smooth decay

  // ── Climax gating (v0.11) ────────────────────────────────────────────
  // Performance intensity is driven by LOUDNESS (mean spectrum energy),
  // not the AST label score:
  //   • normal volume  → "일반노래" — only the MAIN VOCAL performs
  //     (play/dance); the backup idols hold their idle (normal-singing)
  //     sheet.
  //   • loud volume     → climax — the whole group performs together.
  // Hysteresis (ENTER > EXIT) keeps it from flickering, and the ENTER
  // threshold is set a touch high so the calmer "일반노래" state is held a
  // little longer / shows up a little more often than a bare midpoint
  // would give. volumeLevel is an eased 0..1 loudness read each frame.
  const CLIMAX_VOL_ENTER = 0.30;  // mean smoothed-spectrum level to ENTER climax
  const CLIMAX_VOL_EXIT  = 0.20;  // drop below this to fall back to "일반노래"
  const CLIMAX_VOL_EASE  = 0.18;  // per-frame lerp toward the live mean level

  // ── Note particle effect (v0.7) ──────────────────────────────────────
  // Soft streaks rise from the band baseline; X position maps to a
  // spectrum bin so the visual reads like a piano-keyboard pitch chart
  // (left = low pitch, right = high pitch). Tuned conservatively so
  // even sustained loud passages stay sparse.
  const NOTE_EMIT_THRESHOLD = 0.22;   // smoothed bar level required to consider emitting
  const NOTE_EMIT_PROB_GAIN = 0.45;   // multiplier on bar value → per-eligible-tick spawn probability
  const NOTE_RISE_SPEED     = 55;     // px / sec (negative direction = up)
  const NOTE_LIFE_MS_MIN    = 2200;
  const NOTE_LIFE_MS_MAX    = 3500;
  const NOTE_STREAK_LEN_MIN = 22;
  const NOTE_STREAK_LEN_MAX = 38;
  const NOTE_COOLDOWN_MIN_MS = 110;
  const NOTE_COOLDOWN_MAX_MS = 240;
  const NOTE_MAX             = 70;
  const NOTE_PEAK_ALPHA      = 0.62;
  const NOTE_STAGE_MARGIN    = 0.06;  // fraction of canvas width left blank on each side
  const NOTE_BASELINE_Y      = 0.93;  // fraction of canvas height — slightly above band's 0.94 floor

  // ── AudioSet label → performer mapping ───────────────────────────────
  //
  // Tier 1: specific instrument labels (highest priority). The match-first
  //   order matters — `\bcello\b` before `\bviolin\b` because both can co-occur.
  // Tier 2: parent-category labels — these dominate the top-K when the
  //   model can't decide between specific sub-classes. Map to a sensible
  //   default sprite for the family.
  // Vocals are gender-aware: AST emits "Male singing" / "Female singing"
  //   distinctly, so we route to gender-matched sprite pools and fall
  //   back to the female pool when the label is gender-neutral ("Singing"
  //   / "Choir" / "Vocal music" / etc.). Within each pool a per-tick
  //   round-robin cursor fans co-occurring vocal labels across the two
  //   sprites so a "Singing + Choir" tick reads as an ensemble, not a
  //   single confused soloist.
  //
  // Sprite roster (v0.10):
  //   Female pool — the idol group. Slot 0 is the MAIN VOCAL `vocal-ex`
  //     (the lead — sings via its `idle`+`play` sheets, stays dead-center).
  //     Slots 1..7 are the seven backup idols vox7-1 … vox7-7, who DANCE:
  //     their active sheet is `dance`, not `play` (see IDOL_VOCALS /
  //     stateSheet). The lead sings, the backups dance around her.
  //   Male pool — unchanged:
  //     vocal-2 = male (dark hair, red coat)  vocal-4 = male (silver hair, purple coat)
  // VOCAL_FEMALE doubles as the group's *slot order*: index 0 is the
  // permanent main vocal (stays dead-center), and members join in this
  // order as the group grows. Don't reorder casually — vocal-ex is the lead.
  const VOCAL_FEMALE = ['vocal-ex', 'vox7-1', 'vox7-2', 'vox7-3', 'vox7-4', 'vox7-5', 'vox7-6', 'vox7-7'];
  const VOCAL_MALE   = ['vocal-2', 'vocal-4'];
  const MAIN_VOCAL_ID = VOCAL_FEMALE[0];   // 'vocal-ex' — the lead, center stage, sticky
  const FEMALE_POOL   = new Set(VOCAL_FEMALE);  // whole group (lead + backups)
  let maleCursor = 0;

  // Backup idols sing AND dance. They ship `idle` + `dance` sheets (no
  // `play` sheet), so their active logical state ('play') is remapped to
  // the `dance` file by stateSheet(). The MAIN VOCAL (vocal-ex) is NOT in
  // this set — she keeps her `play` (singing) sheet. All are true vocals
  // for stage layout (center main-vocal area).
  const IDOL_VOCALS = new Set(['vox7-1', 'vox7-2', 'vox7-3', 'vox7-4', 'vox7-5', 'vox7-6', 'vox7-7']);

  // ── Idol group staging (v0.9) ────────────────────────────────────────
  // The female pool is staged as a GROUP, not a soloist. The first female
  // recognition brings the main vocal (center). While female vocal keeps
  // coming — and the richer the signal (sustained presence + genre
  // variety) — the group grows one member per tick, fanning out left/right
  // around the main vocal. When the voice stops, it shrinks back one
  // member per tick until only the (then fading) main vocal remains.
  const IDOL_RAMP_TICKS_PER_MEMBER = 2;  // +1 target member per N sustained ticks
  const IDOL_GENRE_BONUS_MAX = 3;        // "여자 + 장르n" → up to +3 members
  const IDOL_GROUP_MAX = VOCAL_FEMALE.length; // 8 — lead + 7 backups

  let idolRenderedSize = 0;  // how many idols are currently staged (ramps ±1/tick)
  let idolPresentTicks = 0;  // consecutive ticks the female vocal has been heard

  // A performer is a "vocal" (center stage) if it's a classic vocal-* id
  // (incl. the male pool and the vocal-ex lead) or one of the backup idols.
  function isVocal(id) {
    return id.startsWith('vocal-') || IDOL_VOCALS.has(id);
  }

  // Resolve a performer's logical state to its on-disk sheet name. Backup
  // idols have no `play` sheet — their active animation is `dance`. The
  // main vocal (vocal-ex) and everyone else map 1:1.
  function stateSheet(id, state) {
    if (state === 'play' && IDOL_VOCALS.has(id)) return 'dance';
    return state;
  }

  function labelToPerformer(label) {
    const s = label.toLowerCase();

    // ── Tier 1: specific instruments ──
    if (/\bcello\b/.test(s))                                       return 'cello';
    if (/\bviola\b/.test(s))                                       return 'viola';
    if (/\bviolin\b|\bfiddle\b/.test(s))                           return 'violin';
    if (/\bcontrabass\b|\bdouble bass\b/.test(s))                  return 'contrabass';
    if (/\bharp\b/.test(s) && !/harpsichord/.test(s))              return 'harp';
    if (/\bguitar\b/.test(s))                                      return 'guitar';
    if (/\bflute\b/.test(s))                                       return 'flute';
    if (/\bclarinet\b/.test(s))                                    return 'clarinet';
    if (/\boboe\b/.test(s))                                        return 'oboe';
    if (/french horn|\bhorn\b/.test(s))                            return 'horn';
    if (/\btrumpet\b/.test(s))                                     return 'trumpet';
    if (/\btrombone\b/.test(s))                                    return 'trombone';
    if (/\btuba\b/.test(s))                                        return 'tuba';
    if (/\bpiano\b/.test(s))                                       return 'piano';
    if (/\bdrum\b|cymbal|tom-tom|hi-hat|tabla|\bgong\b/.test(s))   return 'drum';

    // ── Vocals ──
    // Male-specific labels still map to a single male sprite here.
    // FEMALE + gender-neutral vocal labels are NOT handled here anymore —
    // they feed the idol-group controller (femaleVocalSignal /
    // upsertIdolGroupFromLabels) which stages a full group rather than a
    // single soloist. Male-specific labels are excluded from that signal.
    if (/male sing|\bman sing\b/.test(s) && !/female/.test(s)) {
      const id = VOCAL_MALE[maleCursor % VOCAL_MALE.length];
      maleCursor++;
      return id;
    }

    // ── Tier 2: parent-category fallbacks ──
    // AST's top-K often features these higher than the specific sub-class.
    if (/bowed string|orchestra|symphony|chamber music/.test(s)) return 'violin';
    if (/plucked string/.test(s))                                 return 'guitar';
    if (/woodwind|wind instrument/.test(s))                       return 'flute';
    if (/\bbrass\b/.test(s))                                      return 'trumpet';
    if (/keyboard \(musical\)/.test(s))                           return 'piano';
    if (/percussion/.test(s))                                     return 'drum';

    return null;
  }

  // Strength of the female / gender-neutral vocal signal this tick. Male
  // singing is excluded so a male soloist never inflates the idol group.
  // `explicit` is true when AudioSet actually said "Female singing" (vs a
  // gender-neutral "Singing" / "Choir" / "Vocal music" that we attribute
  // to the female pool by operator convention).
  function femaleVocalSignal(labels) {
    let explicit = 0, neutral = 0;
    for (const l of labels) {
      const s = l.name.toLowerCase();
      if (/male sing|\bman sing\b/.test(s) && !/female/.test(s)) continue; // male soloist
      if (/female sing|\bwoman sing\b/.test(s))                  explicit = Math.max(explicit, l.score);
      else if (/sing(ing)?|choir|vocal|chant|yodel|rapping|hum/.test(s)) neutral = Math.max(neutral, l.score);
    }
    return { score: Math.max(explicit, neutral), explicit: explicit > 0 };
  }

  // How many distinct genres are co-occurring (reuses the dance-style
  // mapping). "여자 + 장르n" — more genres → a richer, bigger idol group.
  function countGenres(labels) {
    const styles = new Set();
    for (const l of labels) {
      const g = labelToDance(l.name);
      if (g && l.score >= DANCE_KEEP) styles.add(g);
    }
    return styles.size;
  }

  // ── Band sprite cache — TexturePacker-style sheet + JSON ─────────────
  //
  // Each performer's idle and play states ship as one sheet PNG (with
  // proper alpha — no chroma-key needed) plus a JSON describing the
  // per-frame rectangles. The cache entry holds the loaded sheet image
  // plus the parsed frame rects; the render loop indexes `frames` by the
  // tick-derived frame counter and uses the 9-arg form of drawImage to
  // blit the sub-rect.
  //
  // Frame keys in the JSON are like "violin_idle_0.png" → "violin_idle_3.png";
  // we sort lexically to get them in the right order (the sheet has them
  // laid out horizontally, padded by ~8 px).
  //
  // Both the PNG and the JSON load asynchronously; rendering tolerates
  // partial state — until both are ready, the slot is skipped.
  //
  // The legacy chroma-key path is still defined below because the dance
  // sprites (separate ~PNG-per-frame files with bright-green background)
  // continue to use it until those assets get migrated to the sheet
  // layout.
  const spriteCache = new Map(); // "id|state" -> { sheet: HTMLImageElement|null, frames: [{x,y,w,h}, ...]|null }

  function chromaKey(img) {
    const canvas = document.createElement('canvas');
    canvas.width = img.naturalWidth;
    canvas.height = img.naturalHeight;
    const ctx = canvas.getContext('2d');
    ctx.drawImage(img, 0, 0);
    const data = ctx.getImageData(0, 0, canvas.width, canvas.height);
    const px = data.data;

    for (let i = 0; i < px.length; i += 4) {
      const r = px[i], g = px[i + 1], b = px[i + 2];
      const gMinusR = g - r;
      const gMinusB = g - b;

      // Hard key — pure bright green background
      if (g > 170 && gMinusR > 55 && gMinusB > 55) {
        px[i + 3] = 0;
        continue;
      }
      // Soft key — anti-aliased edge: still mostly green but dimmer
      if (g > 110 && gMinusR > 28 && gMinusB > 28) {
        const greenness = Math.min(gMinusR, gMinusB); // 28..55 range
        const t = Math.min(1, (greenness - 28) / 27);  // 0..1
        px[i + 3] = px[i + 3] * (1 - t * 0.85);
        // Despill — pull the excess green back so the silhouette edge
        // doesn't read as a green halo. Replace excess G with the avg
        // of R and B (neutralises the cast).
        const spill = Math.max(0, g - Math.max(r, b));
        if (spill > 0) px[i + 1] = g - spill * t * 0.7;
      }
    }
    ctx.putImageData(data, 0, 0);
    return canvas;
  }

  function ensureSpriteSet(id, state) {
    const key = id + '|' + state;
    let entry = spriteCache.get(key);
    if (entry) return entry;
    entry = { sheet: null, frames: null };
    spriteCache.set(key, entry);

    // Idol vocals resolve 'play' → 'dance' sheet; everyone else 1:1.
    const sheet = stateSheet(id, state);
    const sheetUrl = `${SPRITE_BASE}${id}/${sheet}.png`;
    const jsonUrl  = `${SPRITE_BASE}${id}/${sheet}.json`;

    // Fetch the atlas JSON first so the render loop sees a complete entry
    // (sheet AND frames) before it tries to draw. The two requests
    // proceed in parallel — order between them doesn't matter, only that
    // both fields are non-null before draw.
    fetch(jsonUrl)
      .then(r => { if (!r.ok) throw new Error(`HTTP ${r.status}`); return r.json(); })
      .then(data => {
        // Frame order is encoded in the key names ("..._0.png" .. "..._3.png")
        // rather than insertion order; sort lexically so we never depend on
        // JSON-object iteration semantics.
        const sortedNames = Object.keys(data.frames || {}).sort();
        entry.frames = sortedNames.map(n => data.frames[n].frame);
      })
      .catch(err => console.warn(`[agent-band] atlas json failed: ${jsonUrl}`, err.message));

    const img = new Image();
    img.onload  = () => { entry.sheet = img; };
    img.onerror = () => console.warn('[agent-band] sprite sheet missing:', sheetUrl);
    img.src = sheetUrl;

    return entry;
  }

  // ── Performer registry ───────────────────────────────────────────────
  /** @typedef {{
   *    id: string,
   *    score: number,
   *    state: 'play' | 'idle',
   *    addedAt: number,
   *    lastSeenTick: number,
   *    fading: boolean,
   *    fadeAt: number,
   *  }} Performer
   */
  /** @type {Map<string, Performer>} */
  const performers = new Map();
  let tickCounter = 0;

  // Spawn-or-refresh one performer at the given score. Marks it "seen this
  // tick" (so it won't fade) and resolves its idle/active state. Returns
  // false if the stage is full of active performers and this one couldn't
  // get a slot.
  function upsertPerformer(id, score, now) {
    if (!performers.has(id) && performers.size >= MAX_PERFORMERS) {
      // Evict a fading performer to make room; never bump an active one.
      const fadingId = [...performers.entries()].find(([, p]) => p.fading)?.[0];
      if (fadingId) performers.delete(fadingId);
      else return false;
    }
    let p = performers.get(id);
    const state = score >= SCORE_ACTIVE ? 'play' : 'idle';
    if (!p) {
      p = { id, score, state, addedAt: now, lastSeenTick: tickCounter, fading: false, fadeAt: 0 };
      performers.set(id, p);
    } else {
      p.score = score;
      p.state = state;
      p.lastSeenTick = tickCounter;
      p.fading = false;
      p.fadeAt = 0;
    }
    ensureSpriteSet(id, p.state);
    return true;
  }

  function upsertPerformersFromLabels(labels) {
    tickCounter++;
    const now = performance.now();
    maleCursor = 0;

    // Collapse multi-label hits onto a single sprite id with the strongest score
    // ("Guitar" + "Acoustic guitar" → one guitar slot with max score).
    // NOTE: female / neutral vocals return null from labelToPerformer — the
    // idol group is staged separately below.
    const collapsed = new Map();
    for (const l of labels) {
      const id = labelToPerformer(l.name);
      if (!id) continue;
      const prev = collapsed.get(id);
      if (prev === undefined || l.score > prev) collapsed.set(id, l.score);
    }

    // Hysteresis: PRESENT to spawn, KEEP to retain. Once a performer is on
    // stage, a quieter version of the same label still counts as "seen this
    // tick" — that's why a singer doesn't flicker on/off between ticks.
    const ordered = [...collapsed.entries()].sort((a, b) => b[1] - a[1]);

    for (const [id, score] of ordered) {
      const onStage = performers.has(id);
      const required = onStage ? SCORE_KEEP : SCORE_PRESENT;
      if (score < required) continue;
      upsertPerformer(id, score, now);
    }

    // Stage the female idol group (main vocal + fan-out).
    upsertIdolGroup(labels, now);

    // Unseen-for-N-ticks → fade-out → evict.
    for (const [id, p] of performers) {
      if (p.lastSeenTick === tickCounter) continue;
      if (!p.fading && tickCounter - p.lastSeenTick >= PERSIST_TICKS) {
        p.fading = true;
        p.fadeAt = now;
        p.state = 'idle';
      }
      if (p.fading && now - p.fadeAt > FADE_MS) {
        performers.delete(id);
      }
    }
  }

  // ── Idol group controller ────────────────────────────────────────────
  //
  // Stages the female pool as a growing/shrinking GROUP centered on the
  // main vocal:
  //   • first female recognition → main vocal (vox7-1), center stage.
  //   • while the voice keeps coming, the TARGET size climbs — faster the
  //     richer the signal: +1 per IDOL_RAMP_TICKS_PER_MEMBER sustained
  //     ticks, +1 per co-occurring genre (capped), +1 when the label is an
  //     explicit "Female singing".
  //   • the rendered size chases the target by ±1 per tick so members fan
  //     out (and later peel off) one at a time rather than popping in/out.
  //   • members are VOCAL_FEMALE[0..size); index 0 (main) is always present
  //     while the group is up, and computeLayout centers it with the rest
  //     alternating left/right (see centerVocals).
  function upsertIdolGroup(labels, now) {
    const fem = femaleVocalSignal(labels);
    const onStage = idolRenderedSize > 0;
    const required = onStage ? SCORE_KEEP : SCORE_PRESENT;
    const present = fem.score >= required;

    let target;
    if (present) {
      idolPresentTicks++;
      target = 1
        + Math.floor(idolPresentTicks / IDOL_RAMP_TICKS_PER_MEMBER)
        + Math.min(countGenres(labels), IDOL_GENRE_BONUS_MAX)
        + (fem.explicit ? 1 : 0);
      target = Math.max(1, Math.min(IDOL_GROUP_MAX, target));
    } else {
      idolPresentTicks = 0;
      target = 0;   // ramp the whole group down
    }

    // Chase the target one member per tick.
    if (target > idolRenderedSize) idolRenderedSize++;
    else if (target < idolRenderedSize) idolRenderedSize--;
    if (idolRenderedSize < 0) idolRenderedSize = 0;

    const members = VOCAL_FEMALE.slice(0, idolRenderedSize);
    const memberSet = new Set(members);
    // Presence score keeps members on stage; the actual play/dance-vs-idle
    // animation is decided per-frame by performState() (volume-gated), not
    // by this score. Floor at SCORE_PRESENT so members don't fade while the
    // group is up.
    const memberScore = present ? Math.max(fem.score, SCORE_PRESENT) : SCORE_PRESENT;
    for (const id of members) {
      upsertPerformer(id, memberScore, now);
      // Preload both sheets so the climax (idle↔play/dance) flip is instant.
      ensureSpriteSet(id, 'idle');
      ensureSpriteSet(id, 'play');
    }

    // Peel-off: any female-pool member beyond the current group size fades
    // promptly (one per tick as the group shrinks) instead of lingering on
    // the unseen timer. Covers the size==0 case too — the main vocal
    // (vocal-ex) is slot 0 so it's always the last to peel off.
    for (const id of VOCAL_FEMALE) {
      if (memberSet.has(id)) continue;
      const p = performers.get(id);
      if (p && !p.fading) { p.fading = true; p.fadeAt = now; p.state = 'idle'; }
    }
  }

  // ── Dance troupe — AudioSet genre → style mapping ────────────────────
  //
  // Six styles bundled (assets/dancers/{style}/{style}-1..6.png). Each
  // tick we sum scores per style across all matching labels — that way
  // a "Hip hop music + Rapping + Trap music" co-hit reinforces hiphop
  // instead of fighting for the spawn — then pick the top N (cap
  // MAX_DANCERS) above DANCE_PRESENT.
  //
  // Speech rap is treated as a dance trigger per operator request:
  // "Rapping" is technically a speech label in AudioSet's hierarchy but
  // semantically it's a hip-hop performance, so it spawns the hiphop
  // dancer regardless of whether the music label co-occurs.
  function labelToDance(label) {
    const s = label.toLowerCase();

    // Hip-hop & rap (includes the speech "Rapping" label per operator
    // request — rap performance always reads as hip-hop dance).
    if (/hip hop|hiphop|\brap\b|rapping|trap music/.test(s))             return 'hiphop';

    // Disco / funk / Latin → waacking (the style descended directly
    // from 70s disco-funk; salsa/Latin share the percussive groove).
    if (/\bdisco\b|\bfunk\b|salsa|latin america/.test(s))                return 'waacking';

    // Jazz family — jazz / blues / swing / soul / R&B / gospel all
    // share the loose-hip jazz dance vocabulary.
    if (/\bjazz\b|swing music|\bblues\b|soul music|rhythm and blues|gospel/.test(s))
                                                                          return 'jazz';

    // Classical / orchestral / cinematic → ballet. "Orchestra" alone
    // (the instrument category) also signals classical context. "New-age"
    // and "Soundtrack music" tend to be orchestral too.
    if (/classical|\bopera\b|symphony|\borchestra\b|chamber music|new-age|wedding music|tender music|soundtrack music/.test(s))
                                                                          return 'ballet';

    // Modern pop / electronic / EDM → k-pop dance (synced-step idiom).
    if (/pop music|electronic|electronica|\bedm\b|electronic dance|dance music|house music|techno|dubstep|trance/.test(s))
                                                                          return 'kpop';

    // Cheering / festive / high-energy mood music → cheer routine.
    if (/cheering|exciting music|happy music|christmas music/.test(s))   return 'cheer';

    return null;
  }

  /** @typedef {{
   *    style: string,
   *    score: number,
   *    addedAt: number,
   *    lastSeenTick: number,
   *    characterIdx: number,  // 0..5, which sub-character in the style row
   *    framePhase: number,    // 0..3, randomised so simultaneous dancers desync
   *    fading: boolean,
   *    fadeAt: number,
   *  }} Dancer
   */
  /** @type {Map<string, Dancer>} */
  const dancers = new Map();

  function upsertDancersFromLabels(labels) {
    const now = performance.now();

    // Sum scores per dance style across all matching labels.
    const styleScores = new Map();
    for (const l of labels) {
      const style = labelToDance(l.name);
      if (!style) continue;
      styleScores.set(style, (styleScores.get(style) || 0) + l.score);
    }

    const ranked = [...styleScores.entries()].sort((a, b) => b[1] - a[1]);

    for (const [style, score] of ranked) {
      const onStage = dancers.has(style);
      const required = onStage ? DANCE_KEEP : DANCE_PRESENT;
      if (score < required) continue;

      if (!dancers.has(style) && dancers.size >= MAX_DANCERS) {
        // Evict a fading style to make room; never bump an active one.
        const fadingStyle = [...dancers.entries()].find(([, d]) => d.fading)?.[0];
        if (fadingStyle) dancers.delete(fadingStyle);
        else continue;
      }

      let d = dancers.get(style);
      if (!d) {
        d = {
          style,
          score,
          addedAt: now,
          lastSeenTick: tickCounter,
          // Random sub-character pick at spawn so the same style brings
          // visual variety across sessions. 1/6 chance of immediate
          // repeat is acceptable for the troupe context.
          characterIdx: Math.floor(Math.random() * DANCE_CHARS_PER_STYLE),
          framePhase: Math.floor(Math.random() * DANCE_FRAMES),
          fading: false,
          fadeAt: 0,
        };
        dancers.set(style, d);
      } else {
        d.score = score;
        d.lastSeenTick = tickCounter;
        d.fading = false;
        d.fadeAt = 0;
      }
      ensureDanceMaster();
    }

    for (const [style, d] of dancers) {
      if (d.lastSeenTick === tickCounter) continue;
      if (!d.fading && tickCounter - d.lastSeenTick >= DANCE_PERSIST_TICKS) {
        d.fading = true;
        d.fadeAt = now;
      }
      if (d.fading && now - d.fadeAt > DANCE_FADE_MS) {
        dancers.delete(style);
      }
    }
  }

  // ── Dance master sheet (singleton, loaded on first dancer demand) ────
  //
  // One PNG + one JSON, fetched lazily the first time any dancer needs
  // to draw. Subsequent draws hit the cached image directly. Until both
  // resources resolve, `drawDancer` skips the slot — the dancer is
  // already in the registry, it just doesn't render yet.
  let danceMaster = null;       // HTMLImageElement
  let danceIndex  = null;       // parsed index.json (kept for future flexibility)
  let danceMasterLoading = false;

  function ensureDanceMaster() {
    if (danceMaster || danceMasterLoading) return;
    danceMasterLoading = true;

    fetch(DANCE_MASTER_JSON)
      .then(r => { if (!r.ok) throw new Error(`HTTP ${r.status}`); return r.json(); })
      .then(idx => { danceIndex = idx; })
      .catch(err => console.warn(`[agent-band] dance index failed: ${DANCE_MASTER_JSON}`, err.message));

    const img = new Image();
    img.onload = () => {
      danceMaster = img;
      danceMasterLoading = false;
    };
    img.onerror = () => {
      danceMasterLoading = false;
      console.warn('[agent-band] dance master sheet missing:', DANCE_MASTER_PNG);
    };
    img.src = DANCE_MASTER_PNG;
  }

  // ── Stage layout — vocals center, instruments wings ──────────────────
  //
  // ORDER_RANK ascending = stage position from L→R. Vocals stay center
  // regardless — we split the instruments by half over ORDER_RANK, put
  // the lower-rank half (strings/plucked) on the left, higher-rank half
  // (brass/keys/percussion) on the right, with vocals filling the middle.
  const ORDER_RANK = {
    'violin': 10, 'viola': 11, 'cello': 12, 'contrabass': 13,
    'guitar': 20, 'harp': 21,
    'flute': 30, 'clarinet': 31, 'oboe': 32,
    'horn': 40, 'trumpet': 41, 'trombone': 42, 'tuba': 43,
    'piano': 50, 'drum': 60,
  };

  // Order the vocal cluster so the main vocal sits dead-center and the
  // other idols fan out alternately right/left in join order — vox7-2 →
  // right of main, vox7-3 → left, vox7-4 → further right, … — so the group
  // grows symmetrically around a lead that never moves. Any male vocals
  // (rare alongside the idol group) hug the outer edges of the cluster.
  function centerVocals(vocals) {
    const idols = vocals
      .filter(p => FEMALE_POOL.has(p.id))
      .sort((a, b) => VOCAL_FEMALE.indexOf(a.id) - VOCAL_FEMALE.indexOf(b.id));
    const males = vocals.filter(p => !FEMALE_POOL.has(p.id));

    const left = [], right = [];
    idols.forEach((p, i) => {
      if (i === 0) return;                      // main vocal stays center
      (i % 2 === 1 ? right : left).push(p);
    });
    const centeredIdols = [...left.reverse(), ...(idols[0] ? [idols[0]] : []), ...right];

    const mHalf = Math.ceil(males.length / 2);
    return [...males.slice(0, mHalf), ...centeredIdols, ...males.slice(mHalf)];
  }

  function computeLayout(allPerformers, w, h) {
    const layout = new Map();
    if (allPerformers.length === 0) return layout;

    const vocals = centerVocals(allPerformers.filter(p => isVocal(p.id)));
    const insts  = allPerformers
      .filter(p => !isVocal(p.id))
      .sort((a, b) => (ORDER_RANK[a.id] ?? 99) - (ORDER_RANK[b.id] ?? 99));

    // Instruments split into L/R wings around the vocal cluster.
    const half = Math.ceil(insts.length / 2);
    // Reverse left wing so the inner element (closest to vocals) is the
    // highest-ranked of the left group — gives a nice arc from violin
    // (outer-left) → piano-ish (inner-left) → vocals → brass (inner-right)
    // → drums (outer-right).
    const leftWing  = insts.slice(0, half).reverse();
    const rightWing = insts.slice(half);

    const ordered = [...leftWing, ...vocals, ...rightWing];
    const n = ordered.length;

    // Compute slot dimensions. Total natural width = n × MAX_W + (n-1) × MIN_GAP.
    // If it doesn't fit the canvas, scale down uniformly but not below MIN_W.
    const naturalW = n * MAX_SPRITE_W + (n - 1) * MIN_GAP;
    const scale = naturalW <= w ? 1 : (w - (n - 1) * MIN_GAP) / (n * MAX_SPRITE_W);
    const spriteW = Math.max(MIN_SPRITE_W, MAX_SPRITE_W * scale);
    const gap = Math.max(8, MIN_GAP * Math.max(scale, 0.5));
    const totalW = n * spriteW + (n - 1) * gap;
    const startX = (w - totalW) / 2;

    const baseY = h * STAGE_BASE_Y;
    const slotH = h * SPRITE_TARGET_H;

    ordered.forEach((p, i) => {
      layout.set(p.id, {
        x: startX + i * (spriteW + gap),
        baseY,
        slotW: spriteW,
        slotH,
      });
    });
    return layout;
  }

  // ── Dance row layout — row 2 sits above the band row, slightly smaller
  // so the band stays the visual focus and the dancers feel like a back
  // chorus line rather than blocking the lead performers.
  const DANCE_MAX_W   = 120;
  const DANCE_MIN_W   = 60;
  const DANCE_GAP     = 16;
  const DANCE_BASE_Y  = 0.58;   // ground line for the dance row (vs band's 0.94)
  const DANCE_TARGET_H = 0.40;  // height as fraction of canvas (vs band's 0.55)

  function computeDanceLayout(dancerList, w, h) {
    const layout = new Map();
    const n = dancerList.length;
    if (n === 0) return layout;

    const naturalW = n * DANCE_MAX_W + (n - 1) * DANCE_GAP;
    const scale = naturalW <= w ? 1 : (w - (n - 1) * DANCE_GAP) / (n * DANCE_MAX_W);
    const spriteW = Math.max(DANCE_MIN_W, DANCE_MAX_W * scale);
    const gap = Math.max(8, DANCE_GAP * Math.max(scale, 0.5));
    const totalW = n * spriteW + (n - 1) * gap;
    const startX = (w - totalW) / 2;

    const baseY = h * DANCE_BASE_Y;
    const slotH = h * DANCE_TARGET_H;

    dancerList.forEach((d, i) => {
      layout.set(d.style, {
        x: startX + i * (spriteW + gap),
        baseY,
        slotW: spriteW,
        slotH,
      });
    });
    return layout;
  }

  // ── Canvas refs ──────────────────────────────────────────────────────
  const stageCanvas = document.getElementById('stage');
  const stageCtx    = stageCanvas.getContext('2d');
  const specCanvas  = document.getElementById('spectrum');
  const specCtx     = specCanvas.getContext('2d');

  /** @type {HTMLImageElement|null} */
  let stageImg = null;
  let stageReady = false;

  function pickStage(name) {
    const img = new Image();
    img.onload = () => { stageImg = img; stageReady = true; };
    img.onerror = () => { console.warn('[agent-band] stage missing:', img.src); stageReady = false; };
    img.src = `${STAGE_BASE}${name}.png`;
  }

  function fitCanvasToParent(canvas) {
    const dpr = Math.max(1, Math.floor(window.devicePixelRatio || 1));
    const r = canvas.parentElement.getBoundingClientRect();
    const w = Math.max(1, Math.floor(r.width));
    const h = Math.max(1, Math.floor(r.height));
    if (canvas.width !== w * dpr || canvas.height !== h * dpr) {
      canvas.width  = w * dpr;
      canvas.height = h * dpr;
    }
    return { w, h, dpr };
  }

  function drawStageBg(w, h) {
    if (!stageReady || !stageImg) {
      stageCtx.fillStyle = '#0b0d12';
      stageCtx.fillRect(0, 0, w, h);
      return;
    }
    const iw = stageImg.naturalWidth;
    const ih = stageImg.naturalHeight;
    const scale = Math.max(w / iw, h / ih);
    const dw = iw * scale;
    const dh = ih * scale;
    const dx = (w - dw) / 2;
    const dy = h - dh; // bottom-aligned: top crops, performer floor stays
    stageCtx.drawImage(stageImg, dx, dy, dw, dh);
  }

  function drawPerformer(p, x, baseY, slotW, slotH, now) {
    const state = performState(p);
    const set = ensureSpriteSet(p.id, state);
    if (!set.sheet || !set.frames || set.frames.length === 0) return;

    const fps = state === 'play' ? PLAY_FPS : IDLE_FPS;
    const frameIdx = Math.floor(now / 1000 * fps) % set.frames.length;
    const fr = set.frames[frameIdx];
    if (!fr) return;

    const aspect = fr.h / fr.w;
    let drawW = slotW;
    let drawH = drawW * aspect;
    if (drawH > slotH) { drawH = slotH; drawW = drawH / aspect; }

    const dx = x + (slotW - drawW) / 2;
    const dy = baseY - drawH;

    let alpha = 1;
    if (p.fading) {
      alpha = Math.max(0, 1 - (now - p.fadeAt) / FADE_MS);
    } else {
      const since = now - p.addedAt;
      if (since < 280) alpha = Math.max(0.1, since / 280);
    }

    const bob = state === 'play' ? Math.sin(now / 120 + x) * 3 : 0;

    stageCtx.save();
    stageCtx.globalAlpha = alpha;
    if (state === 'play') {
      stageCtx.shadowColor = `rgba(0, 229, 255, ${0.32 * alpha})`;
      stageCtx.shadowBlur = 22;
    }
    // 9-arg form: copy {fr.x, fr.y, fr.w, fr.h} from the sheet onto the
    // destination rect. drawImage handles the sub-rect crop on the GPU
    // path — no per-frame canvas extraction needed.
    stageCtx.drawImage(set.sheet, fr.x, fr.y, fr.w, fr.h, dx, dy + bob, drawW, drawH);
    stageCtx.restore();
  }

  function drawDancer(d, x, baseY, slotW, slotH, now) {
    if (!danceMaster) {
      ensureDanceMaster();  // idempotent; kicks off load on first call
      return;
    }
    const row = DANCE_STYLE_ROW[d.style];
    if (row === undefined) return;

    // Source rect in the master sheet:
    //   cell origin = (col * CELL_W, row * CELL_H)
    //   frame origin = cell + (PAD + frame * STRIDE, PAD)
    const frameIdx = (Math.floor(now / 1000 * DANCE_FPS) + d.framePhase) % DANCE_FRAMES;
    const cellX = d.characterIdx * DANCE_CELL_W;
    const cellY = row * DANCE_CELL_H;
    const srcX = cellX + DANCE_FRAME_PAD + frameIdx * DANCE_FRAME_STRIDE;
    const srcY = cellY + DANCE_FRAME_PAD;

    // Square aspect — sub-frames are 192×192. Fit into the slot.
    const aspect = DANCE_FRAME_H / DANCE_FRAME_W;
    let drawW = slotW;
    let drawH = drawW * aspect;
    if (drawH > slotH) { drawH = slotH; drawW = drawH / aspect; }

    const dx = x + (slotW - drawW) / 2;
    const dy = baseY - drawH;

    let alpha = 1;
    if (d.fading) {
      alpha = Math.max(0, 1 - (now - d.fadeAt) / DANCE_FADE_MS);
    } else {
      const since = now - d.addedAt;
      if (since < 360) alpha = Math.max(0.1, since / 360);
    }
    // Dancers in row 2 sit a touch dimmer so the band stays the visual
    // focus while the troupe is still clearly present.
    alpha *= 0.92;

    const bob = Math.sin(now / 110 + x * 0.7) * 4;

    stageCtx.save();
    stageCtx.globalAlpha = alpha;
    stageCtx.shadowColor = `rgba(255, 45, 149, ${0.28 * alpha})`;
    stageCtx.shadowBlur = 18;
    stageCtx.drawImage(
      danceMaster,
      srcX, srcY, DANCE_FRAME_W, DANCE_FRAME_H,
      dx, dy + bob, drawW, drawH);
    stageCtx.restore();
  }

  // ── Spectrum with asymmetric attack/release lerp ─────────────────────
  let lastSpectrum = null;       // most recent host snapshot
  let smoothed = null;            // per-frame smoothed values

  function setSpectrum(bars) {
    if (!bars || bars.length === 0) return;
    lastSpectrum = bars;
    if (!smoothed || smoothed.length !== bars.length) {
      smoothed = new Float32Array(bars.length);
    }
  }

  function tickSmoothSpectrum() {
    if (!lastSpectrum || !smoothed) return;
    for (let i = 0; i < lastSpectrum.length; i++) {
      const cur = smoothed[i] || 0;
      const tgt = lastSpectrum[i];
      const k = tgt > cur ? SPEC_ATTACK : SPEC_RELEASE;
      smoothed[i] = cur + (tgt - cur) * k;
    }
  }

  // ── Climax (loudness) gate ───────────────────────────────────────────
  // volumeLevel = eased mean of the smoothed spectrum (0..1). climaxActive
  // flips on with hysteresis: a loud passage makes the whole idol group
  // perform; otherwise only the main vocal does.
  let volumeLevel = 0;
  let climaxActive = false;

  function updateClimax() {
    if (smoothed && smoothed.length) {
      let sum = 0;
      for (let i = 0; i < smoothed.length; i++) sum += smoothed[i];
      const mean = sum / smoothed.length;
      volumeLevel += (mean - volumeLevel) * CLIMAX_VOL_EASE;
    } else {
      volumeLevel += (0 - volumeLevel) * CLIMAX_VOL_EASE;
    }
    if (climaxActive) {
      if (volumeLevel < CLIMAX_VOL_EXIT) climaxActive = false;
    } else if (volumeLevel > CLIMAX_VOL_ENTER) {
      climaxActive = true;
    }
  }

  // The animation state a performer should SHOW this frame.
  //   • fading → idle.
  //   • female pool → volume-gated: climax means everyone performs; in the
  //     calm "일반노래" state only the main vocal does, the rest idle.
  //   • instruments / male vocals → their score-derived p.state, unchanged.
  function performState(p) {
    if (p.fading) return 'idle';
    if (FEMALE_POOL.has(p.id)) {
      return (climaxActive || p.id === MAIN_VOCAL_ID) ? 'play' : 'idle';
    }
    return p.state;
  }

  // ── Note particle system ─────────────────────────────────────────────
  //
  // Each note carries everything we need to advance it without re-reading
  // spectrum state, so the spectrum can update at its own cadence (30Hz)
  // while particles glide on the RAF clock. Cooldown timestamps per bar
  // prevent a single high-energy band from spamming the screen frame
  // after frame. lifeMs is randomised per particle so a steady tone
  // still spawns a varied stream instead of a rigid column.
  /** @type {{x:number, y:number, vy:number, birthMs:number, lifeMs:number, intensity:number, streakLen:number, hueT:number}[]} */
  const notes = [];
  const lastEmitMs = new Map(); // bar idx → last emission timestamp

  function emitNotesFromSpectrum(spec, w, h, nowMs) {
    if (!spec || spec.length === 0) return;
    const baselineY = h * NOTE_BASELINE_Y;
    const usableW = w * (1 - NOTE_STAGE_MARGIN * 2);
    const offsetX = w * NOTE_STAGE_MARGIN;
    const n = spec.length;

    for (let i = 0; i < n; i++) {
      const v = spec[i];
      if (v < NOTE_EMIT_THRESHOLD) continue;

      const last = lastEmitMs.get(i) || 0;
      const cooldown = NOTE_COOLDOWN_MIN_MS +
                       Math.random() * (NOTE_COOLDOWN_MAX_MS - NOTE_COOLDOWN_MIN_MS);
      if (nowMs - last < cooldown) continue;
      lastEmitMs.set(i, nowMs);

      // Probabilistic gate so even loud bars stay sparse.
      if (Math.random() > v * NOTE_EMIT_PROB_GAIN) continue;

      if (notes.length >= NOTE_MAX) notes.shift();

      // Center of the bar's pixel column, plus a small jitter so two
      // simultaneous notes on the same bar don't render as a single
      // doubled-up streak.
      const x = offsetX + ((i + 0.5) / n) * usableW + (Math.random() - 0.5) * 6;
      const lifeMs = NOTE_LIFE_MS_MIN + Math.random() * (NOTE_LIFE_MS_MAX - NOTE_LIFE_MS_MIN);
      const streakLen = NOTE_STREAK_LEN_MIN + Math.random() * (NOTE_STREAK_LEN_MAX - NOTE_STREAK_LEN_MIN);
      const speedJitter = 0.85 + Math.random() * 0.4;   // 0.85..1.25
      notes.push({
        x,
        y: baselineY,
        vy: -NOTE_RISE_SPEED * speedJitter,
        birthMs: nowMs,
        lifeMs,
        intensity: v,
        streakLen,
        hueT: i / Math.max(1, n - 1),  // cached 0..1 along the gradient
      });
    }
  }

  function updateAndDrawNotes(dt, nowMs) {
    if (notes.length === 0) return;
    for (let i = notes.length - 1; i >= 0; i--) {
      const note = notes[i];
      const age = nowMs - note.birthMs;
      if (age > note.lifeMs) { notes.splice(i, 1); continue; }

      note.y += note.vy * dt;

      // Envelope: short fade-in then long, eased fade-out. Eased keeps
      // the top of the trail "ghosting" rather than popping out.
      const t = age / note.lifeMs;
      const fadeIn = Math.min(1, age / 180);
      const fadeOut = Math.min(1, Math.max(0, 1 - t * t));
      const alpha = NOTE_PEAK_ALPHA * fadeIn * fadeOut * (0.55 + note.intensity * 0.45);
      if (alpha <= 0.01) continue;

      // Pitch-mapped color along the spectrum's cyan→magenta gradient.
      const r = Math.round(SPEC_GRADIENT_FROM[0] + (SPEC_GRADIENT_TO[0] - SPEC_GRADIENT_FROM[0]) * note.hueT);
      const g = Math.round(SPEC_GRADIENT_FROM[1] + (SPEC_GRADIENT_TO[1] - SPEC_GRADIENT_FROM[1]) * note.hueT);
      const b = Math.round(SPEC_GRADIENT_FROM[2] + (SPEC_GRADIENT_TO[2] - SPEC_GRADIENT_FROM[2]) * note.hueT);
      const rgb = `${r}, ${g}, ${b}`;

      // The streak fades to transparent at both ends so the head feels
      // like a comet trail rather than a hard rectangle.
      const top = note.y - note.streakLen;
      const grad = stageCtx.createLinearGradient(note.x, top, note.x, note.y);
      grad.addColorStop(0,   `rgba(${rgb}, 0)`);
      grad.addColorStop(0.4, `rgba(${rgb}, ${alpha})`);
      grad.addColorStop(1,   `rgba(${rgb}, 0)`);

      stageCtx.save();
      stageCtx.shadowColor = `rgba(${rgb}, ${alpha * 0.9})`;
      stageCtx.shadowBlur = 14;
      stageCtx.fillStyle = grad;
      stageCtx.fillRect(note.x - 1.4, top, 2.8, note.streakLen);
      stageCtx.restore();
    }
  }

  function drawSpectrum() {
    if (!smoothed) return;
    const { w, h } = fitCanvasToParent(specCanvas);
    const dpr = window.devicePixelRatio || 1;
    specCtx.setTransform(dpr, 0, 0, dpr, 0, 0);
    specCtx.clearRect(0, 0, w, h);

    const n = smoothed.length;
    const gap = 2;
    const barW = Math.max(2, (w - (n - 1) * gap) / n);

    for (let i = 0; i < n; i++) {
      const t = i / (n - 1);
      const r = Math.round(SPEC_GRADIENT_FROM[0] + (SPEC_GRADIENT_TO[0] - SPEC_GRADIENT_FROM[0]) * t);
      const g = Math.round(SPEC_GRADIENT_FROM[1] + (SPEC_GRADIENT_TO[1] - SPEC_GRADIENT_FROM[1]) * t);
      const b = Math.round(SPEC_GRADIENT_FROM[2] + (SPEC_GRADIENT_TO[2] - SPEC_GRADIENT_FROM[2]) * t);
      specCtx.fillStyle = `rgb(${r}, ${g}, ${b})`;
      const v = Math.max(0.02, Math.min(1, smoothed[i]));
      const bh = v * (h - 4);
      specCtx.fillRect(i * (barW + gap), h - bh, barW, bh);
    }
  }

  // ── Render loop ──────────────────────────────────────────────────────
  // Z-order: background → row 2 dancers → row 1 band → note particles →
  // (spectrum overlay sits on the separate footer canvas).
  // Dancers are painted first so the band (front row) sits on top —
  // matches the "back chorus line" mental model.
  let lastFrameMs = performance.now();
  function renderLoop() {
    const { w, h } = fitCanvasToParent(stageCanvas);
    const dpr = window.devicePixelRatio || 1;
    stageCtx.setTransform(dpr, 0, 0, dpr, 0, 0);
    stageCtx.clearRect(0, 0, w, h);
    drawStageBg(w, h);

    const now = performance.now();
    // Cap dt at 100 ms so a paused tab / GC stall doesn't teleport
    // every particle off-screen at once when the loop resumes.
    const dt = Math.min(0.1, (now - lastFrameMs) / 1000);
    lastFrameMs = now;

    // Update the smoothed spectrum + climax gate FIRST so this frame's
    // performers draw with the current loudness state.
    tickSmoothSpectrum();
    updateClimax();

    // Row 2 — dancers (back row). Disabled in v0.10 (DANCE_TROUPE_ENABLED).
    const ds = DANCE_TROUPE_ENABLED ? [...dancers.values()] : [];
    if (ds.length > 0) {
      const danceLayout = computeDanceLayout(ds, w, h);
      for (const d of ds) {
        const pos = danceLayout.get(d.style);
        if (!pos) continue;
        drawDancer(d, pos.x, pos.baseY, pos.slotW, pos.slotH, now);
      }
    }

    // Row 1 — band (front row)
    const ps = [...performers.values()];
    const layout = computeLayout(ps, w, h);
    for (const p of ps) {
      const pos = layout.get(p.id);
      if (!pos) continue;
      drawPerformer(p, pos.x, pos.baseY, pos.slotW, pos.slotH, now);
    }

    // Drive the note particles off the SMOOTHED spectrum (already eased
    // above) so emission isn't tied to the 30Hz host event — feels
    // continuous across the 60Hz frame clock.
    emitNotesFromSpectrum(smoothed, w, h, now);
    updateAndDrawNotes(dt, now);

    drawSpectrum();
    requestAnimationFrame(renderLoop);
  }

  // ── Label strip ──────────────────────────────────────────────────────
  const labelStrip = document.getElementById('label-strip');
  function renderLabelStrip(labels) {
    if (!labels || labels.length === 0) {
      labelStrip.innerHTML = '<span class="empty">대기 중 …</span>';
      return;
    }
    labelStrip.innerHTML = labels.slice(0, 8).map(l => {
      const active = l.score >= SCORE_ACTIVE ? ' active' : '';
      const pct = (l.score * 100).toFixed(0);
      return `<span class="chip${active}">${escapeHtml(l.name)} <span class="pct">${pct}%</span></span>`;
    }).join('');
  }

  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => (
      { '&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;', "'":'&#39;' }[c]));
  }

  // ── Bridge wiring ────────────────────────────────────────────────────
  const els = {
    start: document.getElementById('startBtn'),
    stop: document.getElementById('stopBtn'),
    status: document.getElementById('bandStatus'),
    stagePick: document.getElementById('stagePicker'),
    source: document.getElementById('sourcePicker'),
    topk: document.getElementById('topkInput'),
    diagSource: document.getElementById('diag-source'),
    diagTick: document.getElementById('diag-tick'),
    diagInfer: document.getElementById('diag-infer'),
    diagMel: document.getElementById('diag-mel'),
    hint: document.getElementById('band-hint'),
  };

  function setStatus(text, cls) {
    els.status.textContent = text;
    els.status.className = 'status' + (cls ? ' ' + cls : '');
  }

  function ensureZero() {
    if (!window.zero || !window.zero.music) {
      setStatus('window.zero.music unavailable — open inside AgentZero', 'err');
      return false;
    }
    return true;
  }

  async function onStart() {
    if (!ensureZero()) return;
    const source = els.source.value;
    const topK = Math.max(1, Math.min(20, parseInt(els.topk.value, 10) || 5));
    setStatus('starting …');
    els.start.disabled = true;
    try {
      const r = await window.zero.music.start({ source, topK });
      if (!r || !r.ok) {
        const err = r?.error || 'unknown';
        if (err === 'model-missing') {
          setStatus('AST 모델 미설치 — Settings → Music → Download', 'err');
          els.hint.textContent = `모델 파일이 없습니다: ${r.modelPath}`;
        } else {
          setStatus('start failed — ' + err, 'err');
        }
        els.start.disabled = false;
        return;
      }
      setStatus(`LIVE · ${r.source}`, 'live');
      els.diagSource.textContent = r.source;
      els.stop.disabled = false;
    } catch (ex) {
      setStatus('start failed — ' + ex.message, 'err');
      els.start.disabled = false;
    }
  }

  async function onStop() {
    if (!ensureZero()) return;
    setStatus('stopping …');
    try { await window.zero.music.stop(); }
    catch (ex) { console.warn('[agent-band] stop failed', ex); }
    setStatus('idle');
    els.start.disabled = false;
    els.stop.disabled = true;
    performers.clear();
    dancers.clear();
    idolRenderedSize = 0;
    idolPresentTicks = 0;
    volumeLevel = 0;
    climaxActive = false;
    notes.length = 0;
    lastEmitMs.clear();
    renderLabelStrip([]);
    lastSpectrum = null;
    if (smoothed) smoothed.fill(0);
  }

  function bindEvents() {
    els.start.addEventListener('click', onStart);
    els.stop.addEventListener('click', onStop);
    els.stagePick.addEventListener('change', () => pickStage(els.stagePick.value));

    if (window.zero && window.zero.music) {
      // Slow tick (1.5 s) — performer + dance registries + label strip.
      window.zero.music.onTick(tick => {
        const labels = tick.labels || [];
        upsertPerformersFromLabels(labels);
        if (DANCE_TROUPE_ENABLED) upsertDancersFromLabels(labels);
        renderLabelStrip(labels);
        els.diagTick.textContent = `tick ${tickCounter}`;
        els.diagInfer.textContent = `${tick.inferMs} ms`;
        els.diagMel.textContent = `${tick.frames} × ${tick.bins}`;
        // Use the tick's own spectrum as a seed; subsequent 30 Hz events
        // override it. Without this seed, very early in the session the
        // bars sit flat until the first 30 Hz event arrives.
        setSpectrum(tick.spectrum || []);
      });
      // Fast spectrum stream (30 Hz) — used for the bar visualizer.
      window.zero.music.onSpectrum(evt => {
        setSpectrum(evt.spectrum || []);
      });
    }
  }

  // ── Boot ─────────────────────────────────────────────────────────────
  pickStage(els.stagePick.value);
  bindEvents();
  requestAnimationFrame(renderLoop);

  window.addEventListener('beforeunload', () => {
    try { window.zero?.music?.stop(); } catch (_) { /* host gone */ }
  });
})();
