/**
 * Dashboard — three pre-built sections + one auto-aggregated section.
 *  1. Recent Updates           — narrative + best prompts (data/news.json — static)
 *  2. PDSA Learning            — Plan/Do/Solved/Remaining/Learned (data/pdsa-insight.json — static)
 *  3. Build Log                — harness/docs/*.md cards, semver-desc (indexes/harness-docs.json)
 *  4. Build-up Contributors    — donut + stat cards (indexes/harness-docs.json.contributors — git log)
 *
 * Detail screen (#dashboard/<file>) renders the clicked .md as a read-only viewer.
 */
import { h, mount, humanize } from '../utils/dom.js';
import { loadIndex, loadMd, loadData } from '../utils/loader.js';
import { createMdViewer } from '../components/md-viewer.js';
import { renderTopBar, emptyState, loadingState } from './_common.js';
import { ICONS } from '../config/menu.js';

export async function render(ctx) {
  const { viewEl, topbarEl, menu, params } = ctx;

  const index = await loadIndex('harness-docs');
  if (params) return renderDetail(ctx, index, params);

  renderTopBar(topbarEl, {
    title: 'Dashboard',
    subtitle: `${index ? index.base : 'harness/docs'} — resource-reference mode (read-only)`,
    badge: { kind: 'readonly', text: 'Reference' },
  });

  mount(viewEl, loadingState());

  const [news, pdsa] = await Promise.all([
    loadData('news'),
    loadData('pdsa-insight'),
  ]);

  const items = (index?.items || []).slice().sort((a, b) =>
    a.title.localeCompare(b.title, undefined, { numeric: true, sensitivity: 'base' }) * -1);
  const contribAll    = index?.contributorsAll    || index?.contributors || [];
  const contribRecent = index?.contributorsRecent || [];
  const windowDays    = index?.contributorsWindowDays || 14;
  const commitStats   = index?.commitStats || null;
  const topFiles      = index?.topChangedFiles || [];

  const sections = h('div', { class: 'dash-sections' });
  sections.appendChild(renderNewsSection(news));
  sections.appendChild(renderPdsaSection(pdsa));
  sections.appendChild(renderBuildLogSection(menu, items, index));
  sections.appendChild(renderContribSection({
    all: contribAll, recent: contribRecent, windowDays, commitStats, topFiles,
  }, items));
  mount(viewEl, sections);
}

/* ────────────────────────────────────────────────
 *  1) Recent Updates
 * ──────────────────────────────────────────────── */
function renderNewsSection(news) {
  const sec = h('section', { class: 'dash-sec news-sec' });
  const head = h('div', { class: 'dash-sec-head' }, [
    h('div', { class: 'dash-sec-title' }, [
      h('span', { class: 'dash-sec-icon tone-blue', html: ICONS.newspaper }),
      h('span', {}, 'Recent Updates'),
    ]),
    h('div', { class: 'dash-sec-sub' },
      news ? `As of ${news.updatedAt} · summary of recent shipped features` : 'Highlights from recent releases'),
  ]);
  sec.appendChild(head);

  if (!news) {
    sec.appendChild(emptyState('data/news.json is missing.'));
    return sec;
  }

  const body = h('div', { class: 'news-body' });

  // Left: story
  const story = h('div', { class: 'news-story card-plain' });
  story.appendChild(h('div', { class: 'news-lead' }, news.headline));
  story.appendChild(h('p', { class: 'news-narrative' }, news.narrative));
  if (Array.isArray(news.highlights) && news.highlights.length) {
    const hi = h('div', { class: 'news-highlights' });
    for (const it of news.highlights) {
      const cls = `news-hi tone-${it.tone || 'blue'}`;
      hi.appendChild(h('div', { class: cls }, [
        h('div', { class: 'hi-label' }, it.label),
        h('div', { class: 'hi-text' }, it.text),
      ]));
    }
    story.appendChild(hi);
  }
  body.appendChild(story);

  // Right: best prompts
  const prompts = h('div', { class: 'news-prompts card-plain' });
  prompts.appendChild(h('div', { class: 'news-prompts-head' }, [
    h('span', { class: 'dash-sec-icon tone-amber', html: ICONS.sparkles }),
    h('span', {}, 'Best Prompt Examples'),
  ]));
  prompts.appendChild(h('div', { class: 'news-prompts-desc' }, 'Try these to get a feel for the new features'));
  const list = h('div', { class: 'news-prompts-list' });
  for (const p of news.prompts || []) {
    const chip = h('div', { class: 'prompt-chip' });
    chip.appendChild(h('div', { class: `prompt-tag tone-${p.tone || 'blue'}` }, p.tag));
    chip.appendChild(h('div', { class: 'prompt-text' }, `"${p.text}"`));
    list.appendChild(chip);
  }
  prompts.appendChild(list);
  body.appendChild(prompts);

  sec.appendChild(body);
  return sec;
}

