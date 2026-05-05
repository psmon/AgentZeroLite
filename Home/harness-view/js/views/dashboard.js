/**
 * Dashboard — three pre-built sections + one auto-aggregated section.
 *  1. Recent Updates           — narrative + best prompts (data/news.json — static, bilingual)
 *  2. PDSA Learning            — Plan/Do/Solved/Remaining/Learned (data/pdsa-insight.json — static, bilingual)
 *  3. Build Log                — harness/docs/*.md cards, semver-desc (indexes/harness-docs.json)
 *  4. Build-up Contributors    — donut + stat cards (indexes/harness-docs.json.contributors — git log)
 *
 * Bilingual: EN default, KO optional. Toggle persists via localStorage
 * (shared with Principles / Models). Static JSON values use `{ en, ko }`
 * where translation is human-curated; git-derived data (titles, authors,
 * paths) stays language-neutral and only the surrounding labels switch.
 *
 * Detail screen (#dashboard/<file>) renders the clicked .md as a read-only viewer.
 */
import { h, mount, humanize } from '../utils/dom.js';
import { loadIndex, loadMd, loadData } from '../utils/loader.js';
import { createMdViewer } from '../components/md-viewer.js';
import { renderTopBar, emptyState, loadingState } from './_common.js';
import { getLang, makeLangToggle, t } from '../components/bilingual.js';
import { ICONS } from '../config/menu.js';

/* UI label dictionary — short hardcoded strings that aren't part of
 * the JSON content. Anything human-authored in news.json / pdsa-insight.json
 * goes through `t()` instead. */
