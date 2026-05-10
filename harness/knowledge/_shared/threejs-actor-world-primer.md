# Three.js Actor World — viewer primer

> Owner: **`_shared`** — viewer-facing reference card.
> Imported as part of mission **M0016** (Phase B). Distillation of the
> rationale behind the harness-view "Actor World" 3D category.
> Stays here, not under any agent — this is texture/scene knowledge for
> the viewer maintainer, not harness QA logic.

## Why Three.js (not raw WebGL, not WebGPU)

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| Raw WebGL | Zero deps | 200+ lines of boilerplate before first triangle, no built-in camera/controls | rejected — would balloon the viewer |
| **Three.js** | One CDN line, OrbitControls + scene graph + sprite/billboard built-in, biggest community | ~600KB gzipped, opinionated scene graph | **chosen** |
| Babylon.js | More features (physics, GUI lib) | Larger bundle, learning curve | overkill for static visualization |
| WebGPU (raw) | Future-proof, modern API | Browser support still patchy late-2025; same boilerplate problem | premature |

Pinned: `https://unpkg.com/three@0.160.0/build/three.module.js` + matching
`OrbitControls` from `three/examples/jsm/controls/`. Bumping the version
means re-testing — Three.js minor bumps have broken `OrbitControls` import
paths historically.

The viewer convention "vanilla HTML + ES Modules + CDN only" (per
[builder.md](.claude/skills/harness-view-build/references/builder.md)) is
preserved — no bundler, no `npm install`. ES module import map handles the
`three` bare specifier so `OrbitControls` resolves cleanly.

## Scene composition decisions

### Top-level: galaxy cluster wraps the Actor Stage

Per M0016 brief: "Actor Stage 를 감싸는 은하수그룹과같이 구분해 표현."
The Actor Stage (where actor spheres live) sits inside a translucent toroidal
ring of nebula sprites — visually a galaxy. Outside the ring: a starfield of
~5000 BufferGeometry points + a few "planet" sprites for ambient density,
per "공간디자인은 전반적으로 우주안에서 작동하는 느낌."

### Actor representation

Per brief: "Actor 노드요소는 사람이아닌 구체로 표현."
Each agent (`harness/agents/*.md`) becomes a sphere mesh:

| Role | Color (HSL) | Surface character |
|---|---|---|
| `tamer` | `0.55, 0.7, 0.55` (azure) | Smooth — orchestrator, central |
| `code-coach` | `0.10, 0.85, 0.55` (amber) | Warm — reviewer |
| `security-guard` | `0.95, 0.7, 0.50` (crimson) | Cold sheen — gatekeeper |
| `build-doctor` | `0.55, 0.6, 0.45` (steel blue) | Industrial — builder |
| `test-runner` | `0.30, 0.7, 0.50` (emerald) | Active — runner |
| `test-sentinel` | `0.65, 0.5, 0.55` (lavender) | Calm — auditor |

Surface: **procedural shader-based** as the default (gradient + simplex-noise
glow at the rim; fragment shader inline in the view file). This is the
"works zero-config" mode. Phase E swaps in real Gemini-generated textures
via `pencil-design` skill — see `harness/logs/mission-records/M0016-수행결과.md`
for the exact prompts and CLI invocations to swap to image textures.

### Connection lines (event flow)

Per brief: "액터간 상호작용하는 경우 이벤트의 흐름이 시각적으로 전달되는 효과."
Lines connect related actors:

- `tamer ↔ everyone` — orchestrator reaches all
- `code-coach ↔ build-doctor ↔ test-sentinel ↔ test-runner` — pre-commit / release cluster
- `security-guard ↔ tamer / build-doctor` — release-gate path
- Lines are drawn each frame from current sphere positions → **drag persistence
  is automatic** (per brief: "이동이 되어도 링크가 유지되어야함")

Animated event flow: a small particle (sprite) travels along a random line
every ~600 ms, fading at the destination. Reads as message-passing between
actors without forcing the viewer to encode actual Akka.NET message types.

### Cyberpunk 2D tooltip

Per brief: "사이버펑크 2d컨셉을 파악한후 2dui 로 설명하는 툴팁."
Click a sphere → an HTML overlay (positioned via 3D→2D projection) appears
with the agent's name, persona, triggers, and short description. Style: neon
border, semi-transparent dark background, mono font, glow shadow. The overlay
re-projects every frame so it tracks the sphere as the camera orbits or the
sphere is dragged.

Implementation: single `<div class="actor-tooltip">` in the host, position
updated via `requestAnimationFrame`. Avoids HTMLRenderer's overhead.

### Drag interaction

Per brief: "각 액터들은 클릭해 이동할수 있으며."
- Single click on empty area → close tooltip
- Click on sphere (no movement) → open tooltip
- Click + drag on sphere → move it on a 2D plane perpendicular to the camera
  (using `Raycaster` + a virtual drag plane). Connection lines update each
  frame; tooltip follows.

Camera defaults to OrbitControls; dragging a sphere temporarily disables
OrbitControls (`controls.enabled = false`) and re-enables on `pointerup`.

## Anti-patterns we're avoiding

| Anti-pattern | Why we don't do it |
|---|---|
| Loading a heavy GLTF model per actor | 6 spheres + a galaxy = a few KB of geometry; GLTF would balloon download for negligible visual gain |
| `THREE.HTMLRenderer` for the tooltip | It mounts a separate DOM tree per element and adds layout cost; manual projection is cheaper |
| Storing actor positions in viewer state cookies | Intentionally non-persistent — the layout is meant to be *live exploration*, not a saved diagram. If persistence is requested later, store in `Home/harness-view/data/`. |
| Real Akka.NET message tap | Out of scope. The visual edges encode *known* relationships from `harness/agents/*.md` frontmatter; they don't watch real traffic. A future mission could bridge `AgentEventStream`. |

## Cross-references

- View implementation: `Home/harness-view/js/views/actor-world.js`
- Menu entry: `Home/harness-view/js/config/menu.js` (id `actor-world`)
- Mission completion: `harness/logs/mission-records/M0016-수행결과.md`
- Texture pipeline: `harness/knowledge/_shared/pencil-design-skill-origin.md`
- Source agents: `Home/harness-view/indexes/harness-agents.json`
- Sibling reference (out-of-repo): `D:/pencil-creator/design/xaml`
- Sibling Pages (advanced frontend ideas): https://psmon.github.io/pencil-creator/
