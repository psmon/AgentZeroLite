/**
 * Shared "spec card" — renders the YAML frontmatter of a Role / Skill / etc.
 * as a structured card instead of raw YAML.
 *
 * Recognised keys (each gets dedicated formatting):
 *   - name           : id-style identifier (mono, primary)
 *   - persona        : human-readable display name (large, bold)
 *   - description    : long text (collapsible if > 280 chars)
 *   - triggers       : array of trigger phrases (chip list)
 *   - allowed-tools  : CSV string — built-ins / mcp__* parsed (chip list)
 * Anything else is rendered as a generic "label / value" row.
 *
 * Parsing supports:
 *   - "key: value"          (single line)
 *   - "key: |"              (multi-line block; subsequent indented lines)
 *   - "key:\n  - item\n..." (array of strings; quotes stripped)
 */
import { h } from '../utils/dom.js';

/* ─── YAML frontmatter parser (minimal, schema-tolerant) ─── */
export function parseFrontmatter(text) {
  if (!text.startsWith('---')) return { meta: null, body: text };
  const end = text.indexOf('\n---', 3);
  if (end === -1) return { meta: null, body: text };
  const yaml = text.slice(3, end).replace(/^\n/, '');
  const body = text.slice(end + 4).replace(/^\n/, '');

  const meta = {};
  const lines = yaml.split('\n');
  let i = 0;

  while (i < lines.length) {
    const line = lines[i];
    const m = line.match(/^([A-Za-z0-9_-]+):\s*(.*)$/);
    if (!m) { i++; continue; }
    const [, key, rest] = m;
    const restTrim = rest.trim();

    // multi-line block scalar (| or >)
    if (restTrim === '|' || restTrim === '|-' || restTrim === '>' || restTrim === '>-') {
      const block = [];
      i++;
      while (i < lines.length) {
        const l = lines[i];
        if (/^[A-Za-z0-9_-]+:/.test(l) && !l.startsWith(' ') && !l.startsWith('\t')) break;
        block.push(l.replace(/^ {2}/, ''));
        i++;
      }
      meta[key] = block.join('\n').trim();
      continue;
    }

    // possible array — empty value, then indented "- item" lines
    if (restTrim === '') {
      const items = [];
      let j = i + 1;
      while (j < lines.length) {
        const arr = lines[j].match(/^\s+-\s+(.*)$/);
        if (!arr) break;
        items.push(stripQuotes(arr[1].trim()));
        j++;
      }
      if (items.length) {
        meta[key] = items;
        i = j;
        continue;
      }
      meta[key] = '';
      i++;
      continue;
    }

    // simple scalar
    meta[key] = stripQuotes(restTrim);
    i++;
  }

  return { meta, body };
}

function stripQuotes(s) {
  if ((s.startsWith('"') && s.endsWith('"')) || (s.startsWith("'") && s.endsWith("'"))) {
    return s.slice(1, -1);
  }
  return s;
}

/* ─── allowed-tools CSV → chip metadata ─── */
function parseTools(csv) {
  if (!csv) return [];
  return csv.split(',').map(s => s.trim()).filter(Boolean).map(full => {
    const builtin = ['Read', 'Write', 'Edit', 'Glob', 'Grep', 'Bash', 'WebFetch', 'WebSearch', 'TodoWrite', 'Task'];
    if (builtin.includes(full)) return { full, short: full, kind: 'builtin' };
    if (full.startsWith('mcp__')) {
      const parts = full.split('__');
      const short = parts.length >= 3 ? `${parts[1]}.${parts.slice(2).join('.')}` : full;
      return { full, short, kind: 'mcp' };
    }
    return { full, short: full, kind: 'other' };
  });
}

/* ─── Public: build a card element from a parsed `meta` object ───
 *  opts.tag        — small badge label shown next to the title (e.g. "ROLE", "SKILLS 2.0")
 *  opts.fallbackName — used when meta.name is absent (typically the file basename)
 */