const T = {
  pageTitle: { en: 'Dashboard', ko: '대시보드' },
  pageSub:   {
    en: (base) => `${base} — resource-reference mode (read-only)`,
    ko: (base) => `${base} — 리소스 참고 모드 (읽기 전용)`,
  },
  badgeReference: { en: 'Reference', ko: '참고' },
  backToList:     { en: '← Back to list', ko: '← 목록으로' },
  fileLoadError:  { en: 'Could not load file.', ko: '파일을 불러오지 못했습니다.' },

  // News section
  newsTitle:     { en: 'Recent Updates', ko: '최근 업데이트' },
  newsSubAsOf:   { en: (d) => `As of ${d} · summary of recent shipped features`,
                   ko: (d) => `${d} 기준 · 최근 배포된 기능 요약` },
  newsSubFallback: { en: 'Highlights from recent releases', ko: '최근 릴리즈 하이라이트' },
  newsMissing:   { en: 'data/news.json is missing.', ko: 'data/news.json 파일이 없습니다.' },
  bestPrompts:   { en: 'Best Prompt Examples', ko: '추천 프롬프트 예시' },
  bestPromptsDesc: { en: 'Try these to get a feel for the new features',
                     ko: '아래 프롬프트로 새 기능을 직접 체험해 보세요' },

  // PDSA section
  pdsaTitle: { en: 'PDSA Learning', ko: 'PDSA 학습' },
  pdsaSubInline: { en: '— insights distilled from recent activity',
                   ko: '— 최근 활동에서 정제된 인사이트' },
  pdsaSubMeta: {
    en: (days, n, when) => `${days}-day window · ${n} sources · analyzed ${when}`,
    ko: (days, n, when) => `${days}일 윈도우 · 소스 ${n}개 · 분석일 ${when}`,
  },
  pdsaSubFallback: { en: 'Last 14 days · top 5 entries',
                     ko: '최근 14일 · 상위 5건' },
  pdsaMissing: { en: 'data/pdsa-insight.json is missing.',
                 ko: 'data/pdsa-insight.json 파일이 없습니다.' },
  pdsaTried:     { en: 'Tried',     ko: '시도' },
  pdsaSolved:    { en: 'Solved',    ko: '해결' },
  pdsaRemaining: { en: 'Remaining', ko: '잔여' },
  pdsaTagPD:     { en: 'P + D',  ko: 'P + D' },
  pdsaTagDone:   { en: 'DONE',   ko: '완료' },
  pdsaTagTodo:   { en: 'TODO',   ko: '잔여' },
  pdsaStudyAct:  { en: 'STUDY + ACT', ko: 'STUDY + ACT' },
  pdsaLearnedTitle: { en: 'Learned · core insight',
                      ko: '학습 · 핵심 통찰' },

  // Build Log
  buildLogTitle: { en: 'Build Log', ko: '빌드 로그' },
  buildLogSub:   {
    en: (n, base) => `${n} entries · ${base} · newest first`,
    ko: (n, base) => `${n}건 · ${base} · 최신순`,
  },
  buildLogEmpty: {
    en: 'No build-log entries yet at harness/docs/.',
    ko: 'harness/docs/ 에 아직 빌드 로그가 없습니다.',
  },
  unknownAuthor: { en: 'unknown', ko: '미상' },

  // Contributors
  contribTitle: {
    en: 'Build-up Contributors (Docs commits)',
    ko: '빌드 기여자 (Docs 커밋)',
  },
  contribLastN: { en: (n) => `Last ${n} days`, ko: (n) => `최근 ${n}일` },
  contribAll:   { en: 'All time', ko: '전체 기간' },
  contribCaptionRecent: {
    en: (days, total, n) => `Last ${days} days · ${total} commits · ${n} contributors`,
    ko: (days, total, n) => `최근 ${days}일 · 커밋 ${total}건 · 기여자 ${n}명`,
  },
  contribCaptionAll: {
    en: (total, n) => `All time · ${total} commits · ${n} contributors`,
    ko: (total, n) => `전체 기간 · 커밋 ${total}건 · 기여자 ${n}명`,
  },
  contribEmptyRecent: {
    en: (days) => `No Docs commits in the last ${days} days.`,
    ko: (days) => `최근 ${days}일간 Docs 커밋이 없습니다.`,
  },
  contribEmptyAll: {
    en: 'No contributor data available.',
    ko: '기여자 데이터가 없습니다.',
  },
  contribCommitsLine: {
    en: (n, pct) => `${n} commits · ${pct}%`,
    ko: (n, pct) => `커밋 ${n}건 · ${pct}%`,
  },
  statTotalDocs:   { en: 'Total docs', ko: '전체 문서 수' },
  statLatestDoc:   { en: (when) => `Latest doc · ${when}`,
                     ko: (when) => `최신 문서 · ${when}` },
  statCommits7d:   { en: 'Commits · last 7 days',  ko: '커밋 · 최근 7일' },
  statCommits30d:  { en: 'Commits · last 30 days', ko: '커밋 · 최근 30일' },
  statHeroRecent:  { en: (n) => `Commits · last ${n} days`,
                     ko: (n) => `커밋 · 최근 ${n}일` },
  statHeroAll:     { en: 'Commits · all time (Docs scope)',
                     ko: '커밋 · 전체 기간 (Docs 범위)' },
  statContribRecent: { en: (n) => `Contributors (last ${n} days)`,
                       ko: (n) => `기여자 (최근 ${n}일)` },
  statContribAll:    { en: 'Total contributors', ko: '전체 기여자' },
  topFilesTitle:   { en: 'Top changed files · last 30 days',
                     ko: '변경 빈도 상위 파일 · 최근 30일' },
  topFilesSub:     { en: 'union of harness/, Docs/, Home/harness-view/',
                     ko: 'harness/, Docs/, Home/harness-view/ 합집합' },
  donutSub:        { en: 'commits', ko: '커밋' },
  emDash:          { en: '—', ko: '—' },
};

/** Resolve a label entry against the active lang. Function values are
 *  invoked with `args` so call sites can pass interpolation values. */
function lbl(entry, lang, ...args) {
  if (entry == null) return '';
  const v = entry[lang] ?? entry.en;
  return typeof v === 'function' ? v(...args) : v;
}

