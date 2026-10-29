// «Схема связей»: the project as tier bands × feature columns; an arrow is code of one block calling or using
// code of another (thickness = how many uses). Hover: the block's links. Click: inside the block — its files,
// then a file's classes, then a class's functions; every level shows who it calls (right) and who calls it (left).
// Wheel zooms at the pointer, drag pans, F fits. Data: graph.views (ViewsScanner); the ordinary views are untouched.
'use strict';

const LV_CODE = new Set(['call', 'reference', 'inherit', 'implement', 'import', 'xamlEvent', 'binding']);
const LV_ROWS = ['frontend', 'application', 'backend', 'domain', 'data', 'infrastructure', 'mixed', 'unclassified', 'tests', 'docs'];
const LV_OTHER = 'lv:other', LV_REST = 'lv:rest';
const LV_MAXCOLS = 12, LV_TOPLINKS = 45, LV_PARTNERS = 12;
const LV_DECISION_RU = { automatic: 'уверенно', guess: 'предположение', mixed: 'смешанный', unknown: 'не определено', override: 'назначено вручную' };
const LV_FAMILY_RU = { syntax: 'по содержимому', rules: 'правило проекта', path: 'по папке', module: 'по имени модуля', framework: 'по библиотеке модуля', usage: 'по использованию', override: 'вручную' };

const LV = {
  data: null, dataFor: null,
  mode: 'links',              // links | tree | tiles | sun | stars | flows (shapeviews.js)
  stack: [],                  // drill path: [{ tier } | { cell: key } | { node: id, file? }]
  t: { x: 0, y: 0, k: 1 }, scene: null, hover: null,
  showTests: false, showDocs: false,
  overrides: null, overridesRoot: null,
};

// ---------------------------------------------------------------- data

function lvData() {
  const M = S.model;
  if (!M?.g.views) return null;
  if (LV.dataFor === M) return LV.data;
  const v = M.g.views;
  const assign = new Map(v.assignments.map(a => [a.nodeId, a]));
  const tiers = new Map(v.tiers.map(t => [t.id, t]));
  const featOrder = new Map(v.features.map((f, i) => [f.id, i]));
  const feats = new Map(v.features.map(f => [f.id, f]));
  const cols = v.features.filter(f => f.files > 0).slice(0, LV_MAXCOLS).map(f => f.id);
  const colSet = new Set(cols);
  const fileFeats = new Map(), entries = new Map();
  for (const m of v.memberships) {
    if (m.role === 'entry') { (entries.get(m.featureId) || entries.set(m.featureId, []).get(m.featureId)).push(m.nodeId); continue; }
    if (!m.nodeId.startsWith('file:')) continue;
    (fileFeats.get(m.nodeId) || fileFeats.set(m.nodeId, []).get(m.nodeId)).push(m.featureId);
  }
  for (const list of fileFeats.values()) list.sort((a, b) => featOrder.get(a) - featOrder.get(b));
  const colOf = id => { const f = fileFeats.get(id)?.[0]; return !f ? LV_OTHER : colSet.has(f) ? f : LV_REST; };

  const cells = new Map(), cellOf = new Map();
  for (const a of v.assignments) {
    if (!M.byId.has(a.nodeId)) continue;
    const key = a.tier + '|' + colOf(a.nodeId);
    let c = cells.get(key);
    if (!c) cells.set(key, c = { key, tier: a.tier, col: key.slice(a.tier.length + 1), files: [], guesses: 0 });
    c.files.push(a.nodeId);
    if (a.decision === 'guess' || a.decision === 'unknown') c.guesses++;
    cellOf.set(a.nodeId, key);
  }
  // code edges at file level, plus every edge indexed by the files of its ends (for the inside views)
  const ff = new Map(), byFile = new Map();
  const push = (f, e) => (byFile.get(f) || byFile.set(f, []).get(f)).push(e);
  for (const e of M.g.edges) {
    if (!LV_CODE.has(e.kind)) continue;
    const s = lvFileOf(e.source), t = lvFileOf(e.target);
    if (!s || !t) continue;
    push(s, e); if (t !== s) push(t, e);
    if (s === t) continue;
    const k = s + '\n' + t;
    ff.set(k, (ff.get(k) || 0) + (e.weight || 1));
  }
  const cl = new Map();
  for (const [k, w] of ff) {
    const [s, t] = k.split('\n'), a = cellOf.get(s), b = cellOf.get(t);
    if (!a || !b || a === b) continue;
    const kk = a + '\n' + b;
    cl.set(kk, (cl.get(kk) || 0) + w);
  }
  // what each file holds at class level, by where the code is written (partial classes span files)
  const inFile = new Map();
  const put = (f, x) => { if (f) (inFile.get(f) || inFile.set(f, new Set()).get(f)).add(x); };
  for (const n of M.g.nodes) {
    if (!n.path) continue;
    const f = M.byPath.get(n.path.toLowerCase());
    if (n.kind === 'type') { if (M.node(n._parent)?.kind === 'file') put(f, n.id); }
    else if (n.kind === 'member') { const p = M.node(n._parent); put(f, p?.kind === 'type' ? p.id : n.id); }
  }
  const cellLinks = [...cl].map(([k, w]) => { const [a, b] = k.split('\n'); return { a, b, w }; }).sort((x, y) => y.w - x.w);
  LV.dataFor = M;
  return LV.data = { v, inFile, assign, tiers, feats, cols, cells, cellOf, ff, byFile, cellLinks, fileFeats, entries };
}

// the file a node is written in (by path): a member of a partial class belongs to its own file, not the class's first one
function lvFileOf(id) {
  const M = S.model, n = M.node(id);
  if (n?.kind === 'file') return id;
  return (n?.path && M.byPath.get(n.path.toLowerCase())) || M.fileOf(id);
}
const lvTierName = id => LV.data?.tiers.get(id)?.name || id;
const lvColName = id => id === LV_OTHER ? 'Общий код' : id === LV_REST ? 'Другие функции' : LV.data?.feats.get(id)?.name || id;
const lvColor = tier => `var(--t-${LV_ROWS.includes(tier) ? tier : 'unclassified'})`;
const lvNodeName = n => n.subKind === 'handler' ? '▶ ' + n.name : n.kind === 'file' ? n.name : n.name;

