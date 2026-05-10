/**
 * Actor World - cyberpunk 3D model of the AgentZero ActorStage runtime.
 *
 * Scope:
 *   /user/stage is the center. The view models the runtime surfaces around it:
 *   AgentBot, AgentLoop, LLM tool-calling, toolbelt dispatch, workspace/terminal
 *   actors, CLI IPC, voice input, and OS automation. This is not a harness-agent
 *   role graph.
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
    position: [1.2, -1.45, 3.45],
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

const VERT = /* glsl */`
  varying vec3 vNormal;
  varying vec3 vPosition;
  void main() {
    vNormal = normalize(normalMatrix * normal);
    vPosition = position;
    gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
  }
`;

const FRAG = /* glsl */`
  precision highp float;
  varying vec3 vNormal;
  varying vec3 vPosition;
  uniform vec3 uColor;
  uniform float uTime;
  uniform float uHover;

  float hash(vec3 p) { return fract(sin(dot(p, vec3(13.17, 41.91, 87.23))) * 43123.71); }
  float noise(vec3 p) {
    vec3 i = floor(p);
    vec3 f = fract(p);
    f = f*f*(3.0-2.0*f);
    return mix(
      mix(mix(hash(i), hash(i+vec3(1,0,0)), f.x),
          mix(hash(i+vec3(0,1,0)), hash(i+vec3(1,1,0)), f.x), f.y),
      mix(mix(hash(i+vec3(0,0,1)), hash(i+vec3(1,0,1)), f.x),
          mix(hash(i+vec3(0,1,1)), hash(i+vec3(1,1,1)), f.x), f.y),
      f.z);
  }

  void main() {
    float scan = sin((vPosition.y + uTime * 0.65) * 16.0) * 0.5 + 0.5;
    float n = noise(vPosition * 2.3 + vec3(0.0, uTime * 0.08, 0.0));
    float fres = pow(1.0 - abs(dot(normalize(vNormal), vec3(0.0, 0.0, 1.0))), 2.0);
    vec3 base = uColor * (0.45 + n * 0.35);
    vec3 glow = mix(uColor, vec3(1.0), 0.36) * (fres * 1.2 + scan * 0.10 + uHover * 0.35);
    gl_FragColor = vec4(base + glow, 1.0);
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
  const help = h('div', { class: 'aw-help' }, 'orbit stage - click node for function card - drag node to inspect routes');

  mount(viewEl, h('div', { class: 'aw-root aw-cyber' }, [stage, tooltip, hud, toolrail, help]));

  const ac = new AbortController();
  window.addEventListener('hashchange', () => ac.abort(), { once: true, signal: ac.signal });

  requestAnimationFrame(() => {
    if (!ac.signal.aborted) runScene({ THREE, OrbitControls, stage, tooltip, ac });
  });
}

function runScene({ THREE, OrbitControls, stage, tooltip, ac }) {
  const width = stage.clientWidth || stage.parentElement.clientWidth || 1000;
  const height = stage.clientHeight || stage.parentElement.clientHeight || 700;

  const scene = new THREE.Scene();
  scene.background = new THREE.Color(0x05070D);
  scene.fog = new THREE.FogExp2(0x05070D, 0.036);

  const camera = new THREE.PerspectiveCamera(46, width / height, 0.1, 120);
  camera.position.set(0, 6.3, 10.6);

  const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: false });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
  renderer.setSize(width, height);
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  renderer.toneMappingExposure = 1.02;
  stage.appendChild(renderer.domElement);

  const controls = new OrbitControls(camera, renderer.domElement);
  controls.enableDamping = true;
  controls.dampingFactor = 0.08;
  controls.minDistance = 6;
  controls.maxDistance = 22;
  controls.target.set(0, -0.25, 0.7);

  scene.add(new THREE.HemisphereLight(0x7CE8FF, 0x130821, 1.5));
  const key = new THREE.DirectionalLight(0xB9FFEA, 2.0);
  key.position.set(-4, 8, 5);
  scene.add(key);
  const coreLight = new THREE.PointLight(0x41E6FF, 24, 16, 1.8);
  coreLight.position.set(0, 1.8, 0);
  scene.add(coreLight);

  addCyberFloor(THREE, scene);
  addStageFrame(THREE, scene);

  const nodeById = new Map();
  const pickables = [];
  NODES.forEach(node => {
    const group = createNode(THREE, node);
    scene.add(group);
    nodeById.set(node.id, group);
    pickables.push(group.userData.pickable);
  });

  const edgeObjects = EDGES.map(([from, to, label], i) => {
    const edge = createEdge(THREE, nodeById.get(from), nodeById.get(to), label, i);
    scene.add(edge.group);
    return edge;
  });

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
      if (hover) hover.userData.uniforms.uHover.value = 0;
      hover = node;
      if (hover) hover.userData.uniforms.uHover.value = 1;
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
          if (active) openFunctionCard(tooltip, active.userData.node, ac);
          else closeTooltip(tooltip);
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

  const clock = new THREE.Clock();
  function tick() {
    if (ac.signal.aborted) {
      scene.traverse(o => {
        if (o.geometry) o.geometry.dispose();
        const mats = Array.isArray(o.material) ? o.material : o.material ? [o.material] : [];
        mats.forEach(m => m.dispose());
      });
      renderer.dispose();
      renderer.domElement.remove();
      return;
    }

    const t = clock.getElapsedTime();
    NODES.forEach(node => {
      const group = nodeById.get(node.id);
      group.userData.uniforms.uTime.value = t;
      group.rotation.y += group.userData.spin;
      const pulse = 1 + Math.sin(t * 2.0 + group.userData.phase) * 0.012;
      group.scale.setScalar(pulse);
    });
    edgeObjects.forEach(edge => animateEdge(edge, t));

    controls.update();
    renderer.render(scene, camera);

    if (active) projectTooltip(tooltip, active, camera, stage);
    requestAnimationFrame(tick);
  }
  requestAnimationFrame(tick);
}

function createNode(THREE, node) {
  const group = new THREE.Group();
  group.position.fromArray(node.position);
  group.userData.node = node;
  group.userData.phase = Math.random() * Math.PI * 2;
  group.userData.spin = node.id === 'stage' ? 0.006 : 0.0025;

  const color = new THREE.Color(node.color);
  const uniforms = {
    uColor: { value: color },
    uTime: { value: 0 },
    uHover: { value: 0 },
  };
  group.userData.uniforms = uniforms;

  const material = new THREE.ShaderMaterial({ vertexShader: VERT, fragmentShader: FRAG, uniforms });
  const body = createNodeBody(THREE, node, material);
  body.userData.nodeGroup = group;
  group.userData.pickable = body;
  group.add(body);

  const shell = new THREE.Mesh(
    new THREE.IcosahedronGeometry(0.86 * node.scale, 1),
    new THREE.MeshBasicMaterial({
      color,
      transparent: true,
      opacity: 0.16,
      wireframe: true,
      depthWrite: false,
      blending: THREE.AdditiveBlending,
    })
  );
  group.add(shell);

  const ring = new THREE.Mesh(
    new THREE.TorusGeometry(1.05 * node.scale, 0.012, 8, 88),
    new THREE.MeshBasicMaterial({
      color,
      transparent: true,
      opacity: 0.68,
      depthWrite: false,
      blending: THREE.AdditiveBlending,
    })
  );
  ring.rotation.x = Math.PI / 2;
  group.add(ring);

  const plate = new THREE.Mesh(
    new THREE.CylinderGeometry(0.58 * node.scale, 0.76 * node.scale, 0.16, 36),
    new THREE.MeshStandardMaterial({
      color: 0x09111A,
      emissive: color.clone().multiplyScalar(0.16),
      roughness: 0.5,
      metalness: 0.65,
    })
  );
  plate.position.y = -0.86 * node.scale;
  group.add(plate);

  group.add(new THREE.PointLight(color, node.id === 'stage' ? 9 : 4.2, 5.5, 2));
  return group;
}

function createNodeBody(THREE, node, material) {
  if (node.shape === 'bus') {
    return new THREE.Mesh(new THREE.BoxGeometry(1.45 * node.scale, 0.72 * node.scale, 0.72 * node.scale, 8, 4, 4), material);
  }
  if (node.shape === 'terminal') {
    return new THREE.Mesh(new THREE.BoxGeometry(1.22 * node.scale, 0.78 * node.scale, 0.92 * node.scale, 8, 4, 4), material);
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
  return new THREE.Mesh(new THREE.SphereGeometry(0.78 * node.scale, 56, 56), material);
}

function createEdge(THREE, from, to, label, index) {
  const material = new THREE.MeshBasicMaterial({
    color: index % 3 === 0 ? 0x41E6FF : index % 3 === 1 ? 0xF36BFF : 0xFFE066,
    transparent: true,
    opacity: 0.34,
    depthWrite: false,
    blending: THREE.AdditiveBlending,
  });
  const arrowMaterial = new THREE.MeshBasicMaterial({
    color: 0xF7FFE8,
    transparent: true,
    opacity: 0.88,
    depthWrite: false,
    blending: THREE.AdditiveBlending,
  });
  const tube = new THREE.Mesh(new THREE.BufferGeometry(), material);
  const arrow = new THREE.Mesh(new THREE.ConeGeometry(0.11, 0.34, 18), arrowMaterial);
  const pulse = new THREE.Mesh(new THREE.SphereGeometry(0.07, 16, 16), arrowMaterial.clone());
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
  mid.y += 0.58 + a.distanceTo(b) * 0.08;
  const curve = new THREE.CatmullRomCurve3([a, mid, b]);
  edge.curve = curve;
  edge.tube.geometry.dispose();
  edge.tube.geometry = new THREE.TubeGeometry(curve, 36, 0.026, 8, false);

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
  edge.pulse.material.opacity = 0.35 + Math.sin(p * Math.PI) * 0.55;
}

function addCyberFloor(THREE, scene) {
  const grid = new THREE.GridHelper(13, 26, 0x41E6FF, 0x18334A);
  grid.position.y = -1.78;
  grid.material.transparent = true;
  grid.material.opacity = 0.23;
  grid.material.depthWrite = false;
  scene.add(grid);

  const deck = new THREE.Mesh(
    new THREE.PlaneGeometry(13.5, 8.2, 1, 1),
    new THREE.MeshBasicMaterial({
      color: 0x07111A,
      transparent: true,
      opacity: 0.58,
      side: THREE.DoubleSide,
    })
  );
  deck.rotation.x = -Math.PI / 2;
  deck.position.y = -1.82;
  scene.add(deck);
}

function addStageFrame(THREE, scene) {
  const mat = new THREE.MeshBasicMaterial({
    color: 0x41E6FF,
    transparent: true,
    opacity: 0.28,
    depthWrite: false,
    blending: THREE.AdditiveBlending,
  });
  const ring = new THREE.Mesh(new THREE.TorusGeometry(4.55, 0.018, 8, 160), mat);
  ring.rotation.x = Math.PI / 2;
  ring.position.y = -0.18;
  scene.add(ring);

  const spineMat = mat.clone();
  spineMat.opacity = 0.18;
  const spine = new THREE.Mesh(new THREE.CylinderGeometry(0.16, 0.85, 3.4, 44, 1, true), spineMat);
  spine.position.y = 0.08;
  scene.add(spine);
}

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

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, c => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}
