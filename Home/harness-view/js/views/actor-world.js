/**
 * Actor World - cyberpunk 3D model of the AgentZero ActorStage runtime.
 *
 * Scope:
 *   /user/stage is the center. The view models the runtime surfaces around it:
 *   AgentBot, AgentLoop, LLM tool-calling, toolbelt dispatch, workspace/terminal
 *   actors, CLI IPC, voice input, and OS automation. This is not a harness-agent
 *   role graph.
 *
 * Visual upgrade (concept ref: tmp/초안/actor-world.png):
 *   - starfield + nebula equirect skybox via CanvasTexture
 *   - hexagonal metallic disc platform with rim glow + tick marks
 *   - crystalline ActorStage core (outer icosa shell + inner orb + energy column)
 *   - per-node hex pedestal + additive halo sprite for bloom feel
 *   - SYSTEM STATUS bars (bottom-left) + EVENT STREAM rolling log (bottom-right)
 *
 * All textures are procedural (CanvasTexture) — no external image assets required.
 * To swap in real generated images later, drop PNGs at
 *   Home/harness-view/assets/actor-world/{starfield.png, hex-disc.png, ...}
 * and replace the corresponding make*Texture() call with THREE.TextureLoader().load(...).
 */

import { h, mount } from '../utils/dom.js';
import { renderTopBar, emptyState, loadingState } from './_common.js';

const THREE_VERSION = '0.160.0';

const NODES = [
  {
    id: 'stage',
    title: 'ActorStage',
    subtitle: '/user/stage',
    kind: 'CORE',
    shape: 'hub',
    color: 0x41E6FF,
    position: [0, 1.65, 0],
    scale: 1.22,
    summary: 'Top-level Akka supervisor and message broker for bot, workspace, and terminal actor traffic.',
    inputs: ['CLI IPC messages', 'UI events', 'workspace lifecycle', 'terminal signals'],
    outputs: ['Tell(/bot)', 'Tell(/ws-*)', 'event stream updates'],
    files: ['Project/ZeroCommon/Actors', 'Project/ZeroCommon/Actors/Messages.cs'],
  },
  {
    id: 'bot',
    title: 'AgentBotActor',
    subtitle: '/user/stage/bot',
    kind: 'UI GATE',
    shape: 'actor',
    color: 0xFF4FA3,
    position: [-3.6, 0.45, -1.35],
    scale: 0.96,
    summary: 'Chat/key mode gateway. Routes user intent and peer terminal callbacks into the agent loop.',
    inputs: ['AskBot text', 'TerminalSentToBot', 'voice transcript', 'peer DONE(...)'],
    outputs: ['StartAgentLoop', 'UI status', 'peer routing'],
    files: ['AgentBotActor', 'AgentEventStream'],
  },
  {
    id: 'loop',
    title: 'AgentLoopActor',
    subtitle: '/user/stage/bot/loop',
    kind: 'FSM',
    shape: 'actor',
    color: 0x8C6CFF,
    position: [0, 0.15, -1.65],
    scale: 1.0,
    summary: 'The actual agent wrapper. Drives one short run through Idle -> Thinking -> Generating -> Acting -> Done.',
    inputs: ['StartAgentLoop', 'tool result continuations', 'cancel'],
    outputs: ['LLM turn', 'tool dispatch', 'AgentLoopRun'],
    files: ['Project/ZeroCommon/Llm/Tools', 'IAgentLoop', 'AgentLoopRun'],
  },
  {
    id: 'llm',
    title: 'LLM Function Calling',
    subtitle: 'GBNF / OpenAI-compatible REST',
    kind: 'MODEL',
    shape: 'model',
    color: 0xFFE066,
    position: [3.65, 0.45, -1.3],
    scale: 0.94,
    summary: 'Constrained generation surface. The model can emit only tool-call JSON until it emits done.',
    inputs: ['system prompt', 'terminal context', 'tool result'],
    outputs: ['list_terminals', 'read_terminal', 'send_to_terminal', 'send_key', 'wait', 'done'],
    files: ['AgentToolGrammar.Gbnf', 'LocalAgentLoop', 'ExternalAgentLoop'],
  },
  {
    id: 'toolbelt',
    title: 'IAgentToolbelt',
    subtitle: 'side-effect surface',
    kind: 'TOOLS',
    shape: 'bus',
    color: 0x2DFF9A,
    position: [0, -1.05, 1.2],
    scale: 0.98,
    summary: 'Dispatch boundary between model decisions and real side effects in workspace terminals.',
    inputs: ['validated tool-call JSON'],
    outputs: ['terminal read/write', 'key send', 'wait result', 'done result'],
    files: ['WorkspaceTerminalToolHost', 'MockAgentToolbelt'],
  },
  {
    id: 'workspace',
    title: 'WorkspaceActor',
    subtitle: '/user/stage/ws-*',
    kind: 'SCOPE',
    shape: 'stack',
    color: 0x32D7FF,
    position: [-2.25, -1.35, 3.2],
    scale: 0.86,
    summary: 'Owns a workspace folder and its terminal actor children.',
    inputs: ['workspace open/restore', 'toolbelt terminal commands'],
    outputs: ['TerminalActor creation', 'workspace persistence events'],
    files: ['WorkspaceActor', 'CliWorkspacePersistence'],
  },
  {
    id: 'terminal',
    title: 'TerminalActor',
    subtitle: '/term-* / ConPTY',
    kind: 'IO',
    shape: 'terminal',
    color: 0x76FF5F,
    position: [1.2, -1.05, 3.45],
    scale: 0.9,
    summary: 'Wraps ITerminalSession. This is where Claude, Codex, shell, and build/test commands actually run.',
    inputs: ['send_to_terminal', 'send_key', 'read_terminal'],
    outputs: ['terminal buffer', 'peer CLI callback', 'AgentEventStream'],
    files: ['ITerminalSession', 'ConPtyTerminalSession'],
  },
  {
    id: 'cli',
    title: 'CLI IPC',
    subtitle: 'AgentZeroLite.exe -cli',
    kind: 'IPC',
    shape: 'port',
    color: 0xFFB24A,
    position: [-4.35, -1.05, 1.45],
    scale: 0.82,
    summary: 'External scripts and peer terminal AIs push messages into the running GUI through WM_COPYDATA + MMF.',
    inputs: ['terminal-send', 'terminal-read', 'bot-chat', 'os commands'],
    outputs: ['MainWindow.HandleBotChat', 'JSON response MMF'],
    files: ['CliHandler.cs', 'CliTerminalIpcHelper.cs', 'AgentZeroLite.ps1'],
  },
  {
    id: 'voice',
    title: 'Voice Pipeline',
    subtitle: 'STT live, TTS planned',
    kind: 'VOICE',
    shape: 'port',
    color: 0xF36BFF,
    position: [4.25, -1.05, 1.4],
    scale: 0.82,
    summary: 'Microphone input is segmented by VAD and transcribed into AgentBot/terminal flow. TTS response streaming is still in progress.',
    inputs: ['microphone', 'VAD segment', 'GPU picker'],
    outputs: ['transcript to AgentBot', 'future spoken response'],
    files: ['Voice settings', 'Whisper benchmark tests', 'NAudio + VAD'],
  },
  {
    id: 'os',
    title: 'OS Automation',
    subtitle: 'CLI and LLM symmetric tools',
    kind: 'OS',
    shape: 'port',
    color: 0xB9FFEA,
    position: [3.45, -1.15, 3.1],
    scale: 0.78,
    summary: 'Window listing, screenshots, element tree, and keypress automation are exposed from both CLI and LLM routes.',
    inputs: ['os list-windows', 'os screenshot', 'os keypress'],
    outputs: ['window metadata', 'screen capture', 'automation event'],
    files: ['OS automation CLI', 'IAgentToolbelt extension path'],
  },
];