// text that never runs over its box: measured, cut with an ellipsis; the full text is in the tooltip
const lvCanvas = document.createElement('canvas').getContext('2d');
function lvFit(text, width, font) {
  lvCanvas.font = font;
  text = String(text ?? '');
  if (lvCanvas.measureText(text).width <= width) return text;
  let lo = 0, hi = text.length;
  while (lo < hi) { const mid = (lo + hi + 1) >> 1; if (lvCanvas.measureText(text.slice(0, mid) + '…').width <= width) lo = mid; else hi = mid - 1; }
  return text.slice(0, Math.max(1, lo)) + '…';
}
const LV_FONT = '600 13.5px "Segoe UI Variable Text", "Segoe UI", sans-serif';
const LV_FONT_S = '11.5px "Segoe UI Variable Text", "Segoe UI", sans-serif';
const LV_FONT_T = '700 15px "Segoe UI Variable Text", "Segoe UI", sans-serif';

// ---------------------------------------------------------------- scenes (pure layout)

function lvOverview(D) {
  const rows = LV_ROWS.filter(t => [...D.cells.values()].some(c => c.tier === t) &&
    (t !== 'tests' || LV.showTests) && (t !== 'docs' || LV.showDocs));
  const colIds = [...D.cols, LV_REST, LV_OTHER].filter(c => rows.some(t => D.cells.has(t + '|' + c)));
  const bw = 156, bh = 60, gx = 14;
  // Tiers as horizontal bands (functions across) or, when that would be smaller on this screen, as vertical bands
  // (functions down): the orientation that gives the larger, readable picture wins.
  const host = $('#lv'), aw = Math.max(400, (host?.clientWidth || 1300) - 60), ah = Math.max(300, (host?.clientHeight || 900) - 110);
  const labelW = 190, band = 108, top = 8;
  const hW = labelW + colIds.length * (bw + gx) + 10, hH = top + rows.length * band + 10;
  const colW = bw + 36, head = 78, gy = 12;
  const vW = rows.length * (colW + 12) + 10, vH = head + colIds.length * (bh + gy) + 16;
  const vertical = Math.min(aw / vW, ah / vH) > 1.15 * Math.min(aw / hW, ah / hH);
  const blocks = [], bands = [];
  rows.forEach((t, r) => {
    const name = lvTierName(t), sub = D.tiers.get(t)?.description || '';
    bands.push(vertical ? { tier: t, x: r * (colW + 12), y: 0, w: colW, h: vH, name, sub, vertical }
      : { tier: t, x: 0, y: top + r * band, w: hW, h: band - 8, name, sub });
    colIds.forEach((c, i) => {
      const cell = D.cells.get(t + '|' + c);
      if (!cell) return;
      const x = vertical ? r * (colW + 12) + (colW - bw) / 2 : labelW + i * (bw + gx);
      const y = vertical ? head + i * (bh + gy) : top + r * band + (band - 8 - bh) / 2;
      blocks.push({ id: cell.key, kind: 'cell', tier: t, x, y, w: bw, h: bh,
        name: lvColName(c), meta: `${cell.files.length} ${plural(cell.files.length, 'файл', 'файла', 'файлов')}`,
        guess: cell.guesses * 2 > cell.files.length, tip: `${lvTierName(t)} · ${lvColName(c)}` });
    });
  });
  const pos = new Map(blocks.map(b => [b.id, b]));
  const links = D.cellLinks.filter(l => pos.has(l.a) && pos.has(l.b)).map(l => ({ ...l, from: pos.get(l.a), to: pos.get(l.b) }));
  return { kind: 'overview', vertical, W: vertical ? vW : hW, H: vertical ? vH : hH, bands, blocks, links, top: LV_TOPLINKS, labelW: vertical ? colW : labelW };
}