export function createSpecCard(meta, opts = {}) {
  if (!meta || typeof meta !== 'object') return null;
  if (!hasAnyKnownKey(meta)) return null;

  const card = h('div', { class: 'skill-spec' });

  // ── header: persona (or name) + small tag
  const displayName = meta.persona || meta.name || opts.fallbackName || '(unnamed)';
  const head = h('div', { class: 'spec-head' }, [
    h('span', { class: 'spec-name' }, displayName),
    opts.tag ? h('span', { class: 'spec-tag' }, opts.tag) : null,
  ]);
  card.appendChild(head);

  // ── if persona present and name is different, show the id underneath
  if (meta.persona && meta.name && meta.persona !== meta.name) {
    card.appendChild(h('div', { class: 'spec-row' }, [
      h('div', { class: 'spec-label' }, 'id'),
      h('div', { class: 'spec-value' }, h('code', {}, meta.name)),
    ]));
  } else if (!meta.persona && meta.name && opts.fallbackName && meta.name !== opts.fallbackName.replace(/\.md$/, '')) {
    // skill case where name acts as id only
    card.appendChild(h('div', { class: 'spec-row' }, [
      h('div', { class: 'spec-label' }, 'id'),
      h('div', { class: 'spec-value' }, h('code', {}, meta.name)),
    ]));
  }

  // ── description (collapsible if long)
  if (meta.description) {
    appendDescription(card, meta.description);
  }

  // ── triggers (array → chip list)
  const triggers = Array.isArray(meta.triggers) ? meta.triggers : null;
  if (triggers && triggers.length) {
    const chips = h('div', { class: 'spec-tools' });
    for (const t of triggers) {
      chips.appendChild(h('span', { class: 'spec-tool trigger', title: t }, `"${t}"`));
    }
    card.appendChild(h('div', { class: 'spec-row' }, [
      h('div', { class: 'spec-label' }, `Triggers · ${triggers.length}`),
      chips,
    ]));
  }

  // ── allowed-tools (Skill 2.0 spec)
  const tools = parseTools(meta['allowed-tools'] || meta.allowed_tools || '');
  if (tools.length) {
    const toolBox = h('div', { class: 'spec-tools' });
    for (const t of tools) {
      const cls = t.kind === 'mcp' ? 'spec-tool mcp' : (t.kind === 'builtin' ? 'spec-tool builtin' : 'spec-tool');
      toolBox.appendChild(h('span', { class: cls, title: t.full }, t.short));
    }
    card.appendChild(h('div', { class: 'spec-row' }, [
      h('div', { class: 'spec-label' }, `Tools · ${tools.length}`),
      toolBox,
    ]));
  }

  // ── any remaining unknown keys (model, version, etc.) — generic row
  const known = new Set(['name', 'persona', 'description', 'triggers', 'allowed-tools', 'allowed_tools']);
  for (const [k, v] of Object.entries(meta)) {
    if (known.has(k)) continue;
    if (!v) continue;
    if (Array.isArray(v)) {
      const chips = h('div', { class: 'spec-tools' });
      for (const it of v) chips.appendChild(h('span', { class: 'spec-tool', title: it }, String(it)));
      card.appendChild(h('div', { class: 'spec-row' }, [
        h('div', { class: 'spec-label' }, `${k} · ${v.length}`),
        chips,
      ]));
    } else {
      card.appendChild(h('div', { class: 'spec-row' }, [
        h('div', { class: 'spec-label' }, k),
        h('div', { class: 'spec-value' }, String(v)),
      ]));
    }
  }

  return card;
}

function appendDescription(card, desc) {
  const isLong = desc.length > 280;
  const value = h('div', { class: 'spec-value' }, isLong ? desc.slice(0, 280) + '…' : desc);
  const wrap = h('div', { style: { flex: '1' } }, [value]);
  const row = h('div', { class: 'spec-row' }, [
    h('div', { class: 'spec-label' }, 'Description'),
    wrap,
  ]);
  if (isLong) {
    let expanded = false;
    const toggle = h('button', { class: 'spec-toggle' }, '▾ Show all');
    toggle.onclick = () => {
      expanded = !expanded;
      value.textContent = expanded ? desc : desc.slice(0, 280) + '…';
      toggle.textContent = expanded ? '▴ Collapse' : '▾ Show all';
    };
    wrap.appendChild(toggle);
  }
  card.appendChild(row);
}

function hasAnyKnownKey(meta) {
  return ['name', 'persona', 'description', 'triggers', 'allowed-tools', 'allowed_tools']
    .some(k => meta[k] != null && (Array.isArray(meta[k]) ? meta[k].length : meta[k] !== ''));
}