/* ────────────────────────────────────────────────
 *  2) PDSA Learning
 * ──────────────────────────────────────────────── */
function renderPdsaSection(pdsa) {
  const sec = h('section', { class: 'dash-sec pdsa-sec' });
  const head = h('div', { class: 'dash-sec-head' }, [
    h('div', { class: 'dash-sec-title' }, [
      h('span', { class: 'dash-sec-icon tone-purple', html: ICONS.bulb }),
      h('span', {}, 'PDSA Learning'),
      h('span', { class: 'dash-sec-sub-inline' }, '— insights distilled from recent activity'),
    ]),
    h('div', { class: 'dash-sec-sub' },
      pdsa ? `${pdsa.windowDays || 14}-day window · ${pdsa.sources?.length || 0} sources · analyzed ${pdsa.analyzedAt}`
           : 'Last 14 days · top 5 entries'),
  ]);
  sec.appendChild(head);

  if (!pdsa) {
    sec.appendChild(emptyState('data/pdsa-insight.json is missing.'));
    return sec;
  }

  // Source pills
  if (Array.isArray(pdsa.sources) && pdsa.sources.length) {
    const srcBar = h('div', { class: 'pdsa-src-bar' });
    for (const s of pdsa.sources) {
      srcBar.appendChild(h('div', { class: 'pdsa-src-pill', title: s.file || '' }, [
        h('span', { class: 'pdsa-src-date' }, (s.date || '').slice(5)),   // MM-DD
        h('span', { class: 'pdsa-src-title' }, s.title || (s.file || '').split('/').pop()),
      ]));
    }
    sec.appendChild(srcBar);
  }

  // 3-state cards
  const row = h('div', { class: 'pdsa-row' });
  row.appendChild(quadrantCard('P + D', 'tone-blue',  'Tried',     pdsa.tried));
  row.appendChild(quadrantCard('DONE',  'tone-green', 'Solved',    pdsa.solved));
  row.appendChild(quadrantCard('TODO',  'tone-amber', 'Remaining', pdsa.remaining));
  sec.appendChild(row);

  // Learned hero
  const learned = pdsa.learned || {};
  const hero = h('div', { class: 'pdsa-hero' }, [
    h('div', { class: 'pdsa-hero-icon-wrap' }, [
      h('span', { class: 'pdsa-hero-icon', html: ICONS.bulb }),
    ]),
    h('div', { class: 'pdsa-hero-body' }, [
      h('div', { class: 'pdsa-hero-head' }, [
        h('span', { class: 'pdsa-hero-badge' }, 'STUDY + ACT'),
        h('span', { class: 'pdsa-hero-title' }, 'Learned · core insight'),
      ]),
      h('div', { class: 'pdsa-hero-lead' }, learned.lead || '—'),
      learned.body ? h('div', { class: 'pdsa-hero-desc' }, learned.body) : null,
    ]),
  ]);
  sec.appendChild(hero);
  return sec;
}

function quadrantCard(tag, toneClass, title, items) {
  const list = Array.isArray(items) ? items : [];
  return h('div', { class: 'pdsa-card card-plain' }, [
    h('div', { class: 'pdsa-card-head' }, [
      h('span', { class: `pdsa-card-badge ${toneClass}` }, tag),
      h('span', { class: 'pdsa-card-title' }, title),
      h('span', { class: 'pdsa-card-count' }, String(list.length)),
    ]),
    h('ul', { class: 'pdsa-card-list' }, list.map(item => h('li', {}, item))),
  ]);
}

/* ────────────────────────────────────────────────
 *  3) Build Log — harness/docs/*.md (semver-desc release cards)
 * ──────────────────────────────────────────────── */