// Inside a block: its items in the middle, the partners that use them on the left, the ones they use on the right.
function lvInside(D, focus) {
  const M = S.model;
  let items, files, title, leaf = false, fpath = null;
  const focusKind = focus.node ? M.node(focus.node)?.kind : 'cell';
  if (focus.cell) {
    const cell = D.cells.get(focus.cell);
    items = [...(cell?.files || [])]; files = items; title = `${lvTierName(cell?.tier)} · ${lvColName(cell?.col)}`;
  } else {
    const n = M.node(focus.node), file = focusKind === 'file' ? focus.node : focus.file || lvFileOf(focus.node);
    // a partial class lives in many files: inside a file show only what is written in that file
    fpath = M.node(file)?.path || null;
    items = focusKind === 'file' ? [...(D.inFile.get(file) || [])]
      : M.kids(focus.node).filter(k => ['type', 'member'].includes(M.node(k).kind) && (!fpath || M.node(k).path === fpath));
    if (!items.length) { items = [focus.node]; leaf = true; }
    files = [file].filter(Boolean); title = n?.name || focus.node;
  }
  const itemSet = new Set(items);
  const ownerItem = id => {
    if (focus.cell) { const f = lvFileOf(id); return itemSet.has(f) ? f : null; }
    if (fpath && M.node(id)?.path !== fpath) return null;
    for (let x = id; x; x = M.node(x)?._parent) if (itemSet.has(x)) return x;
    return null;
  };
  // partners: files at the file levels; inside a class the other end's class (its file when it is this same class)
  const hiddenTier = f => { const t = D.assign.get(f)?.tier; return t === 'tests' && !LV.showTests || t === 'docs' && !LV.showDocs; };
  const partnerOf0 = id => {
    if (focusKind === 'cell' || focusKind === 'file') return lvFileOf(id);
    if (focusKind === 'member') return id;
    for (let x = id; x; x = M.node(x)?._parent) {
      const n = M.node(x);
      if (n?.kind === 'type') return x === focus.node ? lvFileOf(id) : x;
      if (n?.kind === 'file') return x;
    }
    return lvFileOf(id);
  };
  const partnerOf = id => { const p = partnerOf0(id); return p && hiddenTier(lvFileOf(p)) ? null : p; };
  const inn = new Map(), out = new Map(), inner = new Map(), weight = new Map();
  const add = (map, k, w) => map.set(k, (map.get(k) || 0) + w);
  const seen = new Set();
  for (const f of files) for (const e of D.byFile.get(f) || []) {
    if (seen.has(e)) continue; seen.add(e);
    const a = ownerItem(e.source), b = ownerItem(e.target), w = e.weight || 1;
    if (a && b) { if (a !== b) { add(inner, a + '\n' + b, w); add(weight, a, w); add(weight, b, w); } continue; }
    if (a) { const p = partnerOf(e.target); if (p && !itemSet.has(p)) { add(out, a + '\n' + p, w); add(weight, a, w); } }
    else if (b) { const p = partnerOf(e.source); if (p && !itemSet.has(p)) { add(inn, p + '\n' + b, w); add(weight, b, w); } }
  }
  const sumBy = (map, side) => { const r = new Map(); for (const [k, w] of map) { const p = k.split('\n')[side]; r.set(p, (r.get(p) || 0) + w); } return [...r].sort((x, y) => y[1] - x[1]); };
  const callers = sumBy(inn, 0), callees = sumBy(out, 1);
  items.sort((x, y) => (weight.get(y) || 0) - (weight.get(x) || 0) || M.node(x).name.localeCompare(M.node(y).name));

  const bw = 240, bh = 46, gy = 10, pw = 230, gap = 150;
  // as many item columns as make the whole picture largest on this screen (many functions: taller columns)
  const host = $('#lv'), aw = Math.max(400, (host?.clientWidth || 1300) - 60), ah = Math.max(300, (host?.clientHeight || 900) - 110);
  const sideRows = Math.min(LV_PARTNERS, Math.max(callers.length, callees.length));
  let nCols = 1, best = 0;
  for (let c = 1; c <= Math.max(1, Math.ceil(items.length / 4)) && c <= 12; c++) {
    const rowsN = Math.max(Math.ceil(items.length / c), sideRows, 1);
    const k = Math.min(aw / (2 * (pw + gap) + c * bw + (c - 1) * 16), ah / (rowsN * (bh + gy) + 30));
    if (k > best * 1.02) { best = k; nCols = c; }
  }
  const perCol = Math.ceil(items.length / nCols);
  const midW = nCols * bw + (nCols - 1) * 16;
  const leftX = 0, midX = pw + gap, rightX = midX + midW + gap;
  const blocks = [];
  const nodeBlock = (id, x, y, w, extra) => {
    const n = M.node(id), f = lvFileOf(id), a = D.assign.get(f);
    const tier = a?.tier || 'unclassified';
    const here = fpath && n.kind === 'type' && itemSet.has(id) ? fpath : null;
    const kids = n.kind === 'file' ? (D.inFile.get(id)?.size || 0)
      : M.kids(id).filter(k => ['type', 'member'].includes(M.node(k).kind) && (!here || M.node(k).path === here)).length;
    return { id, kind: 'node', tier, x, y, w, h: bh, name: lvNodeName(n), guess: a && (a.decision === 'guess' || a.decision === 'unknown'),
      meta: extra ?? (n.kind === 'file' ? `${lvTierName(tier)}${kids ? ` · ${kids} ${plural(kids, 'класс', 'класса', 'классов')}` : ''}`
        : n.kind === 'type' ? `класс · ${kids} ${plural(kids, 'функция', 'функции', 'функций')}` : (SUB_KIND_RU[n.subKind] || n.subKind || '')),
      tip: n.path ? `${n.name} — ${n.path}${n.line ? ':' + n.line : ''}` : n.name, drill: !leaf && (kids > 0 || n.kind === 'file') };
  };
  items.forEach((id, i) => blocks.push({ ...nodeBlock(id, midX + Math.floor(i / perCol) * (bw + 16), (i % perCol) * (bh + gy), bw), side: 'mid' }));
  const partnerBlock = (id, w, i, x, side) => {
    const n = M.node(id), f = lvFileOf(id), cell = D.cells.get(D.cellOf.get(f));
    const where = n.kind === 'file' ? (cell ? `${lvTierName(cell.tier)} · ${lvColName(cell.col)}` : '') : (M.node(f)?.name || '');
    return { ...nodeBlock(id, x, i * (bh + gy), pw, where), side, w: pw, weight: w };
  };
  callers.slice(0, LV_PARTNERS).forEach(([id, w], i) => blocks.push(partnerBlock(id, w, i, leftX, 'in')));
  callees.slice(0, LV_PARTNERS).forEach(([id, w], i) => blocks.push(partnerBlock(id, w, i, rightX, 'out')));
  const H = Math.max(perCol, Math.min(LV_PARTNERS, callers.length), Math.min(LV_PARTNERS, callees.length), 1) * (bh + gy) + 30;
  const pos = new Map(blocks.map(b => [b.side + b.id, b]));
  const links = [];
  for (const [k, w] of inn) { const [p, it] = k.split('\n'); if (pos.has('in' + p)) links.push({ a: p, b: it, w, from: pos.get('in' + p), to: pos.get('mid' + it) }); }
  for (const [k, w] of out) { const [it, p] = k.split('\n'); if (pos.has('out' + p)) links.push({ a: it, b: p, w, from: pos.get('mid' + it), to: pos.get('out' + p) }); }
  for (const [k, w] of inner) { const [a, b] = k.split('\n'); links.push({ a, b, w, from: pos.get('mid' + a), to: pos.get('mid' + b), inner: true }); }
  links.sort((x, y) => y.w - x.w);
  return { kind: 'inside', W: rightX + pw + 10, H, blocks, links, top: 400, title, focus,
    more: { in: Math.max(0, callers.length - LV_PARTNERS), out: Math.max(0, callees.length - LV_PARTNERS) },
    heads: [{ x: leftX, text: callers.length ? '← Его используют' : '' }, { x: midX, text: 'Внутри' }, { x: rightX, text: callees.length ? 'Он использует →' : '' }],
    counts: { in: callers.length, out: callees.length, items: items.length } };
}