const EDGES = [
  ['cli', 'stage', 'WM_COPYDATA / MMF'],
  ['voice', 'bot', 'STT transcript'],
  ['stage', 'bot', 'Tell'],
  ['bot', 'loop', 'StartAgentLoop'],
  ['loop', 'llm', 'prompt + context'],
  ['llm', 'loop', 'tool-call JSON'],
  ['loop', 'toolbelt', 'dispatch'],
  ['toolbelt', 'workspace', 'scope'],
  ['workspace', 'terminal', 'owns'],
  ['toolbelt', 'terminal', 'read/write/key'],
  ['terminal', 'cli', 'peer bot-chat'],
  ['os', 'toolbelt', 'automation'],
];

const TOOL_SURFACE = ['list_terminals', 'read_terminal', 'send_to_terminal', 'send_key', 'wait', 'done'];

const EVENT_TEMPLATES = [
  { from: 'CLI',         to: 'ActorStage', msg: 'WM_COPYDATA',       tone: 'amber' },
  { from: 'ActorStage',  to: 'AgentBot',   msg: 'Tell',              tone: 'cyan'  },
  { from: 'Voice',       to: 'AgentBot',   msg: 'STT transcript',    tone: 'pink'  },
  { from: 'AgentBot',    to: 'AgentLoop',  msg: 'StartAgentLoop',    tone: 'purple'},
  { from: 'AgentLoop',   to: 'LLM',        msg: 'prompt+context',    tone: 'gold'  },
  { from: 'LLM',         to: 'AgentLoop',  msg: 'tool_call',         tone: 'gold'  },
  { from: 'AgentLoop',   to: 'Toolbelt',   msg: 'dispatch',          tone: 'green' },
  { from: 'Toolbelt',    to: 'Workspace',  msg: 'scope',             tone: 'cyan'  },
  { from: 'Workspace',   to: 'Terminal',   msg: 'owns',              tone: 'green' },
  { from: 'Toolbelt',    to: 'Terminal',   msg: 'read/write',        tone: 'green' },
  { from: 'Terminal',    to: 'CLI',        msg: 'peer bot-chat',     tone: 'amber' },
  { from: 'OS',          to: 'Toolbelt',   msg: 'automation',        tone: 'mint'  },
];

const VERT = /* glsl */`
  varying vec3 vNormal;
  varying vec3 vPosition;
  void main() {
    vNormal = normalize(normalMatrix * normal);
    vPosition = position;
    gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
  }
`;

// Inner glowing core shader (used for ActorStage center crystal).
const CORE_FRAG = /* glsl */`
  precision highp float;
  varying vec3 vNormal;
  varying vec3 vPosition;
  uniform vec3 uColor;
  uniform float uTime;

  void main() {
    float pulse = 0.55 + 0.45 * sin(uTime * 2.2);
    float fres = pow(1.0 - abs(dot(normalize(vNormal), vec3(0.0, 0.0, 1.0))), 1.6);
    vec3 core = mix(uColor, vec3(1.0, 1.0, 1.0), 0.55) * (1.2 + pulse * 0.5);
    vec3 rim  = mix(uColor, vec3(1.0), 0.2) * fres * 1.6;
    gl_FragColor = vec4(core + rim, 1.0);
  }
`;

export async function render(ctx) {
  const { viewEl, topbarEl } = ctx;

  renderTopBar(topbarEl, {
    title: 'Actor World',
    subtitle: 'ActorStage runtime model - LLM tool calls, voice, CLI IPC, terminal actors',
    badge: { kind: 'readonly', text: 'Model view' },
  });

  mount(viewEl, loadingState('Loading ActorStage model...'));

  let THREE, OrbitControls;
  try {
    THREE = await import('three');
    const oc = await import('three/addons/controls/OrbitControls.js');
    OrbitControls = oc.OrbitControls;
  } catch (e) {
    mount(viewEl, emptyState(
      `Three.js (${THREE_VERSION}) failed to load: ${e.message}. Check the import map in index.html.`
    ));
    return;
  }

  const stage = h('div', { class: 'aw-stage' });
  const tooltip = h('div', { class: 'aw-tooltip', style: { display: 'none' } });
  const hud = buildHud();
  const toolrail = buildToolRail();
  const sysstatus = buildSystemStatus();
  const eventstream = buildEventStream();
  const help = h('div', { class: 'aw-help' }, 'orbit stage - click node for function card - drag node to inspect routes');

  mount(viewEl, h('div', { class: 'aw-root aw-cyber' }, [stage, tooltip, hud, toolrail, sysstatus, eventstream, help]));

  const ac = new AbortController();
  window.addEventListener('hashchange', () => ac.abort(), { once: true, signal: ac.signal });

  requestAnimationFrame(() => {
    if (!ac.signal.aborted) runScene({ THREE, OrbitControls, stage, tooltip, eventstream, ac });
  });
}

