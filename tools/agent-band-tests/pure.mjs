// Hand-aligned reference copies of the pure helpers in
// Project/Plugins/agent-band/agent-band.js.
//
// WHY A COPY: the real functions live inside the plugin's browser IIFE and
// are not exported, and the plugin ships as a classic <script> (not an ES
// module) loaded in a WebView2 sandbox — it cannot `import`. Rather than
// refactor a 172 KB shipping plugin, we mirror the pure, side-effect-free
// helpers here, exactly like the repo already mirrors `parseVideoId` (JS) with
// `ParseYouTubeId` (C#, Project/AgentZeroWpf/Services/Browser/WebDevHost.cs).
//
// CONTRACT: when you edit the regex/logic in agent-band.js, update this file
// AND harness/knowledge/music-curator/agent-band-mapping.md in the same change.
// The tests here then guard the mapping semantics against silent drift.
//
// Source anchors (agent-band.js):
//   labelToPerformer  — lines 638-704
//   parseVideoId      — lines 2130-2141

// Accept a full watch/share/embed/shorts/live URL or a bare 11-char id.
export function parseVideoId(raw) {
  if (!raw) return null;
  const s = String(raw).trim();
  if (/^[A-Za-z0-9_-]{11}$/.test(s)) return s;
  let m;
  if ((m = s.match(/[?&]v=([A-Za-z0-9_-]{11})/)))      return m[1];
  if ((m = s.match(/youtu\.be\/([A-Za-z0-9_-]{11})/))) return m[1];
  if ((m = s.match(/\/embed\/([A-Za-z0-9_-]{11})/)))   return m[1];
  if ((m = s.match(/\/shorts\/([A-Za-z0-9_-]{11})/)))  return m[1];
  if ((m = s.match(/\/live\/([A-Za-z0-9_-]{11})/)))    return m[1];
  return null;
}

// AudioSet label → performer-sprite id. Order is a contract (see
// agent-band-mapping.md "Ordering contract"). The real function calls a
// sticky-random pickMaleVocal(); we inject a deterministic default so the
// mapping *semantics* ("male singing → some male vocal") stay testable.
export function labelToPerformer(label, pickMaleVocal = () => 'vocal-male') {
  const s = String(label).toLowerCase();

  // ── Tier 1: specific instruments ──
  // Variants (electric/bass/machine) MUST precede the base-instrument regex.
  if (/bass guitar/.test(s))                                    return 'elec-bass';
  if (/electric guitar|tapping \(guitar/.test(s))              return 'elec-guitar';
  if (/drum machine|beatbox/.test(s))                          return 'drum-machine';
  if (/dubstep|drum and bass/.test(s))                         return 'edrum';        // genre pre-empt before \bdrum\b
  if (/(?<!speech )synthesizer|\bsampler\b|theremin/.test(s))  return 'synth';        // exclude "Speech synthesizer" (TTS)
  if (/electric piano|electronic organ|hammond organ/.test(s)) return 'keytar';
  if (/scratch(ing)? \(performance|turntable/.test(s))         return 'dj-deck';
  if (/\bcello\b/.test(s))                                     return 'cello';
  if (/\bviola\b/.test(s))                                     return 'viola';
  if (/\bviolin\b|\bfiddle\b/.test(s))                         return 'violin';
  if (/\bcontrabass\b|\bdouble bass\b/.test(s))                return 'contrabass';
  if (/\bharp\b/.test(s) && !/harpsichord/.test(s))            return 'harp';
  if (/\bguitar\b/.test(s))                                    return 'guitar';
  if (/\bflute\b/.test(s))                                     return 'flute';
  if (/\bclarinet\b/.test(s))                                  return 'clarinet';
  if (/\boboe\b/.test(s))                                      return 'oboe';
  if (/french horn|\bhorn\b/.test(s))                          return 'horn';
  if (/\btrumpet\b/.test(s))                                   return 'trumpet';
  if (/\btrombone\b/.test(s))                                  return 'trombone';
  if (/\btuba\b/.test(s))                                      return 'tuba';
  if (/\bpiano\b|\borgan\b/.test(s))                           return 'piano';        // electric/hammond pre-empted to keytar above
  if (/\bdrum\b|cymbal|tom-tom|hi-hat|tabla|\bgong\b/.test(s)) return 'drum';

  // ── Vocals (male only here; female/neutral feed the idol-group controller) ──
  if (/male sing|\bman sing\b/.test(s) && !/female/.test(s))   return pickMaleVocal();

  // ── Tier 2: parent-category fallbacks ──
  if (/bowed string|orchestra|symphony|chamber music/.test(s)) return 'violin';
  if (/plucked string/.test(s))                                return 'guitar';
  if (/woodwind|wind instrument/.test(s))                      return 'flute';
  if (/\bbrass\b/.test(s))                                     return 'trumpet';
  if (/keyboard \(musical\)/.test(s))                          return 'piano';
  if (/percussion/.test(s))                                    return 'drum';

  // ── Genre fallbacks (M0031) ──
  if (/house music|techno|electronic dance|dance music|\bdisco\b|hip hop/.test(s)) return 'dj-deck';
  if (/electronica|electronic music|ambient music|trance music|new-age music/.test(s)) return 'synth';
  if (/\bfunk\b/.test(s))                                       return 'elec-bass';
  if (/\bska\b/.test(s))                                        return 'trumpet';
  if (/\brock\b|heavy metal|grunge/.test(s))                    return 'elec-guitar';

  return null;
}