// ---------------------------------------------------------------- drawing

const SVGNS = 'http://www.w3.org/2000/svg';
function lvEl(tag, attrs, parent) {
  const e = document.createElementNS(SVGNS, tag);
  for (const k in attrs) if (attrs[k] != null) e.setAttribute(k, attrs[k]);
  if (parent) parent.appendChild(e);
  return e;
}

function lvPath(l) {
  const a = l.from, b = l.to;
  if (l.inner) { // an arc on the left side of the middle column
    const ax = a.x, ay = a.y + a.h / 2, by = b.y + b.h / 2, dx = 24 + Math.min(80, Math.abs(by - ay) * 0.25);
    return { d: `M${ax},${ay} C${ax - dx},${ay} ${ax - dx},${by} ${b.x},${by}`, ex: b.x, ey: by, dirx: 1, diry: 0 };
  }
  if (LV.scene.vertical) {
    if (a.x === b.x) { // same tier column: a loop on the right side
      const x = a.x + a.w, ay = a.y + a.h / 2, by = b.y + b.h / 2, dx = 16 + Math.abs(by - ay) * 0.06;
      return { d: `M${x},${ay} C${x + dx},${ay} ${x + dx},${by} ${x + 2},${by}`, ex: x + 2, ey: by, dirx: -1, diry: 0 };
    }
    const right = b.x > a.x, ax = right ? a.x + a.w : a.x, bx = right ? b.x - 2 : b.x + b.w + 2;
    const ay = a.y + a.h / 2, by = b.y + b.h / 2, m = (ax + bx) / 2;
    return { d: `M${ax},${ay} C${m},${ay} ${m},${by} ${bx},${by}`, ex: bx, ey: by, dirx: right ? 1 : -1, diry: 0 };
  }
  if (Math.abs(a.y - b.y) < 2 && a.y === b.y && LV.scene.kind === 'overview') { // same band: a loop under the blocks
    const ax = a.x + a.w / 2, bx = b.x + b.w / 2, y = a.y + a.h, dy = 22 + Math.abs(ax - bx) * 0.08;
    return { d: `M${ax},${y} C${ax},${y + dy} ${bx},${y + dy} ${bx},${y + 2}`, ex: bx, ey: y + 2, dirx: 0, diry: -1 };
  }
  if (LV.scene.kind === 'inside') { // left → right
    const ax = a.x + a.w, ay = a.y + a.h / 2, bx = b.x, by = b.y + b.h / 2, m = (ax + bx) / 2;
    return { d: `M${ax},${ay} C${m},${ay} ${m},${by} ${bx - 2},${by}`, ex: bx - 2, ey: by, dirx: 1, diry: 0 };
  }
  const down = b.y > a.y, ax = a.x + a.w / 2, bx = b.x + b.w / 2;
  const ay = down ? a.y + a.h : a.y, by = down ? b.y - 2 : b.y + b.h + 2, m = (ay + by) / 2;
  return { d: `M${ax},${ay} C${ax},${m} ${bx},${m} ${bx},${by}`, ex: bx, ey: by, dirx: 0, diry: down ? 1 : -1 };
}
const lvHead = (p, s) => { // an arrow tip along the end direction
  const L = 7 + s, Wd = 4 + s / 2, bx = p.ex - p.dirx * L, by = p.ey - p.diry * L, nx = -p.diry, ny = p.dirx;
  return `M${p.ex},${p.ey} L${bx + nx * Wd},${by + ny * Wd} L${bx - nx * Wd},${by - ny * Wd} z`;
};