function runScene({ THREE, OrbitControls, stage, tooltip, eventstream, ac }) {
  const width = stage.clientWidth || stage.parentElement.clientWidth || 1000;
  const height = stage.clientHeight || stage.parentElement.clientHeight || 700;

  const scene = new THREE.Scene();

  // Track resources for explicit disposal on hashchange.
  const disposables = [];

  scene.fog = new THREE.FogExp2(0x03050E, 0.022);

  const camera = new THREE.PerspectiveCamera(46, width / height, 0.1, 220);
  camera.position.set(0, 7.4, 13.6);

  const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: false });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
  renderer.setSize(width, height);
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  renderer.toneMappingExposure = 1.05;
  stage.appendChild(renderer.domElement);

  const controls = new OrbitControls(camera, renderer.domElement);
  controls.enableDamping = true;
  controls.dampingFactor = 0.08;
  controls.minDistance = 6;
  controls.maxDistance = 28;
  controls.target.set(0, -0.8, 0.7);

  // --- Cosmos background (starfield equirect skybox) ---
  // Procedural fallback first; loader swaps in real PNG when available.
  const procStarfield = makeStarfieldTexture(THREE);
  procStarfield.mapping = THREE.EquirectangularReflectionMapping;
  scene.background = procStarfield;
  disposables.push(procStarfield);

  // PMREMGenerator turns the equirect into a proper environment map for PBR.
  const pmrem = new THREE.PMREMGenerator(renderer);
  pmrem.compileEquirectangularShader();
  let envRT = pmrem.fromEquirectangular(procStarfield);
  scene.environment = envRT.texture;

  loadIfAvailable(THREE, 'assets/actor-world/space-nebula.png', tex => {
    tex.colorSpace = THREE.SRGBColorSpace;
    tex.mapping = THREE.EquirectangularReflectionMapping;
    scene.background = tex;
    try { envRT.dispose(); } catch {}
    envRT = pmrem.fromEquirectangular(tex);
    scene.environment = envRT.texture;
  });

  // --- Lighting ---
  scene.add(new THREE.HemisphereLight(0x7CE8FF, 0x130821, 1.1));
  const key = new THREE.DirectionalLight(0xB9FFEA, 1.4);
  key.position.set(-4, 8, 5);
  scene.add(key);
  const rim = new THREE.DirectionalLight(0xFF6BC8, 0.6);
  rim.position.set(6, 4, -7);
  scene.add(rim);
  const coreLight = new THREE.PointLight(0x41E6FF, 28, 18, 1.8);
  coreLight.position.set(0, 1.8, 0);
  scene.add(coreLight);

  // --- Texture-bearing materials for the space station ---
  // Procedural fallback CanvasTextures get replaced by the real PNGs once loaded.
  const procDeck = makeHexDiscTexture(THREE);
  const procHull = makeHullTextureProc(THREE);
  procHull.wrapS = THREE.RepeatWrapping;
  procHull.wrapT = THREE.RepeatWrapping;
  disposables.push(procDeck, procHull);

  const deckMat = new THREE.MeshStandardMaterial({
    map: procDeck,
    roughness: 0.5,
    metalness: 0.55,
    emissive: new THREE.Color(0x122036),
    emissiveIntensity: 0.4,
    side: THREE.DoubleSide,
  });
  const hullMat = new THREE.MeshStandardMaterial({
    map: procHull,
    roughness: 0.62,
    metalness: 0.86,
    side: THREE.DoubleSide,
  });
  disposables.push(deckMat, hullMat);

  loadIfAvailable(THREE, 'assets/actor-world/station-deck.png', tex => {
    tex.colorSpace = THREE.SRGBColorSpace;
    tex.anisotropy = 8;
    deckMat.map = tex;
    deckMat.needsUpdate = true;
  });
  loadIfAvailable(THREE, 'assets/actor-world/station-hull.png', tex => {
    tex.colorSpace = THREE.SRGBColorSpace;
    tex.wrapS = THREE.RepeatWrapping;
    tex.wrapT = THREE.RepeatWrapping;
    tex.repeat.set(4, 1);
    tex.anisotropy = 8;
    hullMat.map = tex;
    hullMat.needsUpdate = true;
  });

  // --- Space station model ---
  const stationParts = addSpaceStation(THREE, scene, { deckMat, hullMat });
  disposables.push(...stationParts.disposables);

  // --- Central energy column + inner glowing core ---
  const corePulseRefs = addStageCore(THREE, scene);
  disposables.push(...corePulseRefs.disposables);

  // --- Particles ---
  const particles = addParticles(THREE, scene);
  disposables.push(particles.points.geometry, particles.points.material);

  // --- Halo glow (shared texture for sprites) ---
  const haloTex = makeRadialGlowTexture(THREE);
  disposables.push(haloTex);

  // --- Hex pedestal texture (shared) ---
  const pedestalTex = makeHexPedestalTexture(THREE);
  disposables.push(pedestalTex);

  // --- Nodes ---
  const nodeById = new Map();
  const pickables = [];
  NODES.forEach(node => {
    const group = createNode(THREE, node, { haloTex, pedestalTex });
    scene.add(group);
    nodeById.set(node.id, group);
    pickables.push(group.userData.pickable);
  });

  // --- Edges ---
  const edgeObjects = EDGES.map(([from, to, label], i) => {
    const edge = createEdge(THREE, nodeById.get(from), nodeById.get(to), label, i);
    scene.add(edge.group);
    return edge;
  });

  // --- Interaction ---
  const raycaster = new THREE.Raycaster();
  const pointer = new THREE.Vector2();
  let hover = null;
  let active = null;
  let drag = null;
  let dragPlane = new THREE.Plane();
  let dragStart = new THREE.Vector3();
  let pointerDown = null;

  function setPointer(ev) {
    const r = renderer.domElement.getBoundingClientRect();
    pointer.x = ((ev.clientX - r.left) / r.width) * 2 - 1;
    pointer.y = -((ev.clientY - r.top) / r.height) * 2 + 1;
  }

  function pick(ev) {
    setPointer(ev);
    raycaster.setFromCamera(pointer, camera);
    const hits = raycaster.intersectObjects(pickables, false);
    return hits[0]?.object?.userData.nodeGroup || null;
  }

  function updateEdges() {
    edgeObjects.forEach(edge => updateEdge(THREE, edge));
  }

  renderer.domElement.addEventListener('pointerdown', ev => {
    pointerDown = { x: ev.clientX, y: ev.clientY };
    const node = pick(ev);
    if (node) {
      drag = node;
      controls.enabled = false;
      const camDir = new THREE.Vector3();
      camera.getWorldDirection(camDir);
      dragPlane.setFromNormalAndCoplanarPoint(camDir, node.position);
      raycaster.ray.intersectPlane(dragPlane, dragStart);
      renderer.domElement.setPointerCapture(ev.pointerId);
    }
  }, { signal: ac.signal });

  renderer.domElement.addEventListener('pointermove', ev => {
    if (drag) {
      setPointer(ev);
      raycaster.setFromCamera(pointer, camera);
      const hit = new THREE.Vector3();
      if (raycaster.ray.intersectPlane(dragPlane, hit)) {
        const delta = hit.clone().sub(dragStart);
        drag.position.add(delta);
        dragStart.copy(hit);
        updateEdges();
      }
      return;
    }

    const node = pick(ev);
    if (node !== hover) {
      hover = node;
      if (hover) emitEventForNode(eventstream, hover.userData.node);
      renderer.domElement.style.cursor = node ? 'grab' : 'default';
    }
  }, { signal: ac.signal });

  renderer.domElement.addEventListener('pointerup', ev => {
    if (drag) {
      try { renderer.domElement.releasePointerCapture(ev.pointerId); } catch {}
      drag = null;
      controls.enabled = true;
    }

    if (pointerDown) {
      const moved = Math.hypot(ev.clientX - pointerDown.x, ev.clientY - pointerDown.y) > 4;
      if (!moved) {
        const node = pick(ev);
        if (node) {
          active = active === node ? null : node;
          if (active) {
            openFunctionCard(tooltip, active.userData.node, ac);
            emitEventForNode(eventstream, active.userData.node, true);
          } else {
            closeTooltip(tooltip);
          }
        } else {
          active = null;
          closeTooltip(tooltip);
        }
      }
      pointerDown = null;
    }
  }, { signal: ac.signal });

  function onResize() {
    const w = stage.clientWidth || stage.parentElement.clientWidth;
    const h2 = stage.clientHeight || stage.parentElement.clientHeight;
    camera.aspect = w / h2;
    camera.updateProjectionMatrix();
    renderer.setSize(w, h2);
  }
  window.addEventListener('resize', onResize, { signal: ac.signal });

  // Background event stream ticker — light, just to keep panel alive.
  const tickerId = setInterval(() => {
    if (ac.signal.aborted) return;
    const t = EVENT_TEMPLATES[Math.floor(Math.random() * EVENT_TEMPLATES.length)];
    pushEvent(eventstream, t.from, t.to, t.msg, t.tone);
  }, 2400);
  ac.signal.addEventListener('abort', () => clearInterval(tickerId));

  // Seed a few initial events so the panel is not empty.
  EVENT_TEMPLATES.slice(0, 4).forEach(t => pushEvent(eventstream, t.from, t.to, t.msg, t.tone));

  const clock = new THREE.Clock();
  function tick() {
    if (ac.signal.aborted) {
      // Dispose meshes/materials/geoms.
      scene.traverse(o => {
        if (o.geometry) o.geometry.dispose();
        const mats = Array.isArray(o.material) ? o.material : o.material ? [o.material] : [];
        mats.forEach(m => m.dispose());
      });
      // Dispose tracked textures + extra resources.
      disposables.forEach(d => { try { d.dispose(); } catch {} });
      try { envRT?.dispose(); } catch {}
      try { pmrem?.dispose(); } catch {}
      renderer.dispose();
      renderer.domElement.remove();
      return;
    }

    const t = clock.getElapsedTime();
    NODES.forEach(node => {
      const group = nodeById.get(node.id);
      group.rotation.y += group.userData.spin;
      const pulse = 1 + Math.sin(t * 2.0 + group.userData.phase) * 0.012;
      group.scale.setScalar(pulse);

      // Gem emissive — slow pulse on idle, bright on hover/active.
      const mat = group.userData.bodyMaterial;
      if (mat) {
        const isLit = group === hover || group === active;
        const idle  = group.userData.baseEmissive + Math.sin(t * 1.3 + group.userData.phase) * 0.10;
        const target = isLit ? 1.05 : idle;
        mat.emissiveIntensity += (target - mat.emissiveIntensity) * 0.18;
      }

      // Halo subtle breathing.
      if (group.userData.halo) {
        const haloBase = 0.28 + Math.sin(t * 1.2 + group.userData.phase) * 0.10;
        const isLit = group === hover || group === active;
        const haloTarget = isLit ? 0.55 : haloBase;
        group.userData.halo.material.opacity += (haloTarget - group.userData.halo.material.opacity) * 0.18;
      }
    });
    edgeObjects.forEach(edge => animateEdge(edge, t));

    // Energy core pulses.
    if (corePulseRefs.coreUniforms) corePulseRefs.coreUniforms.uTime.value = t;
    if (corePulseRefs.column) corePulseRefs.column.material.opacity = 0.35 + Math.sin(t * 1.4) * 0.10;
    if (stationParts.platformRing) stationParts.platformRing.material.opacity = 0.5 + Math.sin(t * 1.0) * 0.18;
    if (stationParts.engineGlow) stationParts.engineGlow.opacity = 0.55 + Math.sin(t * 1.6) * 0.18;

    // Running lights breathe (each with its own phase) — small organic flicker.
    if (stationParts.runningLights) {
      for (const dot of stationParts.runningLights) {
        const phase = dot.userData.phase || 0;
        const base = dot.userData.baseOpacity || 0.85;
        dot.material.opacity = base * (0.55 + 0.45 * Math.abs(Math.sin(t * 1.6 + phase)));
      }
    }

    // Particles slow rotation for parallax feel.
    particles.points.rotation.y += 0.0005;

    controls.update();
    renderer.render(scene, camera);

    if (active) projectTooltip(tooltip, active, camera, stage);
    requestAnimationFrame(tick);
  }
  requestAnimationFrame(tick);
}