function renderBuildLogSection(menu, items, index) {
  const sec = h('section', { class: 'dash-sec release-sec' });
  const head = h('div', { class: 'dash-sec-head' }, [
    h('div', { class: 'dash-sec-title' }, [
      h('span', { class: 'dash-sec-icon tone-amber', html: ICONS.tag }),
      h('span', {}, 'Build Log'),
    ]),
    h('div', { class: 'dash-sec-sub' }, `${items.length} entries · ${index?.base || 'harness/docs'} · newest first`),
  ]);
  sec.appendChild(head);

  if (!items.length) {
    sec.appendChild(emptyState('No build-log entries yet at harness/docs/.'));
    return sec;
  }

  const grid = h('div', { class: 'release-grid' });
  for (const it of items) {
    const kind = versionKind(it.title);
    const card = h('div', {
      class: 'release-card',
      dataset: { search: `${it.title} ${it.heading || ''} ${it.author || ''}`.toLowerCase() },
      onclick: () => { location.hash = `#${menu.id}/${encodeURIComponent(it.file)}`; },
    }, [
      h('div', { class: 'rc-head' }, [
        h('span', { class: `rc-badge tone-${kind.tone}` }, it.title),
        h('span', { class: 'rc-date' }, it.committed || it.modified || ''),
      ]),
      h('div', { class: 'rc-title', title: it.heading || humanize(it.title) }, it.heading || humanize(it.title)),
      h('div', { class: 'rc-foot' }, [
        h('span', { class: 'rc-avatar', style: { background: '#9CA3AF' } },
          it.author ? it.author.slice(0, 1).toUpperCase() : '?'),
        h('span', { class: 'rc-author' }, it.author || 'unknown'),
      ]),
    ]);
    grid.appendChild(card);
  }
  sec.appendChild(grid);
  return sec;
}

/** vN.0.0 → major (purple), vN.N.0 → minor (blue), vN.N.N → patch (green) */
function versionKind(title) {
  const m = (title || '').replace(/^v/, '').split('.');
  if (m.length >= 3 && m[1] === '0' && m[2] === '0') return { kind: 'major', tone: 'purple' };
  if (m.length >= 3 && m[2] === '0')                  return { kind: 'minor', tone: 'blue'   };
  if (m.length >= 3)                                  return { kind: 'patch', tone: 'green'  };
  return { kind: 'doc', tone: 'amber' };  // README, non-semver
}

/* ────────────────────────────────────────────────
 *  4) Build-up Contributors
 * ──────────────────────────────────────────────── */
