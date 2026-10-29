// Diagram modes besides the main map: roadmap ("План"), database ("База данных") and the
// hierarchy tree ("Подробное древо"). Each builds Cytoscape elements in the same conventions as the
// map (cards as SVG, frames with data.frame, hierarchy via data.pid) and reuses the ELK layout.
'use strict';

const TREE = { open: new Set(['tree:root', 'tree:cat:app']) };

function viewElements(view) {
  if (view === 'plan') return buildPlanElements();
  if (view === 'db') return buildDbElements();
  if (view === 'tree') return buildTreeElements();
  if (view === 'file') return buildFileElements();
  return [];
}

function node(id, data) {
  // a negative text width is invalid and makes Cytoscape warn for every element
  return { group: 'nodes', pannable: true, data: { id, z: data.frame ? 1 : 20, tw: Math.max(20, (data.w || 200) - 14), label: '', ...data } };
}
function frameNode(id, name, pid, col, depth = 0) {
  return node(id, { kind: 'vframe', frame: true, name, pid: pid || null, w: 10, h: 10, z: 1 + depth, fill: col.fill, line: col.line, soft: col.soft });
}
function cardNode(id, kind, card, col, pid, extra = {}) {
  return node(id, { kind, name: extra.name || id, svg: card.svg, w: card.w, h: card.h, fill: col.fill, line: col.line, soft: col.soft, pid: pid || null, ...extra });
}
function edge(id, source, target, cls, data = {}) {
  return { group: 'edges', data: { id, source, target, width: 2.4, op: 0.85, lbl: '', ...data }, classes: cls };
}
const NEUTRAL = () => document.documentElement.dataset.theme === 'dark'
  ? { fill: 'hsl(220,14%,24%)', line: 'hsl(220,12%,62%)', soft: 'hsl(220,10%,40%)' }
  : { fill: 'hsl(40,20%,96%)', line: 'hsl(220,10%,45%)', soft: 'hsl(40,15%,80%)' };
// Cytoscape doesn't parse CSS color-mix(), so blend hex colours here
const mixHex = (a, b, t) => {
  const p = h => [1, 3, 5].map(i => parseInt(h.slice(i, i + 2), 16));
  const [x, y] = [p(a), p(b)];
  return '#' + x.map((v, i) => Math.round(v * t + y[i] * (1 - t)).toString(16).padStart(2, '0')).join('');
};
const statusCol = s => {
  const c = STATUS_COLOR[s] || STATUS_COLOR.unknown, dark = document.documentElement.dataset.theme === 'dark';
  return { line: c, fill: mixHex(c, dark ? '#232831' : '#ffffff', dark ? 0.22 : 0.14), soft: c };
};

// ---------------------------------------------------------------- План

