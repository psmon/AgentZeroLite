#!/usr/bin/env node
/**
 * Scans resource directories under the project and emits the
 * Home/harness-view/indexes/*.json manifests.
 *
 * Resource-Reference mode core principle: when .md files are added/edited,
 * just re-run this script — no web build, indexes go straight into the UI.
 *
 * Usage: node Home/harness-view/scripts/build-indexes.js
 */
const fs = require('fs');
const path = require('path');
const { execSync } = require('child_process');

// Home/harness-view/scripts/ → ../../../ = repo root (AgentZeroLite)
const ROOT = path.resolve(__dirname, '..', '..', '..');
const OUT  = path.resolve(__dirname, '..', 'indexes');

// Path mapping (canonical for this project):
//   Dashboard "Build Log"    → harness/docs       (release / build notes — vN.N.N.md)
//   Roles                    → harness/agents     (real harness root)
//   Skills                   → Docs/harness/template (in-repo snapshot of plugin skills)
//   Expert Knowledge         → harness/knowledge
//   Tech / Domain (TECH-DOC) → Docs                (full Docs tree)
//   Activity Log             → harness/logs/<subdir>/*.md  (subdir = dynamic tag)
//   Onboarding (design)      → Docs/design         (.pen + .md)
//   Workflow engine          → harness/engine
//   CLI-TIPS.md              → repo root           (absent in this repo)
const PATHS = {
  docs:      'harness/docs',
  agents:    'harness/agents',
  skills:    'Docs/harness/template', // in-repo snapshot of harness plugin skills
  knowledge: 'harness/knowledge',
  document:  'Docs',
  logs:      'harness/logs',
  design:    'Docs/design',
  engine:    'harness/engine',
  cliTips:   'CLI-TIPS.md',
  missions:        'harness/missions',
  missionRecords:  'harness/logs/mission-records',
};

// Skills are sourced from the in-repo snapshot at Docs/harness/template/.
// The user manually syncs this from the harness-kakashi plugin repo when needed.
// Reading from outside the repo is an anti-pattern — keep it strictly in-tree.
// SKILL.md bodies are still embedded inline at build time so the view doesn't
// need to fetch from the resource folder at runtime (matches reference design).
const SKILLS_DIR = path.join(ROOT, PATHS.skills);

function ensureDir(p) { fs.mkdirSync(p, { recursive: true }); }
function listMd(dir) {
  if (!fs.existsSync(dir)) return [];
  return fs.readdirSync(dir)
    .filter(f => f.endsWith('.md'))
    .sort(naturalCompare);
}
function naturalCompare(a, b) {
  return a.localeCompare(b, undefined, { numeric: true, sensitivity: 'base' });
}

function mdMeta(baseDir, relPath) {
  const abs = path.join(baseDir, relPath);
  const stat = fs.statSync(abs);
  return {
    path: relPath,
    size: stat.size,
    modified: stat.mtime.toISOString().slice(0, 10),
  };
}

/**
 * Pull title from the first H1 of an MD file.
 *  - "# v0.10.0 — New TC Mode"   →  "New TC Mode"
 *  - Strips a leading version prefix (vN.N.N — / – / -).
 *  - Returns null if no H1 is present.
 */