function renderContribSection({ all, recent, windowDays, commitStats, topFiles }, items) {
  const sec = h('section', { class: 'dash-sec contrib-sec' });
  let mode = 'recent';

  const toggle = h('div', { class: 'toggle contrib-toggle' });
  const btnRecent = h('button', { class: 'active', onclick: () => setMode('recent') }, `Last ${windowDays} days`);
  const btnAll    = h('button', { onclick: () => setMode('all') }, 'All time');
  toggle.appendChild(btnRecent);
  toggle.appendChild(btnAll);

  const head = h('div', { class: 'dash-sec-head' }, [
    h('div', { class: 'dash-sec-title' }, [
      h('span', { class: 'dash-sec-icon tone-green', html: ICONS.pieChart }),
      h('span', {}, 'Build-up Contributors (Docs commits)'),
    ]),
    toggle,
  ]);
  sec.appendChild(head);

  const body = h('div', { class: 'contrib-body' });
  const donutCard = h('div', { class: 'contrib-donut card-plain' });
  body.appendChild(donutCard);

  // Right: stat cards + (optional) top-changed-files panel
  const stats = h('div', { class: 'contrib-stats' });
  const latest = items[0];
  // Velocity card — total commits across the broadened scope. Big number
  // so single-contributor projects see motion (the old "1 contributor"
  // hero stat was technically right but conveyed no growth).
  const totalCommitsCard = h('div', { class: 'stat-card card-plain' });
  stats.appendChild(totalCommitsCard);
  stats.appendChild(statCard(String(items.length), 'Total docs', 'tone-blue'));
  stats.appendChild(statCard(latest?.title || '—', `Latest doc · ${latest?.committed || latest?.modified || ''}`, 'tone-purple'));
  // 7d / 30d activity tiles — only when build supplied them.
  if (commitStats) {
    stats.appendChild(statCard(String(commitStats.last7d ?? 0),  'Commits · last 7 days',  'tone-amber'));
    stats.appendChild(statCard(String(commitStats.last30d ?? 0), 'Commits · last 30 days', 'tone-amber'));
  }
  // Subordinate "contributors count" card — kept so the original metric
  // is still visible, just no longer the hero.
  const contribCountCard = h('div', { class: 'stat-card card-plain' });
  stats.appendChild(contribCountCard);
  body.appendChild(stats);

  sec.appendChild(body);

  // Top-changed-files list — surfaces "where activity actually lives"
  // (single-author projects can't differentiate via the donut). Hidden
  // when build supplied no list or 0 entries.
  if (topFiles && topFiles.length) {
    sec.appendChild(renderTopFiles(topFiles));
  }

  function setMode(next) {
    mode = next;
    btnRecent.classList.toggle('active', mode === 'recent');
    btnAll.classList.toggle('active',    mode === 'all');
    renderDonut();
  }

  function renderDonut() {
    const contributors = mode === 'recent' ? recent : all;
    const totalCommits = contributors.reduce((s, c) => s + c.commits, 0);

    const subLabel = mode === 'recent'
      ? `Last ${windowDays} days · ${totalCommits} commits · ${contributors.length} contributors`
      : `All time · ${totalCommits} commits · ${contributors.length} contributors`;

    const inner = h('div', { class: 'contrib-donut-inner' });
    if (!contributors.length) {
      inner.appendChild(emptyState(mode === 'recent'
        ? `No Docs commits in the last ${windowDays} days.`
        : 'No contributor data available.'));
    } else {
      const colored = contributors.map((c, i) => ({ ...c, color: contribColor(i) }));
      inner.appendChild(buildDonut(colored, totalCommits));
      const legend = h('div', { class: 'contrib-legend' });
      colored.forEach((c) => {
        legend.appendChild(h('div', { class: 'contrib-leg' }, [
          h('span', { class: 'leg-dot', style: { background: c.color } }),
          h('div', { class: 'leg-info' }, [
            h('div', { class: 'leg-name' }, c.name),
            h('div', { class: 'leg-det' }, `${c.commits} commits · ${c.percent}%`),
          ]),
        ]));
      });
      inner.appendChild(legend);
    }

    mount(donutCard,
      h('div', { class: 'contrib-donut-caption' }, subLabel),
      inner,
    );

    // Velocity hero — total commits in the active mode (recent vs all).
    // commitStats.totalAllTime is the union-deduped count from the build;
    // when only contributors data is available we approximate via sum.
    const heroCommits = mode === 'recent'
      ? totalCommits
      : (commitStats?.totalAllTime ?? totalCommits);
    const heroLabel = mode === 'recent'
      ? `Commits · last ${windowDays} days`
      : 'Commits · all time (Docs scope)';
    mount(totalCommitsCard,
      h('div', { class: 'stat-val tone-green' }, String(heroCommits)),
      h('div', { class: 'stat-label' }, heroLabel),
    );

    const countLabel = mode === 'recent' ? `Contributors (last ${windowDays} days)` : 'Total contributors';
    mount(contribCountCard,
      h('div', { class: 'stat-val tone-blue' }, String(contributors.length)),
      h('div', { class: 'stat-label' }, countLabel),
    );
  }

  renderDonut();
  return sec;
}

function renderTopFiles(topFiles) {
  const wrap = h('div', { class: 'contrib-topfiles card-plain' });
  wrap.appendChild(h('div', { class: 'topfiles-head' }, [
    h('span', { class: 'topfiles-title' }, 'Top changed files · last 30 days'),
    h('span', { class: 'topfiles-sub' }, 'union of harness/, Docs/, Home/harness-view/'),
  ]));
  const list = h('ul', { class: 'topfiles-list' });
  const max = Math.max(...topFiles.map(f => f.commits), 1);
  topFiles.forEach((f) => {
    const ratio = (f.commits / max) * 100;
    const row = h('li', { class: 'topfile-row' }, [
      h('span', { class: 'topfile-path', title: f.path }, f.path),
      h('span', { class: 'topfile-bar' }, [
        h('span', { class: 'topfile-bar-fill', style: { width: ratio + '%' } }),
      ]),
      h('span', { class: 'topfile-count' }, `${f.commits}`),
    ]);
    list.appendChild(row);
  });
  wrap.appendChild(list);
  return wrap;
}

function statCard(value, label, toneClass) {
  return h('div', { class: 'stat-card card-plain' }, [
    h('div', { class: `stat-val ${toneClass}` }, value),
    h('div', { class: 'stat-label' }, label),
  ]);
}

const CONTRIB_PALETTE = ['#2563EB', '#7C3AED', '#15803D', '#B45309', '#DB2777', '#0891B2', '#CA8A04', '#DC2626'];
function contribColor(idx) { return CONTRIB_PALETTE[idx % CONTRIB_PALETTE.length]; }

