#!/usr/bin/env node
/**
 * AgentZero Dev Harness — local dev server (zero-dependency).
 *
 * Purpose:
 *  - Run the harness view locally for Playwright / browser testing.
 *  - Single-command launch: `node Home/harness-view/scripts/serve.js`.
 *
 * Behavior:
 *  1) Preflight — compares indexes/_meta.json against scanned source mtime.
 *     → If sources are newer, runs build-indexes.js automatically (trigger=serve).
 *     → If already current, skips and starts the server right away.
 *  2) Starts an HTTP server (127.0.0.1 only, no auth).
 *
 * Logs:
 *  - Home/harness-view/.meta-build.log gets a 1-line append per meta rebuild
 *    (gitignored).
 *
 * Open in browser:
 *  http://127.0.0.1:8765/Home/harness-view/
 *
 * Flags:
 *  node Home/harness-view/scripts/serve.js              # default 127.0.0.1:8765
 *  node Home/harness-view/scripts/serve.js 9000         # custom port
 *  node Home/harness-view/scripts/serve.js --no-build   # skip preflight rebuild
 */
const http = require('http');
const fs = require('fs');
const path = require('path');
const url = require('url');
const { spawnSync } = require('child_process');

const args = process.argv.slice(2);
const noBuild = args.includes('--no-build');
const portArg = args.find(a => /^\d+$/.test(a));
const PORT = Number(portArg) || 8765;
const HOST = '127.0.0.1';
// Home/harness-view/scripts/ → ../../../ = repo root (AgentZeroLite)
const ROOT = path.resolve(__dirname, '..', '..', '..');

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js':   'application/javascript; charset=utf-8',
  '.mjs':  'application/javascript; charset=utf-8',
  '.css':  'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.md':   'text/markdown; charset=utf-8',
  '.svg':  'image/svg+xml',
  '.png':  'image/png',
  '.jpg':  'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.gif':  'image/gif',
  '.ico':  'image/x-icon',
  '.pen':  'application/octet-stream',
  '.txt':  'text/plain; charset=utf-8',
};

function send(res, status, body, headers = {}) {
  res.writeHead(status, {
    'Cache-Control': 'no-store',
    'Access-Control-Allow-Origin': '*',
    ...headers,
  });
  res.end(body);
}

function safeJoin(root, reqPath) {
  const decoded = decodeURIComponent(reqPath.split('?')[0]);
  const joined = path.join(root, decoded);
  const resolved = path.resolve(joined);
  if (!resolved.startsWith(root)) return null;
  return resolved;
}

const server = http.createServer((req, res) => {
  try {
    const parsed = url.parse(req.url);
    let filePath = safeJoin(ROOT, parsed.pathname);
    if (!filePath) return send(res, 403, 'Forbidden');

    if (fs.existsSync(filePath) && fs.statSync(filePath).isDirectory()) {
      filePath = path.join(filePath, 'index.html');
    }
    if (!fs.existsSync(filePath)) return send(res, 404, `Not Found: ${parsed.pathname}`);

    const ext = path.extname(filePath).toLowerCase();
    const mime = MIME[ext] || 'application/octet-stream';
    const data = fs.readFileSync(filePath);
    send(res, 200, data, { 'Content-Type': mime });
  } catch (e) {
    send(res, 500, `Server Error: ${e.message}`);
  }
});

/* ────────────────────────────────────────────────
 *  Preflight — meta staleness check + auto rebuild
 * ──────────────────────────────────────────────── */

const SCANNED_PATHS_FOR_CHECK = [
  'Docs',
  'Docs/harness/agents',
  '.claude/skills',
  'Docs/harness/knowledge',
  'Docs/harness/logs',
  'Docs/design',
  'Docs/harness/engine',
  'CLI-TIPS.md',
];
function maxMtimeOf(relPath) {
  const abs = path.join(ROOT, relPath);
  if (!fs.existsSync(abs)) return 0;
  const st = fs.statSync(abs);
  if (st.isFile()) return st.mtimeMs;
  let maxMs = st.mtimeMs;
  const stack = [abs];
  while (stack.length) {
    const cur = stack.pop();
    for (const ent of fs.readdirSync(cur, { withFileTypes: true })) {
      if (ent.name.startsWith('.')) continue;
      const ch = path.join(cur, ent.name);
      const cst = fs.statSync(ch);
      if (cst.mtimeMs > maxMs) maxMs = cst.mtimeMs;
      if (ent.isDirectory()) stack.push(ch);
    }
  }
  return maxMs;
}

function preflight() {
  const metaPath = path.join(ROOT, 'Home', 'harness-view', 'indexes', '_meta.json');
  let meta = null;
  if (fs.existsSync(metaPath)) {
    try { meta = JSON.parse(fs.readFileSync(metaPath, 'utf8')); } catch { meta = null; }
  }

  const sourceMaxMs = SCANNED_PATHS_FOR_CHECK.reduce((m, p) => Math.max(m, maxMtimeOf(p)), 0);

  let decision, reason;
  if (noBuild) {
    decision = 'skipped'; reason = '--no-build flag';
  } else if (!meta) {
    decision = 'rebuild'; reason = 'no _meta.json (first build)';
  } else if (sourceMaxMs > meta.builtAtMs) {
    const diffSec = Math.round((sourceMaxMs - meta.builtAtMs) / 1000);
    decision = 'rebuild'; reason = `sources are ${diffSec}s newer (meta.builtAt=${meta.builtAt})`;
  } else {
    decision = 'up-to-date'; reason = `last built ${meta.builtAt}`;
  }

  console.log(`  [preflight] ${decision} — ${reason}`);

  if (decision === 'rebuild') {
    const t0 = Date.now();
    const result = spawnSync(process.execPath, [path.join(__dirname, 'build-indexes.js')], {
      cwd: ROOT,
      env: { ...process.env, BUILD_TRIGGER: 'serve' },
      stdio: 'inherit',
    });
    if (result.status !== 0) {
      console.error(`  [preflight] ⚠ build-indexes.js failed (exit=${result.status}) — starting server with stale manifests`);
    } else {
      console.log(`  [preflight] ✓ meta rebuild complete (${Date.now() - t0}ms)`);
    }
  }
}

preflight();

server.listen(PORT, HOST, () => {
  const base = `http://${HOST}:${PORT}`;
  console.log(`\n  AgentZero Dev Harness — local server (no auth)`);
  console.log(`  ────────────────────────────────────────`);
  console.log(`  ROOT       : ${ROOT}`);
  console.log(`  URL        : ${base}/Home/harness-view/`);
  console.log(`  Screenshots: tmp/playwright/ (gitignored)`);
  console.log(`  Build log  : Home/harness-view/.meta-build.log (gitignored)`);
  console.log(`\n  Stop: Ctrl+C\n`);
});