function lvDraw() {
  const host = $('#lv'), sc = LV.scene;
  host.innerHTML = '';
  LV.hover = null; // the hovered block was just removed with the old picture: no mouseleave will come
  const svg = lvEl('svg', {}, host);
  const g = lvEl('g', { id: 'lvg' }, svg);
  if (!sc) return;
  if (sc.empty) { lvEl('text', { x: 40, y: 80, class: 'empty' }, g).textContent = sc.empty; return; }
  if (sc.custom) { sc.blocks = []; sc.links = []; sc.custom(g); lvApply(); return; }
  for (const b of sc.bands || []) {
    const bg = lvEl('g', { class: 'band' }, g);
    lvEl('rect', { x: b.x, y: b.y, width: b.w, height: b.h, rx: 14, style: `fill:${lvColor(b.tier)};fill-opacity:.07;stroke:${lvColor(b.tier)};stroke-opacity:.35` }, bg);
    lvEl('text', { x: b.x + 16, y: b.y + 30, class: 't', style: `fill:${lvColor(b.tier)}` }, bg).textContent = lvFit(b.name, sc.labelW - 26, LV_FONT_T);
    // the description in two lines at most, each cut to the label column
    const words = b.sub.split(' '); let l1 = '', i = 0;
    lvCanvas.font = LV_FONT_S;
    while (i < words.length && lvCanvas.measureText((l1 + ' ' + words[i]).trim()).width <= sc.labelW - 26) l1 = (l1 + ' ' + words[i++]).trim();
    lvEl('text', { x: b.x + 16, y: b.y + 50, class: 's' }, bg).textContent = l1;
    if (i < words.length) lvEl('text', { x: b.x + 16, y: b.y + 66, class: 's' }, bg).textContent = lvFit(words.slice(i).join(' '), sc.labelW - 26, LV_FONT_S);
  }
  for (const h of sc.heads || []) if (h.text) lvEl('text', { x: h.x, y: -14, class: 'col' }, g).textContent = h.text;
  const maxW = Math.max(1, ...sc.links.map(l => l.w));
  const lg = lvEl('g', {}, g);
  sc.linkEls = sc.links.map((l, i) => {
    const p = lvPath(l), s = 1 + 6 * Math.sqrt(l.w / maxW);
    const path = lvEl('path', { d: p.d, class: 'ln', 'stroke-width': s }, lg);
    const head = lvEl('path', { d: lvHead(p, s), class: 'ah' }, lg);
    path.addEventListener('mousemove', e => lvTip(e, `<b>${esc(lvLabel(l.from))} → ${esc(lvLabel(l.to))}</b><span>${l.w} ${plural(l.w, 'обращение', 'обращения', 'обращений')} в коде</span>`));
    path.addEventListener('mouseleave', lvTipHide);
    return { l, path, head, top: i < sc.top && !l.inner };
  });
  const bg = lvEl('g', {}, g);
  sc.blockEls = new Map();
  for (const b of sc.blocks) {
    const el = lvEl('g', { class: 'blk' + (b.guess ? ' guess' : ''), transform: `translate(${b.x},${b.y})` }, bg);
    lvEl('rect', { class: 'b', width: b.w, height: b.h, rx: 10, style: `stroke:${lvColor(b.tier)}` }, el);
    lvEl('rect', { x: 8, y: 8, width: 5, height: b.h - 16, rx: 2, style: `fill:${lvColor(b.tier)}` }, el);
    lvEl('text', { x: 21, y: b.h > 50 ? 25 : 20, class: 'n' }, el).textContent = lvFit(b.name, b.w - 30, LV_FONT);
    lvEl('text', { x: 21, y: b.h > 50 ? 44 : 36, class: 'm' }, el).textContent = lvFit(b.meta + (b.weight ? ` · ${b.weight}` : ''), b.w - 30, LV_FONT_S);
    el.addEventListener('mouseenter', e => { LV.hover = b; lvHighlight(); });
    el.addEventListener('mousemove', e => lvTip(e, `<b>${esc(b.tip)}</b><span>${esc(b.meta)}${b.guess ? ' · ярус — предположение' : ''}<br>${b.kind === 'cell' || b.drill ? 'нажмите — заглянуть внутрь' : 'нажмите — подробности'}</span>`));
    el.addEventListener('mouseleave', () => { LV.hover = null; lvHighlight(); lvTipHide(); });
    el.addEventListener('click', e => { e.stopPropagation(); lvTipHide(); lvOpen(b); });
    sc.blockEls.set(b.side ? b.side + b.id : b.id, el);
  }
  if (sc.more) {
    const tail = (n, x) => { if (n) lvEl('text', { x, y: LV_PARTNERS * 56 + 14, class: 's' }, g).textContent = `и ещё ${n} ${plural(n, 'блок', 'блока', 'блоков')} со слабыми связями`; };
    tail(sc.more.in, 0);
    tail(sc.more.out, sc.blocks.find(b => b.side === 'out')?.x ?? 0);
  }
  lvHighlight();
  lvApply();
}

const lvLabel = b => b.kind === 'cell' ? b.tip : b.name;

function lvHighlight() {
  const sc = LV.scene; if (!sc?.linkEls) return;
  const h = LV.hover;
  const key = b => b.side ? b.side + b.id : b.id;
  const near = new Set();
  for (const x of sc.linkEls) {
    const on = h && (x.l.from === h || x.l.to === h);
    const show = on || (!h && x.top) || (sc.kind === 'inside' && !h && !x.l.inner);
    x.path.style.display = x.head.style.display = show ? '' : 'none';
    x.path.classList.toggle('hl', !!on); x.head.classList.toggle('hl', !!on);
    x.path.style.strokeOpacity = on ? 0.9 : 0.55;
    if (on) { near.add(key(x.l.from)); near.add(key(x.l.to)); }
  }
  for (const [k, el] of sc.blockEls) el.classList.toggle('dim', !!h && !near.has(k) && k !== key(h));
}

// ---------------------------------------------------------------- zoom and pan

function lvApply() { const g = $('#lvg'); if (g) g.setAttribute('transform', `translate(${LV.t.x},${LV.t.y}) scale(${LV.t.k})`); }
function lvFitView() {
  const sc = LV.scene, host = $('#lv'); if (!sc || !host) return;
  const pad = 30, top = 64, W = host.clientWidth, H = host.clientHeight;
  const k = Math.max(0.25, Math.min(1.25, (W - 2 * pad) / sc.W, (H - top - pad) / Math.max(1, sc.H + 20)));
  LV.t = { k, x: (W - sc.W * k) / 2, y: top + 20 * k + Math.max(0, (H - top - pad - (sc.H + 20) * k) / 2) };
  lvApply();
}
(function lvInput() {
  const host = $('#lv');
  host.addEventListener('wheel', e => {
    e.preventDefault();
    const r = host.getBoundingClientRect(), px = e.clientX - r.left, py = e.clientY - r.top;
    const k = Math.max(0.2, Math.min(4, LV.t.k * Math.exp(-e.deltaY * 0.0015)));
    LV.t = { k, x: px - (px - LV.t.x) * k / LV.t.k, y: py - (py - LV.t.y) * k / LV.t.k };
    lvApply();
  }, { passive: false });
  let drag = null;
  host.addEventListener('pointerdown', e => {
    if (e.button !== 0 || e.target.closest('.blk')) return;
    // the pointer is captured only once it really moves: a plain click must still reach the shape under it
    drag = { x: e.clientX, y: e.clientY, t: { ...LV.t }, moved: false, empty: e.target.tagName === 'svg', id: e.pointerId };
  });
  host.addEventListener('pointermove', e => {
    if (!drag) return;
    const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
    if (!drag.moved && Math.abs(dx) + Math.abs(dy) > 3) { drag.moved = true; host.classList.add('dragging'); host.setPointerCapture(drag.id); }
    if (!drag.moved) return;
    LV.t = { ...drag.t, x: drag.t.x + dx, y: drag.t.y + dy }; lvApply();
  });
  host.addEventListener('pointerup', e => {
    const d = drag; drag = null; host.classList.remove('dragging');
    // a click on empty space steps back out of a block
    if (d && !d.moved && d.empty && LV.stack.length) lvBack();
  });
})();

// ---------------------------------------------------------------- navigation