export async function render(ctx) {
  const { viewEl, topbarEl, menu, params } = ctx;

  const index = await loadIndex('harness-docs');
  if (params) return renderDetail(ctx, index, params);

  let news = null;
  let pdsa = null;
  let firstDraw = true;

  const draw = async () => {
    const lang = getLang();
    renderTopBar(topbarEl, {
      title: lbl(T.pageTitle, lang),
      subtitle: lbl(T.pageSub, lang, index ? index.base : 'harness/docs'),
      badge: { kind: 'readonly', text: lbl(T.badgeReference, lang) },
      extra: makeLangToggle(() => draw()),
    });

    if (firstDraw) {
      mount(viewEl, loadingState());
      [news, pdsa] = await Promise.all([loadData('news'), loadData('pdsa-insight')]);
      firstDraw = false;
    }

    const items = (index?.items || []).slice().sort((a, b) =>
      a.title.localeCompare(b.title, undefined, { numeric: true, sensitivity: 'base' }) * -1);
    const contribAll    = index?.contributorsAll    || index?.contributors || [];
    const contribRecent = index?.contributorsRecent || [];
    const windowDays    = index?.contributorsWindowDays || 14;
    const commitStats   = index?.commitStats || null;
    const topFiles      = index?.topChangedFiles || [];

    const sections = h('div', { class: 'dash-sections' });
    sections.appendChild(renderNewsSection(news, lang));
    sections.appendChild(renderPdsaSection(pdsa, lang));
    sections.appendChild(renderBuildLogSection(menu, items, index, lang));
    sections.appendChild(renderContribSection({
      all: contribAll, recent: contribRecent, windowDays, commitStats, topFiles,
    }, items, lang));
    mount(viewEl, sections);
  };

  await draw();
}

/* ────────────────────────────────────────────────
 *  1) Recent Updates
 * ──────────────────────────────────────────────── */