// ─────────────────────────────────────────────────────────────────────────────
// Procedural texture helpers
// ─────────────────────────────────────────────────────────────────────────────

function makeStarfieldTexture(THREE) {
  const c = document.createElement('canvas');
  c.width = 2048;
  c.height = 1024;
  const ctx = c.getContext('2d');

  // Deep-space gradient
  const grad = ctx.createLinearGradient(0, 0, 0, 1024);
  grad.addColorStop(0, '#020410');
  grad.addColorStop(0.5, '#070a1c');
  grad.addColorStop(1, '#01020a');
  ctx.fillStyle = grad;
  ctx.fillRect(0, 0, 2048, 1024);

  // Soft nebula clouds
  const blobs = [
    [400,  300, 380, 'rgba(65, 230, 255, 0.10)'],
    [1500, 250, 440, 'rgba(180, 110, 255, 0.10)'],
    [1100, 720, 480, 'rgba(255, 100, 200, 0.07)'],
    [200,  800, 320, 'rgba(80, 200, 255, 0.07)'],
    [1750, 800, 360, 'rgba(120, 80, 220, 0.08)'],
  ];
  blobs.forEach(([x, y, r, color]) => {
    const g = ctx.createRadialGradient(x, y, 0, x, y, r);
    g.addColorStop(0, color);
    g.addColorStop(1, 'rgba(0,0,0,0)');
    ctx.fillStyle = g;
    ctx.fillRect(0, 0, 2048, 1024);
  });

  // Star field (small)
  for (let i = 0; i < 1600; i++) {
    const x = Math.random() * 2048;
    const y = Math.random() * 1024;
    const r = Math.random() * 1.3 + 0.2;
    const a = Math.random() * 0.7 + 0.2;
    const tint = Math.random();
    const cr = 200 + (tint * 55) | 0;
    const cg = 220 + ((1 - tint) * 35) | 0;
    ctx.fillStyle = `rgba(${cr}, ${cg}, 255, ${a.toFixed(2)})`;
    ctx.beginPath();
    ctx.arc(x, y, r, 0, Math.PI * 2);
    ctx.fill();
  }

  // Brighter accent stars with cross flare
  for (let i = 0; i < 22; i++) {
    const x = Math.random() * 2048;
    const y = Math.random() * 1024;
    const a = Math.random() * 0.4 + 0.6;
    ctx.fillStyle = `rgba(220, 240, 255, ${a})`;
    ctx.beginPath();
    ctx.arc(x, y, 1.6, 0, Math.PI * 2);
    ctx.fill();
    ctx.strokeStyle = `rgba(220, 240, 255, ${a * 0.4})`;
    ctx.lineWidth = 0.6;
    ctx.beginPath();
    ctx.moveTo(x - 6, y); ctx.lineTo(x + 6, y);
    ctx.moveTo(x, y - 6); ctx.lineTo(x, y + 6);
    ctx.stroke();
  }

  const tex = new THREE.CanvasTexture(c);
  tex.colorSpace = THREE.SRGBColorSpace;
  tex.wrapS = THREE.RepeatWrapping;
  return tex;
}

function makeHexDiscTexture(THREE) {
  const c = document.createElement('canvas');
  c.width = 1024;
  c.height = 1024;
  const ctx = c.getContext('2d');
  const cx = 512, cy = 512;

  // Base disc
  const base = ctx.createRadialGradient(cx, cy, 60, cx, cy, 512);
  base.addColorStop(0, '#0a1322');
  base.addColorStop(0.5, '#070d18');
  base.addColorStop(0.85, '#040810');
  base.addColorStop(1, '#020308');
  ctx.fillStyle = base;
  ctx.beginPath();
  ctx.arc(cx, cy, 510, 0, Math.PI * 2);
  ctx.fill();

  // Hex grid clipped to circle
  ctx.save();
  ctx.beginPath();
  ctx.arc(cx, cy, 470, 0, Math.PI * 2);
  ctx.clip();
  drawHexGrid(ctx, 1024, 1024, 26, 'rgba(70, 200, 255, 0.10)', 'rgba(70, 200, 255, 0.04)');
  ctx.restore();

  // Concentric guide rings
  [400, 340, 270, 200, 130, 80].forEach((r, i) => {
    ctx.lineWidth = i === 0 ? 2 : 1;
    ctx.strokeStyle = `rgba(70, 200, 255, ${0.08 + i * 0.04})`;
    ctx.beginPath();
    ctx.arc(cx, cy, r, 0, Math.PI * 2);
    ctx.stroke();
  });

  // Outer rim glow band
  ctx.lineWidth = 8;
  ctx.strokeStyle = 'rgba(80, 220, 255, 0.55)';
  ctx.beginPath();
  ctx.arc(cx, cy, 492, 0, Math.PI * 2);
  ctx.stroke();
  ctx.lineWidth = 2;
  ctx.strokeStyle = 'rgba(170, 240, 255, 0.9)';
  ctx.beginPath();
  ctx.arc(cx, cy, 484, 0, Math.PI * 2);
  ctx.stroke();

  // Tick marks
  for (let i = 0; i < 60; i++) {
    const a = (i / 60) * Math.PI * 2;
    const r1 = 458;
    const r2 = i % 5 === 0 ? 432 : 446;
    ctx.strokeStyle = `rgba(80, 220, 255, ${i % 5 === 0 ? 0.65 : 0.32})`;
    ctx.lineWidth = i % 5 === 0 ? 2 : 1;
    ctx.beginPath();
    ctx.moveTo(cx + Math.cos(a) * r1, cy + Math.sin(a) * r1);
    ctx.lineTo(cx + Math.cos(a) * r2, cy + Math.sin(a) * r2);
    ctx.stroke();
  }

  // Six radial spokes for that "stargate" feel
  ctx.lineWidth = 2;
  ctx.strokeStyle = 'rgba(80, 220, 255, 0.18)';
  for (let i = 0; i < 6; i++) {
    const a = (i / 6) * Math.PI * 2;
    ctx.beginPath();
    ctx.moveTo(cx + Math.cos(a) * 80, cy + Math.sin(a) * 80);
    ctx.lineTo(cx + Math.cos(a) * 460, cy + Math.sin(a) * 460);
    ctx.stroke();
  }

  // Center socket darken
  const cg = ctx.createRadialGradient(cx, cy, 0, cx, cy, 120);
  cg.addColorStop(0, 'rgba(0, 0, 0, 0.85)');
  cg.addColorStop(0.7, 'rgba(0, 0, 0, 0.55)');
  cg.addColorStop(1, 'rgba(0, 0, 0, 0)');
  ctx.fillStyle = cg;
  ctx.fillRect(0, 0, 1024, 1024);

  // Inner core ring
  ctx.lineWidth = 3;
  ctx.strokeStyle = 'rgba(120, 240, 255, 0.75)';
  ctx.beginPath();
  ctx.arc(cx, cy, 70, 0, Math.PI * 2);
  ctx.stroke();
  ctx.lineWidth = 1;
  ctx.strokeStyle = 'rgba(120, 240, 255, 0.40)';
  ctx.beginPath();
  ctx.arc(cx, cy, 60, 0, Math.PI * 2);
  ctx.stroke();

  const tex = new THREE.CanvasTexture(c);
  tex.colorSpace = THREE.SRGBColorSpace;
  tex.anisotropy = 8;
  return tex;
}