function lvRender({ keepView = false } = {}) {
  const D = lvData();
  const top = LV.stack[LV.stack.length - 1];
  if (!D) LV.scene = { empty: 'В этой схеме ещё нет ярусов и функций — нажмите ⟳ «Пересканировать».', blocks: [], links: [] };
  else {
    if (top?.cell && !D.cells.has(top.cell) || top?.node && !S.model.byId.has(top.node)) LV.stack = [];
    const t = LV.stack[LV.stack.length - 1];
    // a file or a class is always shown with its links; a block too in the views that have no own inner level
    const own = SV_BUILD[LV.mode] && !t?.node && !(t?.cell && ['stars', 'flows'].includes(LV.mode));
    LV.scene = own ? SV_BUILD[LV.mode](D) : t?.node || t?.cell ? lvInside(D, t) : lvOverview(D);
  }
  lvDraw();
  if (!keepView) lvFitView();
  lvBar();
  lvDetails();
}
function lvOpen(b) {
  if (b.kind === 'cell') LV.stack = [{ cell: b.id }];
  else if (b.side === 'mid' && !b.drill) { LV.sel = b.id; lvDetails(b.id); return; }
  else if (b.side === 'mid') { const cur = LV.stack[LV.stack.length - 1]; LV.stack.push({ node: b.id, file: cur?.file || (cur?.node && S.model.node(cur.node)?.kind === 'file' ? cur.node : undefined) }); }
  else LV.stack = lvPathTo(b.id); // a partner: jump there, with its own path back
  LV.sel = null;
  lvRender();
}
function lvPathTo(id) {
  const M = S.model, D = lvData(), f = lvFileOf(id), path = [];
  if (f && D.cellOf.has(f)) path.push({ cell: D.cellOf.get(f) });
  if (f && f !== id) path.push({ node: f });
  // classes between the file and the node, seen from this file
  const chain = M.ancestors(id).reverse().filter(x => M.node(x)?.kind === 'type');
  for (const x of chain) path.push({ node: x, file: f });
  if (M.node(id)?.kind !== 'member') path.push({ node: id, file: f }); else LV.sel = id;
  return path;
}
function lvBack(to = LV.stack.length - 1) { LV.stack = LV.stack.slice(0, Math.max(0, to)); LV.sel = null; lvRender(); }

function lvBar() {
  const bar = $('#lvBar'), D = LV.data;
  const crumbs = [{ text: 'Вся программа', i: 0 }];
  LV.stack.forEach((s, i) => crumbs.push({ text: s.tier ? lvTierName(s.tier) : s.cell ? `${lvTierName(D.cells.get(s.cell)?.tier)} · ${lvColName(D.cells.get(s.cell)?.col)}` : S.model.node(s.node)?.name, i: i + 1 }));
  const cur = LV.stack[LV.stack.length - 1];
  bar.innerHTML = `<div class="seg small" title="Как показать">${SV_MODES.map(([m, n]) => `<button data-lv-mode="${m}" class="${LV.mode === m ? 'active' : ''}">${n}</button>`).join('')}</div>` + crumbs.map((c, i) => `${i ? '<i>›</i>' : ''}<span class="crumb${i === crumbs.length - 1 ? ' cur' : ''}" data-lv-back="${c.i}" title="${esc(c.text)}">${esc(c.text)}</span>`).join('') +
    '<span class="sp"></span>' +
    (cur?.node ? '<button data-lv-map title="Тот же блок на обычной «Схеме»">На обычной схеме</button>' : '') +
    (D ? `<span class="chip${LV.showTests ? '' : ' off'}" data-lv-toggle="tests">Проверки</span><span class="chip${LV.showDocs ? '' : ' off'}" data-lv-toggle="docs">Документы</span>` : '');
}
$('#lvBar').addEventListener('click', e => {
  const md = e.target.closest('[data-lv-mode]');
  if (md) { LV.mode = md.dataset.lvMode; if (LV.stack[LV.stack.length - 1]?.node) LV.stack = []; lvRender(); return; }
  const b = e.target.closest('[data-lv-back]');
  if (b && !b.classList.contains('cur')) { lvBack(+b.dataset.lvBack); return; }
  const tg = e.target.closest('[data-lv-toggle]');
  if (tg) { if (tg.dataset.lvToggle === 'tests') LV.showTests = !LV.showTests; else LV.showDocs = !LV.showDocs; lvRender(); return; }
  if (e.target.closest('[data-lv-map]')) { const cur = LV.stack[LV.stack.length - 1]; if (cur?.node) reveal(LV.sel || cur.node); }
});

// ---------------------------------------------------------------- tooltip

const lvTipEl = Object.assign(document.createElement('div'), { id: 'lvTip', className: 'hidden' });
document.body.appendChild(lvTipEl);
function lvTip(e, html) {
  lvTipEl.innerHTML = html; lvTipEl.classList.remove('hidden');
  const r = lvTipEl.getBoundingClientRect();
  lvTipEl.style.left = Math.min(e.clientX + 14, innerWidth - r.width - 8) + 'px';
  lvTipEl.style.top = Math.min(e.clientY + 14, innerHeight - r.height - 8) + 'px';
}
function lvTipHide() { lvTipEl.classList.add('hidden'); }

// ---------------------------------------------------------------- details and corrections

function lvList(rows) {
  return `<div class="lvlist">${rows.map(r => `<div class="r" ${r.attr}><span class="dot" style="background:${lvColor(r.tier)}"></span><span class="nm" title="${esc(r.title || r.name)}">${esc(r.name)}</span><span class="n">${r.n ?? ''}</span></div>`).join('')}</div>`;
}
function lvTierSelect(current) {
  return `<select data-lv-tier>${LV_ROWS.filter(t => t !== 'mixed').map(t => `<option value="${t}"${t === current ? ' selected' : ''}>${esc(lvTierName(t))}</option>`).join('')}</select>`;
}