function extractH1(absPath) {
  try {
    const text = fs.readFileSync(absPath, 'utf8');
    const lines = text.split(/\r?\n/);
    for (const line of lines) {
      const m = line.match(/^#\s+(.+?)\s*$/);
      if (!m) continue;
      let t = m[1].trim();
      t = t.replace(/^v\d+\.\d+(?:\.\d+)?\s*[—–\-:]\s*/, '').trim();
      return t || null;
    }
  } catch (e) { /* ignore */ }
  return null;
}

function gitMeta(absPath) {
  try {
    const rel = path.relative(ROOT, absPath).replace(/\\/g, '/');
    const out = execSync(
      `git log -1 --format=%an%x09%ad --date=short -- "${rel}"`,
      { cwd: ROOT, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }
    ).trim();
    if (!out) return null;
    const [author, date] = out.split('\t');
    return { author, date };
  } catch (e) {
    return null;
  }
}

function gitMetaBatch(relDirPosix) {
  const map = Object.create(null);
  try {
    const out = execSync(
      `git log --name-only --format="|%H|%an|%ad" --date=short -- "${relDirPosix}"`,
      { cwd: ROOT, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }
    );
    let curAuthor = null, curDate = null;
    for (const line of out.split(/\r?\n/)) {
      if (line.startsWith('|')) {
        const parts = line.split('|');
        curAuthor = parts[2]; curDate = parts[3];
      } else if (line.trim()) {
        const base = path.posix.basename(line.trim());
        if (!(base in map)) map[base] = { author: curAuthor, date: curDate };
      }
    }
  } catch (e) { /* git unavailable — ignore */ }
  return map;
}

// Accepts one path or many; counts unique commits across the union so a
// single commit touching two paths isn't double-counted.
//
// Why this widened (2026-05-03): the dashboard's "Build-up Contributors
// (Docs commits)" was scoped to `harness/docs/` only, which is the
// release-note folder — touched once per release by exactly one author,
// so the donut was permanently "1 contributor / 11 commits". Real doc
// activity lives across the whole `harness/` and `Docs/` trees plus the
// dashboard itself (`Home/harness-view/`); broaden the union to those.
function gitContributors(relPaths, since /* optional, e.g. "14 days ago" */) {
  try {
    const paths = Array.isArray(relPaths) ? relPaths : [relPaths];
    const sinceArg = since ? ` --since="${since}"` : '';
    const pathArgs = paths.map(p => `"${p}"`).join(' ');
    // `git log` with multiple paths emits one line per matching commit;
    // %H+%an lets us dedupe across paths cleanly.
    const out = execSync(
      `git log --format=%H%x09%an${sinceArg} -- ${pathArgs}`,
      { cwd: ROOT, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }
    ).trim();
    if (!out) return [];
    const seen = new Set();
    const counts = Object.create(null);
    for (const line of out.split(/\r?\n/)) {
      if (!line) continue;
      const tab = line.indexOf('\t');
      if (tab < 0) continue;
      const sha = line.slice(0, tab);
      const name = line.slice(tab + 1);
      if (seen.has(sha)) continue;
      seen.add(sha);
      counts[name] = (counts[name] || 0) + 1;
    }
    const total = Object.values(counts).reduce((a, b) => a + b, 0);
    return Object.entries(counts)
      .map(([name, commits]) => ({ name, commits, percent: total ? +(commits / total * 100).toFixed(1) : 0 }))
      .sort((a, b) => b.commits - a.commits);
  } catch (e) { return []; }
}

function writeJson(name, data) {
  ensureDir(OUT);
  const file = path.join(OUT, `${name}.json`);
  fs.writeFileSync(file, JSON.stringify(data, null, 2), 'utf8');
  let count;
  if (Array.isArray(data))            count = data.length;
  else if (Array.isArray(data.items)) count = data.items.length;
  else if (Array.isArray(data.tree))  count = countTreeFiles(data.tree);
  else                                count = Object.keys(data).length;
  console.log(`✓ ${name}.json  (${count})`);
}
function countTreeFiles(tree) {
  let n = 0;
  for (const x of tree) {
    if (x.type === 'file') n++;
    else if (x.children) n += countTreeFiles(x.children);
  }
  return n;
}

// ─── Dashboard: Docs/*.md (flat, repo root MD files) ───
function buildDocs() {
  const dir = path.join(ROOT, PATHS.docs);
  const gitMap = gitMetaBatch(PATHS.docs);
  const files = listMd(dir).map(f => {
    const gm = gitMap[f] || gitMeta(path.join(dir, f));
    return {
      file: f,
      title: f.replace(/\.md$/, ''),
      heading: extractH1(path.join(dir, f)),
      ...mdMeta(dir, f),
      author: gm ? gm.author : null,
      committed: gm ? gm.date : null,
    };
  });
  files.sort((a, b) => b.title.localeCompare(a.title, undefined, { numeric: true, sensitivity: 'base' }));
  // Broader scope than `harness/docs/` alone — captures real doc activity
  // across harness (agents/knowledge/engine/missions/logs) + Docs/ (project
  // architecture & design) + the dashboard itself (Home/harness-view/).
  const CONTRIB_PATHS = ['harness', 'Docs', 'Home/harness-view'];
  const contributorsAll = gitContributors(CONTRIB_PATHS);
  const contributorsRecent = gitContributors(CONTRIB_PATHS, '14 days ago');
  writeJson('harness-docs', {
    base: PATHS.docs,
    sort: 'semver-desc',
    items: files,
    contributors: contributorsAll,
    contributorsAll,
    contributorsRecent,
    contributorsWindowDays: 14,
  });
}

// ─── Wide-harvest helper — collect *.md from any folder named `dirName`
//      anywhere under `rootAbs`. Returns ROOT-relative paths (posix) sorted
//      naturally. Used by Roles / Knowledge / Activity Log so that a single
//      `Docs/harness/template/` snapshot can populate every section. ───
function collectMdInDirsNamed(rootAbs, dirName) {
  if (!fs.existsSync(rootAbs)) return [];
  const out = [];
  const stack = [rootAbs];
  while (stack.length) {
    const cur = stack.pop();
    let entries;
    try { entries = fs.readdirSync(cur, { withFileTypes: true }); } catch { continue; }
    for (const ent of entries) {
      if (ent.name.startsWith('.')) continue;
      const ch = path.join(cur, ent.name);
      if (ent.isDirectory()) {
        stack.push(ch);
        if (ent.name === dirName) {
          for (const f of fs.readdirSync(ch)) {
            if (!f.endsWith('.md')) continue;
            out.push(path.relative(ROOT, path.join(ch, f)).replace(/\\/g, '/'));
          }
        }
      }
    }
  }
  return out.sort(naturalCompare);
}

// ─── Roles: harness/agents/*.md ───
function buildAgents() {
  const dir = path.join(ROOT, PATHS.agents);
  const items = listMd(dir).map(f => {
    const abs = path.join(dir, f);
    const stat = fs.statSync(abs);
    return {
      id: `${PATHS.agents}/${f}`,
      file: `${PATHS.agents}/${f}`,
      name: f,
      title: f.replace(/\.md$/, ''),
      heading: extractH1(abs),
      size: stat.size,
      modified: stat.mtime.toISOString().slice(0, 10),
    };
  });
  writeJson('harness-agents', { base: PATHS.agents, items });
}

// ─── Skills: in-repo snapshot at Docs/harness/template/ ───
// User manually syncs this folder from the harness plugin repo. SKILL.md body
// is still embedded inline so view rendering stays consistent with the original
// design (no per-skill fetch at runtime).
//
// SKILL_EXCLUDE: any skill listed here is filtered out. Reserved for personal
// or private skills that must never appear in the public-facing view.
const SKILL_EXCLUDE = new Set([
  // (currently empty — all template skills are intended for display)
]);
function buildSkills() {
  const dir = SKILLS_DIR;
  if (!fs.existsSync(dir)) {
    console.warn(`[buildSkills] skills snapshot dir not found: ${dir}`);
    writeJson('claude-skills', { base: PATHS.skills, items: [] });
    return;
  }
  const items = [];
  for (const sub of fs.readdirSync(dir)) {
    if (SKILL_EXCLUDE.has(sub)) continue;
    const skillDir = path.join(dir, sub);
    if (!fs.statSync(skillDir).isDirectory()) continue;
    const candidates = ['SKILL.md', 'skill.md', `${sub}.md`];
    const file = candidates.find(f => fs.existsSync(path.join(skillDir, f)));
    if (!file) continue;
    const absMd = path.join(skillDir, file);
    const stat = fs.statSync(absMd);
    let content = '';
    try { content = fs.readFileSync(absMd, 'utf8'); } catch (e) { content = `(failed to read SKILL.md: ${e.message})`; }
    items.push({
      id: sub,
      name: sub,
      file: `${PATHS.skills}/${sub}/${file}`,
      content,                                  // embedded body — view reads this directly
      size: stat.size,
      modified: stat.mtime.toISOString().slice(0, 10),
    });
  }
  items.sort((a, b) => a.name.localeCompare(b.name));
  writeJson('claude-skills', {
    base: PATHS.skills,
    note: 'SKILL.md bodies are embedded inline at build time (in-repo snapshot).',
    items,
  });
}

// ─── Expert Knowledge: harness/knowledge/**/*.md (recurse — subfolders included) ───
function buildKnowledge() {
  const root = path.join(ROOT, PATHS.knowledge);
  const items = [];
  function walk(absDir) {
    if (!fs.existsSync(absDir)) return;
    for (const ent of fs.readdirSync(absDir, { withFileTypes: true })) {
      if (ent.name.startsWith('.')) continue;
      const ch = path.join(absDir, ent.name);
      if (ent.isDirectory()) {
        walk(ch);
      } else if (ent.name.endsWith('.md')) {
        const rel = path.relative(ROOT, ch).replace(/\\/g, '/');
        const stat = fs.statSync(ch);
        items.push({
          id: rel,
          file: rel,
          name: ent.name,
          title: ent.name.replace(/\.md$/, ''),
          heading: extractH1(ch),
          size: stat.size,
          modified: stat.mtime.toISOString().slice(0, 10),
        });
      }
    }
  }
  walk(root);
  items.sort((a, b) => a.file.localeCompare(b.file));
  writeJson('harness-knowledge', { base: PATHS.knowledge, items });
}

// ─── Domain knowledge tree: Docs/** ───
function walkDir(abs, rel = '') {
  if (!fs.existsSync(abs)) return [];
  const entries = fs.readdirSync(abs, { withFileTypes: true });
  const nodes = [];
  for (const ent of entries) {
    if (ent.name.startsWith('.')) continue;
    const nextRel = path.posix.join(rel, ent.name);
    const nextAbs = path.join(abs, ent.name);
    if (ent.isDirectory()) {
      const children = walkDir(nextAbs, nextRel);
      if (children.length) nodes.push({ type: 'dir', name: ent.name, path: nextRel, children });
    } else if (ent.name.endsWith('.md')) {
      const stat = fs.statSync(nextAbs);
      nodes.push({ type: 'file', name: ent.name, path: nextRel, size: stat.size, modified: stat.mtime.toISOString().slice(0, 10) });
    }
  }
  return nodes;
}
function buildDocument() {
  const dir = path.join(ROOT, PATHS.document);
  const tree = walkDir(dir);
  writeJson('document-tree', { base: PATHS.document, tree });
}

// ─── Activity log: harness/logs/<subfolder>/*.md  (subfolder = dynamic tag).
//      Filename patterns supported:
//        YYYY-MM-DD-HHMM-title.md       (e.g. 2026-04-25-1620-aimode-research.md)
//        YYYY-MM-DD-HH-MM-title.md      (e.g. 2026-04-28-09-30-dotnet-test.md)
//      Categories are discovered dynamically — every immediate subfolder of
//      harness/logs becomes a tag. No hard-coded qa/ba/code/cto list anymore. ───
function buildLogs() {
  const root = path.join(ROOT, PATHS.logs);
  const cats = [];
  const items = [];
  if (fs.existsSync(root)) {
    for (const ent of fs.readdirSync(root, { withFileTypes: true })) {
      if (!ent.isDirectory() || ent.name.startsWith('.')) continue;
      cats.push(ent.name);
      const catDir = path.join(root, ent.name);
      for (const f of listMd(catDir)) {
        const rel = `${PATHS.logs}/${ent.name}/${f}`;
        const stat = fs.statSync(path.join(catDir, f));
        // Match both -HHMM- and -HH-MM- time formats.
        const m = f.match(/^(\d{4}-\d{2}-\d{2})-(\d{2})-?(\d{2})-(.+)\.md$/);
        const date = m ? m[1] : null;
        const time = m ? `${m[2]}:${m[3]}` : null;
        const title = m ? m[4].replace(/-/g, ' ') : f.replace(/\.md$/, '');
        items.push({
          category: ent.name,
          file: rel,
          date, time, title,
          size: stat.size,
        });
      }
    }
  }
  cats.sort();
  items.sort((a, b) => {
    const ka = (a.date || '') + (a.time || '00:00');
    const kb = (b.date || '') + (b.time || '00:00');
    return kb.localeCompare(ka);
  });
  const counts = cats.reduce((o, c) => (o[c] = items.filter(i => i.category === c).length, o), {});
  writeJson('harness-logs', {
    base: PATHS.logs,
    categories: cats,
    counts,
    items,
  });
}

// ─── Missions: TODO-style PRD ↔ result pairing ───
//      Sources:
//        harness/missions/M{NNNN}-*.md         — operator request (PRD)
//        harness/logs/mission-records/M{NNNN}-*.md — execution log (Result)
//      Pairing key: the M{NNNN} prefix in the filename. The view renders a
//      checklist where each row = one mission with status from the PRD's
//      frontmatter and a link to the result file when one exists.
//      Discovery is pattern-based — adding a new mission file requires only
//      a re-run of this builder, no view code change.
function parseSimpleFrontmatter(text) {
  const m = text.match(/^---\s*\r?\n([\s\S]*?)\r?\n---\s*(?:\r?\n|$)/);
  if (!m) return null;
  const meta = {};
  for (const line of m[1].split(/\r?\n/)) {
    // skip blank, comments, list-continuation, indented (array items)
    if (!line.trim() || line.trim().startsWith('#') || /^\s/.test(line)) continue;
    const kv = line.match(/^([a-zA-Z_][a-zA-Z0-9_-]*):\s*(.*)$/);
    if (!kv) continue;
    let val = kv[2].trim();
    // strip wrapping quotes
    if ((val.startsWith('"') && val.endsWith('"')) || (val.startsWith("'") && val.endsWith("'"))) {
      val = val.slice(1, -1);
    }
    // inline arrays "[a, b]" → pass through as raw — the view decides
    meta[kv[1]] = val;
  }
  return meta;
}

function buildMissions() {
  const missionsDir = path.join(ROOT, PATHS.missions);
  const recordsDir  = path.join(ROOT, PATHS.missionRecords);
  const designDir   = path.join(ROOT, PATHS.design);

  // Pre-scan completion logs by mission id (M{NNNN}).
  const records = new Map();
  if (fs.existsSync(recordsDir)) {
    for (const f of listMd(recordsDir)) {
      const idMatch = f.match(/^(M\d+)\b/);
      if (!idMatch) continue;
      const id  = idMatch[1];
      const abs = path.join(recordsDir, f);
      let fm = null;
      try { fm = parseSimpleFrontmatter(fs.readFileSync(abs, 'utf8')); } catch { /* ignore */ }
      const stat = fs.statSync(abs);
      records.set(id, {
        file: `${PATHS.missionRecords}/${f}`,
        status:    fm?.status   || null,
        started:   fm?.started  || null,
        finished:  fm?.finished || null,
        modified:  stat.mtime.toISOString().slice(0, 10),
      });
    }
  }

  // Pre-scan Pencil design files keyed by mission id (M{NNNN}).
  // Convention: Docs/design/M{NNNN}-{slug}.pen — mirrors the mission/log
  // naming. Picked up automatically; no view code change needed when a
  // mission ships a design.
  const designs = new Map();
  if (fs.existsSync(designDir)) {
    for (const f of fs.readdirSync(designDir)) {
      if (!f.endsWith('.pen')) continue;
      const idMatch = f.match(/^(M\d+)\b/);
      if (!idMatch) continue;
      designs.set(idMatch[1], `${PATHS.design}/${f}`);
    }
  }

  // Scan PRDs.
  const items = [];
  if (fs.existsSync(missionsDir)) {
    for (const f of listMd(missionsDir)) {
      // skip README and any non-mission md
      if (f.toLowerCase() === 'readme.md') continue;
      const idMatch = f.match(/^(M\d+)\b/);
      if (!idMatch) continue; // unknown layout, skip silently
      const id  = idMatch[1];
      const abs = path.join(missionsDir, f);
      let raw = '';
      try { raw = fs.readFileSync(abs, 'utf8'); } catch { /* ignore */ }
      const fm    = parseSimpleFrontmatter(raw) || {};
      const stat  = fs.statSync(abs);
      const rec   = records.get(id) || null;
      const design = designs.get(id) || null;

      items.push({
        id,
        file:     `${PATHS.missions}/${f}`,
        title:    fm.title    || f.replace(/\.md$/, ''),
        operator: fm.operator || null,
        language: fm.language || null,
        status:   fm.status   || (rec ? 'done' : 'inbox'),
        priority: fm.priority || null,
        created:  fm.created  || null,
        recordFile:     rec?.file     || null,
        recordStatus:   rec?.status   || null,
        recordStarted:  rec?.started  || null,
        recordFinished: rec?.finished || null,
        designFile:     design,
        modified: stat.mtime.toISOString().slice(0, 10),
        size:     stat.size,
      });
    }
  }

  // Newest mission ID first (M0099 above M0001).
  items.sort((a, b) => b.id.localeCompare(a.id, undefined, { numeric: true }));

  // Group counts — keep all known statuses present (zero is fine).
  const STATUSES = ['inbox', 'in_progress', 'done', 'partial', 'blocked', 'cancelled'];
  const counts = { all: items.length };
  for (const s of STATUSES) counts[s] = items.filter(i => i.status === s).length;

  writeJson('harness-missions', {
    base:        PATHS.missions,
    recordsBase: PATHS.missionRecords,
    statuses:    STATUSES,
    counts,
    items,
  });
}

// ─── Onboarding: Docs/design/*.md (+ *.pen file metadata) ───
function buildDesign() {
  const dir = path.join(ROOT, PATHS.design);
  const mdFiles = listMd(dir).map(f => ({
    type: 'md',
    file: f,
    title: f.replace(/\.md$/, ''),
    ...mdMeta(dir, f),
  }));
  const pens = fs.existsSync(dir)
    ? fs.readdirSync(dir).filter(f => f.endsWith('.pen')).map(f => ({ type: 'pen', file: f }))
    : [];
  writeJson('design-index', { base: PATHS.design, items: [...mdFiles, ...pens] });
}

// ─── Workflow engine: Docs/harness/engine — file list (graph defs live in data/) ───
function buildEngine() {
  const dir = path.join(ROOT, PATHS.engine);
  const files = listMd(dir).map(f => ({
    file: f,
    title: f.replace(/\.md$/, ''),
    ...mdMeta(dir, f),
  }));
  writeJson('harness-engine', { base: PATHS.engine, items: files });
}

// ─── Claude Tips: CLI-TIPS.md at repo root (absent in this repo by default) ───
function buildClaudeTips() {
  const tipsFile = path.join(ROOT, PATHS.cliTips);
  const exists = fs.existsSync(tipsFile);
  writeJson('claude-tips', { file: PATHS.cliTips, exists });
}

// ─── Resource mirror — copy site-external folders into Home/_resources/ so
//      GitHub Pages can serve them. The Pages workflow uploads only `Home/`,
//      which means anything outside (Docs/, harness/, …) returns 404 in
//      production. The view fetches everything via `../_resources/<rel>`,
//      which resolves to Home/_resources/<rel> in both local and Pages runs.
//
//      Only mirrors directories that the view actually reads. Cleans the
//      target dir each build so deletes propagate. ───
const MIRROR_DIRS = ['Docs', 'harness'];
function mirrorResources() {
  const dst = path.join(ROOT, 'Home', '_resources');
  if (fs.existsSync(dst)) {
    fs.rmSync(dst, { recursive: true, force: true });
  }
  fs.mkdirSync(dst, { recursive: true });
  let fileCount = 0;
  for (const dir of MIRROR_DIRS) {
    const src = path.join(ROOT, dir);
    if (!fs.existsSync(src)) continue;
    const target = path.join(dst, dir);
    fs.cpSync(src, target, {
      recursive: true,
      filter: (s) => !path.basename(s).startsWith('.'),  // skip dotfiles
    });
    fileCount += countFiles(target);
  }
  console.log(`✓ _resources mirror  (${MIRROR_DIRS.join(', ')} → Home/_resources, ${fileCount} files)`);
}
function countFiles(absDir) {
  let n = 0;
  if (!fs.existsSync(absDir)) return 0;
  for (const ent of fs.readdirSync(absDir, { withFileTypes: true })) {
    if (ent.name.startsWith('.')) continue;
    const ch = path.join(absDir, ent.name);
    if (ent.isDirectory()) n += countFiles(ch);
    else n++;
  }
  return n;
}

// ─── Meta snapshot — used by serve.js to decide whether a rebuild is needed ───
const SCANNED_PATHS = [
  PATHS.docs,
  PATHS.agents,
  PATHS.skills,
  PATHS.knowledge,
  PATHS.document,
  PATHS.logs,
  PATHS.design,
  PATHS.engine,
  PATHS.cliTips,
  PATHS.missions,
  PATHS.missionRecords,
];
function maxMtime(relPath) {
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
function writeMeta(durationMs, trigger) {
  const sourceMaxMs = SCANNED_PATHS.reduce((m, p) => Math.max(m, maxMtime(p)), 0);
  const meta = {
    builtAt: new Date().toISOString(),
    builtAtMs: Date.now(),
    durationMs,
    trigger: trigger || 'manual',
    scannedPaths: SCANNED_PATHS,
    sourceMaxMs,
  };
  fs.writeFileSync(path.join(OUT, '_meta.json'), JSON.stringify(meta, null, 2), 'utf8');

  // 1-line append-only build log (gitignored)
  const logPath = path.join(ROOT, 'Home', 'harness-view', '.meta-build.log');
  const line = `${meta.builtAt}\tdurationMs=${durationMs}\ttrigger=${trigger || 'manual'}\n`;
  try { fs.appendFileSync(logPath, line, 'utf8'); } catch (e) { /* ignore */ }
  console.log(`✓ _meta.json  (trigger=${trigger || 'manual'}, ${durationMs}ms)`);
}

const __t0 = Date.now();
buildDocs();
buildAgents();
buildSkills();
buildKnowledge();
buildDocument();
buildLogs();
buildMissions();
buildDesign();
buildEngine();
buildClaudeTips();
mirrorResources();
writeMeta(Date.now() - __t0, process.env.BUILD_TRIGGER || 'manual');
console.log('done.');