/**
 * Procedural fallback for the station hull side panels — used until the real
 * generated PNG (assets/actor-world/station-hull.png) loads in.
 */
function makeHullTextureProc(THREE) {
  const c = document.createElement('canvas');
  c.width = 512;
  c.height = 256;
  const ctx = c.getContext('2d');

  // Dark navy/gunmetal gradient
  const grad = ctx.createLinearGradient(0, 0, 0, 256);
  grad.addColorStop(0,   '#0c1322');
  grad.addColorStop(0.5, '#0a1018');
  grad.addColorStop(1,   '#070b14');
  ctx.fillStyle = grad;
  ctx.fillRect(0, 0, 512, 256);

  // Vertical panel divisions
  ctx.strokeStyle = 'rgba(60, 110, 160, 0.35)';
  ctx.lineWidth = 1;
  for (let x = 0; x < 512; x += 64) {
    ctx.beginPath();
    ctx.moveTo(x, 0);
    ctx.lineTo(x, 256);
    ctx.stroke();
  }
  // Horizontal panel divisions
  for (let y = 0; y < 256; y += 64) {
    ctx.beginPath();
    ctx.moveTo(0, y);
    ctx.lineTo(512, y);
    ctx.stroke();
  }

  // Subtle plate weathering / scratches
  for (let i = 0; i < 80; i++) {
    const x = Math.random() * 512;
    const y = Math.random() * 256;
    const len = 4 + Math.random() * 24;
    const a = Math.random() * 0.06 + 0.02;
    ctx.strokeStyle = `rgba(180, 200, 220, ${a.toFixed(2)})`;
    ctx.lineWidth = 0.5;
    ctx.beginPath();
    ctx.moveTo(x, y);
    ctx.lineTo(x + len, y + (Math.random() - 0.5) * 4);
    ctx.stroke();
  }

  // Glowing cyan LED strips across panels
  for (const y of [40, 168]) {
    ctx.fillStyle = 'rgba(80, 220, 255, 0.55)';
    for (let x = 16; x < 512; x += 40) {
      ctx.fillRect(x, y, 14, 2);
    }
    // soft glow halo around strips
    const glow = ctx.createLinearGradient(0, y - 4, 0, y + 6);
    glow.addColorStop(0, 'rgba(80, 220, 255, 0)');
    glow.addColorStop(0.5, 'rgba(80, 220, 255, 0.18)');
    glow.addColorStop(1, 'rgba(80, 220, 255, 0)');
    ctx.fillStyle = glow;
    ctx.fillRect(0, y - 4, 512, 10);
  }

  // Warning chevron stripes (subtle)
  ctx.fillStyle = 'rgba(255, 178, 74, 0.10)';
  for (let x = 0; x < 512; x += 8) {
    ctx.fillRect(x, 240, 4, 8);
  }

  const tex = new THREE.CanvasTexture(c);
  tex.colorSpace = THREE.SRGBColorSpace;
  return tex;
}

/**
 * Optional async swap-in: try to load `url` from disk; on success run `onLoad`,
 * on 404 silently keep whatever fallback the caller already wired up.
 */
function loadIfAvailable(THREE, url, onLoad) {
  new THREE.TextureLoader().load(
    url,
    tex => { try { onLoad(tex); } catch {} },
    undefined,
    () => { /* 404 — keep the procedural fallback */ }
  );
}

function makeHexPedestalTexture(THREE) {
  const c = document.createElement('canvas');
  c.width = 256;
  c.height = 256;
  const ctx = c.getContext('2d');
  const cx = 128, cy = 128;

  const base = ctx.createRadialGradient(cx, cy, 8, cx, cy, 128);
  base.addColorStop(0, '#0d1827');
  base.addColorStop(0.6, '#070d18');
  base.addColorStop(1, '#020308');
  ctx.fillStyle = base;
  ctx.beginPath();
  ctx.arc(cx, cy, 126, 0, Math.PI * 2);
  ctx.fill();

  ctx.save();
  ctx.beginPath();
  ctx.arc(cx, cy, 116, 0, Math.PI * 2);
  ctx.clip();
  drawHexGrid(ctx, 256, 256, 14, 'rgba(110, 220, 255, 0.20)', 'rgba(110, 220, 255, 0.06)');
  ctx.restore();

  ctx.lineWidth = 2;
  ctx.strokeStyle = 'rgba(170, 240, 255, 0.7)';
  ctx.beginPath();
  ctx.arc(cx, cy, 120, 0, Math.PI * 2);
  ctx.stroke();

  ctx.lineWidth = 1;
  ctx.strokeStyle = 'rgba(120, 200, 255, 0.30)';
  ctx.beginPath();
  ctx.arc(cx, cy, 80, 0, Math.PI * 2);
  ctx.stroke();

  return new THREE.CanvasTexture(c);
}

function makeRadialGlowTexture(THREE) {
  const c = document.createElement('canvas');
  c.width = 256;
  c.height = 256;
  const ctx = c.getContext('2d');
  const g = ctx.createRadialGradient(128, 128, 0, 128, 128, 128);
  g.addColorStop(0,    'rgba(255,255,255,1.0)');
  g.addColorStop(0.20, 'rgba(255,255,255,0.55)');
  g.addColorStop(0.55, 'rgba(255,255,255,0.18)');
  g.addColorStop(1,    'rgba(255,255,255,0)');
  ctx.fillStyle = g;
  ctx.fillRect(0, 0, 256, 256);
  return new THREE.CanvasTexture(c);
}