function buildDonut(contributors, total) {
  const size = 200, stroke = 28, radius = (size - stroke) / 2 - 18, cx = size / 2, cy = size / 2;
  const circ = 2 * Math.PI * radius;
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', `0 0 ${size} ${size}`);
  svg.setAttribute('class', 'donut');
  svg.setAttribute('width', size);
  svg.setAttribute('height', size);

  const bg = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
  bg.setAttribute('cx', cx); bg.setAttribute('cy', cy); bg.setAttribute('r', radius);
  bg.setAttribute('fill', 'none'); bg.setAttribute('stroke', '#F3F4F6'); bg.setAttribute('stroke-width', stroke);
  svg.appendChild(bg);

  let offset = 0;
  contributors.forEach((c, i) => {
    const ratio = total ? (c.commits / total) : 0;
    const segLen = ratio * circ;
    const color = c.color || contribColor(i);

    const seg = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
    seg.setAttribute('cx', cx); seg.setAttribute('cy', cy); seg.setAttribute('r', radius);
    seg.setAttribute('fill', 'none');
    seg.setAttribute('stroke', color);
    seg.setAttribute('stroke-width', stroke);
    seg.setAttribute('stroke-dasharray', `${segLen} ${circ - segLen}`);
    seg.setAttribute('stroke-dashoffset', `-${offset}`);
    seg.setAttribute('transform', `rotate(-90 ${cx} ${cy})`);
    svg.appendChild(seg);

    if (ratio >= 0.05) {
      const midRatio = (offset + segLen / 2) / circ;
      const angle = midRatio * 2 * Math.PI - Math.PI / 2;
      const lx = cx + radius * Math.cos(angle);
      const ly = cy + radius * Math.sin(angle);

      const pctText = Math.round(ratio * 100) + '%';
      const chip = document.createElementNS('http://www.w3.org/2000/svg', 'g');
      chip.setAttribute('transform', `translate(${lx} ${ly})`);

      const bgRect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
      const chipW = pctText.length <= 3 ? 34 : 40;
      bgRect.setAttribute('x', -chipW / 2); bgRect.setAttribute('y', -9);
      bgRect.setAttribute('width', chipW); bgRect.setAttribute('height', 18);
      bgRect.setAttribute('rx', 9);
      bgRect.setAttribute('fill', color);
      chip.appendChild(bgRect);

      const pct = document.createElementNS('http://www.w3.org/2000/svg', 'text');
      pct.setAttribute('text-anchor', 'middle');
      pct.setAttribute('dominant-baseline', 'central');
      pct.setAttribute('class', 'donut-pct');
      pct.textContent = pctText;
      chip.appendChild(pct);

      svg.appendChild(chip);
    }

    offset += segLen;
  });

  const valText = document.createElementNS('http://www.w3.org/2000/svg', 'text');
  valText.setAttribute('x', cx); valText.setAttribute('y', cy - 2);
  valText.setAttribute('text-anchor', 'middle');
  valText.setAttribute('dominant-baseline', 'middle');
  valText.setAttribute('class', 'donut-val');
  valText.textContent = String(total);
  svg.appendChild(valText);
  const labelText = document.createElementNS('http://www.w3.org/2000/svg', 'text');
  labelText.setAttribute('x', cx); labelText.setAttribute('y', cy + 18);
  labelText.setAttribute('text-anchor', 'middle');
  labelText.setAttribute('class', 'donut-sub');
  labelText.textContent = 'commits';
  svg.appendChild(labelText);

  return svg;
}

/* ────────────────────────────────────────────────
 *  Detail screen
 * ──────────────────────────────────────────────── */
async function renderDetail(ctx, index, filename) {
  const { viewEl, topbarEl } = ctx;
  const file = decodeURIComponent(filename);
  const base = index?.base || 'Docs';

  renderTopBar(topbarEl, {
    title: humanize(file),
    subtitle: `${base} / ${file}`,
    badge: { kind: 'readonly', text: 'Reference' },
    extra: h('button', {
      class: 'btn',
      onclick: () => { location.hash = '#dashboard'; },
    }, '← Back to list'),
  });

  mount(viewEl, loadingState());

  const content = await loadMd(`${base}/${file}`);
  if (content == null) {
    mount(viewEl, emptyState('Could not load file.'));
    return;
  }

  const viewer = createMdViewer({
    content,
    readOnly: true,
    breadcrumb: [base, file],
  });
  mount(viewEl, viewer);
}