function lvDetails(selId) {
  if (S.view !== 'links') return;
  const el = $('#details'), D = LV.data, M = S.model;
  if (!D) { el.innerHTML = `<div class="card"><h3>Схема связей</h3><p class="why">Нужен новый скан: нажмите ⟳.</p></div>`; return; }
  const cur = LV.stack[LV.stack.length - 1];
  const sc = LV.scene;
  if (!cur && LV.mode === 'time') return svTimeDetails(D, el);
  if (!cur && LV.mode === 'path') return svPathDetails(D, el);
  if (!cur) {
    const v = D.v, files = v.assignments.length, sure = v.assignments.filter(a => a.decision === 'automatic' || a.decision === 'override').length;
    el.innerHTML = `<div class="card head"><span class="kind">вид</span><h2>${esc(SV_MODES.find(m => m[0] === LV.mode)?.[1] || 'Связи')}</h2>
      <div class="what prose"><p>${SV_HELP[LV.mode] || SV_HELP.links}</p>
      <p>Колесо — приближение, перетаскивание — сдвиг, F — показать всё, Esc — шаг назад.</p>
      <p>Блок с пунктирной рамкой: ярус большинства его файлов угадан по именам папок, а не по содержимому. Исправить — внутри блока.</p></div></div>
      <div class="card"><h3>Что показано</h3><div class="facts">${fact(files, 'файлов с ярусом')}${fact(sure, 'из них уверенно')}${fact(v.features.length, 'функций найдено')}${fact(D.cellLinks.length, 'связей между блоками')}</div>
      ${LV.mode === 'links' ? `<p class="why">Видны ${Math.min(LV_TOPLINKS, D.cellLinks.length)} самых сильных связей; остальные — при наведении на блок.</p>` : ''}</div>
      ${v.staleOverrides?.length ? `<div class="card"><h3>Правки без цели<span class="c">${v.staleOverrides.length}</span></h3><p class="why">Эти ручные правки больше ни к чему не относятся (файл переименован или удалён) и не применяются: ${v.staleOverrides.slice(0, 6).map(esc).join(', ')}</p></div>` : ''}`;
    return;
  }
  if (cur.tier && !selId) {
    const cells = [...D.cells.values()].filter(c => c.tier === cur.tier);
    el.innerHTML = `<div class="card head" style="--hd:${lvColor(cur.tier)}"><span class="kind">ярус</span><h2>${esc(lvTierName(cur.tier))}</h2>
      <div class="what prose"><p>${esc(D.tiers.get(cur.tier)?.description || '')}.</p></div></div>
      <div class="card"><h3>Функции на ярусе<span class="c">${cells.length}</span></h3>${lvList(cells.sort((a, b) => b.files.length - a.files.length).map(c => ({ tier: c.tier, name: lvColName(c.col), n: c.files.length, attr: `data-lv-cell="${esc(c.key)}"` })))}</div>`;
    return;
  }
  if (cur.cell && !selId) {
    const cell = D.cells.get(cur.cell), feat = D.feats.get(cell.col);
    const outBy = new Map(), inBy = new Map();
    for (const l of D.cellLinks) { if (l.a === cell.key) outBy.set(l.b, l.w); if (l.b === cell.key) inBy.set(l.a, l.w); }
    const cellRows = m => [...m].slice(0, 8).map(([k, w]) => { const c = D.cells.get(k); return { tier: c.tier, name: `${lvTierName(c.tier)} · ${lvColName(c.col)}`, n: w, attr: `data-lv-cell="${esc(k)}"` }; });
    const entries = feat ? (D.entries.get(feat.id) || []).filter(id => M.byId.has(id)) : [];
    el.innerHTML = `<div class="card head" style="--hd:${lvColor(cell.tier)}"><span class="kind">блок схемы</span><h2>${esc(lvColName(cell.col))}</h2>
      <div class="what prose"><p><b>${esc(lvTierName(cell.tier))}</b> — ${esc(D.tiers.get(cell.tier)?.description || '')}.</p>
      ${feat ? `<p>Функция найдена по словам в именах файлов: ${feat.terms.map(t => `<code>${esc(t)}</code>`).join(', ')}. Она есть на ярусах: ${feat.tierIds.map(lvTierName).map(esc).join(', ')}.</p>`
        : cell.col === LV_OTHER ? '<p>Файлы, которые не относятся ни к одной найденной функции.</p>' : '<p>Файлы небольших функций, которым не хватило отдельной колонки.</p>'}
      ${cell.guesses ? `<p>Ярус ${cell.guesses} из ${cell.files.length} файлов — предположение.</p>` : ''}</div></div>
      ${entries.length ? `<div class="card"><h3>Кнопки, с которых она начинается<span class="c">${entries.length}</span></h3>${lvList(entries.slice(0, 10).map(id => ({ tier: 'frontend', name: M.node(id).name, title: M.node(id).path, attr: `data-lv-node="${esc(id)}"` })))}</div>` : ''}
      <div class="card"><h3>Использует →<span class="c">${outBy.size}</span></h3>${cellRows(outBy) .length ? lvList(cellRows(outBy)) : '<p class="why">—</p>'}</div>
      <div class="card"><h3>← Его используют<span class="c">${inBy.size}</span></h3>${cellRows(inBy).length ? lvList(cellRows(inBy)) : '<p class="why">—</p>'}</div>
      <div class="card lvedit"><h3>Исправить</h3>
        <label>Ярус всех ${cell.files.length} файлов блока ${lvTierSelect(cell.tier)}</label>
        <div class="row"><button data-lv-save-tier="cell">Сохранить ярус</button></div>
        ${feat ? `<label>Название функции <input data-lv-name value="${esc(feat.name)}"></label>
        <label>Объединить с функцией <select data-lv-merge><option value="">—</option>${D.v.features.filter(f => f.id !== feat.id).map(f => `<option value="${esc(f.id)}">${esc(f.name)}</option>`).join('')}</select></label>
        <div class="row"><button data-lv-save-feature="${esc(feat.id)}">Сохранить</button><button data-lv-hide-feature="${esc(feat.id)}">Это не функция — скрыть</button></div>` : ''}
        <p class="why">Правки хранятся у projmap на этом компьютере, в сам проект ничего не пишется. После сохранения схема пересчитается.</p></div>`;
    return;
  }
  const id = selId || cur.node, n = M.node(id);
  if (!n) { el.innerHTML = ''; return; }
  const f = lvFileOf(id), a = D.assign.get(f), feats = (D.fileFeats.get(f) || []).map(x => D.feats.get(x)?.name).filter(Boolean);
  const ins = sc.kind === 'inside' ? sc.blocks.filter(b => b.side === 'in') : [], outs = sc.kind === 'inside' ? sc.blocks.filter(b => b.side === 'out') : [];
  el.innerHTML = `<div class="card head" style="--hd:${lvColor(a?.tier)}"><span class="kind">${esc(KIND_RU[n.kind] || n.kind)}</span><h2>${esc(n.name)}</h2>
    ${n.path ? `<div class="path" data-path="${esc(n.path)}" data-line="${n.line || 0}" title="Открыть в редакторе">${esc(n.path)}${n.line ? ':' + n.line : ''}</div>` : ''}
    ${n.summary ? `<div class="what prose">${mdLite(n.summary)}</div>` : ''}
    <div class="actions"><button data-lv-act="view" class="main">Открыть файл здесь</button><button data-lv-act="map">На обычной схеме</button>${n.path ? '<button data-lv-act="open">В редакторе</button>' : ''}</div></div>
    ${a ? `<div class="card"><h3>Ярус · ${esc(LV_DECISION_RU[a.decision] || a.decision)}</h3>
      <div class="why"><b style="color:${lvColor(a.tier)}">${esc(lvTierName(a.tier))}</b>${a.alternatives?.length ? ` (или ${a.alternatives.map(lvTierName).map(esc).join(', ')})` : ''} — ${esc(a.reason || '')}.</div>
      <ul class="why">${a.evidence.slice(0, 6).map(e => `<li>${esc(e.detail)} → ${esc(lvTierName(e.tier))} <span class="muted">(${esc(LV_FAMILY_RU[e.family] || e.family)}, ${Math.round(e.score * 100)}%)</span></li>`).join('')}</ul>
      ${feats.length ? `<p class="why">Функции: ${feats.map(esc).join(', ')}.</p>` : ''}
      <div class="lvedit"><label>Ярус этого файла ${lvTierSelect(a.tier)}</label><div class="row"><button data-lv-save-tier="file">Сохранить ярус</button>${a.decision === 'override' ? '<button data-lv-clear-tier>Вернуть автоматический</button>' : ''}</div></div></div>` : ''}
    ${ins.length ? `<div class="card"><h3>← Его используют<span class="c">${sc.counts.in}</span></h3>${lvList(ins.map(b => ({ tier: b.tier, name: b.name, title: b.tip, n: b.weight, attr: `data-lv-node="${esc(b.id)}"` })))}</div>` : ''}
    ${outs.length ? `<div class="card"><h3>Он использует →<span class="c">${sc.counts.out}</span></h3>${lvList(outs.map(b => ({ tier: b.tier, name: b.name, title: b.tip, n: b.weight, attr: `data-lv-node="${esc(b.id)}"` })))}</div>` : ''}`;
}