function drawHexGrid(ctx, w, h, size, mainStroke, faintStroke) {
  const dx = size * 1.5;
  const dy = size * Math.sqrt(3);
  ctx.lineWidth = 1;
  for (let row = -1; row * dy < h + dy; row++) {
    for (let col = -1; col * dx < w + dx; col++) {
      const x = col * dx;
      const y = row * dy + (col % 2 ? dy / 2 : 0);
      ctx.beginPath();
      for (let i = 0; i < 6; i++) {
        const a = (Math.PI / 3) * i;
        const px = x + Math.cos(a) * size;
        const py = y + Math.sin(a) * size;
        if (i === 0) ctx.moveTo(px, py); else ctx.lineTo(px, py);
      }
      ctx.closePath();
      ctx.strokeStyle = (col + row) % 4 === 0 ? mainStroke : faintStroke;
      ctx.stroke();
    }
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Scene builders
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Build a multi-deck space station that sits BENEATH the actor stage so the
 * actors no longer feel like they are floating in empty space. Layout (top→bottom):
 *
 *   ┌─ TOP DECK (textured deck disc + glowing outer rim)
 *   │     ↓ upper hull cylinder (textured) + running lights
 *   ├─ MID DECK (slightly smaller dark disc)
 *   │     ↓ mid hull cylinder (textured, taper inward)
 *   ├─ ENGINE RING (small disc)
 *   │     ↓ engine column (lowest) with downward cyan glow
 *   ↓
 * Plus 4 antennas on the upper hull rim.
 *
 * `materials.deckMat` and `materials.hullMat` are passed in already constructed
 * (they own the textures so the loader can swap procedural ↔ generated PNG).
 */
function addSpaceStation(THREE, scene, materials) {
  const disposables = [];
  const root = new THREE.Group();

  // ── Top deck (the surface the actors stand on) ────────────────────────────
  const topDeck = new THREE.Mesh(new THREE.CircleGeometry(5.4, 96), materials.deckMat);
  topDeck.rotation.x = -Math.PI / 2;
  topDeck.position.y = -1.78;
  root.add(topDeck);

  // ── Upper hull (between top deck and mid deck) ────────────────────────────
  const upperHull = new THREE.Mesh(
    new THREE.CylinderGeometry(5.4, 5.0, 0.95, 64, 1, true),
    materials.hullMat
  );
  upperHull.position.y = -2.255;
  root.add(upperHull);

  // ── Mid deck (smaller, dark) ──────────────────────────────────────────────
  const midDeckMat = new THREE.MeshStandardMaterial({
    color: 0x07111A,
    roughness: 0.5,
    metalness: 0.7,
    emissive: 0x0a2030,
    emissiveIntensity: 0.18,
    side: THREE.DoubleSide,
  });
  const midDeck = new THREE.Mesh(new THREE.CircleGeometry(4.6, 64), midDeckMat);
  midDeck.rotation.x = -Math.PI / 2;
  midDeck.position.y = -2.74;
  root.add(midDeck);

  // ── Mid hull (tapered cylinder, textured) ─────────────────────────────────
  const midHull = new THREE.Mesh(
    new THREE.CylinderGeometry(4.6, 3.6, 1.5, 56, 1, true),
    materials.hullMat
  );
  midHull.position.y = -3.50;
  root.add(midHull);

  // ── Engine ring deck ──────────────────────────────────────────────────────
  const engineRingMat = new THREE.MeshStandardMaterial({
    color: 0x05080F,
    roughness: 0.55,
    metalness: 0.7,
    side: THREE.DoubleSide,
  });
  const engineRingDeck = new THREE.Mesh(new THREE.CircleGeometry(3.6, 64), engineRingMat);
  engineRingDeck.rotation.x = -Math.PI / 2;
  engineRingDeck.position.y = -4.27;
  root.add(engineRingDeck);

  // ── Engine column (lowest hull) ───────────────────────────────────────────
  const engineColumn = new THREE.Mesh(
    new THREE.CylinderGeometry(2.8, 2.4, 1.2, 48, 1, true),
    materials.hullMat
  );
  engineColumn.position.y = -4.88;
  root.add(engineColumn);

  // Engine cap (closes the bottom — visible if camera dips below horizon)
  const engineCap = new THREE.Mesh(
    new THREE.CircleGeometry(2.4, 48),
    new THREE.MeshStandardMaterial({
      color: 0x05080F,
      emissive: 0x0a4060,
      emissiveIntensity: 0.55,
      roughness: 0.4,
      metalness: 0.7,
      side: THREE.DoubleSide,
    })
  );
  engineCap.rotation.x = Math.PI / 2;
  engineCap.position.y = -5.48;
  root.add(engineCap);

  // ── Engine downward glow ──────────────────────────────────────────────────
  const engineGlowMat = new THREE.MeshBasicMaterial({
    color: 0x66E2FF,
    transparent: true,
    opacity: 0.72,
    side: THREE.DoubleSide,
    depthWrite: false,
    blending: THREE.AdditiveBlending,
  });
  const engineGlow = new THREE.Mesh(new THREE.CircleGeometry(2.45, 48), engineGlowMat);
  engineGlow.rotation.x = Math.PI / 2;
  engineGlow.position.y = -5.52;
  root.add(engineGlow);

  const engineLight = new THREE.PointLight(0x66E2FF, 9, 14, 2);
  engineLight.position.y = -5.4;
  root.add(engineLight);

  // ── Glowing rim torus on the top deck ────────────────────────────────────
  const platformRing = new THREE.Mesh(
    new THREE.TorusGeometry(5.42, 0.026, 8, 160),
    new THREE.MeshBasicMaterial({
      color: 0x66E2FF,
      transparent: true,
      opacity: 0.65,
      depthWrite: false,
      blending: THREE.AdditiveBlending,
    })
  );
  platformRing.rotation.x = Math.PI / 2;
  platformRing.position.y = -1.75;
  root.add(platformRing);

  // Mid deck rim glow
  const midRing = new THREE.Mesh(
    new THREE.TorusGeometry(4.62, 0.018, 8, 140),
    new THREE.MeshBasicMaterial({
      color: 0x41E6FF,
      transparent: true,
      opacity: 0.45,
      depthWrite: false,
      blending: THREE.AdditiveBlending,
    })
  );
  midRing.rotation.x = Math.PI / 2;
  midRing.position.y = -2.71;
  root.add(midRing);

  // Inner orbit ring (mid radius on top deck)
  const orbitRing = new THREE.Mesh(
    new THREE.TorusGeometry(3.9, 0.012, 8, 140),
    new THREE.MeshBasicMaterial({
      color: 0x41E6FF,
      transparent: true,
      opacity: 0.32,
      depthWrite: false,
      blending: THREE.AdditiveBlending,
    })
  );
  orbitRing.rotation.x = Math.PI / 2;
  orbitRing.position.y = -1.74;
  root.add(orbitRing);

  // ── Running lights along upper hull rim (top + mid) ──────────────────────
  const runningLights = [];
  function addRunningLightRow(count, radius, y, color, accent, size = 0.05) {
    for (let i = 0; i < count; i++) {
      const a = (i / count) * Math.PI * 2;
      const dot = new THREE.Mesh(
        new THREE.SphereGeometry(size, 8, 8),
        new THREE.MeshBasicMaterial({
          color: i % 6 === 0 ? accent : color,
          transparent: true,
          opacity: 0.92,
          depthWrite: false,
          blending: THREE.AdditiveBlending,
        })
      );
      dot.position.set(Math.cos(a) * radius, y, Math.sin(a) * radius);
      dot.userData.phase = i * 0.4 + Math.random();
      dot.userData.baseOpacity = i % 6 === 0 ? 0.95 : 0.7;
      runningLights.push(dot);
      root.add(dot);
    }
  }
  addRunningLightRow(36, 5.45, -1.92, 0x66E2FF, 0xFFB24A, 0.05);
  addRunningLightRow(28, 4.68, -2.86, 0x66E2FF, 0xFFB24A, 0.045);
  addRunningLightRow(20, 3.65, -4.40, 0x66E2FF, 0xFF6BC8, 0.04);

  // ── Antennas (4 spires) ──────────────────────────────────────────────────
  for (let i = 0; i < 4; i++) {
    const a = (i / 4) * Math.PI * 2 + Math.PI / 4;
    const x = Math.cos(a) * 5.55;
    const z = Math.sin(a) * 5.55;
    const ant = new THREE.Mesh(
      new THREE.CylinderGeometry(0.04, 0.10, 1.55, 6),
      new THREE.MeshStandardMaterial({
        color: 0x182838,
        roughness: 0.4,
        metalness: 0.9,
      })
    );
    ant.position.set(x, -1.18, z);
    root.add(ant);

    const tip = new THREE.Mesh(
      new THREE.SphereGeometry(0.09, 12, 12),
      new THREE.MeshBasicMaterial({
        color: 0xFFB24A,
        transparent: true,
        opacity: 0.95,
        depthWrite: false,
        blending: THREE.AdditiveBlending,
      })
    );
    tip.position.set(x, -0.42, z);
    tip.userData.phase = i * 1.7;
    tip.userData.isAntenna = true;
    runningLights.push(tip);
    root.add(tip);
  }

  scene.add(root);
  disposables.push(midDeckMat, engineRingMat, engineGlowMat);
  return { root, disposables, platformRing, runningLights, engineGlow: engineGlowMat };
}

function addStageCore(THREE, scene) {
  const disposables = [];

  // Energy column rising from disc center to under ActorStage.
  const columnGeom = new THREE.CylinderGeometry(0.18, 0.92, 3.45, 48, 1, true);
  const columnMat = new THREE.MeshBasicMaterial({
    color: 0x66E2FF,
    transparent: true,
    opacity: 0.4,
    side: THREE.DoubleSide,
    depthWrite: false,
    blending: THREE.AdditiveBlending,
  });
  const column = new THREE.Mesh(columnGeom, columnMat);
  column.position.y = 0.05;
  scene.add(column);

  // Inner crystal core (separate from ActorStage node so the node can still be picked).
  const coreUniforms = {
    uColor: { value: new THREE.Color(0xA9F4FF) },
    uTime:  { value: 0 },
    uHover: { value: 0 }, // unused but matches uniform layout
  };
  const coreMat = new THREE.ShaderMaterial({
    vertexShader: VERT,
    fragmentShader: CORE_FRAG,
    uniforms: coreUniforms,
    transparent: true,
    blending: THREE.AdditiveBlending,
    depthWrite: false,
    depthTest: false,
  });
  const innerCore = new THREE.Mesh(new THREE.SphereGeometry(0.42, 48, 48), coreMat);
  innerCore.position.set(0, 1.65, 0);
  innerCore.renderOrder = 999;
  scene.add(innerCore);

  // Ground halo at column base
  const groundHalo = new THREE.Mesh(
    new THREE.RingGeometry(0.4, 1.4, 64),
    new THREE.MeshBasicMaterial({
      color: 0x66E2FF,
      transparent: true,
      opacity: 0.55,
      side: THREE.DoubleSide,
      depthWrite: false,
      blending: THREE.AdditiveBlending,
    })
  );
  groundHalo.rotation.x = -Math.PI / 2;
  groundHalo.position.y = -1.74;
  scene.add(groundHalo);

  return { disposables, column, coreUniforms };
}

function addParticles(THREE, scene) {
  const N = 360;
  const positions = new Float32Array(N * 3);
  for (let i = 0; i < N; i++) {
    const r = 6 + Math.random() * 12;
    const a = Math.random() * Math.PI * 2;
    const y = -1.6 + Math.random() * 5.5;
    positions[i * 3]     = Math.cos(a) * r;
    positions[i * 3 + 1] = y;
    positions[i * 3 + 2] = Math.sin(a) * r;
  }
  const geom = new THREE.BufferGeometry();
  geom.setAttribute('position', new THREE.BufferAttribute(positions, 3));
  const mat = new THREE.PointsMaterial({
    color: 0x9CD9FF,
    size: 0.045,
    transparent: true,
    opacity: 0.5,
    depthWrite: false,
    blending: THREE.AdditiveBlending,
  });
  const points = new THREE.Points(geom, mat);
  scene.add(points);
  return { points };
}

// ─────────────────────────────────────────────────────────────────────────────
// Nodes
// ─────────────────────────────────────────────────────────────────────────────

function createNode(THREE, node, { haloTex, pedestalTex }) {
  const group = new THREE.Group();
  group.position.fromArray(node.position);
  group.userData.node = node;
  group.userData.phase = Math.random() * Math.PI * 2;
  group.userData.spin = node.id === 'stage' ? 0.006 : 0.0025;

  const color = new THREE.Color(node.color);

  // Gem-like body — physical material with iridescence + clearcoat + transmission.
  // envMap reflection comes from scene.environment (set in runScene from the starfield).
  const baseEmissive = 0.42;
  const bodyMaterial = new THREE.MeshPhysicalMaterial({
    color,
    metalness: 0.18,
    roughness: 0.06,
    transmission: 0.30,
    thickness: 0.45,
    ior: 1.78,
    iridescence: 0.65,
    iridescenceIOR: 1.3,
    iridescenceThicknessRange: [120, 800],
    clearcoat: 1.0,
    clearcoatRoughness: 0.05,
    emissive: color,
    emissiveIntensity: baseEmissive,
    envMapIntensity: 1.6,
  });
  group.userData.bodyMaterial = bodyMaterial;
  group.userData.baseEmissive = baseEmissive;

  const body = createNodeBody(THREE, node, bodyMaterial);
  body.userData.nodeGroup = group;
  group.userData.pickable = body;
  group.add(body);

  // Wireframe shell
  const shell = new THREE.Mesh(
    new THREE.IcosahedronGeometry(0.86 * node.scale, 1),
    new THREE.MeshBasicMaterial({
      color,
      transparent: true,
      opacity: 0.18,
      wireframe: true,
      depthWrite: false,
      blending: THREE.AdditiveBlending,
    })
  );
  group.add(shell);

  // Equator ring
  const ring = new THREE.Mesh(
    new THREE.TorusGeometry(1.05 * node.scale, 0.012, 8, 88),
    new THREE.MeshBasicMaterial({
      color,
      transparent: true,
      opacity: 0.7,
      depthWrite: false,
      blending: THREE.AdditiveBlending,
    })
  );
  ring.rotation.x = Math.PI / 2;
  group.add(ring);

  // Hex pedestal (textured top + dark frustum body)
  const pedestalTop = new THREE.Mesh(
    new THREE.CircleGeometry(0.78 * node.scale, 32),
    new THREE.MeshBasicMaterial({
      map: pedestalTex,
      transparent: true,
      opacity: 0.95,
      side: THREE.DoubleSide,
    })
  );
  pedestalTop.rotation.x = -Math.PI / 2;
  pedestalTop.position.y = -0.78 * node.scale;
  group.add(pedestalTop);

  const pedestalBody = new THREE.Mesh(
    new THREE.CylinderGeometry(0.78 * node.scale, 0.92 * node.scale, 0.22, 36, 1, true),
    new THREE.MeshStandardMaterial({
      color: 0x09111A,
      emissive: color.clone().multiplyScalar(0.18),
      roughness: 0.45,
      metalness: 0.7,
      side: THREE.DoubleSide,
    })
  );
  pedestalBody.position.y = -0.89 * node.scale;
  group.add(pedestalBody);

  // Halo sprite (additive bloom under the orb)
  const haloMat = new THREE.SpriteMaterial({
    map: haloTex,
    color,
    transparent: true,
    opacity: 0.35,
    depthWrite: false,
    blending: THREE.AdditiveBlending,
  });
  const halo = new THREE.Sprite(haloMat);
  halo.scale.set(2.6 * node.scale, 2.6 * node.scale, 1);
  halo.position.y = -0.05;
  group.add(halo);
  group.userData.halo = halo;

  // Local point light
  group.add(new THREE.PointLight(color, node.id === 'stage' ? 9 : 4.4, 5.8, 2));
  return group;
}

function createNodeBody(THREE, node, material) {
  // Box-shape nodes stand UP like buildings (long axis vertical), per concept image.
  if (node.shape === 'bus') {
    return new THREE.Mesh(new THREE.BoxGeometry(0.78 * node.scale, 1.25 * node.scale, 0.78 * node.scale), material);
  }
  if (node.shape === 'terminal') {
    return new THREE.Mesh(new THREE.BoxGeometry(0.86 * node.scale, 1.05 * node.scale, 0.86 * node.scale), material);
  }
  if (node.shape === 'port') {
    return new THREE.Mesh(new THREE.CylinderGeometry(0.55 * node.scale, 0.72 * node.scale, 0.94 * node.scale, 6), material);
  }
  if (node.shape === 'stack') {
    return new THREE.Mesh(new THREE.CylinderGeometry(0.72 * node.scale, 0.72 * node.scale, 0.9 * node.scale, 8), material);
  }
  if (node.shape === 'model') {
    return new THREE.Mesh(new THREE.OctahedronGeometry(0.85 * node.scale, 1), material);
  }
  if (node.shape === 'hub') {
    // Outer crystalline shell for ActorStage. Inner glowing orb is added separately by addStageCore.
    return new THREE.Mesh(new THREE.IcosahedronGeometry(0.82 * node.scale, 1), material);
  }
  // Smooth jewel-like sphere for actor nodes.
  return new THREE.Mesh(new THREE.SphereGeometry(0.78 * node.scale, 64, 64), material);
}

// ─────────────────────────────────────────────────────────────────────────────
// Edges
// ─────────────────────────────────────────────────────────────────────────────

function createEdge(THREE, from, to, label, index) {
  const material = new THREE.MeshBasicMaterial({
    color: index % 3 === 0 ? 0x41E6FF : index % 3 === 1 ? 0xF36BFF : 0xFFE066,
    transparent: true,
    opacity: 0.42,
    depthWrite: false,
    blending: THREE.AdditiveBlending,
  });
  const arrowMaterial = new THREE.MeshBasicMaterial({
    color: 0xF7FFE8,
    transparent: true,
    opacity: 0.92,
    depthWrite: false,
    blending: THREE.AdditiveBlending,
  });
  const tube = new THREE.Mesh(new THREE.BufferGeometry(), material);
  const arrow = new THREE.Mesh(new THREE.ConeGeometry(0.11, 0.34, 18), arrowMaterial);
  const pulse = new THREE.Mesh(new THREE.SphereGeometry(0.085, 16, 16), arrowMaterial.clone());
  const group = new THREE.Group();
  group.add(tube, arrow, pulse);
  const edge = { from, to, label, group, tube, arrow, pulse, curve: null, offset: index * 0.13 };
  updateEdge(THREE, edge);
  return edge;
}

function updateEdge(THREE, edge) {
  const a = edge.from.position.clone();
  const b = edge.to.position.clone();
  const mid = a.clone().add(b).multiplyScalar(0.5);
  mid.y += 0.6 + a.distanceTo(b) * 0.08;
  const curve = new THREE.CatmullRomCurve3([a, mid, b]);
  edge.curve = curve;
  edge.tube.geometry.dispose();
  edge.tube.geometry = new THREE.TubeGeometry(curve, 36, 0.03, 8, false);

  const p1 = curve.getPoint(0.58);
  const p2 = curve.getPoint(0.64);
  const dir = p2.clone().sub(p1).normalize();
  edge.arrow.position.copy(p2);
  edge.arrow.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), dir);
}