const PLAN_KEY = /^[\s*_`#\[]*([A-ZА-Я][A-Za-zА-Яа-я]{0,4}-?\d{1,4}[a-z]?)(?![\p{L}\p{N}])/u;

function buildPlanElements() {
  const M = S.model, els = [];
  if (!M.roadmap.length) return [cardNode('plan:none', 'note', svgCard({ key: 'plan:none', w: 420, title: 'Дорожная карта не найдена', body: 'В проекте нет файлов ROADMAP, TODO, STATE, PLAN или PROGRESS с задачами.' }), NEUTRAL())];

  // items with a real status that passes the status chips; the same stage written in two documents
  // (e.g. «R09» in the roadmap and in the progress table) is shown once
  const byKey = new Map(), shown = [];
  // a changelog or a decision log is history, not a plan
  const isLog = r => /(?:^|\/)(?:CHANGELOG|DECISIONS)[^/]*$|(?:^|\/)journal\//i.test(r.source?.path || '');
  for (const r of M.roadmap) {
    if (r.status === 'unknown' || !S.planFilter.has(r.status) || isLog(r)) continue;
    const k = (r.title.match(PLAN_KEY) || [])[1];
    if (k) {
      const prev = byKey.get(k.toUpperCase());
      if (prev && roadmapLinks(prev).length >= roadmapLinks(r).length) continue;
      if (prev) shown.splice(shown.indexOf(prev), 1);
      byKey.set(k.toUpperCase(), r);
    }
    shown.push(r);
  }
  const shownIds = new Set(shown.map(r => r.id));
  // only two levels of frames: the document and its top section; deeper headings are folded in
  const depth = id => { let d = 0; for (let p = M.rmById.get(id)._parent; p; p = M.rmById.get(p)._parent) d++; return d; };
  const chain = r => { const c = []; for (let p = r._parent; p; p = M.rmById.get(p)._parent) c.unshift(p); return c; };
  const frameIds = new Set();
  for (const r of shown) for (const p of chain(r).slice(0, 2)) frameIds.add(p);
  for (const r of shown) frameIds.delete(r.id); // a shown item is a card, even if it is a heading
  const nearestFrame = r => { const c = chain(r).slice(0, 2).filter(p => frameIds.has(p)); return c.length ? c[c.length - 1] : null; };
  shown.sort((a, b) => ['blocked', 'active', 'planned', 'deferred', 'done'].indexOf(a.status) - ['blocked', 'active', 'planned', 'deferred', 'done'].indexOf(b.status));

  for (const id of frameIds) {
    const r = M.rmById.get(id);
    els.push(frameNode(id, r.title.replace(/[*`_]/g, ''), depth(id) === 0 ? null : nearestFrame(r), NEUTRAL(), depth(id)));
  }
  const touched = new Map(); // code block → plan items touching it
  for (const r of shown) {
    const links = roadmapLinks(r);
    const blocks = [...new Set(links.map(l => [l, ...M.ancestors(l)].find(x => M.top.includes(x))).filter(Boolean))];
    for (const b of blocks) { if (!touched.has(b)) touched.set(b, []); touched.get(b).push(r.id); }
    const card = svgCard({
      key: 'rm:' + r.id, w: 300, title: r.title.replace(/[*`_]/g, ''), pill: { text: STATUS_RU[r.status], color: STATUS_COLOR[r.status] },
      body: blocks.length ? 'Затрагивает: ' + blocks.map(b => titleOf(M.node(b))).join(', ') : 'Связей с кодом не найдено', bodyLines: 3,
      fs: 15, bodyFs: 13, align: 'start',
    });
    els.push(cardNode(r.id, 'rm', card, statusCol(r.status), nearestFrame(r), { name: r.title }));
  }
  // code blocks the plan touches, in one frame; plan→code arrows appear when an item or block is selected
  if (touched.size) {
    els.push(frameNode('plan:code', 'Код, который затрагивает план (выберите задачу или блок — появятся стрелки)', null, NEUTRAL()));
    for (const [b, items] of touched) {
      const n = M.node(b);
      const card = cardFor(n, M.hasKids(b));
      els.push(cardNode(b, n.kind, card, M.colorOf(b), 'plan:code', { name: n.name }));
      for (const it of items) els.push(edge(`plan|${it}|${b}`, it, b, 'overlay planlink', { color: M.colorOf(b).line }));
    }
  }
  return els;
}

// Plan layout without ELK (there are no arrows to route): every frame packs its children into
// rows of a width that keeps the frame close to the screen's proportions; frames grow around them.
// Works on the element list before it is put on screen (positions and frame sizes are written into it).
function packLayout(els) {
  const kids = new Map(), roots = [], ids = new Set(els.map(e => e.data.id));
  // the same small interface the packing below used on Cytoscape nodes
  const wrap = e => ({ id: () => e.data.id, data: (k) => typeof k === 'string' ? e.data[k] : Object.assign(e.data, k), position: p => { e.position = p; } });
  for (const e of els) {
    if (e.group !== 'nodes') continue;
    const n = wrap(e), p = e.data.pid;
    if (p && ids.has(p)) { if (!kids.has(p)) kids.set(p, []); kids.get(p).push(n); } else roots.push(n);
  }
  const GAP = 26, PAD = 28, TOP = 72;
  const aspect = Math.max(1, cy.width() / Math.max(1, cy.height()));
  const size = new Map();
  const measure = n => {
    const list = kids.get(n.id());
    if (!list) { const s = { w: n.data('w'), h: n.data('h') }; size.set(n.id(), s); return s; }
    const rows = shelf(list.map(k => ({ n: k, ...measure(k) })), 1.6);
    const s = { w: rows.w + 2 * PAD, h: rows.h + TOP + PAD, rows };
    size.set(n.id(), s); return s;
  };
  // rows of items; the row width is chosen among several candidates so that the result fits a
  // box of proportions `ratio` (width / height) as large as possible
  const rowsFor = (items, target) => {
    const rows = []; let row = [], x = 0;
    for (const i of items) {
      if (row.length && x + i.w > target) { rows.push(row); row = []; x = 0; }
      row.push(i); x += i.w + GAP;
    }
    if (row.length) rows.push(row);
    const w = Math.max(...rows.map(r => r.reduce((a, i) => a + i.w + GAP, -GAP)));
    const h = rows.reduce((a, r) => a + Math.max(...r.map(i => i.h)) + GAP, -GAP);
    return { list: rows, w, h };
  };
  const shelf = (items, ratio) => {
    const maxW = Math.max(...items.map(i => i.w)), sumW = items.reduce((a, i) => a + i.w + GAP, 0);
    let best = null;
    for (let k = 0; k <= 24; k++) {
      const r = rowsFor(items, maxW + (sumW - maxW) * k / 24);
      const cost = Math.max(r.w / ratio, r.h); // the side that limits the zoom
      if (!best || cost < best.cost) best = { ...r, cost };
    }
    return best;
  };
  const place = (n, x, y) => {
    const s = size.get(n.id());
    if (s.rows) {
      n.data({ w: s.w, h: s.h, tw: s.w - 40 });
      placeRows(s.rows, x + PAD, y + TOP);
    }
    n.position({ x: x + s.w / 2, y: y + s.h / 2 });
  };
  const placeRows = (rows, x0, y0) => {
    let y = y0;
    for (const r of rows.list) {
      let x = x0; const rh = Math.max(...r.map(i => i.h));
      for (const i of r) { place(i.n, x, y); x += i.w + GAP; }
      y += rh + GAP;
    }
  };
  const top = shelf(roots.map(n => ({ n, ...measure(n) })), aspect);
  placeRows(top, 0, 0);
}

// ---------------------------------------------------------------- База данных

function buildDbElements() {
  const M = S.model, db = M.g.database, els = [];
  if (!db?.tables?.length) return [cardNode('db:none', 'note', svgCard({ key: 'db:none', w: 440, title: 'База данных не найдена', body: 'В коде нет CREATE TABLE, файлов *.db/*.sqlite, миграций EF или схемы Prisma.' }), NEUTRAL())];
  const tableNode = name => [...M.byId.values()].find(n => n.kind === 'table' && n.name.toLowerCase() === name.toLowerCase());
  const dbCol = document.documentElement.dataset.theme === 'dark'
    ? { fill: 'hsl(172,30%,22%)', line: 'hsl(172,55%,58%)', soft: 'hsl(172,40%,40%)' }
    : { fill: 'hsl(172,45%,94%)', line: 'hsl(172,55%,32%)', soft: 'hsl(172,40%,80%)' };
  els.push(frameNode('db:frame', `База данных · ${db.engine} · ${db.tables.length} ${plural(db.tables.length, 'таблица', 'таблицы', 'таблиц')}`, null, dbCol));
  const ids = new Map();
  for (const t of db.tables) {
    const n = tableNode(t.name), id = n?.id || 'dbt:' + t.name;
    ids.set(t.name.toLowerCase(), id);
    const rows = t.columns.slice(0, 12).map(c => `${c.primaryKey ? '🔑' : '  '} ${c.name}: ${c.type}${c.notNull ? ' !' : ''}`);
    const card = svgCard({ key: 'tbl:' + t.name, w: 300, title: t.name, sub: 'таблица', rows, foot: t.columns.length > 12 ? `и ещё ${t.columns.length - 12} колонок` : `${t.columns.length} ${plural(t.columns.length, 'колонка', 'колонки', 'колонок')}`, fs: 18, bodyFs: 13 });
    els.push(cardNode(id, 'table', card, dbCol, 'db:frame', { name: t.name }));
  }
  for (const t of db.tables) for (const fk of t.foreignKeys || []) {
    const a = ids.get(t.name.toLowerCase()), b = ids.get(fk.refTable.toLowerCase());
    if (a && b) els.push(edge(`fk|${a}|${b}|${fk.column}`, a, b, 'db', { color: dbCol.line, lbl: `${fk.column} → ${fk.refColumn}` }));
  }
  // code that reads/writes the tables, grouped by module
  const modFrames = new Set(), files = new Set();
  for (const [, id] of ids) for (const e of M.edgesOf.get(id) || []) {
    if (EDGE_CAT[e.kind] !== 'db') continue;
    const f = M.fileOf(e.source) || e.source;
    if (!M.byId.has(f)) continue;
    const mod = [f, ...M.ancestors(f)].find(x => M.top.includes(x));
    if (mod && !modFrames.has(mod)) { modFrames.add(mod); els.push(frameNode('dbm:' + mod, titleOf(M.node(mod)) + ' · ' + M.node(mod).name, null, M.colorOf(mod))); }
    if (!files.has(f)) { files.add(f); els.push(cardNode(f, 'file', cardFor(M.node(f), false), M.colorOf(f), mod ? 'dbm:' + mod : null, { name: M.node(f).name })); }
    els.push(edge(`dbu|${e.kind}|${f}|${id}`, f, id, 'db', { color: M.colorOf(f).line, lbl: e.kind === 'dbWrite' ? 'пишет' : 'читает' }));
  }
  return dedupeEdges(els);
}

function dedupeEdges(els) {
  const seen = new Set();
  return els.filter(e => e.group !== 'edges' || (!seen.has(e.data.id) && seen.add(e.data.id)));
}

// ---------------------------------------------------------------- Подробное древо (hierarchy as a tree diagram)

function buildTreeElements() {
  const M = S.model, els = [], p = M.g.project || {};
  const files = M.g.nodes.filter(n => n.kind === 'file').length;
  els.push(cardNode('tree:root', 'troot', svgCard({ key: 'tree:root', w: 300, title: p.name || 'Проект', body: 'весь проект', foot: `${files} ${plural(files, 'файл', 'файла', 'файлов')}`, fs: 24 }), NEUTRAL(), null, { name: p.name }));
  const add = (id, parentId) => {
    const n = M.node(id);
    const open = TREE.open.has(id) && M.hasKids(id) && n.kind !== 'type';
    els.push(cardNode(id, n.kind, cardFor(n, M.hasKids(id) && !open), M.colorOf(id), null, { name: n.name, closedKids: M.hasKids(id) && !open && n.kind !== 'type' }));
    els.push(edge(`tree|${parentId}|${id}`, parentId, id, 'tree', { color: M.colorOf(id).line }));
    if (open) for (const k of M.kids(id)) if (M.node(k).kind !== 'member') add(k, id);
  };
  if (TREE.open.has('tree:root')) for (const [key, title, about] of CATEGORIES) {
    const ids = M.top.filter(id => M.categoryOf(id) === key);
    if (!ids.length) continue;
    const cid = 'tree:cat:' + key, open = TREE.open.has(cid);
    els.push(cardNode(cid, 'tcat', svgCard({ key: cid, w: 300, title, body: about, foot: `${ids.length} ${plural(ids.length, 'часть', 'части', 'частей')}${open ? '' : ' · двойной клик — раскрыть'}`, fs: 20 }), NEUTRAL(), null, { name: title, closedKids: !open }));
    els.push(edge(`tree|tree:root|${cid}`, 'tree:root', cid, 'tree', { color: NEUTRAL().line }));
    if (open) for (const id of ids) add(id, cid);
  }
  return els;
}

function toggleTree(id) {
  if (TREE.open.has(id)) {
    TREE.open.delete(id);
    for (const x of [...TREE.open]) if (S.model.byId.has(x) && S.model.byId.has(id) && S.model.isUnder(x, id)) TREE.open.delete(x);
  } else {
    TREE.open.add(id);
    if (countTreeBlocks() > MAX_BLOCKS) { TREE.open.delete(id); toast('Внутри слишком много блоков для одной схемы — раскрывайте по одной папке.', 5000); return; }
  }
  render({ anchor: id });
}
function countTreeBlocks() { return buildTreeElements().filter(e => e.group === 'nodes').length; }

// In the tree, the selected block also shows its dependencies on the other blocks on screen.
function treeDependencyEdges(id) {
  const M = S.model;
  if (!M.byId.has(id)) return [];
  const c = connections(id), out = [];
  const add = (map, dir) => [...map.values()].sort((a, b) => b.w - a.w).slice(0, 25).forEach(r => {
    if (cy.getElementById(r.peer).empty()) return;
    const [s, t] = dir === 'out' ? [id, r.peer] : [r.peer, id];
    out.push(edge(`dep|${s}|${t}`, s, t, 'overlay dep', { color: M.colorOf(s).line, lbl: `×${r.w}` }));
  });
  add(c.out, 'out'); add(c.in, 'in');
  return dedupeEdges(out);
}

// ---------------------------------------------------------------- «Горячие»: heat on the map

const HEAT_RU = { commits: 'чем чаще меняют', complexity: 'чем сложнее код', loc: 'чем больше код', links: 'чем больше связей' };
function heatValue(n, metric, linkCount) {
  const a = n._agg;
  return metric === 'links' ? (linkCount.get(n.id) || 0) : metric === 'complexity' ? a.complexity : metric === 'loc' ? a.loc : a.commits;
}
function applyHeat(els, metric) {
  const M = S.model, nodes = els.filter(e => e.group === 'nodes' && M.byId.has(e.data.id) && !e.data.frame);
  const linkCount = new Map();
  for (const e of els) if (e.group === 'edges') { linkCount.set(e.data.source, (linkCount.get(e.data.source) || 0) + e.data.w); linkCount.set(e.data.target, (linkCount.get(e.data.target) || 0) + e.data.w); }
  const vals = nodes.map(e => heatValue(M.node(e.data.id), metric, linkCount)).sort((a, b) => a - b);
  const rank = v => { let lo = 0, hi = vals.length; while (lo < hi) { const m = (lo + hi) >> 1; if (vals[m] < v) lo = m + 1; else hi = m; } return vals.length > 1 ? lo / (vals.length - 1) : 1; };
  const dark = document.documentElement.dataset.theme === 'dark';
  for (const e of nodes) {
    const t = rank(heatValue(M.node(e.data.id), metric, linkCount)); // 0 cold … 1 hot
    const h = Math.round(210 - 210 * t), s = 40 + 50 * t;
    e.data.fill = dark ? `hsl(${h},${s}%,${18 + 14 * t}%)` : `hsl(${h},${s}%,${95 - 25 * t}%)`;
    e.data.line = `hsl(${h},${s + 5}%,${dark ? 62 : 42}%)`;
  }
}