function lvSaveOverrides(change) {
  const o = structuredClone(LV.overrides && LV.overridesRoot === S.root ? LV.overrides : {});
  for (const k of ['tiers', 'featureMerges', 'hiddenFeatures', 'addedFeatures', 'members']) o[k] ??= [];
  o.featureNames ??= {};
  change(o);
  LV.overrides = o; LV.overridesRoot = S.root;
  send({ type: 'saveViewsOverrides', overrides: o });
  toast('Правка сохранена — пересчитываю схему…');
}

// capture phase: the details panel's own handler (app.js) must not see these clicks
$('#details').addEventListener('click', e => {
  if (S.view !== 'links') return;
  const t = e.target, M = S.model, D = LV.data;
  const stop = () => { e.stopPropagation(); e.preventDefault(); };
  const cellRow = t.closest('[data-lv-cell]'); if (cellRow) { stop(); LV.stack = [{ cell: cellRow.dataset.lvCell }]; lvRender(); return; }
  const nodeRow = t.closest('[data-lv-node]'); if (nodeRow) { stop(); LV.stack = lvPathTo(nodeRow.dataset.lvNode); lvRender(); return; }
  const cur = LV.stack[LV.stack.length - 1], id = LV.sel || cur?.node;
  const act = t.closest('[data-lv-act]')?.dataset.lvAct;
  if (act) {
    stop();
    const n = M.node(id); if (!n) return;
    if (act === 'view') openFileView(id, n.kind === 'file' ? null : id);
    if (act === 'map') reveal(id);
    if (act === 'open') send({ type: 'open', path: n.path, line: n.line || 0 });
    return;
  }
  const save = t.closest('[data-lv-save-tier]');
  if (save) {
    stop();
    const tier = $('#details [data-lv-tier]').value;
    const files = save.dataset.lvSaveTier === 'cell' ? D.cells.get(cur.cell).files : [lvFileOf(id)];
    lvSaveOverrides(o => { o.tiers = o.tiers.filter(x => !files.includes(x.nodeId)); for (const f of files) o.tiers.push({ nodeId: f, tier, reason: 'назначено вручную' }); });
    return;
  }
  if (t.closest('[data-lv-clear-tier]')) { stop(); const f = lvFileOf(id); lvSaveOverrides(o => { o.tiers = o.tiers.filter(x => x.nodeId !== f); }); return; }
  const sf = t.closest('[data-lv-save-feature]');
  if (sf) {
    stop();
    const fid = sf.dataset.lvSaveFeature, name = $('#details [data-lv-name]').value.trim(), into = $('#details [data-lv-merge]').value;
    lvSaveOverrides(o => {
      if (name && name !== D.feats.get(fid)?.name) o.featureNames[fid] = name;
      if (into) { const m = o.featureMerges.find(x => x.into === into); if (m) m.from.push(fid); else o.featureMerges.push({ into, from: [fid] }); }
    });
    return;
  }
  const hf = t.closest('[data-lv-hide-feature]');
  if (hf) { stop(); lvSaveOverrides(o => { if (!o.hiddenFeatures.includes(hf.dataset.lvHideFeature)) o.hiddenFeatures.push(hf.dataset.lvHideFeature); }); LV.stack = []; }
}, true);