function animateEdge(edge, t) {
  if (!edge.curve) return;
  const p = (t * 0.18 + edge.offset) % 1;
  edge.pulse.position.copy(edge.curve.getPoint(p));
  edge.pulse.material.opacity = 0.4 + Math.sin(p * Math.PI) * 0.55;
}

// ─────────────────────────────────────────────────────────────────────────────
// Tooltip
// ─────────────────────────────────────────────────────────────────────────────

function openFunctionCard(tooltip, node, ac) {
  const list = arr => arr.map(v => `<span class="aw-tt-trig">${escapeHtml(v)}</span>`).join('');
  tooltip.innerHTML = `
    <div class="aw-tt-head">
      <span class="aw-tt-glyph">${escapeHtml(node.kind)}</span>
      <div class="aw-tt-titles">
        <div class="aw-tt-name">${escapeHtml(node.title)}</div>
        <div class="aw-tt-role">${escapeHtml(node.subtitle)}</div>
      </div>
      <button class="aw-tt-x" aria-label="close">x</button>
    </div>
    <div class="aw-tt-body">
      <div class="aw-tt-desc">${escapeHtml(node.summary)}</div>
      <div class="aw-tt-section">INPUT</div>
      <div class="aw-tt-trigs">${list(node.inputs)}</div>
      <div class="aw-tt-section">OUTPUT</div>
      <div class="aw-tt-trigs">${list(node.outputs)}</div>
      <div class="aw-tt-section">IMPLEMENTATION</div>
      <div class="aw-tt-files">${node.files.map(f => `<code>${escapeHtml(f)}</code>`).join('')}</div>
    </div>`;
  tooltip.style.display = 'block';
  tooltip.querySelector('.aw-tt-x').addEventListener('click', () => closeTooltip(tooltip), { signal: ac.signal });
}