function renderNewsSection(news, lang) {
  const sec = h('section', { class: 'dash-sec news-sec' });
  const head = h('div', { class: 'dash-sec-head' }, [
    h('div', { class: 'dash-sec-title' }, [
      h('span', { class: 'dash-sec-icon tone-blue', html: ICONS.newspaper }),
      h('span', {}, lbl(T.newsTitle, lang)),
    ]),
    h('div', { class: 'dash-sec-sub' },
      news ? lbl(T.newsSubAsOf, lang, news.updatedAt)
           : lbl(T.newsSubFallback, lang)),
  ]);
  sec.appendChild(head);

  if (!news) {
    sec.appendChild(emptyState(lbl(T.newsMissing, lang)));
    return sec;
  }

  const body = h('div', { class: 'news-body' });

  // Left: story
  const story = h('div', { class: 'news-story card-plain' });
  story.appendChild(h('div', { class: 'news-lead' }, t(news.headline, lang)));
  story.appendChild(h('p', { class: 'news-narrative' }, t(news.narrative, lang)));
  if (Array.isArray(news.highlights) && news.highlights.length) {
    const hi = h('div', { class: 'news-highlights' });
    for (const it of news.highlights) {
      const cls = `news-hi tone-${it.tone || 'blue'}`;
      hi.appendChild(h('div', { class: cls }, [
        h('div', { class: 'hi-label' }, t(it.label, lang)),
        h('div', { class: 'hi-text' }, t(it.text, lang)),
      ]));
    }
    story.appendChild(hi);
  }
  body.appendChild(story);

  // Right: best prompts
  const prompts = h('div', { class: 'news-prompts card-plain' });
  prompts.appendChild(h('div', { class: 'news-prompts-head' }, [
    h('span', { class: 'dash-sec-icon tone-amber', html: ICONS.sparkles }),
    h('span', {}, lbl(T.bestPrompts, lang)),
  ]));
  prompts.appendChild(h('div', { class: 'news-prompts-desc' }, lbl(T.bestPromptsDesc, lang)));
  const list = h('div', { class: 'news-prompts-list' });
  for (const p of news.prompts || []) {
    const chip = h('div', { class: 'prompt-chip' });
    chip.appendChild(h('div', { class: `prompt-tag tone-${p.tone || 'blue'}` }, t(p.tag, lang)));
    chip.appendChild(h('div', { class: 'prompt-text' }, `"${t(p.text, lang)}"`));
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
function renderPdsaSection(pdsa, lang) {
  const sec = h('section', { class: 'dash-sec pdsa-sec' });
  const head = h('div', { class: 'dash-sec-head' }, [
    h('div', { class: 'dash-sec-title' }, [
      h('span', { class: 'dash-sec-icon tone-purple', html: ICONS.bulb }),
      h('span', {}, lbl(T.pdsaTitle, lang)),
      h('span', { class: 'dash-sec-sub-inline' }, lbl(T.pdsaSubInline, lang)),
    ]),
    h('div', { class: 'dash-sec-sub' },
      pdsa
        ? lbl(T.pdsaSubMeta, lang, pdsa.windowDays || 14, pdsa.sources?.length || 0, pdsa.analyzedAt)
        : lbl(T.pdsaSubFallback, lang)),
  ]);
  sec.appendChild(head);

  if (!pdsa) {
    sec.appendChild(emptyState(lbl(T.pdsaMissing, lang)));
    return sec;
  }

  // Source pills
  if (Array.isArray(pdsa.sources) && pdsa.sources.length) {
    const srcBar = h('div', { class: 'pdsa-src-bar' });
    for (const s of pdsa.sources) {
      const titleStr = t(s.title, lang) || (s.file || '').split('/').pop();
      srcBar.appendChild(h('div', { class: 'pdsa-src-pill', title: s.file || '' }, [
        h('span', { class: 'pdsa-src-date' }, (s.date || '').slice(5)),   // MM-DD
        h('span', { class: 'pdsa-src-title' }, titleStr),
      ]));
    }
    sec.appendChild(srcBar);
  }

  // 3-state cards
  const row = h('div', { class: 'pdsa-row' });
  row.appendChild(quadrantCard(lbl(T.pdsaTagPD, lang),   'tone-blue',  lbl(T.pdsaTried, lang),     pdsa.tried,     lang));
  row.appendChild(quadrantCard(lbl(T.pdsaTagDone, lang), 'tone-green', lbl(T.pdsaSolved, lang),    pdsa.solved,    lang));
  row.appendChild(quadrantCard(lbl(T.pdsaTagTodo, lang), 'tone-amber', lbl(T.pdsaRemaining, lang), pdsa.remaining, lang));
  sec.appendChild(row);

  // Learned hero
  const learned = pdsa.learned || {};
  const leadStr = t(learned.lead, lang) || lbl(T.emDash, lang);
  const bodyStr = t(learned.body, lang);
  const hero = h('div', { class: 'pdsa-hero' }, [
    h('div', { class: 'pdsa-hero-icon-wrap' }, [
      h('span', { class: 'pdsa-hero-icon', html: ICONS.bulb }),
    ]),
    h('div', { class: 'pdsa-hero-body' }, [
      h('div', { class: 'pdsa-hero-head' }, [
        h('span', { class: 'pdsa-hero-badge' }, lbl(T.pdsaStudyAct, lang)),
        h('span', { class: 'pdsa-hero-title' }, lbl(T.pdsaLearnedTitle, lang)),
      ]),
      h('div', { class: 'pdsa-hero-lead' }, leadStr),
      bodyStr ? h('div', { class: 'pdsa-hero-desc' }, bodyStr) : null,
    ]),
  ]);
  sec.appendChild(hero);
  return sec;
}

function quadrantCard(tag, toneClass, title, items, lang) {
  const list = Array.isArray(items) ? items : [];
  return h('div', { class: 'pdsa-card card-plain' }, [
    h('div', { class: 'pdsa-card-head' }, [
      h('span', { class: `pdsa-card-badge ${toneClass}` }, tag),
      h('span', { class: 'pdsa-card-title' }, title),
      h('span', { class: 'pdsa-card-count' }, String(list.length)),
    ]),
    h('ul', { class: 'pdsa-card-list' }, list.map(item => h('li', {}, t(item, lang)))),
  ]);
}

/* ────────────────────────────────────────────────
 *  3) Build Log — harness/docs/*.md (semver-desc release cards)
 * ──────────────────────────────────────────────── */
function renderBuildLogSection(menu, items, index, lang) {
  const sec = h('section', { class: 'dash-sec release-sec' });
  const head = h('div', { class: 'dash-sec-head' }, [
    h('div', { class: 'dash-sec-title' }, [
      h('span', { class: 'dash-sec-icon tone-amber', html: ICONS.tag }),
      h('span', {}, lbl(T.buildLogTitle, lang)),
    ]),
    h('div', { class: 'dash-sec-sub' },
      lbl(T.buildLogSub, lang, items.length, index?.base || 'harness/docs')),
  ]);
  sec.appendChild(head);

  if (!items.length) {
    sec.appendChild(emptyState(lbl(T.buildLogEmpty, lang)));
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
        h('span', { class: 'rc-author' }, it.author || lbl(T.unknownAuthor, lang)),
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
function renderContribSection({ all, recent, windowDays, commitStats, topFiles }, items, lang) {
  const sec = h('section', { class: 'dash-sec contrib-sec' });
  let mode = 'recent';

  const toggle = h('div', { class: 'toggle contrib-toggle' });
  const btnRecent = h('button', { class: 'active', onclick: () => setMode('recent') },
    lbl(T.contribLastN, lang, windowDays));
  const btnAll    = h('button', { onclick: () => setMode('all') },
    lbl(T.contribAll, lang));
  toggle.appendChild(btnRecent);
  toggle.appendChild(btnAll);

  const head = h('div', { class: 'dash-sec-head' }, [
    h('div', { class: 'dash-sec-title' }, [
      h('span', { class: 'dash-sec-icon tone-green', html: ICONS.pieChart }),
      h('span', {}, lbl(T.contribTitle, lang)),
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
  stats.appendChild(statCard(String(items.length), lbl(T.statTotalDocs, lang), 'tone-blue'));
  stats.appendChild(statCard(
    latest?.title || lbl(T.emDash, lang),
    lbl(T.statLatestDoc, lang, latest?.committed || latest?.modified || ''),
    'tone-purple',
  ));
  // 7d / 30d activity tiles — only when build supplied them.
  if (commitStats) {
    stats.appendChild(statCard(String(commitStats.last7d ?? 0),  lbl(T.statCommits7d, lang),  'tone-amber'));
    stats.appendChild(statCard(String(commitStats.last30d ?? 0), lbl(T.statCommits30d, lang), 'tone-amber'));
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
    sec.appendChild(renderTopFiles(topFiles, lang));
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
      ? lbl(T.contribCaptionRecent, lang, windowDays, totalCommits, contributors.length)
      : lbl(T.contribCaptionAll, lang, totalCommits, contributors.length);

    const inner = h('div', { class: 'contrib-donut-inner' });
    if (!contributors.length) {
      inner.appendChild(emptyState(mode === 'recent'
        ? lbl(T.contribEmptyRecent, lang, windowDays)
        : lbl(T.contribEmptyAll, lang)));
    } else {
      const colored = contributors.map((c, i) => ({ ...c, color: contribColor(i) }));
      inner.appendChild(buildDonut(colored, totalCommits, lang));
      const legend = h('div', { class: 'contrib-legend' });
      colored.forEach((c) => {
        legend.appendChild(h('div', { class: 'contrib-leg' }, [
          h('span', { class: 'leg-dot', style: { background: c.color } }),
          h('div', { class: 'leg-info' }, [
            h('div', { class: 'leg-name' }, c.name),
            h('div', { class: 'leg-det' }, lbl(T.contribCommitsLine, lang, c.commits, c.percent)),
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
      ? lbl(T.statHeroRecent, lang, windowDays)
      : lbl(T.statHeroAll, lang);
    mount(totalCommitsCard,
      h('div', { class: 'stat-val tone-green' }, String(heroCommits)),
      h('div', { class: 'stat-label' }, heroLabel),
    );

    const countLabel = mode === 'recent'
      ? lbl(T.statContribRecent, lang, windowDays)
      : lbl(T.statContribAll, lang);
    mount(contribCountCard,
      h('div', { class: 'stat-val tone-blue' }, String(contributors.length)),
      h('div', { class: 'stat-label' }, countLabel),
    );
  }

  renderDonut();
  return sec;
}

function renderTopFiles(topFiles, lang) {
  const wrap = h('div', { class: 'contrib-topfiles card-plain' });
  wrap.appendChild(h('div', { class: 'topfiles-head' }, [
    h('span', { class: 'topfiles-title' }, lbl(T.topFilesTitle, lang)),
    h('span', { class: 'topfiles-sub' }, lbl(T.topFilesSub, lang)),
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

function buildDonut(contributors, total, lang) {
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
  labelText.textContent = lbl(T.donutSub, lang);
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
  const lang = getLang();

  renderTopBar(topbarEl, {
    title: humanize(file),
    subtitle: `${base} / ${file}`,
    badge: { kind: 'readonly', text: lbl(T.badgeReference, lang) },
    extra: h('button', {
      class: 'btn',
      onclick: () => { location.hash = '#dashboard'; },
    }, lbl(T.backToList, lang)),
  });

  mount(viewEl, loadingState());

  const content = await loadMd(`${base}/${file}`);
  if (content == null) {
    mount(viewEl, emptyState(lbl(T.fileLoadError, lang)));
    return;
  }

  const viewer = createMdViewer({
    content,
    readOnly: true,
    breadcrumb: [base, file],
  });
  mount(viewEl, viewer);
}
