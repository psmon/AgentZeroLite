// agent-band.js — M0025 Agent Band plugin.
//
// v0.21.0 changelog (M0030 후속 #1 — 3단 레이아웃 + 스캔 성능):
//   • MP3 레이아웃 3단 분리 — [미니 재생기 280px | 목록(남는 폭 전부) |
//     검색·필터 패널 232px]. 검색/장르 탭/가수 칩이 3단 패널로 이동해
//     목록 가시성 확보. 패널은 🔍 버튼으로 접힘/펼침.
//   • 목록 헤더에 ▶ 전체(순차재생) / 🔀 랜덤재생 토글 — 현재 필터 목록
//     기준, 곡 종료 시 순차 or 랜덤으로 자동 이어재생.
//   • 스캔 성능 — 호스트가 트랙 upsert를 파일당 1건 → ~2s/100건 배치
//     (mp3.tracks)로 전환해 WPF 디스패처·postMessage 폭주 제거. 플러그인은
//     스캔 중 목록 렌더를 15s 스로틀(진행 스트립은 그대로), O(1) 인덱스맵
//     업서트, per-item 리스너 → plList 위임 리스너 1개. 스캔 완료 시
//     mp3.list 재로드로 정렬 복원 + 즉시 렌더.
//
// v0.20.0 changelog (M0030 — 태깅 체계·무대 규칙 재정의·느낌 카드):
//   • 태깅 4계열 확립 — 장르(Category) / 가수(Artist) / 음악적느낌(Moods,
//     AST 무드 라벨 7종 실시간 누적·영속) / 악기(Instruments).
//   • 리얼타임 재조정 — 명시적 성별 라벨 2틱 지속 시 사전분류(LLM/비전)보다
//     우선해 무대 성별을 재조정 ('그룹' 판정만은 유지 — 오디오로 솔로/그룹
//     구분 불가). 곡 전환 시 리셋.
//   • 무대 규칙 — 솔로 판정: 1인 보컬만 등장하고 재생 중 절대 사라지지
//     않음(노래 신호 시 노래 애니, 아니면 기본 애니). 걸그룹 모드는
//     '그룹 판정 && 신나는 음악(업비트 장르 or exciting/댄스 라벨 10s
//     히스테리시스)'일 때만 — 신나도 솔로 판정이면 1인 모드.
//   • 검색 강화 — 통합 검색(가수/제목/느낌/악기, 한/영), 장르 선택 시
//     하위 가수 칩(곡수 표시) 원클릭 필터.
//   • 느낌 카드 자동추천 — 🃏 카드 뷰: LLM이 라이브러리 태그 인벤토리로
//     카드(제목+설명+장르/가수/느낌/악기 필터)를 생성·영속. 카드 클릭 시
//     풀에서 랜덤 재생, 곡이 끝나면 같은 풀에서 자동 이어재생.
//
// v0.19.0 changelog (M0029 후속 #5 — 커버 아트 비전 성별):
//   • 성별 미확정 곡이 재생을 시작하면 호스트가 ID3 APIC 커버를 Florence-2
//     OD로 분석해 남/녀/그룹(person 3+)을 판정 — 판정 즉시 무대 캐릭터가
//     동적 전환되고 결과는 SQLite에 영속(다음 재생은 캐시 히트).
//   • 사전 프로브(실제 커버 8장)로 검증: 인물 커버는 man/woman 라벨이
//     안정적으로 나오고, 일러스트/사물 커버는 판정 보류 → 보조 신호로 설계.
//     우선순위: LLM(아티스트 지식) > 커버 비전 > 환경음(female 감지) > 기본 남성.
//     (LLM 분류가 나중에 도착하면 비전 판정을 덮어씀 — 태그 우선 계약 유지)
//
// v0.18.1 changelog (M0029 후속 #4 — 스캔 진행 UI 레이아웃 분리):
//   • 스캔 중 가변 길이 진행 텍스트(파일명 포함)가 #ytMeta 배지 폭을 바꿔
//     좌측 버튼들이 출렁이며 클릭이 어렵던 문제 — 진행 표시를 버튼 줄에서
//     분리해 바 아래 고정 높이(22px) 진행 스트립(#mp3-progress, 진행바+
//     텍스트)으로 이동. 상태 배지는 max-width 220px + 말줄임으로 고정,
//     재생 배지도 '▶ 재생'으로 단축. 탭 전환/부트 시 스트립 복원.
//
// v0.18.0 changelog (M0029 후속 #3 — MP3 무대 디렉터 + 버그픽스 3종):
//   • MP3 전용 공연 연출 계층 신설 (유튜브 비전 연출과 별도) — 장르를 이미
//     알기에 컨셉 선결정: 발라드/재즈/클래식 → 🎙 솔로 무대(성별 중요 —
//     LLM 태그 판정 vocalGender 우선, 미확정 시 환경음 기준·기본 남성,
//     female 라벨 감지 시 여성 전환), K-Pop/EDM/힙합/록 또는 LLM '그룹'
//     판정 → 🎤 걸그룹 댄스 무대(기존 group 스테이징 재사용, 노래/댄스
//     전환은 환경음 그대로). 남성 솔로는 vocal-2가 노래 신호로 등장하고
//     여성 풀은 무대에서 내려감. 악기 등장 연출은 기존과 동일.
//   • LLM 분류가 성별까지 판정 ('카테고리|성별' 응답 → vocalGender 영속,
//     재생 중 분류 도착 시 연출 즉시 갱신, ♀/♂/👥 배지 표시).
//   • 버그픽스: (1) 탭 전환 시 스캔 진행표시 소실 — 마지막 진행 텍스트
//     보관·복원 (스캔 잡 자체는 원래 계속 돌고 있었음). (2) 악기 누적이
//     1개에서 멈춤 — 첫 푸시 후 2.5s 스로틀 안에 감지된 신규 악기가 영구
//     유실되던 것을 dirty 플래그로 다음 틱 재시도. (3) 재스캔 효율 —
//     LLM 판정(✦) 곡은 분류 패스 제외(기존), LLM 꺼짐 시 카테고리 보유
//     곡도 스킵.
//
// v0.17.0 changelog (M0029 후속 #2 — 2단계 스캔 + 미니 플레이어 UX):
//   • 스캔이 2단계로 분리 — 1차: LLM 없이 태그만 빠르게 전곡 등록(미분류),
//     2차: 미분류 백로그를 비동기로 하나씩 LLM 분류(mp3.track 이벤트로
//     라이브 갱신). 분류가 끝날 때까지 목록을 못 보던 병목 제거.
//     상태 배지가 단계를 표시(📂 스캔 n/m → 🧠 분류 n/m), '미분류' 탭 추가.
//   • MP3 플레이어 미니멀화 — 커버(ID3 APIC, /__cover/{id} 호스트 라우트)
//     + 메타 + 재생 컨트롤만 담는 고정폭(280px) 카드로 축소하고,
//     우측 플레이리스트가 나머지 폭 전부를 차지(정보의 중심은 목록).
//
// v0.16.1 changelog (M0029 후속 #1 — MP3 재생 실패 수정):
//   • 원인: SetVirtualHostNameToFolderMapping은 Chromium 미디어 스택의
//     HTTP Range 요청에 응답하지 못함 → 스캔(파일 fetch)은 되는데
//     <audio> 재생만 실패. 호스트가 mp3.local을 WebResourceRequested
//     인터셉터로 직접 서빙(206 Partial Content, 진짜 스트리밍+시킹)하도록
//     전환. 플러그인은 재생 오류를 코드명으로 배지 표시(진단 가시화).
//
// v0.16.0 changelog (M0029 — local MP3 playlist):
//   • 상단 소스 탭 [▶ YouTube | 🎵 MP3] — 재생 소스를 선택. 어느 쪽이든
//     하단 악단 시각화는 동일하게 동작 (SystemLoopback이 OS 믹서를 듣는
//     구조라 <audio> 재생도 새 배선 없이 밴드를 구동).
//   • MP3 모드는 일반 플레이어 모드(악기·가수) 전용 — 걸그룹(비전) 모드는
//     유튜브 iframe 캡처 기반이므로 MP3 선택 시 Singer가 solo로 강제되고
//     비전 루프는 꺼진다 (유튜브로 돌아오면 복원).
//   • 재생목록은 유튜브(localStorage)와 분리된 SQLite 영속 (host mp3.* op):
//     재설치/마이그레이션에도 유지. 스캔은 백그라운드 잡 — mp3.track 이벤트로
//     스캔 중에도 목록이 증분 갱신되어 스캔된 곡부터 바로 재생 가능.
//   • 분류는 mp3 태그+파일명+폴더명 힌트로 호스트가 LLM 일회성 호출
//     (YT_CATEGORIES 동일 세트, tag/fallback 폴백 + 출처 배지).
//   • 재생 중 AST 라벨에서 들리는 악기를 실시간 누적 → mp3.setInstruments로
//     영속. 목록에서 악기명 검색(포함/단독) 필터 지원 — "피아노" 포함,
//     "피아노만 있음(단독)" 등. 한/영 악기명 모두 인식.
//
// v0.14.1 changelog (M0026 follow-up #4 — girl-group dance distribution):
//   • even dance distribution — the genre→backup matching now ROTATES every
//     ~6s (ROTATE_TICKS) by dropping assignments so assignBackups re-picks
//     the least-used idols, spreading the dance spotlight across the group
//     instead of one member holding it while a genre sustains.
//   • periodic climax — every 60s of live group time (CLIMAX_PERIOD_MS) the
//     whole group does a fixed 5s all-dance burst (CLIMAX_HOLD_MS), checked
//     per-frame in performState() so the 5s window is exact, not quantized
//     to the 1.5s tick. Group mode only; solo is unaffected.
//
// v0.14.0 changelog (M0026 follow-up #3):
//   • played list now PERSISTS to localStorage (YT_STORE_KEY) so the
//     history survives app restarts. The playlist panel can be opened /
//     closed independently via a "📂 목록" toggle in the URL bar — open it
//     to browse / replay saved videos even with no video loaded (the player
//     shows a placeholder). Auto-opens on boot when saved history exists.
//   • girl-group policy change — the GROUP ('group') mode now EXCLUDES the
//     main vocal (vocal-ex): the girl-group is the 7 idols vox7-1…7 only.
//     The center idol (vox7-1) performs on vocal / jazz-ballad lead mood;
//     the rest dance to their assigned genres. SOLO mode is unchanged —
//     it still stages vocal-ex (the dedicated main female vocal). Roster is
//     now mode-derived via femalePool() (SOLO=[vocal-ex], GROUP=the 7).
//
// v0.13.0 changelog (M0026 — YouTube embed stage):
//   • the stage area is split — an embedded YouTube player occupies the
//     upper region (above an optional playlist panel), the band stays in
//     the lower region. The window grid (header / 1fr / footer) is
//     unchanged; only the 1fr cell is divided (see agent-band.css), and
//     the band canvas now lives in #band-region so fitCanvasToParent sizes
//     it to that lower area automatically.
//   • 무대 상단 URL bar: paste a YouTube link → [붙이기] embeds + plays it.
//     videoId is parsed from watch / youtu.be / embed / shorts / live URLs
//     or a bare 11-char id.
//   • metadata + category come from the host (new bridge ops):
//       - youtube.oembed → title / author / thumbnail (fetched host-side to
//         dodge the oEmbed endpoint's missing CORS headers; SSRF-safe).
//       - llm.classify   → ONE category from YT_CATEGORIES via a STATELESS
//         one-shot LLM call (does not pollute chat.* history). Offline /
//         failure falls back to keywordCategory().
//   • played videos accumulate in a categorized playlist (tabs filter by
//     category; click an item to replay it).
//   • band reaction is UNCHANGED — the video's audio rides the system
//     mixer, the existing SystemLoopback capture + AST tick drive the
//     performers. No new audio path. Press ▶ Start (Source=System Loopback).
//
// v0.12.2 changelog:
//   • per-stage layout adjustment (first introduction) — STAGE_Y_OFFSET maps
//     a background name → vertical px nudge for the cast so performers land
//     on that background's stage. Stadium (fifa26) raises the cast 50px;
//     all other stages are unchanged (offset 0).
//
// v0.12.1 changelog:
//   • new stage background "Stadium (FIFA 26)" (assets/stages/fifa26.png).
//   • girl-group ('group') mode is female-only — male singers are dropped
//     (instruments still appear). Solo mode keeps prior behavior.
//
// v0.12 changelog:
//   • singer ASSIGNEE staging (replaces v0.11 loudness climax, which didn't
//     distribute who-performs well). Singers now work like instruments —
//     each is responsible for a label/mood and performs only while it
//     sounds — but the genre→backup assignment is randomised and spread
//     EVENLY (least-used wins) since a group's voices can't be told apart:
//       - main vocal (vocal-ex): lead + spotlight for jazz / ballad-
//         classical moods; performs while singing or a lead mood is active.
//       - backup idols (vox7): each assigned a danceable genre (kpop /
//         hiphop / waacking / cheer; Speech→rap counts as hiphop). Two
//         co-occurring genres a,b light up two different backups.
//       - females prioritised; males only on explicit "Male singing".
//   • SINGER MODE option (#singerMode): 'group' (girl-group, main+backups)
//     or 'solo' (main only). Concept targets an orchestra / girl-group pop
//     stage.
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

  // Per-stage layout adjustment (first introduced v0.12.2). Some backgrounds
  // put the usable "floor" at a different height than the default ground
  // line, so performers need a vertical nudge to land on the stage. Value =
  // pixels added to the band-row baseY (negative = up). Stages not listed
  // here use 0 (unchanged). Add entries as new backgrounds need it.
  const STAGE_Y_OFFSET = {
    'fifa26': -50,   // World Cup stadium — raise the cast 50px to sit on the pitch stage
  };

  // Spectrum visual config.
  const SPEC_GRADIENT_FROM = [0x00, 0xe5, 0xff];
  const SPEC_GRADIENT_TO   = [0xff, 0x2d, 0x95];
  const SPEC_ATTACK  = 0.40;   // bars going UP — fast snap
  const SPEC_RELEASE = 0.10;   // bars going DOWN — smooth decay

  // ── Singer staging — assignee model (v0.12) ──────────────────────────
  // Replaces the v0.11 loudness-climax gate (it didn't distribute who-was-
  // performing well). Singers now work like instruments: each performer is
  // RESPONSIBLE for a label/mood and plays its active animation only while
  // that responsibility is sounding. But because a group's voices can't be
  // told apart, the genre→backup assignment is randomised and spread
  // EVENLY across the pool (least-used wins) so everyone gets featured.
  //
  //   • main vocal (vocal-ex) — the lead. Always performs while present,
  //     and is the spotlight for the LEAD moods (jazz / ballad / classical
  //     + female singing).
  //   • backup idols (vox7) — each gets assigned a danceable genre
  //     (kpop / hiphop / waacking / cheer; speech→rap counts as hiphop).
  //     A backup performs only while its assigned genre is active, so two
  //     co-occurring genres a,b light up two different backups.
  //   • SINGER MODE: 'group' (girl-group, main + backups) or 'solo'
  //     (main only). Read live from the #singerMode <select>.
  const STYLE_ACTIVE = DANCE_PRESENT;        // genre score to count as "sounding" this tick
  const LEAD_STYLES  = new Set(['jazz', 'ballet']); // moods the MAIN VOCAL owns (jazz / ballad-classical)

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
  // Vocals: only MALE-specific labels ("Male singing") are mapped here
  //   (to the male pool, by operator rule males appear only when explicitly
  //   recognized). FEMALE + gender-neutral vocals are handled by the idol
  //   group controller (femaleVocalSignal / upsertIdolGroup), not here.
  //
  // Sprite roster (v0.10):
  //   Female roster splits by Singer mode (M0026 policy change):
  //     • SOLO  → `vocal-ex`, the dedicated main vocal (idle+play sheets —
  //       she SINGS, stays dead-center).
  //     • GROUP → the SEVEN idols vox7-1 … vox7-7 (sing+dance; idle+dance
  //       sheets). Per operator: the main vocal (vocal-ex) is EXCLUDED from
  //       the girl-group — the group is the 7 idols ONLY (vocal-ex never
  //       appears in group mode). The center idol (vox7-1) acts as the
  //       on-vocal performer; the rest dance to their assigned genres.
  //   Male pool — unchanged:
  //     vocal-2 = male (dark hair, red coat)  vocal-4 = male (silver hair, purple coat)
  const SOLO_VOCAL   = 'vocal-ex';   // SOLO mode only
  const GIRL_GROUP   = ['vox7-1', 'vox7-2', 'vox7-3', 'vox7-4', 'vox7-5', 'vox7-6', 'vox7-7']; // GROUP mode (7)
  // Full roster — only for layout / fade / FEMALE_POOL bookkeeping. The
  // ACTIVE roster for a given mode comes from femalePool() below.
  const VOCAL_FEMALE = [SOLO_VOCAL, ...GIRL_GROUP];
  const VOCAL_MALE   = ['vocal-2', 'vocal-4'];
  const FEMALE_POOL   = new Set(VOCAL_FEMALE);
  let maleCursor = 0;

  // The active female roster for the current Singer mode. SOLO = just the
  // main vocal; GROUP = the 7-member girl-group (vocal-ex excluded).
  function femalePool() { return singerMode === 'solo' ? [SOLO_VOCAL] : GIRL_GROUP; }

  // Idols sing AND dance. They ship `idle` + `dance` sheets (no `play`
  // sheet), so their active logical state ('play') is remapped to the
  // `dance` file by stateSheet(). vocal-ex (SOLO) is NOT in this set — she
  // keeps her `play` (singing) sheet. All are true vocals for stage layout.
  const IDOL_VOCALS = new Set(GIRL_GROUP);

  // ── Idol group staging (v0.9) ────────────────────────────────────────
  // The female pool is staged as a GROUP, not a soloist. The first female
  // recognition brings the main vocal (center). While female vocal keeps
  // coming — and the richer the signal (sustained presence + genre
  // variety) — the group grows one member per tick, fanning out left/right
  // around the main vocal. When the voice stops, it shrinks back one
  // member per tick until only the (then fading) main vocal remains.
  const IDOL_RAMP_TICKS_PER_MEMBER = 2;  // +1 target member per N sustained ticks
  // Max group size is now mode-derived (femalePool().length) — SOLO=1, GROUP=7.

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

  // Sum AST label scores per dance-style (genre) this tick — e.g.
  // "Hip hop music" + "Rapping" stack onto hiphop. Speech is folded into
  // hiphop (rap) inside labelToDance.
  function styleScoresFromLabels(labels) {
    const m = new Map();
    for (const l of labels) {
      const st = labelToDance(l.name);
      if (!st) continue;
      m.set(st, (m.get(st) || 0) + l.score);
    }
    return m;
  }

  // Assign each active danceable (non-lead) genre to a backup singer,
  // spread EVENLY across the pool (least-used wins) so the group takes
  // turns. Assignments persist while the genre stays active and the singer
  // stays on stage; co-occurring genres a,b therefore light up two
  // different backups. Returns the set of backup ids that should perform.
  function assignBackups(activeStyles, presentBackups) {
    for (const [style, id] of [...styleAssignee]) {
      if (!activeStyles.includes(style) || !presentBackups.includes(id)) styleAssignee.delete(style);
    }
    const taken = new Set(styleAssignee.values());
    for (const style of activeStyles) {
      if (styleAssignee.has(style)) continue;
      const avail = presentBackups.filter(id => !taken.has(id));
      if (!avail.length) break;
      avail.sort((a, b) => (backupUsage.get(a) || 0) - (backupUsage.get(b) || 0));
      const pick = avail[0];
      styleAssignee.set(style, pick);
      taken.add(pick);
      backupUsage.set(pick, (backupUsage.get(pick) || 0) + 1);
    }
    return taken;
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
   *    playing: boolean,    // female pool only — assignee-model active flag (per tick)
   *    addedAt: number,
   *    lastSeenTick: number,
   *    fading: boolean,
   *    fadeAt: number,
   *  }} Performer
   */
  /** @type {Map<string, Performer>} */
  const performers = new Map();
  let tickCounter = 0;

  // Singer mode — 'group' (girl-group: main + backups) or 'solo' (main
  // only). Read live from the #singerMode <select> each tick.
  let singerMode = 'group';

  // Backup assignee distribution. styleAssignee maps an active danceable
  // genre → the backup id currently performing it; backupUsage tracks how
  // many times each backup has ever been assigned so new assignments go to
  // the LEAST-used singer (even spread across the pool over a session).
  const styleAssignee = new Map();   // style → backupId
  const backupUsage   = new Map();   // backupId → cumulative assignment count

  // Even dance distribution + periodic climax (girl-group only).
  //   • ROTATE the genre→backup matching every ROTATE_TICKS so the dance
  //     duty spreads across the group instead of one member holding it while
  //     a genre sustains — clearing styleAssignee forces assignBackups to
  //     re-pick the LEAST-used members, which rotates the spotlight.
  //   • CLIMAX: every CLIMAX_PERIOD_MS of live group time, hold an all-dance
  //     burst for CLIMAX_HOLD_MS (every present member dances at once).
  const ROTATE_TICKS     = 4;        // ~6s at the 1.5s tick — reassign least-used
  const CLIMAX_PERIOD_MS = 60000;    // climax every 60s
  const CLIMAX_HOLD_MS   = 5000;     // 5s all-dance hold
  let   rotateTick   = 0;
  let   climaxNextAt = 0;            // scheduled next climax start (0 = unscheduled)
  let   climaxUntil  = 0;            // all-dance active while performance.now() < this

  // ── Vision (M0028) — GIRL-GROUP MODE ONLY ───────────────────────────
  // Florence-2 object detection over the playing MV frame drives, in group
  // mode only:
  //   • person count       → girl-group member count (recent-max smoothed),
  //   • frame-diff motion   → dance idle(sing) ↔ action(dance) sync,
  //   • visible instruments → vision-first instrument summon (audio = fallback).
  // Degrades cleanly to the prior audio-only behavior when the Florence-2 model
  // isn't downloaded, the mode is solo, or no video is playing.
  const VISION_ANALYZE_MS   = 2500;   // Florence-2 OD cadence (slow; ~1.4s CPU)
  const VISION_MOTION_MS    = 300;    // frame-diff cadence (cheap, host-side)
  const VISION_COUNT_WIN_MS = 8000;   // rolling window for "recent max" person count
  const VISION_MOTION_ON    = 0.055;  // motion energy → action (hysteresis high)
  const VISION_MOTION_OFF   = 0.028;  // → idle (hysteresis low)
  let visionActive       = false;     // loops running (group + model + live)
  let visionAnalyzeTimer = 0;
  let visionMotionTimer  = 0;
  let visionAnalyzeBusy  = false;
  let visionMotionBusy   = false;
  let visionCountSamples = [];        // [{ t, n }] recent person counts
  let visionMemberTarget = 0;         // recent-max person count (0 = no override)
  let visionAction       = false;     // motion → dance(action) vs idle(sing)
  let visionInstruments  = new Map(); // sprite id → score, refreshed each analyze
  let bandLive           = false;     // music session running

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
      p = { id, score, state, playing: false, addedAt: now, lastSeenTick: tickCounter, fading: false, fadeAt: 0 };
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
      // Girl-group mode is female-only: drop male singers (instruments are
      // still fine). Solo mode keeps the existing behavior.
      if (singerMode === 'group' && VOCAL_MALE.includes(id)) continue;
      const prev = collapsed.get(id);
      if (prev === undefined || l.score > prev) collapsed.set(id, l.score);
    }

    // M0028 — vision-first instruments (girl-group). When the MV visibly shows
    // instruments, prefer them; the audio AST list is the fallback only when
    // vision saw none this cycle.
    if (visionActive && singerMode === 'group' && visionInstruments.size > 0) {
      collapsed.clear();
      for (const [id, sc] of visionInstruments) collapsed.set(id, sc);
    }

    // M0029/M0030 — MP3 남성 솔로 연출: 성별이 남성이면 여성 아이돌 대신
    // 남성 보컬이 무대에 선다. M0030 고정 규칙 — 재생 중에는 항상 최소
    // PRESENT 점수로 upsert되어 절대 사라지지 않고, 노래 신호(v ≥ ACTIVE)일
    // 때만 노래 애니(play), 아니면 기본 애니(idle). 악기 등장은 기존 경로.
    if (mp3MaleSoloActive()) {
      const v = mp3AnyVocalSignal(labels);
      const maleId = VOCAL_MALE[0];
      collapsed.set(maleId, Math.max(collapsed.get(maleId) || 0, Math.max(v, SCORE_PRESENT)));
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

  // ── Idol group controller (assignee model) ──────────────────────────
  //
  // Presence: the girl-group comes on when there's a female/neutral vocal
  // OR an active danceable genre (instrumental pop/dance still summons the
  // group). vocal-ex is slot 0 (always center). In SOLO mode only the lead
  // is staged; in GROUP mode the group grows with richness.
  //
  // Performance (who animates): assignee model, not loudness —
  //   • main vocal (vocal-ex) performs while singing OR a LEAD mood
  //     (jazz / ballad-classical) is active → she's the spotlight there.
  //   • each active non-lead genre is assigned to a backup (least-used →
  //     even spread); that backup performs while its genre sounds, so two
  //     co-occurring genres a,b light up two different backups.
  function upsertIdolGroup(labels, now) {
    // M0029 후속#3 — MP3 남성 솔로 중에는 여성 풀 전체가 무대에서 내려간다
    // (남성 보컬은 upsertPerformersFromLabels의 collapsed 경로로 등장).
    if (mp3MaleSoloActive()) {
      idolRenderedSize = 0;
      idolPresentTicks = 0;
      styleAssignee.clear();
      for (const id of VOCAL_FEMALE) {
        const p = performers.get(id);
        if (p && !p.fading) { p.fading = true; p.fadeAt = now; p.state = 'idle'; }
      }
      return;
    }
    const fem = femaleVocalSignal(labels);
    const styles = styleScoresFromLabels(labels);
    const activeStyles   = [...styles.entries()].filter(([, sc]) => sc >= STYLE_ACTIVE).map(([st]) => st);
    const leadActive     = activeStyles.some(st => LEAD_STYLES.has(st));
    const nonLeadStyles  = activeStyles.filter(st => !LEAD_STYLES.has(st));

    // Active roster for the current mode: SOLO=[vocal-ex], GROUP=the 7 idols.
    const pool = femalePool();
    const poolMax = pool.length;   // 1 (solo) or 7 (girl-group)

    const onStage = idolRenderedSize > 0;
    const vocalPresent = fem.score >= (onStage ? SCORE_KEEP : SCORE_PRESENT);
    // M0030 — MP3 솔로(여성) 고정: 재생 중에는 리드가 절대 무대에서 내려가지
    // 않는다. 노래 애니는 playing 플래그(vocalPresent/leadActive)가 결정.
    const present = vocalPresent || activeStyles.length > 0 || mp3SoloPinActive();

    let target;
    if (present) {
      idolPresentTicks++;
      if (singerMode === 'solo') {
        target = 1;   // main vocal only
      } else {
        // Big enough to give every active non-lead genre its own dancer,
        // and grows further with sustained singing / explicit female.
        target = Math.max(
          1 + nonLeadStyles.length,
          1 + Math.floor(idolPresentTicks / IDOL_RAMP_TICKS_PER_MEMBER) + (fem.explicit ? 1 : 0),
        );
        target = Math.max(1, Math.min(poolMax, target));
      }
    } else {
      idolPresentTicks = 0;
      target = 0;   // ramp the whole group down
    }

    // M0028 — vision override (girl-group only): the recent-max person count in
    // the MV sets the member target directly. 0 persons over the whole window =
    // no override (keep the audio-derived target so an instrumental/object cut
    // doesn't collapse the group).
    const visionMembers = (visionActive && singerMode === 'group' && visionMemberTarget > 0)
      ? Math.max(1, Math.min(poolMax, visionMemberTarget)) : 0;
    if (visionMembers > 0) target = visionMembers;
    const presentEff = present || visionMembers > 0;

    // Chase the target one member per tick.
    if (target > idolRenderedSize) idolRenderedSize++;
    else if (target < idolRenderedSize) idolRenderedSize--;
    if (idolRenderedSize < 0) idolRenderedSize = 0;
    if (idolRenderedSize > poolMax) idolRenderedSize = poolMax;  // clamp when mode shrinks the pool

    const members = pool.slice(0, idolRenderedSize);
    const memberSet = new Set(members);
    // Center performer: SOLO → vocal-ex, GROUP → vox7-1 (the lead idol).
    const leadId = members[0];
    const presentBackups = members.filter(id => id !== leadId);

    // Periodic climax — every 60s of live group time, a 5s all-dance burst.
    // performState() reads climaxUntil per-frame so the 5s window is crisp
    // regardless of the 1.5s tick. Reset when the group is absent / in solo.
    if (singerMode === 'group' && presentEff && idolRenderedSize > 0) {
      if (climaxNextAt === 0) climaxNextAt = now + CLIMAX_PERIOD_MS;   // first climax at +60s
      if (now >= climaxNextAt) { climaxUntil = now + CLIMAX_HOLD_MS; climaxNextAt = now + CLIMAX_PERIOD_MS; }
    } else {
      climaxNextAt = 0; climaxUntil = 0; rotateTick = 0;
    }

    // Rotate the matching duty so dancers take turns (even distribution):
    // periodically drop assignments so assignBackups re-picks least-used.
    if (++rotateTick >= ROTATE_TICKS) { rotateTick = 0; styleAssignee.clear(); }

    // Distribute active non-lead genres across the present backups.
    const playingBackups = assignBackups(nonLeadStyles, presentBackups);

    // Presence score keeps members on stage (animation is decided by the
    // `playing` flag below, not this score). Floor so they don't fade
    // while the group is up.
    const memberScore = presentEff ? Math.max(fem.score, SCORE_PRESENT) : SCORE_PRESENT;
    for (const id of members) {
      upsertPerformer(id, memberScore, now);
      const p = performers.get(id);
      if (p) {
        p.playing = id === leadId
          ? (vocalPresent || leadActive)   // center: performs on vocal / jazz-ballad lead mood
          : playingBackups.has(id);        // others: dance while their assigned genre sounds
      }
      // Preload both sheets so the idle↔play/dance flip is instant.
      ensureSpriteSet(id, 'idle');
      ensureSpriteSet(id, 'play');
    }

    if (idolRenderedSize === 0) styleAssignee.clear();

    // Peel-off: any female-pool member NOT in the current roster fades
    // promptly (one per tick as the group shrinks) instead of lingering on
    // the unseen timer. Iterates the FULL roster so a mode switch (e.g.
    // group→solo) also fades the now-excluded sprites — vocal-ex in group
    // mode, the vox7 idols in solo mode.
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
    // Speech is treated as rap (operator decision) — spoken-word reads as a
    // hip-hop performance on the band stage.
    if (/hip hop|hiphop|\brap\b|rapping|trap music|\bspeech\b|speaking|narration|monologue/.test(s))
                                                                          return 'hiphop';

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

    const baseY = h * STAGE_BASE_Y + (STAGE_Y_OFFSET[currentStage] || 0);
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
  let currentStage = '';   // active background name — drives STAGE_Y_OFFSET

  function pickStage(name) {
    currentStage = name;
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

  // The animation state a performer should SHOW this frame.
  //   • fading → idle.
  //   • female pool → assignee model: each member carries a `playing` flag
  //     set per tick (main = lead/spotlight, backups = assigned-genre active).
  //   • instruments / male vocals → their score-derived p.state, unchanged.
  function performState(p) {
    if (p.fading) return 'idle';
    if (FEMALE_POOL.has(p.id)) {
      // M0028 — in girl-group with vision active, the MV's frame-diff motion
      // drives the whole group: action → dance, still → idle(singing). This
      // takes over from the audio assignee/climax model for tight dance sync.
      if (visionActive && singerMode === 'group') return visionAction ? 'play' : 'idle';
      // Climax window → every member dances at once (checked per-frame so the
      // 5s hold is exact, not quantized to the 1.5s tick).
      if (climaxUntil && performance.now() < climaxUntil) return 'play';
      return p.playing ? 'play' : 'idle';
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

    // Keep the smoothed spectrum current (drives the bars + note particles).
    tickSmoothSpectrum();

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
    singerMode: document.getElementById('singerMode'),
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
      // M0028 — kick the girl-group vision loops (no-op in solo / no model).
      bandLive = true;
      startVisionLoops();
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
    styleAssignee.clear();
    backupUsage.clear();
    rotateTick = 0;
    climaxNextAt = 0;
    climaxUntil = 0;
    notes.length = 0;
    lastEmitMs.clear();
    renderLabelStrip([]);
    lastSpectrum = null;
    if (smoothed) smoothed.fill(0);
    // M0028 — tear down vision loops + baseline.
    bandLive = false;
    stopVisionLoops();
    try { window.zero && window.zero.vision && window.zero.vision.reset(); } catch (_) {}
  }

  function bindEvents() {
    els.start.addEventListener('click', onStart);
    els.stop.addEventListener('click', onStop);
    els.stagePick.addEventListener('change', () => pickStage(els.stagePick.value));

    // Singer mode (group / solo). Switching to solo while live trims the
    // group back to the lead on the next tick (handled by upsertIdolGroup).
    singerMode = els.singerMode ? els.singerMode.value : 'group';
    if (els.singerMode) {
      els.singerMode.addEventListener('change', () => {
        singerMode = els.singerMode.value;
        // M0028 — vision is group-only: start/stop the loops on mode toggle.
        if (bandLive) {
          if (singerMode === 'group') startVisionLoops();
          else stopVisionLoops();
        }
      });
    }

    if (window.zero && window.zero.music) {
      // Slow tick (1.5 s) — performer + dance registries + label strip.
      window.zero.music.onTick(tick => {
        const labels = tick.labels || [];
        upsertPerformersFromLabels(labels);
        if (DANCE_TROUPE_ENABLED) upsertDancersFromLabels(labels);
        mp3StageTick(labels);        // M0030 — 무대 디렉션(모드/성별 리얼타임 재조정)
        mp3AccumulateTags(labels);   // M0029/M0030 — 재생 중 악기+느낌 태그 누적
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

  // ── YouTube embed + categorized playlist (M0026) ─────────────────────
  //
  // The stage's upper region hosts an embedded YouTube player; the band
  // stays in the lower region. Audio reaction is automatic — the video's
  // sound goes out the system mixer, the existing SystemLoopback capture
  // picks it up, and the AST tick drives the performers exactly as before
  // (no new audio wiring). Metadata + category come from the host:
  //   • youtube.oembed  → title / author / thumbnail (no CORS, SSRF-safe)
  //   • llm.classify    → one category from YT_CATEGORIES (stateless; does
  //                       NOT touch the chat history). Offline/failure falls
  //                       back to keywordCategory().
  const YT_CATEGORIES = ['재즈', 'K-Pop', '클래식', '힙합', 'EDM', '발라드', '록', 'OST', '기타'];
  const YT_STORE_KEY  = 'agentBand.playlist.v1';   // localStorage — survives restarts
  const YT_STORE_MAX  = 60;                          // cap persisted entries
  const ytPlaylist = [];   // { videoId, title, author, thumbnail, category, url, by }
  let ytFilter = 'all';
  let currentVideoId = null;   // the video currently loaded in the iframe
  let playlistOpen = false;    // panel open/closed (set on boot from saved history)

  const ytEls = {
    top:      document.getElementById('yt-top'),
    player:   document.getElementById('yt-player'),
    playlist: document.getElementById('yt-playlist'),
    empty:    document.getElementById('ytEmpty'),
    toggle:   document.getElementById('ytListToggle'),
    url:      document.getElementById('ytUrl'),
    paste:    document.getElementById('ytPaste'),
    meta:     document.getElementById('ytMeta'),
    frame:    document.getElementById('ytFrame'),
    title:    document.getElementById('ytTitle'),
    channel:  document.getElementById('ytChannel'),
    by:       document.getElementById('ytBy'),
    cat:      document.getElementById('ytCat'),
    plCount:  document.getElementById('plCount'),
    plTabs:   document.getElementById('plTabs'),
    plList:   document.getElementById('plList'),
  };

  // ── Persistence (localStorage) ──────────────────────────────────────
  // The played list is kept per WebView origin so it survives app
  // restarts — the user's history is there next time they open the band.
  function savePlaylist() {
    try { localStorage.setItem(YT_STORE_KEY, JSON.stringify(ytPlaylist.slice(0, YT_STORE_MAX))); }
    catch (_) { /* storage full / disabled — non-fatal */ }
  }
  function loadStoredPlaylist() {
    try {
      const arr = JSON.parse(localStorage.getItem(YT_STORE_KEY) || '[]');
      if (Array.isArray(arr)) {
        for (const p of arr) {
          if (p && typeof p.videoId === 'string' && /^[A-Za-z0-9_-]{11}$/.test(p.videoId)) {
            ytPlaylist.push({
              videoId: p.videoId, title: p.title || '', author: p.author || '',
              thumbnail: p.thumbnail || '', category: p.category || '기타',
              url: p.url || `https://www.youtube.com/watch?v=${p.videoId}`, by: p.by || 'keyword',
            });
          }
        }
      }
    } catch (_) { /* corrupt store — start fresh */ }
  }

  // Show/hide the top region. #yt-top is visible when media is loaded OR
  // the playlist panel is open. Source-aware (M0029): the YouTube iframe
  // pane and the MP3 player pane swap depending on sourceMode; the playlist
  // aside is shared (its contents re-render per source).
  function updateTopVisibility() {
    const hasMedia = sourceMode === 'mp3' ? (mp3CurrentId != null) : !!currentVideoId;
    const listLen = sourceMode === 'mp3' ? mp3Tracks.length : ytPlaylist.length;
    if (ytEls.top)      ytEls.top.classList.toggle('hidden', !(hasMedia || playlistOpen));
    if (ytEls.player) {
      ytEls.player.classList.toggle('empty', !currentVideoId);
      ytEls.player.classList.toggle('hidden', sourceMode === 'mp3');
    }
    if (mp3Els.player)  mp3Els.player.classList.toggle('hidden', sourceMode !== 'mp3');
    if (ytEls.playlist) ytEls.playlist.classList.toggle('hidden', !playlistOpen);
    if (ytEls.toggle) {
      ytEls.toggle.classList.toggle('on', playlistOpen);
      ytEls.toggle.textContent = playlistOpen
        ? '📂 목록 닫기'
        : `📂 목록${listLen ? ' (' + listLen + ')' : ''}`;
    }
  }

  function togglePlaylist() { playlistOpen = !playlistOpen; updateTopVisibility(); }

  // Call a host op directly via the bridge's generic invoke so the plugin
  // works even if the typed zero.youtube / zero.llm surface isn't present
  // (older bridge) — it only needs the host to know the op.
  function hostInvoke(op, args) {
    if (window.zero && typeof window.zero.invoke === 'function') return window.zero.invoke(op, args);
    return Promise.reject(new Error('bridge unavailable'));
  }

  // ── Vision poll loops (M0028) ───────────────────────────────────────
  // The MV region in DEVICE pixels (CSS px × dpr) matching the host's frame
  // capture coords, or null when no video is showing.
  function videoRectDevicePx() {
    if (!currentVideoId || !ytEls.frame) return null;
    if (ytEls.top && ytEls.top.classList.contains('hidden')) return null;
    const r = ytEls.frame.getBoundingClientRect();
    if (r.width < 8 || r.height < 8) return null;
    const dpr = window.devicePixelRatio || 1;
    return {
      x: Math.round(r.left * dpr), y: Math.round(r.top * dpr),
      w: Math.round(r.width * dpr), h: Math.round(r.height * dpr),
    };
  }

  async function startVisionLoops() {
    stopVisionLoops();
    // M0029 후속#3 — MP3 공연은 걸그룹 컨셉이어도 비전 없음(iframe 캡처 불가).
    if (sourceMode === 'mp3') return;
    if (singerMode !== 'group') return;
    if (!window.zero || !window.zero.vision) return;
    let present = false;
    try { const st = await window.zero.vision.status(); present = !!(st && st.present); }
    catch (_) { present = false; }
    if (!present) {
      els.hint.textContent = '👁 비전 모델 미설치 — Settings → Vision → Download (걸그룹 비전 연동은 비활성, 오디오는 정상)';
      return;
    }
    visionActive = true;
    visionCountSamples = [];
    visionMemberTarget = 0;
    visionAction = false;
    visionInstruments = new Map();
    try { await window.zero.vision.reset(); } catch (_) {}
    visionAnalyzeTimer = setInterval(visionAnalyzeTick, VISION_ANALYZE_MS);
    visionMotionTimer  = setInterval(visionMotionTick, VISION_MOTION_MS);
  }

  function stopVisionLoops() {
    if (visionAnalyzeTimer) { clearInterval(visionAnalyzeTimer); visionAnalyzeTimer = 0; }
    if (visionMotionTimer)  { clearInterval(visionMotionTimer);  visionMotionTimer = 0; }
    visionActive = false;
    visionAction = false;
    visionMemberTarget = 0;
    visionCountSamples = [];
    visionInstruments = new Map();
    visionAnalyzeBusy = false;
    visionMotionBusy = false;
  }

  async function visionAnalyzeTick() {
    if (visionAnalyzeBusy || !visionActive || singerMode !== 'group') return;
    const rect = videoRectDevicePx();
    if (!rect) return;
    visionAnalyzeBusy = true;
    try {
      const r = await window.zero.vision.analyze(rect);
      if (!r || !r.ok) return;
      const now = performance.now();
      visionCountSamples.push({ t: now, n: (r.personCount | 0) });
      visionCountSamples = visionCountSamples.filter(s => now - s.t <= VISION_COUNT_WIN_MS);
      const recentMax = visionCountSamples.reduce((m, s) => Math.max(m, s.n), 0);
      // 0 persons across the whole window → no override (audio fallback).
      visionMemberTarget = recentMax > 0 ? Math.min(7, recentMax) : 0;
      // Vision-first instruments: the audio label→sprite mapper's regexes
      // (guitar/piano/violin/drum/…) also match Florence-2's visual labels.
      const vi = new Map();
      for (const d of (r.detections || [])) {
        const id = labelToPerformer(d.label || '');
        if (id && !id.startsWith('vocal-')) vi.set(id, Math.max(vi.get(id) || 0, 0.9));
      }
      visionInstruments = vi;
      els.hint.textContent =
        `👁 인원 ${visionMemberTarget || '-'} · ${visionAction ? '동작(댄스)' : '유휴(노래)'} · 악기 ${vi.size} · ${r.inferMs}ms`;
    } catch (_) { /* transient — ignore */ }
    finally { visionAnalyzeBusy = false; }
  }

  async function visionMotionTick() {
    if (visionMotionBusy || !visionActive || singerMode !== 'group') return;
    const rect = videoRectDevicePx();
    if (!rect) return;
    visionMotionBusy = true;
    try {
      const r = await window.zero.vision.motion(rect);
      if (r && r.ok) {
        const m = r.motion || 0;
        if (!visionAction && m >= VISION_MOTION_ON) visionAction = true;
        else if (visionAction && m <= VISION_MOTION_OFF) visionAction = false;
      }
    } catch (_) {}
    finally { visionMotionBusy = false; }
  }

  // Accept a full watch/share/embed/shorts/live URL or a bare 11-char id.
  function parseVideoId(raw) {
    if (!raw) return null;
    const s = raw.trim();
    if (/^[A-Za-z0-9_-]{11}$/.test(s)) return s;
    let m;
    if ((m = s.match(/[?&]v=([A-Za-z0-9_-]{11})/)))       return m[1];
    if ((m = s.match(/youtu\.be\/([A-Za-z0-9_-]{11})/)))  return m[1];
    if ((m = s.match(/\/embed\/([A-Za-z0-9_-]{11})/)))    return m[1];
    if ((m = s.match(/\/shorts\/([A-Za-z0-9_-]{11})/)))   return m[1];
    if ((m = s.match(/\/live\/([A-Za-z0-9_-]{11})/)))     return m[1];
    return null;
  }

  function setYtMeta(text, cls) {
    if (!ytEls.meta) return;
    ytEls.meta.textContent = text;
    ytEls.meta.className = 'yt-meta' + (cls ? ' ' + cls : '');
  }

  function loadVideoFrame(videoId) {
    if (!ytEls.frame) return;
    // autoplay=1 is honoured because this runs from the paste/click gesture.
    ytEls.frame.src = `https://www.youtube.com/embed/${videoId}?autoplay=1&rel=0&modestbranding=1`;
    currentVideoId = videoId;
    updateTopVisibility();
    // M0028 — new video: drop the motion baseline + recent-count window so the
    // first frames of the new MV don't read as a huge motion spike / stale count.
    visionCountSamples = [];
    visionMemberTarget = 0;
    try { window.zero && window.zero.vision && window.zero.vision.reset(); } catch (_) {}
  }

  function setCaption(item) {
    if (ytEls.title)   ytEls.title.textContent   = item.title || '(제목 없음)';
    if (ytEls.channel) ytEls.channel.textContent = (item.author || 'YouTube') + ' · oEmbed';
    if (ytEls.cat)     ytEls.cat.textContent     = item.category || '분류 중…';
    // Provenance badge — so the operator can see at a glance whether the LLM
    // actually classified it or we fell back to a keyword guess.
    if (ytEls.by) {
      const by = item.by || 'llm';
      ytEls.by.textContent = by === 'llm' ? '✦ LLM 분류'
                           : by === 'keyword' ? '≈ 키워드 추정'
                           : '· 분류 중';
      ytEls.by.classList.toggle('guess', by === 'keyword');
    }
  }

  async function onPasteYoutube() {
    let url = ytEls.url ? ytEls.url.value.trim() : '';
    // Empty field → try the clipboard so "붙이기" works as a one-click paste.
    if (!url && navigator.clipboard && navigator.clipboard.readText) {
      try { url = (await navigator.clipboard.readText()).trim(); if (ytEls.url) ytEls.url.value = url; }
      catch (_) { /* clipboard denied — user can type instead */ }
    }
    const videoId = parseVideoId(url);
    if (!videoId) { setYtMeta('유효한 유튜브 링크가 아님', 'err'); return; }

    // 1) Play immediately — don't make the user wait on metadata.
    const item = { videoId, title: '', author: '', thumbnail: '', category: '', url };
    loadVideoFrame(videoId);
    setCaption(item);
    setYtMeta('메타 가져오는 중…');

    // 2) oEmbed metadata (host-side; avoids CORS).
    try {
      const meta = await hostInvoke('youtube.oembed', { videoId });
      if (meta && meta.ok) {
        item.title = meta.title || '';
        item.author = meta.author || '';
        item.thumbnail = meta.thumbnail || '';
        setCaption(item);
        setYtMeta('✓ 메타 획득', 'ok');
      } else {
        setYtMeta('메타 실패 — 재생은 계속', 'warn');
      }
    } catch (_) { setYtMeta('메타 오류 — 재생은 계속', 'warn'); }

    // 3) LLM categorization (host-side, stateless; fallback to keyword/기타).
    const res = await classifyVideo(item);
    item.category = res.category;
    item.by = res.by;
    setCaption(item);
    if (res.by === 'keyword') {
      setYtMeta(res.llmOff ? 'LLM 꺼짐 → 키워드 추정' : '키워드 추정', 'warn');
    }

    // 4) Register in the categorized playlist (dedupe by videoId).
    upsertPlaylist(item);
  }

  // Returns { category, by:'llm'|'keyword', llmOff } so the UI can show
  // provenance (LLM 분류 vs 키워드 추정) — the all-"기타" regression was an
  // invisible LLM-off fallback, so provenance is now first-class.
  async function classifyVideo(item) {
    const title = item.title || item.url;
    if (!title) return { category: '기타', by: 'keyword', llmOff: false };
    try {
      const r = await hostInvoke('llm.classify', { title, channel: item.author, categories: YT_CATEGORIES });
      if (r && r.ok && r.category) return { category: r.category, by: 'llm', llmOff: false };
      const llmOff = !!(r && r.error === 'llm-not-ready');
      return { category: keywordCategory(title, item.author), by: 'keyword', llmOff };
    } catch (_) {
      return { category: keywordCategory(title, item.author), by: 'keyword', llmOff: false };
    }
  }

  // Offline / LLM-unavailable fallback — coarse keyword buckets over the
  // title + channel. Only a heuristic; the LLM path is what makes this
  // accurate. Order matters (OST/주제가 before the idol-name K-Pop net so a
  // game/drama theme song isn't swallowed as K-Pop).
  function keywordCategory(title, channel) {
    const s = ((title || '') + ' ' + (channel || '')).toLowerCase();
    if (/\bost\b|soundtrack|sound\s?track|테마곡|주제가|theme song|more than a game|드라마|애니메이션/.test(s)) return 'OST';
    if (/jazz|재즈|bossa|보사노바|swing|블루스|blues/.test(s))               return '재즈';
    if (/classic|클래식|symphony|교향|orchestra|관현악|베토벤|모차르트|쇼팽|바흐|sonata|협주|concerto/.test(s)) return '클래식';
    if (/hip\s?hop|힙합|\brap\b|래퍼|랩\b|trap/.test(s))                     return '힙합';
    if (/\bedm\b|house music|techno|trance|dubstep|일렉트로|electronic dance/.test(s)) return 'EDM';
    if (/ballad|발라드/.test(s))                                            return '발라드';
    if (/\brock\b|록 밴드|메탈|metal|punk|밴드 라이브/.test(s))               return '록';
    // K-Pop net last among the specific buckets — broad idol/group lexicon.
    if (/k-?pop|아이돌|걸그룹|보이그룹|newjeans|뉴진스|babymonster|베이비몬스터|le\s?sserafim|르세라핌|aespa|에스파|ive|아이브|nmixx|엔믹스|bts|blackpink|블랙핑크|twice|트와이스|seventeen|세븐틴|stray\s?kids|스트레이키즈|riize|라이즈|gidle|여자아이들|컬투쇼/.test(s)) return 'K-Pop';
    return '기타';
  }

  function upsertPlaylist(item) {
    const i = ytPlaylist.findIndex(p => p.videoId === item.videoId);
    if (i >= 0) ytPlaylist[i] = item; else ytPlaylist.unshift(item);
    if (ytPlaylist.length > YT_STORE_MAX) ytPlaylist.length = YT_STORE_MAX;
    savePlaylist();
    renderPlaylist();
    updateTopVisibility();   // refresh the toggle's count badge
  }

  // Source-aware dispatcher (M0029) — the aside is shared; its contents
  // come from ytPlaylist or mp3Tracks depending on the top source tab.
  function renderPlaylist() {
    if (sourceMode === 'mp3') { renderMp3Playlist(); return; }
    renderYtPlaylist();
  }

  function renderYtPlaylist() {
    if (ytEls.plCount) ytEls.plCount.textContent = String(ytPlaylist.length);

    if (ytEls.plTabs) {
      const used = YT_CATEGORIES.filter(c => ytPlaylist.some(p => p.category === c));
      const cats = ['all', ...used];
      if (!cats.includes(ytFilter)) ytFilter = 'all';
      ytEls.plTabs.innerHTML = cats.map(c => {
        const label = c === 'all' ? '전체' : c;
        const active = c === ytFilter ? ' active' : '';
        return `<button class="pl-tab${active}" data-cat="${escapeHtml(c)}">${escapeHtml(label)}</button>`;
      }).join('');
      ytEls.plTabs.querySelectorAll('.pl-tab').forEach(btn =>
        btn.addEventListener('click', () => { ytFilter = btn.getAttribute('data-cat'); renderPlaylist(); }));
    }

    if (ytEls.plList) {
      const items = ytPlaylist.filter(p => ytFilter === 'all' || p.category === ytFilter);
      if (items.length === 0) {
        ytEls.plList.innerHTML = '<span class="pl-empty">목록이 비어 있습니다</span>';
        return;
      }
      ytEls.plList.innerHTML = items.map(p =>
        `<div class="pl-item" data-id="${escapeHtml(p.videoId)}">` +
          `<div class="pl-thumb" style="background-image:url('${escapeHtml(p.thumbnail || '')}')"></div>` +
          `<div class="pl-col">` +
            `<div class="pl-t">${escapeHtml(p.title || p.videoId)}</div>` +
            `<div class="pl-c">✦ ${escapeHtml(p.category || '기타')}</div>` +
          `</div>` +
        `</div>`).join('');
      ytEls.plList.querySelectorAll('.pl-item').forEach(el =>
        el.addEventListener('click', () => {
          const it = ytPlaylist.find(p => p.videoId === el.getAttribute('data-id'));
          if (it) { loadVideoFrame(it.videoId); setCaption(it); setYtMeta('▶ 재생', 'ok'); }
        }));
    }
  }

  function bindYouTube() {
    loadStoredPlaylist();                       // restore history from localStorage
    if (ytEls.paste)  ytEls.paste.addEventListener('click', onPasteYoutube);
    if (ytEls.url)    ytEls.url.addEventListener('keydown', e => { if (e.key === 'Enter') onPasteYoutube(); });
    if (ytEls.toggle) ytEls.toggle.addEventListener('click', togglePlaylist);
    playlistOpen = ytPlaylist.length > 0;       // auto-open when there's saved history
    renderPlaylist();
    updateTopVisibility();
  }

  // ── Local MP3 playlist + player (M0029) ─────────────────────────────
  //
  // 유튜브 목록(localStorage)과 완전히 분리 — 트랙 rows는 호스트 SQLite
  // (Mp3Track)에 영속되어 재설치/마이그레이션에도 유지된다. 스캔은 호스트
  // 백그라운드 잡: mp3.track 이벤트가 도착할 때마다 목록이 증분 갱신되어
  // 스캔이 도는 중에도 이미 스캔된 곡은 바로 재생 가능(확장2).
  //
  // 재생: 호스트가 스캔 루트를 https://mp3.local/ 가상 호스트로 매핑 —
  // <audio>가 디스크에서 직접 스트리밍(시킹 포함)하고, 소리는 OS 믹서를
  // 거쳐 기존 SystemLoopback 캡처가 듣는다. 밴드 반응에 새 배선 없음.
  //
  // MP3 모드는 일반 플레이어 모드(악기·가수) 전용 — 걸그룹(비전) 모드는
  // 유튜브 iframe 캡처 기반이라 오디오 전용 소스에서는 성립하지 않는다.
  // Singer 셀렉트는 solo로 강제+잠금, 유튜브 복귀 시 복원.

  let sourceMode = 'yt';           // 'yt' | 'mp3' — 상단 소스 탭
  const mp3Tracks = [];            // host DTO cache (id-keyed upsert)
  let mp3Filter = 'all';           // 카테고리 탭 (plTabs 공유)
  let mp3InstQuery = '';           // 악기 검색어 (확장 — 포함/단독 필터)
  let mp3InstMode = 'has';         // 'has'(포함) | 'only'(단독)
  let mp3CurrentId = null;
  let mp3FolderPath = '';
  let mp3Scanning = false;
  let prevSingerMode = null;       // 유튜브로 복귀할 때 복원할 Singer 모드
  let mp3HeardInst = new Set();    // 이번 재생에서 이미 알고/들은 악기 키
  let mp3HeardMoods = new Set();   // 이번 재생에서 들은 음악적 느낌 키 (M0030)
  let mp3InstDirty = false;        // 아직 영속 안 된 신규 태그 존재 (악기/느낌 공용)
  let mp3LastInstPush = 0;
  let mp3RenderTimer = 0;
  let mp3LastProgress = null;      // { pct, text } — 탭 전환 시 진행 스트립 복원용

  // ── 리얼타임 재조정 계층 (M0030) ─────────────────────────────────────
  // 사전분류(LLM/비전)보다 우선순위가 높았어도, 재생 중 명시적 성별 라벨이
  // 지속되면(≥2틱) 무대 성별을 재조정한다. '그룹' 판정만은 유지 — 오디오로
  // 솔로/그룹을 구분할 수 없기 때문. 곡이 바뀌면 리셋.
  let mp3RtFem = 0, mp3RtMale = 0;   // 연속 명시 라벨 틱 카운터
  let mp3RealtimeGender = '';        // '' | 'male' | 'female' — 재조정 결과
  let mp3LastExcitingAt = -1e9;      // 마지막 '신나는 음악' 신호 시각
  const MP3_EXCITING_HOLD_MS = 10000; // 신남 히스테리시스 — 잔잔 구간 10s 유지

  // ── 검색/뷰 상태 (M0030) ────────────────────────────────────────────
  let mp3ArtistFilter = '';        // 장르 하위 가수 칩 필터 ('' = 전체)
  let mp3ViewMode = 'list';        // 'list' | 'cards' (느낌 카드 자동추천)
  const mp3Cards = [];             // 저장된 느낌 카드 (host DTO)
  let mp3ActiveCard = null;        // 카드 재생 중이면 해당 카드 (자동 다음곡 풀)
  let mp3CardBusy = false;         // 카드 생성 LLM 진행 중
  let mp3FilterOpen = true;        // 3단 검색·필터 패널 접힘/펼침 (후속#1)
  let mp3ShuffleOn = false;        // 🔀 랜덤재생 토글 (후속#1)
  const mp3TrackIdx = new Map();   // id → mp3Tracks index (O(1) 배치 upsert용)

  // 악기 키 ↔ 한국어 표기 — 검색은 한/영 모두 허용 ("피아노" = "piano")
  const INSTRUMENT_KO = {
    piano: '피아노', violin: '바이올린', viola: '비올라', cello: '첼로',
    contrabass: '콘트라베이스', harp: '하프', guitar: '기타', flute: '플루트',
    clarinet: '클라리넷', oboe: '오보에', horn: '호른', trumpet: '트럼펫',
    trombone: '트롬본', tuba: '튜바', drum: '드럼', vocal: '보컬',
  };

  // 음악적 느낌 키 ↔ 한국어 (M0030) — AST AudioSet의 무드 라벨 7종.
  const MOOD_KO = {
    happy: '행복', funny: '유쾌', sad: '슬픔', tender: '서정',
    exciting: '신남', angry: '격정', scary: '긴장',
  };
  function moodKeyOf(name) {
    const m = /(happy|funny|sad|tender|exciting|angry|scary) music/i.exec(name || '');
    return m ? m[1].toLowerCase() : null;
  }
  function mp3MoodList(t) {
    return String((t && t.moods) || '').split(',').filter(Boolean);
  }

  const mp3Els = {
    tabYt:    document.getElementById('srcYt'),
    tabMp3:   document.getElementById('srcMp3'),
    ytCtrls:  document.getElementById('yt-src-controls'),
    ctrls:    document.getElementById('mp3-src-controls'),
    folder:   document.getElementById('mp3Folder'),
    pick:     document.getElementById('mp3Pick'),
    scan:     document.getElementById('mp3Scan'),
    cancel:   document.getElementById('mp3CancelScan'),
    player:   document.getElementById('mp3-player'),
    cover:    document.getElementById('mp3Cover'),
    coverFb:  document.getElementById('mp3CoverFb'),
    audio:    document.getElementById('mp3Audio'),
    title:    document.getElementById('mp3Title'),
    artist:   document.getElementById('mp3Artist'),
    by:       document.getElementById('mp3By'),
    cat:      document.getElementById('mp3Cat'),
    nowInst:  document.getElementById('mp3NowInst'),
    filter:   document.getElementById('mp3Filter'),
    instQ:    document.getElementById('mp3InstQuery'),
    instMode: document.getElementById('mp3InstMode'),
    plTitle:  document.getElementById('plTitle'),
    artistChips: document.getElementById('mp3ArtistChips'),
    artistSec:   document.getElementById('mp3ArtistSec'),
    viewToggle:  document.getElementById('mp3ViewToggle'),
    catTabs:     document.getElementById('mp3CatTabs'),
    filterPane:  document.getElementById('mp3-filter-pane'),
    filterCollapse: document.getElementById('mp3FilterCollapse'),
    playAll:     document.getElementById('mp3PlayAll'),
    shuffle:     document.getElementById('mp3Shuffle'),
    progress:     document.getElementById('mp3-progress'),
    progressFill: document.getElementById('mp3ProgressFill'),
    progressText: document.getElementById('mp3ProgressText'),
  };

  // 후속#4 — 스캔 진행은 버튼 줄이 아닌 하단 고정 스트립에 표시.
  // 가변 길이 텍스트가 버튼 위치를 밀어내던 출렁임 제거.
  function mp3UpdateProgressStrip() {
    if (!mp3Els.progress) return;
    const show = sourceMode === 'mp3' && mp3Scanning && !!mp3LastProgress;
    mp3Els.progress.classList.toggle('hidden', !show);
    if (!show) return;
    if (mp3Els.progressFill)
      mp3Els.progressFill.style.width = Math.max(0, Math.min(100, mp3LastProgress.pct)) + '%';
    if (mp3Els.progressText) mp3Els.progressText.textContent = mp3LastProgress.text;
  }

  function mp3InstList(t) {
    return String((t && t.instruments) || '').split(',').filter(Boolean);
  }

  function fmtDur(sec) {
    const s = Math.round(sec || 0);
    if (s <= 0) return '';
    return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
  }

  function mp3ByBadge(by) {
    return by === 'llm' ? '✦' : by === 'tag' ? '🏷' : by === 'fallback' ? '≈' : '·';
  }

  function mp3TrackUrl(t) {
    return 'https://mp3.local/' +
      String(t.relativePath || '').split('/').map(encodeURIComponent).join('/');
  }

  // ── MP3 무대 디렉터 (후속#3) — 장르 기반 공연 컨셉 ─────────────────
  //
  // 유튜브(비전 기반 걸그룹)와 별도의 MP3 전용 연출 계층. 장르를 이미
  // 알기 때문에 컨셉을 선결정한다:
  //   • 발라드/재즈/클래식 등 → 🎙 솔로 무대. 성별이 중요 — LLM 태그 판정
  //     (vocalGender) 우선, 미확정이면 환경음 기준(기본 남성, female 라벨
  //     감지 시 여성 전환 — 가수 음역대가 넓어 환경음만으론 불완전).
  //   • K-Pop/EDM/힙합/록 또는 LLM이 '그룹' 판정 → 🎤 걸그룹 댄스 무대
  //     (기존 group 스테이징 재사용 — 노래/댄스 전환은 환경음이 그대로 결정).
  //   • 악기 등장은 기존 연출 그대로 (labelToPerformer 경로 무변경).
  const MP3_UPBEAT_CATS = ['K-Pop', 'EDM', '힙합', '록'];   // 장르 자체가 '신나는' 힌트

  function mp3CurrentTrack() { return mp3Tracks.find(x => x.id === mp3CurrentId) || null; }

  // 성별 결정 (M0030 규칙):
  //   1) '그룹' 판정(LLM/비전)은 항상 유지 — 오디오로 솔로/그룹 구분 불가.
  //   2) 리얼타임 재조정 — 명시적 성별 라벨 지속 시 사전분류보다 우선.
  //   3) 사전분류(LLM > 커버비전) → 4) 기본 남성.
  function mp3EffectiveGender(t) {
    const tagG = (t && t.vocalGender) || '';
    if (tagG === 'group') return 'group';
    if (mp3RealtimeGender) return mp3RealtimeGender;
    return tagG || 'male';
  }

  // 걸그룹 모드 조건 (M0030): 그룹 판정 && 신나는 음악(장르 업비트 or 리얼타임
  // exciting 신호 10s 히스테리시스). 신나는 음악이어도 솔로 판정이면 1인 모드.
  function mp3DesiredMode(t) {
    if (mp3EffectiveGender(t) !== 'group') return 'solo';
    const upbeat = MP3_UPBEAT_CATS.includes((t && t.category) || '')
      || performance.now() - mp3LastExcitingAt < MP3_EXCITING_HOLD_MS;
    return upbeat ? 'group' : 'solo';
  }

  // 솔로 무대 고정 — 곡이 재생 중이면 솔로 가수는 절대 무대에서 사라지지
  // 않는다 (노래 신호 없을 땐 기본(idle) 애니로 서 있음).
  function mp3SoloPinActive() {
    return sourceMode === 'mp3' && mp3CurrentId != null && singerMode === 'solo'
      && !!mp3Els.audio && !mp3Els.audio.paused;
  }

  function mp3MaleSoloActive() {
    return mp3SoloPinActive() && mp3EffectiveGender(mp3CurrentTrack()) === 'male';
  }

  // AST의 모든 노래 신호(남/여/중립) 최대 점수 — 솔로 노래 애니 트리거.
  function mp3AnyVocalSignal(labels) {
    let v = 0;
    for (const l of (labels || []))
      if (/sing(ing)?|choir|vocal|chant|yodel|rapping|hum/i.test(l.name || ''))
        v = Math.max(v, l.score || 0);
    return v;
  }

  // 매 틱 무대 디렉션 (M0030) — 신남 신호 갱신 + 성별 리얼타임 재조정 +
  // 모드(솔로↔걸그룹) 자연 전환.
  function mp3StageTick(labels) {
    if (sourceMode !== 'mp3' || mp3CurrentId == null) return;
    if (!mp3Els.audio || mp3Els.audio.paused) return;
    const now = performance.now();

    let femTick = false, maleTick = false, excitingTick = false;
    for (const l of (labels || [])) {
      if (!l || (l.score || 0) < SCORE_ACTIVE) continue;
      const s = (l.name || '').toLowerCase();
      if (/female sing|\bwoman sing/.test(s)) femTick = true;
      else if (/male sing|\bman sing\b/.test(s)) maleTick = true;
      if (moodKeyOf(s) === 'exciting' || labelToDance(l.name)) excitingTick = true;
    }
    if (excitingTick) mp3LastExcitingAt = now;

    // 명시 라벨 2틱 연속 → 리얼타임 성별 재조정 (사전분류 재조정 가능).
    if (femTick && !maleTick)      { mp3RtFem = Math.min(3, mp3RtFem + 1); mp3RtMale = 0; }
    else if (maleTick && !femTick) { mp3RtMale = Math.min(3, mp3RtMale + 1); mp3RtFem = 0; }
    if (mp3RtFem >= 2 && mp3RealtimeGender !== 'female')      { mp3RealtimeGender = 'female'; mp3ApplyStageConcept(mp3CurrentTrack()); }
    else if (mp3RtMale >= 2 && mp3RealtimeGender !== 'male')  { mp3RealtimeGender = 'male';   mp3ApplyStageConcept(mp3CurrentTrack()); }

    // 모드 자연 전환 (그룹 판정 곡의 잔잔↔신남 구간).
    const t = mp3CurrentTrack();
    if (t && mp3DesiredMode(t) !== singerMode) mp3ApplyStageConcept(t);
  }

  // 후속#5 — 커버 아트 비전 성별. 성별 미확정 곡이 재생을 시작하면 호스트가
  // APIC 커버를 Florence-2로 분석 (프로브 검증: 인물 커버 → man/woman 라벨,
  // 일러스트 커버 → 판정 보류). 판정이 오면 캐릭터가 그 자리에서 전환된다.
  // 우선순위: LLM(아티스트 지식) > 커버 비전 > 환경음 > 기본 남성.
  async function mp3RequestCoverGender(id) {
    try {
      const r = await hostInvoke('mp3.coverGender', { id });
      if (!r || !r.ok || !r.gender) return;
      const t = mp3Tracks.find(x => x.id === id);
      if (!t) return;
      t.vocalGender = r.gender;
      if (id === mp3CurrentId) {
        renderMp3Caption(t);
        mp3ApplyStageConcept(t);   // 무대 캐릭터 동적 전환
      }
      scheduleMp3Render();
    } catch (_) { /* 모델 미설치/바쁨 — 환경음 폴백 그대로 */ }
  }

  function mp3ApplyStageConcept(t) {
    if (sourceMode !== 'mp3') return;
    const mode = mp3DesiredMode(t);
    if (mode !== singerMode) {
      singerMode = mode;
      if (els.singerMode) els.singerMode.value = mode;   // 표시만 — mp3에선 셀렉트 잠금 유지
    }
    const cat = (t && t.category) || '미분류';
    const g = mp3EffectiveGender(t);
    els.hint.textContent = mode === 'group'
      ? `MP3 공연 — 🎤 걸그룹 댄스 무대 (${cat})`
      : `MP3 공연 — 🎙 ${g === 'female' ? '여성' : '남성'} 솔로 무대 (${cat})${g === 'group' ? ' · 그룹(잔잔 구간)' : ''}`;
  }

  // ── 소스 전환 (상단 탭) ────────────────────────────────────────────
  function setSourceMode(mode) {
    if (mode === sourceMode) return;
    sourceMode = mode;
    const mp3 = mode === 'mp3';
    if (mp3Els.tabYt)   mp3Els.tabYt.classList.toggle('active', !mp3);
    if (mp3Els.tabMp3)  mp3Els.tabMp3.classList.toggle('active', mp3);
    if (mp3Els.ytCtrls) mp3Els.ytCtrls.classList.toggle('hidden', mp3);
    if (mp3Els.ctrls)   mp3Els.ctrls.classList.toggle('hidden', !mp3);
    if (mp3Els.filter)  mp3Els.filter.classList.toggle('hidden', !mp3);
    if (mp3Els.plTitle) mp3Els.plTitle.textContent = mp3 ? 'MP3 목록 · 카테고리' : '재생 목록 · 카테고리';
    if (ytEls.top)      ytEls.top.classList.toggle('mp3-mode', mp3);   // 미니 플레이어 + 넓은 목록

    if (mp3) {
      // 한 번에 한 소스만 소리 나게 — 유튜브는 iframe unload로 정지.
      if (ytEls.frame) ytEls.frame.src = '';
      currentVideoId = null;
      stopVisionLoops();
      try { window.zero && window.zero.vision && window.zero.vision.reset(); } catch (_) {}
      // MP3는 비전 없음 → 걸그룹 모드 불가. Singer를 solo로 강제+잠금.
      prevSingerMode = singerMode;
      singerMode = 'solo';
      if (els.singerMode) { els.singerMode.value = 'solo'; els.singerMode.disabled = true; }
      els.hint.textContent = 'MP3 공연 모드 — 곡을 재생하면 장르에 맞는 무대 연출이 적용됩니다.';
      playlistOpen = true;
      setYtMeta(mp3Tracks.length ? `MP3 ${mp3Tracks.length}곡` : '📁 폴더 선택 후 🔍 스캔');
      renderMp3ScanUi();
      mp3UpdateProgressStrip();   // 후속#4 — 진행 중이면 하단 스트립 복원
      const cur = mp3CurrentTrack();
      if (cur) mp3ApplyStageConcept(cur);
    } else {
      if (mp3Els.audio) { try { mp3Els.audio.pause(); } catch (_) {} }
      mp3CurrentId = null;
      if (els.singerMode) els.singerMode.disabled = false;
      if (prevSingerMode) {
        singerMode = prevSingerMode;
        if (els.singerMode) els.singerMode.value = prevSingerMode;
        prevSingerMode = null;
      }
      if (bandLive && singerMode === 'group') startVisionLoops();
      els.hint.textContent = '유튜브 링크를 붙이면 상단 무대에서 재생되고, ▶ Start(System Loopback)로 악단이 반응합니다.';
      setYtMeta('대기');
      mp3UpdateProgressStrip();   // yt 모드에서는 스트립 숨김
      if (mp3Els.viewToggle)  mp3Els.viewToggle.classList.add('hidden');
      if (mp3Els.playAll)     mp3Els.playAll.classList.add('hidden');
      if (mp3Els.shuffle)     mp3Els.shuffle.classList.add('hidden');
      if (mp3Els.filterPane)  mp3Els.filterPane.classList.add('hidden');
      if (mp3Els.artistChips) mp3Els.artistChips.classList.add('hidden');
      if (ytEls.plTabs)       ytEls.plTabs.classList.remove('hidden');
    }
    renderPlaylist();
    updateTopVisibility();
  }

  // ── 재생 ──────────────────────────────────────────────────────────
  function playMp3Track(t, opts) {
    if (!t) return;
    if (!t.available) { setYtMeta('파일 없음 — 현재 폴더 밖이거나 삭제됨 (재스캔 필요)', 'warn'); return; }
    if (!(opts && opts.keepCard)) mp3ActiveCard = null;   // 목록 직접 선택 → 카드 모드 해제
    mp3CurrentId = t.id;
    mp3HeardInst = new Set(mp3InstList(t));   // 이미 아는 악기는 재푸시하지 않음
    mp3HeardMoods = new Set(mp3MoodList(t));  // 이미 아는 느낌도 재푸시하지 않음
    mp3InstDirty = false;
    mp3LastInstPush = 0;
    mp3RtFem = 0; mp3RtMale = 0; mp3RealtimeGender = '';   // 곡마다 리얼타임 재조정 리셋
    mp3LastExcitingAt = -1e9;
    mp3ApplyStageConcept(t);                  // 공연 컨셉 적용 (그룹판정+신남 게이트)
    if (!t.vocalGender) mp3RequestCoverGender(t.id);   // 커버 비전 성별 (후속#5)
    if (mp3Els.audio) {
      mp3Els.audio.src = mp3TrackUrl(t);
      mp3Els.audio.play().catch(() => setYtMeta('재생 실패 — 파일 접근 불가', 'err'));
    }
    // 커버 아트 (후속#2) — ID3 APIC가 있으면 표시, 없으면 💿 폴백.
    if (mp3Els.cover) {
      mp3Els.cover.classList.add('hidden');
      if (mp3Els.coverFb) mp3Els.coverFb.classList.remove('hidden');
      mp3Els.cover.onload = () => {
        mp3Els.cover.classList.remove('hidden');
        if (mp3Els.coverFb) mp3Els.coverFb.classList.add('hidden');
      };
      mp3Els.cover.src = 'https://mp3.local/__cover/' + t.id;
    }
    renderMp3Caption(t);
    setYtMeta('▶ 재생', 'ok');   // 제목은 플레이어 카드에 — 배지는 짧게(버튼 흔들림 방지)
    hostInvoke('mp3.markPlayed', { id: t.id }).catch(() => {});
    updateTopVisibility();
    renderMp3Playlist();
  }

  // 자동 다음 곡 — 카드 재생 중이면 카드 풀에서 랜덤(자동추천), 아니면
  // 현재 필터 목록에서 순차(기본) 또는 🔀 랜덤(후속#1).
  function playNextMp3() {
    if (mp3ActiveCard) {
      const pool = mp3CardPool(mp3ActiveCard).filter(t => t.id !== mp3CurrentId);
      if (pool.length > 0) {
        playMp3Track(pool[Math.floor(Math.random() * pool.length)], { keepCard: true });
        return;
      }
      mp3ActiveCard = null;   // 풀 소진 — 일반 순환으로 폴백
    }
    const items = mp3FilteredTracks().filter(t => t.available);
    if (items.length === 0) return;
    if (mp3ShuffleOn) {
      const pool = items.filter(t => t.id !== mp3CurrentId);
      const pick = pool.length > 0 ? pool[Math.floor(Math.random() * pool.length)] : items[0];
      playMp3Track(pick);
      return;
    }
    const i = items.findIndex(t => t.id === mp3CurrentId);
    const next = items[(i + 1) % items.length];
    if (next && next.id !== mp3CurrentId) playMp3Track(next);
  }

  // ▶ 전체 — 현재 필터 목록을 처음부터(🔀 켜져 있으면 랜덤 시작) 재생.
  function onMp3PlayAll() {
    const items = mp3FilteredTracks().filter(t => t.available);
    if (items.length === 0) { setYtMeta('재생 가능한 곡이 없습니다', 'warn'); return; }
    mp3ActiveCard = null;
    const first = mp3ShuffleOn ? items[Math.floor(Math.random() * items.length)] : items[0];
    playMp3Track(first);
  }

  function renderMp3Caption(t) {
    if (!t) return;
    if (mp3Els.title)  mp3Els.title.textContent  = t.title || t.fileName || '—';
    if (mp3Els.artist) mp3Els.artist.textContent =
      [t.artist || '알 수 없는 아티스트', t.album].filter(Boolean).join(' · ');
    if (mp3Els.cat) {
      const g = t.vocalGender === 'female' ? ' · ♀' : t.vocalGender === 'male' ? ' · ♂'
              : t.vocalGender === 'group' ? ' · 👥' : '';
      mp3Els.cat.textContent = (t.category || '미분류') + g;
    }
    if (mp3Els.by) {
      const by = t.categoryBy || '';
      mp3Els.by.textContent = by === 'llm' ? '✦ LLM 분류'
                            : by === 'tag' ? '🏷 태그 장르'
                            : by === 'fallback' ? '≈ 추정' : '· 분류 대기';
      mp3Els.by.classList.toggle('guess', by !== 'llm');
    }
    if (mp3Els.nowInst) {
      const inst = mp3InstList(t);
      const mood = mp3MoodList(t);
      const chips =
        inst.map(k => `<span class="inst-chip">${escapeHtml(INSTRUMENT_KO[k] || k)}</span>`).join('') +
        mood.map(k => `<span class="inst-chip mood">${escapeHtml(MOOD_KO[k] || k)}</span>`).join('');
      mp3Els.nowInst.innerHTML = chips ||
        '<span class="inst-none">🎧 재생하면 들리는 악기·느낌이 여기 쌓입니다</span>';
    }
  }

  // ── 보유악기 실시간 누적 (M0029 확장) ─────────────────────────────
  // music.tick의 AST 라벨을 기존 악기 매퍼(labelToPerformer)로 정규화해
  // 재생 중인 트랙의 악기 세트에 합류 → 호스트가 SQLite에 머지 영속.
  // 노래(보컬) 라벨은 'vocal' 키 하나로 접는다.
  // 재생 중 태그 실시간 누적 (M0030 확장): 악기 + 음악적 느낌(무드 라벨)을
  // 함께 수집해 영속. dirty 플래그 방식 — 스로틀에 걸린 신규 태그도 다음
  // 틱(1.5s)에 반드시 재시도된다.
  function mp3AccumulateTags(labels) {
    if (sourceMode !== 'mp3' || mp3CurrentId == null) return;
    if (!mp3Els.audio || mp3Els.audio.paused) return;
    for (const l of (labels || [])) {
      if (!l || (l.score || 0) < SCORE_ACTIVE) continue;
      // 악기 (+보컬 폴드)
      let key = null;
      const id = labelToPerformer(l.name || '');
      if (id && !id.startsWith('vocal-') && !id.startsWith('vox7-')) key = id;
      else if (/sing|choir|vocal/i.test(l.name || '')) key = 'vocal';
      if (key && !mp3HeardInst.has(key)) { mp3HeardInst.add(key); mp3InstDirty = true; }
      // 음악적 느낌 (M0030)
      const mood = moodKeyOf(l.name);
      if (mood && !mp3HeardMoods.has(mood)) { mp3HeardMoods.add(mood); mp3InstDirty = true; }
    }
    const now = performance.now();
    if (!mp3InstDirty || now - mp3LastInstPush < 2500) return;
    mp3LastInstPush = now;
    mp3InstDirty = false;
    const id2 = mp3CurrentId;
    Promise.all([
      hostInvoke('mp3.setInstruments', { id: id2, instruments: Array.from(mp3HeardInst) }),
      hostInvoke('mp3.setMoods',       { id: id2, moods: Array.from(mp3HeardMoods) }),
    ]).then(([ri, rm]) => {
        const t = mp3Tracks.find(x => x.id === id2);
        if (!t) return;
        if (ri && ri.ok) t.instruments = (ri.instruments || []).join(',');
        if (rm && rm.ok) t.moods = (rm.moods || []).join(',');
        if (id2 === mp3CurrentId) renderMp3Caption(t);
        scheduleMp3Render();
      })
      .catch(() => { mp3InstDirty = true; });   // 실패 시 다음 틱 재시도
  }

  // ── 통합 검색 (M0030) — 가수/제목/느낌/악기 ─────────────────────────
  // 검색어(쉼표/공백 구분, 한/영)는 각각: 악기 키, 느낌 키, 가수 부분일치,
  // 제목 부분일치 중 하나라도 만족해야 매치(AND across terms). '단독'은
  // 악기로 해석된 검색어에만 적용 — 그 악기 외 다른 악기가 없어야 매치
  // (보컬은 악기로 치지 않음, '보컬'을 직접 검색한 경우만 카운트).
  function mp3MatchesSearch(t) {
    const q = (mp3InstQuery || '').trim().toLowerCase();
    if (!q) return true;
    const haveInst = new Set(mp3InstList(t));
    const haveMood = new Set(mp3MoodList(t));
    const artist = (t.artist || '').toLowerCase();
    const title  = (t.title || '').toLowerCase();
    const terms = q.split(/[,\s]+/).filter(Boolean);
    if (terms.length === 0) return true;
    const matchedInst = new Set();
    let anyInstTerm = false;
    for (const term of terms) {
      const instKeys = Object.keys(INSTRUMENT_KO).filter(k =>
        k.includes(term) || INSTRUMENT_KO[k].includes(term));
      const moodKeys = Object.keys(MOOD_KO).filter(k =>
        k.includes(term) || MOOD_KO[k].includes(term));
      if (instKeys.length) anyInstTerm = true;
      const instHit = instKeys.filter(k => haveInst.has(k));
      const ok = instHit.length > 0
        || moodKeys.some(k => haveMood.has(k))
        || artist.includes(term)
        || title.includes(term);
      if (!ok) return false;
      instHit.forEach(k => matchedInst.add(k));
    }
    if (mp3InstMode === 'only' && anyInstTerm) {
      for (const k of haveInst) {
        if (k === 'vocal' && !matchedInst.has('vocal')) continue;
        if (!matchedInst.has(k)) return false;
      }
    }
    return true;
  }

  // 현재 목록 필터 체인: 카테고리 탭 → 가수 칩 → 통합 검색.
  function mp3FilteredTracks() {
    return mp3Tracks
      .filter(t => mp3Filter === 'all'
        || (mp3Filter === '__none' ? !t.category : t.category === mp3Filter))
      .filter(t => !mp3ArtistFilter || (t.artist || '') === mp3ArtistFilter)
      .filter(mp3MatchesSearch);
  }

  // ── 목록 렌더 (증분 upsert + 400ms 스로틀) ─────────────────────────
  function mp3ReindexTracks() {
    mp3TrackIdx.clear();
    mp3Tracks.forEach((t, i) => mp3TrackIdx.set(t.id, i));
  }

  // O(1) upsert (후속#1) — 배치 이벤트가 수백 건씩 도착해도 인덱스 맵으로
  // 즉시 반영. 신규는 뒤에 붙이고(스캔 순서), 스캔 완료 시 mp3.list 재로드로
  // 정식 정렬(최신순)을 복원한다.
  function upsertMp3Track(dto) {
    if (!dto || typeof dto.id !== 'number') return;
    const i = mp3TrackIdx.get(dto.id);
    if (i !== undefined) mp3Tracks[i] = dto;
    else { mp3TrackIdx.set(dto.id, mp3Tracks.length); mp3Tracks.push(dto); }
    if (dto.id === mp3CurrentId) {
      renderMp3Caption(dto);
      mp3ApplyStageConcept(dto);   // 재생 중 분류(장르/성별)가 도착하면 연출 즉시 반영
    }
  }

  // 렌더 스로틀 (후속#1) — 스캔 중에는 15s(목록 갱신이 실시간일 필요 없음,
  // UI 반응속도 우선), 평시에는 400ms. 진행 스트립/카운트는 별도로 가볍게 돈다.
  function scheduleMp3Render() {
    if (mp3RenderTimer) return;
    const delay = mp3Scanning ? 15000 : 400;
    mp3RenderTimer = setTimeout(() => {
      mp3RenderTimer = 0;
      if (sourceMode === 'mp3') { renderMp3Playlist(); updateTopVisibility(); }
    }, delay);
  }

  function mp3RenderNow() {
    if (mp3RenderTimer) { clearTimeout(mp3RenderTimer); mp3RenderTimer = 0; }
    if (sourceMode === 'mp3') { renderMp3Playlist(); updateTopVisibility(); }
  }

  function renderMp3Playlist() {
    const mp3 = sourceMode === 'mp3';
    const cardsMode = mp3ViewMode === 'cards';
    if (mp3Els.viewToggle) {
      mp3Els.viewToggle.classList.toggle('hidden', !mp3);
      mp3Els.viewToggle.textContent = cardsMode ? '📃 목록' : '🃏 카드';
      mp3Els.viewToggle.classList.toggle('on', cardsMode);
    }
    // 후속#1 — 전체/랜덤 재생 버튼 (mp3 목록 뷰 전용)
    if (mp3Els.playAll) mp3Els.playAll.classList.toggle('hidden', !mp3 || cardsMode);
    if (mp3Els.shuffle) {
      mp3Els.shuffle.classList.toggle('hidden', !mp3 || cardsMode);
      mp3Els.shuffle.classList.toggle('on', mp3ShuffleOn);
    }
    if (mp3Els.plTitle)
      mp3Els.plTitle.textContent = cardsMode ? '느낌 카드 · 자동추천' : 'MP3 목록';
    // 후속#1 — 3단 검색·필터 패널 (카드 뷰에선 숨김, 접힘 상태 반영)
    if (mp3Els.filterPane) {
      mp3Els.filterPane.classList.toggle('hidden', !mp3 || cardsMode);
      mp3Els.filterPane.classList.toggle('collapsed', !mp3FilterOpen);
    }
    if (ytEls.plTabs) ytEls.plTabs.classList.toggle('hidden', mp3);   // 유튜브 탭은 mp3에서 숨김
    if (cardsMode) { renderMp3Cards(); return; }

    if (ytEls.plCount) ytEls.plCount.textContent = String(mp3Tracks.length);

    // 장르 탭 — 3단 필터 패널에 렌더 (후속#1).
    if (mp3Els.catTabs) {
      const used = YT_CATEGORIES.filter(c => mp3Tracks.some(t => t.category === c));
      const hasNone = mp3Tracks.some(t => !t.category);
      const cats = ['all', ...used, ...(hasNone ? ['__none'] : [])];
      if (!cats.includes(mp3Filter)) mp3Filter = 'all';
      mp3Els.catTabs.innerHTML = cats.map(c => {
        const label = c === 'all' ? '전체' : c === '__none' ? '미분류' : c;
        const active = c === mp3Filter ? ' active' : '';
        return `<button class="pl-tab${active}" data-cat="${escapeHtml(c)}">${escapeHtml(label)}</button>`;
      }).join('');
      mp3Els.catTabs.querySelectorAll('.pl-tab').forEach(btn =>
        btn.addEventListener('click', () => {
          mp3Filter = btn.getAttribute('data-cat');
          mp3ArtistFilter = '';   // 장르가 바뀌면 가수 칩 필터 리셋
          renderMp3Playlist();
        }));
    }

    renderMp3ArtistChips();

    if (!ytEls.plList) return;
    const items = mp3FilteredTracks();
    if (items.length === 0) {
      ytEls.plList.innerHTML = `<span class="pl-empty">${
        mp3Tracks.length === 0
          ? '📁 폴더를 선택하고 🔍 스캔을 눌러주세요'
          : '검색/필터와 일치하는 곡이 없습니다'}</span>`;
      return;
    }
    // 후속#1 — 클릭은 plList의 위임 리스너 하나가 처리 (per-item 리스너 제거:
    // 대용량 목록 렌더 비용 대폭 절감).
    ytEls.plList.innerHTML = items.map(t => {
      const inst = mp3InstList(t).map(k => INSTRUMENT_KO[k] || k).join('·');
      const mood = mp3MoodList(t).map(k => MOOD_KO[k] || k).join('·');
      const cls = 'pl-item mp3'
        + (t.id === mp3CurrentId ? ' playing' : '')
        + (t.available ? '' : ' unavailable');
      const sub = [t.artist, fmtDur(t.durationSeconds)].filter(Boolean).join(' · ');
      return `<div class="${cls}" data-id="${t.id}">` +
        `<div class="pl-thumb mp3-thumb">${t.id === mp3CurrentId ? '▶' : '💿'}</div>` +
        `<div class="pl-col">` +
          `<div class="pl-t">${escapeHtml(t.title || t.fileName)}</div>` +
          (sub ? `<div class="pl-s">${escapeHtml(sub)}</div>` : '') +
          `<div class="pl-c">${mp3ByBadge(t.categoryBy)} ${escapeHtml(t.category || '미분류')}` +
            `${inst ? ' · 🎹 ' + escapeHtml(inst) : ''}` +
            `${mood ? ' · 💫 ' + escapeHtml(mood) : ''}</div>` +
        `</div>` +
        `<button class="pl-del" data-del="${t.id}" title="목록에서 제거 (파일은 유지)">🗑</button>` +
      `</div>`;
    }).join('');
  }

  // plList 클릭 위임 (후속#1) — mp3 소스에서만 동작. 목록 아이템/삭제/카드/
  // 카드생성 버튼을 한 리스너로 처리해 렌더마다 리스너를 다시 달지 않는다.
  async function onMp3ListClick(e) {
    if (sourceMode !== 'mp3') return;
    const del = e.target.closest('[data-del]');
    if (del) {
      e.stopPropagation();
      const id = Number(del.getAttribute('data-del'));
      try {
        const r = await hostInvoke('mp3.remove', { id });
        if (r && r.ok) {
          const i = mp3Tracks.findIndex(t => t.id === id);
          if (i >= 0) { mp3Tracks.splice(i, 1); mp3ReindexTracks(); }
          if (mp3CurrentId === id) {
            mp3CurrentId = null;
            if (mp3Els.audio) { try { mp3Els.audio.pause(); } catch (_) {} }
          }
          renderMp3Playlist();
          updateTopVisibility();
        }
      } catch (_) {}
      return;
    }
    const cdel = e.target.closest('[data-cdel]');
    if (cdel) {
      e.stopPropagation();
      const id = Number(cdel.getAttribute('data-cdel'));
      try {
        const r = await hostInvoke('mp3.cardRemove', { id });
        if (r && r.ok) {
          const i = mp3Cards.findIndex(c => c.id === id);
          if (i >= 0) mp3Cards.splice(i, 1);
          if (mp3ActiveCard && mp3ActiveCard.id === id) mp3ActiveCard = null;
          renderMp3Playlist();
        }
      } catch (_) {}
      return;
    }
    if (e.target.closest('#mp3CardCreate')) { onMp3CardCreate(); return; }
    const cardEl = e.target.closest('.mood-card');
    if (cardEl) {
      const c = mp3Cards.find(x => x.id === Number(cardEl.getAttribute('data-card')));
      if (c) playMp3Card(c);
      return;
    }
    const item = e.target.closest('.pl-item.mp3');
    if (item) {
      const t = mp3Tracks.find(x => x.id === Number(item.getAttribute('data-id')));
      if (t) playMp3Track(t);
    }
  }

  // 장르(1차 필터) 선택 시 하위 가수 칩 — 원클릭 가수 필터 (M0030).
  function renderMp3ArtistChips() {
    if (!mp3Els.artistChips) return;
    const hide = () => {
      mp3Els.artistChips.classList.add('hidden');
      if (mp3Els.artistSec) mp3Els.artistSec.classList.add('hidden');
    };
    if (mp3Els.artistSec) mp3Els.artistSec.classList.remove('hidden');
    const genreActive = mp3Filter !== 'all';
    if (!genreActive) { hide(); mp3ArtistFilter = ''; return; }
    const inCat = mp3Tracks.filter(t =>
      mp3Filter === '__none' ? !t.category : t.category === mp3Filter);
    const counts = new Map();
    for (const t of inCat) {
      const a = (t.artist || '').trim();
      if (a) counts.set(a, (counts.get(a) || 0) + 1);
    }
    const artists = [...counts.entries()].sort((a, b) => b[1] - a[1]).slice(0, 12);
    if (artists.length === 0) { hide(); return; }
    if (mp3ArtistFilter && !counts.has(mp3ArtistFilter)) mp3ArtistFilter = '';
    mp3Els.artistChips.classList.remove('hidden');
    mp3Els.artistChips.innerHTML =
      `<button class="pl-tab artist${mp3ArtistFilter === '' ? ' active' : ''}" data-artist="">가수 전체</button>` +
      artists.map(([a, n]) =>
        `<button class="pl-tab artist${a === mp3ArtistFilter ? ' active' : ''}" data-artist="${escapeHtml(a)}">🎤 ${escapeHtml(a)} <span class="cnt">${n}</span></button>`
      ).join('');
    mp3Els.artistChips.querySelectorAll('.pl-tab').forEach(btn =>
      btn.addEventListener('click', () => {
        mp3ArtistFilter = btn.getAttribute('data-artist') || '';
        renderMp3Playlist();
      }));
  }

  // ── 느낌 카드 (M0030 — 자동추천 모드) ───────────────────────────────
  function mp3CardFiltersOf(card) {
    try {
      const f = JSON.parse(card.filtersJson || '{}');
      return {
        categories: Array.isArray(f.categories) ? f.categories : [],
        artists: Array.isArray(f.artists) ? f.artists : [],
        moods: Array.isArray(f.moods) ? f.moods : [],
        instruments: Array.isArray(f.instruments) ? f.instruments : [],
      };
    } catch (_) { return { categories: [], artists: [], moods: [], instruments: [] }; }
  }

  // 카드 필터와 일치하는 재생 가능 트랙 풀 — 비어있지 않은 차원만 AND,
  // 차원 내부는 OR(any-of).
  function mp3CardPool(card) {
    const f = mp3CardFiltersOf(card);
    return mp3Tracks.filter(t => {
      if (!t.available) return false;
      if (f.categories.length && !f.categories.includes(t.category || '')) return false;
      if (f.artists.length) {
        const a = (t.artist || '').toLowerCase();
        if (!f.artists.some(x => a.includes(String(x).toLowerCase()))) return false;
      }
      if (f.moods.length) {
        const m = new Set(mp3MoodList(t));
        if (!f.moods.some(x => m.has(x))) return false;
      }
      if (f.instruments.length) {
        const i = new Set(mp3InstList(t));
        if (!f.instruments.some(x => i.has(x))) return false;
      }
      return true;
    });
  }

  function renderMp3Cards() {
    if (ytEls.plCount) ytEls.plCount.textContent = String(mp3Cards.length);
    if (!ytEls.plList) return;
    const chips = (f) => {
      const parts = [];
      f.categories.forEach(c => parts.push(`<span class="mc-chip cat">${escapeHtml(c)}</span>`));
      f.artists.forEach(a => parts.push(`<span class="mc-chip art">🎤 ${escapeHtml(a)}</span>`));
      f.moods.forEach(m => parts.push(`<span class="mc-chip mood">💫 ${escapeHtml(MOOD_KO[m] || m)}</span>`));
      f.instruments.forEach(i => parts.push(`<span class="mc-chip inst">🎹 ${escapeHtml(INSTRUMENT_KO[i] || i)}</span>`));
      return parts.join('');
    };
    ytEls.plList.innerHTML =
      `<button class="mc-create" id="mp3CardCreate"${mp3CardBusy ? ' disabled' : ''}>` +
        `${mp3CardBusy ? '⏳ LLM이 카드를 만드는 중…' : '➕ 카드 생성하기 (LLM이 태그를 파악해 자동생성)'}</button>` +
      (mp3Cards.length === 0
        ? '<span class="pl-empty">아직 카드가 없습니다 — 곡을 몇 개 재생해 태그를 모은 뒤 생성해 보세요</span>'
        : mp3Cards.map(c => {
            const f = mp3CardFiltersOf(c);
            const n = mp3CardPool(c).length;
            const active = mp3ActiveCard && mp3ActiveCard.id === c.id;
            return `<div class="mood-card${active ? ' playing' : ''}" data-card="${c.id}">` +
              `<div class="mc-head"><span class="mc-title">${escapeHtml(c.title)}</span>` +
                `<button class="pl-del" data-cdel="${c.id}" title="카드 삭제">🗑</button></div>` +
              `<div class="mc-desc">${escapeHtml(c.description || '')}</div>` +
              `<div class="mc-tags">${chips(f)}</div>` +
              `<div class="mc-count">${active ? '▶ 재생 중 · ' : ''}${n}곡</div>` +
            `</div>`;
          }).join(''));

    // 클릭 처리는 plList 위임 리스너(onMp3ListClick)가 담당.
  }

  // 카드 클릭 → 풀에서 랜덤 재생 (자동추천). 곡이 끝나면 같은 풀에서 계속.
  function playMp3Card(card) {
    const pool = mp3CardPool(card);
    if (pool.length === 0) { setYtMeta('카드와 일치하는 곡이 없습니다', 'warn'); return; }
    mp3ActiveCard = card;
    playMp3Track(pool[Math.floor(Math.random() * pool.length)], { keepCard: true });
    renderMp3Playlist();   // 카드 재생중 표시 갱신
  }

  async function onMp3CardCreate() {
    if (mp3CardBusy) return;
    mp3CardBusy = true;
    renderMp3Playlist();
    try {
      const r = await hostInvoke('mp3.cardCreate', {});
      if (r && r.ok && r.card) {
        mp3Cards.unshift(r.card);
        setYtMeta('✓ 카드 생성됨', 'ok');
      } else {
        const err = r && r.error;
        setYtMeta(err === 'llm-not-ready' ? '카드 생성엔 LLM 필요 — Settings → LLM'
                : err === 'empty-library' ? '먼저 폴더를 스캔하세요'
                : err === 'parse-failed' ? 'LLM 응답 해석 실패 — 다시 시도'
                : '카드 생성 실패', 'warn');
      }
    } catch (ex) { setYtMeta('카드 생성 실패 — ' + ex.message, 'err'); }
    finally {
      mp3CardBusy = false;
      renderMp3Playlist();
    }
  }

  async function loadMp3Cards() {
    try {
      const r = await hostInvoke('mp3.cards', {});
      if (r && r.ok && Array.isArray(r.cards)) {
        mp3Cards.length = 0;
        for (const c of r.cards) mp3Cards.push(c);
      }
    } catch (_) { /* 구버전 호스트 — 카드 모드 비활성 */ }
  }

  // ── 폴더 / 스캔 잡 UI ──────────────────────────────────────────────
  function renderMp3Folder() {
    if (!mp3Els.folder) return;
    mp3Els.folder.textContent = mp3FolderPath || '폴더 미설정';
    mp3Els.folder.title = mp3FolderPath || '';
    mp3Els.folder.classList.toggle('unset', !mp3FolderPath);
  }

  function renderMp3ScanUi() {
    if (mp3Els.scan) {
      mp3Els.scan.disabled = mp3Scanning;
      mp3Els.scan.textContent = mp3Scanning ? '⏳ 스캔 중' : '🔍 스캔';
    }
    if (mp3Els.cancel) mp3Els.cancel.classList.toggle('hidden', !mp3Scanning);
  }

  async function onMp3Pick() {
    try {
      const r = await hostInvoke('mp3.pickFolder', {});
      if (!r || !r.ok) return;                       // 취소
      const s = await hostInvoke('mp3.setFolder', { folder: r.folder });
      if (s && s.ok) {
        mp3FolderPath = s.folder;
        renderMp3Folder();
        setYtMeta('폴더 설정 — 🔍 스캔을 눌러주세요', 'ok');
      } else setYtMeta('폴더 설정 실패', 'err');
    } catch (ex) { setYtMeta('폴더 선택 실패 — ' + ex.message, 'err'); }
  }

  async function onMp3Scan() {
    try {
      const r = await hostInvoke('mp3.scan', { categories: YT_CATEGORIES });
      if (r && r.ok) {
        mp3Scanning = true;
        renderMp3ScanUi();
        setYtMeta('⏳ 스캔 시작');
        return;
      }
      const err = r && r.error;
      setYtMeta(err === 'folder-missing' ? '📁 폴더를 먼저 선택하세요'
              : err === 'busy' ? '이미 스캔이 진행 중입니다'
              : '스캔 시작 실패 — ' + (err || '?'), 'warn');
    } catch (ex) { setYtMeta('스캔 시작 실패 — ' + ex.message, 'err'); }
  }

  function onMp3CancelScan() { hostInvoke('mp3.scan.cancel', {}).catch(() => {}); }

  // ── 부트 로드 + 이벤트 구독 ────────────────────────────────────────
  async function loadMp3List() {
    try {
      const st = await hostInvoke('mp3.status', {});
      if (st) {
        mp3FolderPath = st.folder || '';
        mp3Scanning = !!st.scanning;
        renderMp3Folder();
        renderMp3ScanUi();
        // 부트 시 이미 스캔이 돌고 있으면 진행 스트립 복원 (후속#4).
        if (st.scanning && st.progress) {
          const phase = st.progress.phase === 'classify' ? '🧠 분류' : '📂 스캔';
          mp3LastProgress = {
            pct: st.progress.total > 0 ? (st.progress.done / st.progress.total) * 100 : 0,
            text: `${phase} ${st.progress.done}/${st.progress.total}`,
          };
          mp3UpdateProgressStrip();
        }
      }
      const r = await hostInvoke('mp3.list', {});
      if (r && r.ok && Array.isArray(r.tracks)) {
        mp3Tracks.length = 0;
        for (const t of r.tracks) mp3Tracks.push(t);
        mp3ReindexTracks();
        if (sourceMode === 'mp3') { renderMp3Playlist(); updateTopVisibility(); }
      }
    } catch (_) { /* 구버전 호스트 (mp3.* 없음) — MP3 탭은 빈 상태 유지 */ }
  }

  function bindMp3() {
    if (mp3Els.tabYt)  mp3Els.tabYt.addEventListener('click', () => setSourceMode('yt'));
    if (mp3Els.tabMp3) mp3Els.tabMp3.addEventListener('click', () => setSourceMode('mp3'));
    if (mp3Els.pick)   mp3Els.pick.addEventListener('click', onMp3Pick);
    if (mp3Els.scan)   mp3Els.scan.addEventListener('click', onMp3Scan);
    if (mp3Els.cancel) mp3Els.cancel.addEventListener('click', onMp3CancelScan);
    if (mp3Els.instQ)
      mp3Els.instQ.addEventListener('input', () => { mp3InstQuery = mp3Els.instQ.value; scheduleMp3Render(); });
    if (mp3Els.instMode)
      mp3Els.instMode.addEventListener('change', () => { mp3InstMode = mp3Els.instMode.value; scheduleMp3Render(); });
    if (mp3Els.viewToggle)
      mp3Els.viewToggle.addEventListener('click', () => {
        mp3ViewMode = mp3ViewMode === 'cards' ? 'list' : 'cards';
        renderMp3Playlist();
      });
    // 후속#1 — 3단 필터 패널 접힘/펼침 + 전체/랜덤 재생.
    if (mp3Els.filterCollapse)
      mp3Els.filterCollapse.addEventListener('click', () => {
        mp3FilterOpen = !mp3FilterOpen;
        if (mp3Els.filterPane) mp3Els.filterPane.classList.toggle('collapsed', !mp3FilterOpen);
      });
    if (mp3Els.playAll) mp3Els.playAll.addEventListener('click', onMp3PlayAll);
    if (mp3Els.shuffle)
      mp3Els.shuffle.addEventListener('click', () => {
        mp3ShuffleOn = !mp3ShuffleOn;
        mp3Els.shuffle.classList.toggle('on', mp3ShuffleOn);
        setYtMeta(mp3ShuffleOn ? '🔀 랜덤재생 켜짐' : '🔀 랜덤재생 꺼짐');
      });
    // 후속#1 — 목록 클릭 위임 리스너 (렌더마다 재부착 없음).
    if (ytEls.plList) ytEls.plList.addEventListener('click', onMp3ListClick);
    if (mp3Els.audio) {
      mp3Els.audio.addEventListener('ended', playNextMp3);
      // 진단 가시화 — 미디어 스택 실패를 코드명으로 표시 (2=네트워크,
      // 3=디코드, 4=소스 미지원). "조용한 재생 실패"를 없애기 위한 배지.
      mp3Els.audio.addEventListener('error', () => {
        const code = (mp3Els.audio.error && mp3Els.audio.error.code) || 0;
        const name = { 1: 'aborted', 2: 'network', 3: 'decode', 4: 'src-not-supported' }[code] || code;
        setYtMeta(`재생 오류 — ${name}`, 'err');
      });
    }

    if (window.zero && typeof window.zero.on === 'function') {
      // 후속#1 — 배치 업서트: 스캔 중에는 호스트가 ~2s/100건으로 묶어 보낸다.
      window.zero.on('mp3.tracks', d => {
        const arr = (d && d.tracks) || [];
        for (const t of arr) upsertMp3Track(t);
        scheduleMp3Render();
      });
      window.zero.on('mp3.scan.progress', p => {
        mp3Scanning = true;
        renderMp3ScanUi();
        if (!p) return;
        // 후속#2 — 2단계 표시: 스캔(태그, 빠름) → LLM 분류(미분류 백로그).
        // 후속#4 — 진행은 하단 고정 스트립에만 표시 (버튼 줄 불변).
        const phase = p.phase === 'classify' ? '🧠 분류' : '📂 스캔';
        mp3LastProgress = {
          pct: p.total > 0 ? (p.done / p.total) * 100 : 0,
          text: `${phase} ${p.done}/${p.total}${p.current ? ' · ' + p.current : ''}`,
        };
        mp3UpdateProgressStrip();
      });
      window.zero.on('mp3.scan.done', d => {
        mp3Scanning = false;
        mp3LastProgress = null;
        renderMp3ScanUi();
        mp3UpdateProgressStrip();   // 스트립 숨김
        if (sourceMode === 'mp3') {
          if (d && d.ok) setYtMeta(`✓ 완료 ${d.total}곡`, 'ok');
          else setYtMeta(d && d.error === 'cancelled' ? '스캔 중지됨' : '스캔 실패', 'warn');
        }
        // 후속#1 — 정식 정렬 복원(재로드) + 즉시 렌더 (스캔 중 15s 스로틀 해제).
        loadMp3List().then(mp3RenderNow);
      });
    }
    renderMp3Folder();
    renderMp3ScanUi();
    loadMp3List();
    loadMp3Cards();
  }

  // ── Boot ─────────────────────────────────────────────────────────────
  pickStage(els.stagePick.value);
  bindEvents();
  bindYouTube();
  bindMp3();
  requestAnimationFrame(renderLoop);

  window.addEventListener('beforeunload', () => {
    try { window.zero?.music?.stop(); } catch (_) { /* host gone */ }
  });
})();