function projectTooltip(tooltip, group, camera, stage) {
  const v = group.position.clone();
  v.y += 0.72;
  v.project(camera);
  const x = (v.x + 1) / 2 * stage.clientWidth;
  const y = (-v.y + 1) / 2 * stage.clientHeight;
  const behind = v.z > 1;
  tooltip.style.transform = `translate(${Math.min(x + 18, stage.clientWidth - 330)}px, ${Math.max(y - 20, 12)}px)`;
  tooltip.style.opacity = behind ? '0' : '1';
  tooltip.style.pointerEvents = behind ? 'none' : 'auto';
}

function closeTooltip(tooltip) {
  tooltip.style.display = 'none';
}

// ─────────────────────────────────────────────────────────────────────────────
// HUD panels
// ─────────────────────────────────────────────────────────────────────────────

function buildHud() {
  const rows = NODES.map(node => h('div', { class: 'aw-hud-row' }, [
    h('span', { class: 'aw-hud-dot', style: { background: `#${node.color.toString(16).padStart(6, '0')}` } }),
    h('span', { class: 'aw-hud-title' }, node.title),
    h('span', { class: 'aw-hud-kind' }, node.kind),
  ]));
  return h('div', { class: 'aw-legend aw-hud' }, rows);
}

function buildToolRail() {
  return h('div', { class: 'aw-toolrail' }, [
    h('div', { class: 'aw-toolrail-title' }, 'TOOL SURFACE'),
    ...TOOL_SURFACE.map(tool => h('span', { class: 'aw-tool-chip' }, tool)),
  ]);
}

function buildSystemStatus() {
  const bars = [
    { label: 'CPU', value: 42, color: '#41E6FF' },
    { label: 'GPU', value: 67, color: '#8C6CFF' },
    { label: 'MEM', value: 38, color: '#2DFF9A' },
    { label: 'PWR', value: 81, color: '#FFE066' },
  ];
  const rows = bars.map(b => h('div', { class: 'aw-sys-row' }, [
    h('span', { class: 'aw-sys-label' }, b.label),
    h('div',  { class: 'aw-sys-track' }, [
      h('div', { class: 'aw-sys-fill', style: { width: `${b.value}%`, background: b.color } }),
    ]),
    h('span', { class: 'aw-sys-pct' }, `${b.value}%`),
  ]));
  return h('div', { class: 'aw-sysstatus' }, [
    h('div', { class: 'aw-sysstatus-title' }, 'SYSTEM STATUS'),
    ...rows,
  ]);
}

function buildEventStream() {
  const list = h('div', { class: 'aw-events-list' });
  const wrap = h('div', { class: 'aw-eventstream' }, [
    h('div', { class: 'aw-eventstream-title' }, 'EVENT STREAM'),
    list,
  ]);
  wrap._list = list;
  return wrap;
}

function pushEvent(streamEl, from, to, msg, tone) {
  const list = streamEl._list;
  if (!list) return;
  const ts = formatTime();
  const row = h('div', { class: `aw-event aw-event-${tone || 'cyan'}` }, [
    h('span', { class: 'aw-event-ts' }, ts),
    h('span', { class: 'aw-event-from' }, from),
    h('span', { class: 'aw-event-arrow' }, '▶'),
    h('span', { class: 'aw-event-to' }, to),
    h('span', { class: 'aw-event-msg' }, msg),
  ]);
  list.appendChild(row);
  while (list.children.length > 12) list.removeChild(list.firstChild);
  list.scrollTop = list.scrollHeight;
}

function emitEventForNode(streamEl, node, isClick = false) {
  const tone = pickToneForNode(node);
  const msg = isClick ? `inspect ${node.kind.toLowerCase()}` : 'hover';
  pushEvent(streamEl, 'operator', node.title, msg, tone);
}

function pickToneForNode(node) {
  // Map node colors to event panel tones.
  const id = node.id;
  if (id === 'stage' || id === 'workspace') return 'cyan';
  if (id === 'bot' || id === 'voice') return 'pink';
  if (id === 'loop') return 'purple';
  if (id === 'llm') return 'gold';
  if (id === 'toolbelt' || id === 'terminal') return 'green';
  if (id === 'cli') return 'amber';
  if (id === 'os') return 'mint';
  return 'cyan';
}

function formatTime() {
  const d = new Date();
  const pad = n => String(n).padStart(2, '0');
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}

// ─────────────────────────────────────────────────────────────────────────────
// Util
// ─────────────────────────────────────────────────────────────────────────────

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, c => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}
