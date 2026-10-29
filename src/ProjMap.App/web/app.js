// projmap viewer: progressive-disclosure project map over graph.json.
'use strict';


const $ = s => document.querySelector(s);
const $$ = s => [...document.querySelectorAll(s)];
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
// Text from project documents (Markdown) as readable prose: paragraphs, lists, small headings, bold and code.
// Lines wrapped in the source are joined; a blank line, a list item or a heading starts a new block.
function mdLite(text) {
  const inline = s => esc(s)
    .replace(/\[([^\]]+)\]\([^)]*\)/g, '$1')
    .replace(/`([^`]+)`/g, '<code>$1</code>')
    .replace(/\*\*([^*]+)\*\*|__([^_]+)__/g, (_, a, b) => `<b>${a || b}</b>`);
  const out = [];
  let para = [], list = null;
  // a text shorter than about two lines is not stretched: justification would only open wide gaps in it
  const p = s => `<p${s.length < 110 ? ' class="short"' : ''}>${inline(s)}</p>`;
  const flushPara = () => { if (para.length) out.push(p(para.join(' '))); para = []; };
  const flushList = () => { if (list) out.push(`<${list.tag}>${list.items.map(i => `<li>${inline(i)}</li>`).join('')}</${list.tag}>`); list = null; };
  for (const raw of String(text ?? '').replace(/\r/g, '').split('\n')) {
    const line = raw.trim();
    const h = line.match(/^#{1,6}\s+(.*)$/), li = line.match(/^(?:[-*+]|(\d+)[.)])\s+(.*)$/);
    if (!line) { flushPara(); flushList(); }
    else if (h) { flushPara(); flushList(); out.push(`<h4>${inline(h[1])}</h4>`); }
    else if (li) {
      flushPara();
      const tag = li[1] ? 'ol' : 'ul';
      if (list && list.tag !== tag) flushList();
      (list ??= { tag, items: [] }).items.push(li[2]);
    }
    else if (list && /^\s{2,}/.test(raw)) list.items[list.items.length - 1] += ' ' + line; // continuation of an item
    else { flushList(); para.push(line); }
  }
  flushPara(); flushList();
  return out.join('');
}
const fmt = n => n >= 10000 ? (n / 1000).toFixed(0) + 'k' : n >= 1000 ? (n / 1000).toFixed(1) + 'k' : String(n ?? 0);
const plural = (n, one, few, many) => {
  const m10 = n % 10, m100 = n % 100;
  return m10 === 1 && m100 !== 11 ? one : m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14) ? few : many;
};
const ago = iso => {
  if (!iso) return '—';
  const d = (Date.now() - new Date(iso)) / 86400000;
  return d < 1 ? 'сегодня' : d < 2 ? 'вчера' : d < 31 ? `${Math.floor(d)} дн. назад` : d < 365 ? `${Math.floor(d / 30)} мес. назад` : `${(d / 365).toFixed(1)} г. назад`;
};

const CAT_UI = {
  code: { label: 'Кто кем пользуется', color: '#6ea8fe' },
  cochange: { label: 'Правятся вместе', color: '#c38cff' },
  db: { label: 'База данных', color: '#35c6b8' },
};
const STATUS_COLOR = { done: '#4cc38a', active: '#6ea8fe', planned: '#9b8cff', deferred: '#8b95a7', blocked: '#ef5d5d', unknown: '#5d6779' };
const MAX_EDGES = 1600;
const CATEGORIES = [
  ['app', 'Программа', 'основные части самой программы — то, что работает у пользователя'],
  ['data', 'Данные и материалы', 'база данных, содержимое и ресурсы, которые использует программа'],
  ['tools', 'Инструменты и эксперименты', 'вспомогательное для разработки; в саму программу не входит'],
  ['tests', 'Проверки', 'автоматические тесты, которые следят, чтобы ничего не сломалось'],
  ['docs', 'Документы и настройки', 'описания, планы, настройки сборки и ИИ-помощников'],
];

// ---------------------------------------------------------------- state

const S = {
  model: null, root: null, version: 0,
  expanded: new Set(), treeOpen: new Set(),
  selected: null, focus: null,
  view: 'map', heat: null, cats: { code: true, cochange: false, db: true },
  showTests: false, showDocs: false, live: true,
  planFilter: new Set(['active', 'planned', 'blocked', 'deferred', 'unknown']),
  hotSort: 'commits',
  changed: new Map(),
  catClosed: new Set(['tests', 'docs']),
  simplify: true,
  dtab: 'about',
};

function saveState() {
  if (!S.root) return;
  try {
    localStorage.setItem('pm:' + S.root, JSON.stringify({
      expanded: [...S.expanded], treeOpen: [...S.treeOpen], cats: S.cats,
      showTests: S.showTests, showDocs: S.showDocs, planFilter: [...S.planFilter], hotSort: S.hotSort,
    }));
  } catch { }
}
function loadState(root) {
  try {
    const s = JSON.parse(localStorage.getItem('pm:' + root) || 'null');
    if (!s) return false;
    S.expanded = new Set(s.expanded || []); S.treeOpen = new Set(s.treeOpen || []);
    S.cats = { ...S.cats, ...(s.cats || {}) };
    S.showTests = s.showTests ?? false; S.showDocs = s.showDocs ?? false;
    if (s.planFilter) S.planFilter = new Set(s.planFilter);
    S.hotSort = s.hotSort || 'commits';
    return true;
  } catch { return false; }
}

// ---------------------------------------------------------------- host bridge

const host = window.chrome?.webview;
function send(msg) { if (host) host.postMessage(msg); else console.log('[host]', msg); }

function onHost(m) {
  switch (m.type) {
    case 'recent': renderRecent(m.items || []); break;
    case 'start': $('#start').classList.remove('hidden'); break;
    case 'project':
      $('#start').classList.add('hidden');
      S.root = m.root;
      showProgress('Первое сканирование…', 0.02);
      break;
    case 'progress': {
      const stage = { structure: 'структура папок', csharp: 'разбор кода C#', imports: 'импорты JS/TS/Python', git: 'история git',
        roadmap: 'дорожная карта', database: 'база данных', descriptions: 'описания из документации', complete: 'готово' }[m.stage] || m.stage;
      showProgress(`Сканирую проект: ${stage}…`, m.fraction);
      break;
    }
    case 'graph':
      fetch('/graph.json?v=' + m.version).then(r => r.json()).then(g => {
        loadGraph(g, m.root);
        if (!m.cached) hideProgress();
        else showProgress('Показан сохранённый результат · проверяю изменения…', 0.05);
        if (!m.cached && m.ms) toast(`Схема обновлена за ${(m.ms / 1000).toFixed(1)} с`);
        send({ type: 'loaded' }); // lets the host start the background rescan only now
      }).catch(err => { hideProgress(); toast('Не удалось загрузить схему: ' + err.message, 6000); send({ type: 'loaded' }); });
      break;
    case 'changed':
      for (const p of m.paths || []) S.changed.set(p.toLowerCase(), Date.now());
      break;
    case 'error': hideProgress(); toast(m.message, 6000); break;
    case 'unchanged': hideProgress(); break; // rescan found nothing new: keep the picture as it is
    case 'testAction': toast(`(проверка) ${m.action}: ${m.arg}`); break;
  }
}
if (host) host.addEventListener('message', e => onHost(e.data));

// ---------------------------------------------------------------- cytoscape

// theme: light ("paper") by default, dark on request
// theme follows Windows (light/dark app mode) and switches live when it changes
const systemDark = window.matchMedia('(prefers-color-scheme: dark)');
function setTheme(t) { document.documentElement.dataset.theme = t; }
setTheme(systemDark.matches ? 'dark' : 'light');

function buildStyle() {
  const dark = document.documentElement.dataset.theme === 'dark';
  const T = dark
    ? { text: '#eef1f6', soft: '#b9c1ce', test: '#a4adbb', sel: '#ffffff', tbg: '#20252f', tfg: '#ffffff', found: '#7cb1ff', catBg: '#ffffff', catOp: 0.03, catLine: '#4a5466', canvas: '#20252d' }
    : { text: '#1f2430', soft: '#4a5262', test: '#7a8190', sel: '#1f2430', tbg: '#ffffff', tfg: '#1f2430', found: '#2f6fdb', catBg: '#000000', catOp: 0.025, catLine: '#c9c2b2', canvas: '#fbfaf6' };
  return [
    { selector: 'node', style: {
      'shape': 'round-rectangle', 'width': 'data(w)', 'height': 'data(h)',
      'background-color': 'data(fill)', 'border-color': 'data(line)', 'border-width': 1.5,
      // drawing order: frames (z 1..9) < arrows (15) < blocks (20) < highlighted arrows (25)
      'z-index-compare': 'manual', 'z-index': 'data(z)',
      'label': 'data(label)', 'color': T.text, 'font-size': 12, 'font-family': 'Segoe UI Variable Text, Segoe UI, sans-serif',
      'text-valign': 'center', 'text-halign': 'center', 'text-wrap': 'wrap', 'text-max-width': 'data(tw)',
      'line-height': 1.25, 'min-zoomed-font-size': 6,
    } },
    { selector: 'node[svg]', style: {
      'background-image': 'data(svg)', 'background-fit': 'none', 'background-width': '100%', 'background-height': '100%',
      'background-clip': 'node', 'background-image-containment': 'inside', 'background-image-smoothing': 'yes',
    } },
    { selector: 'node[kind="module"], node[kind="group"]', style: { 'font-size': 22, 'font-weight': 600, 'border-width': 3 } },
    { selector: 'node[kind="folder"]', style: { 'font-size': 19, 'border-width': 2 } },
    { selector: 'node[kind="group"]', style: { 'shape': 'round-octagon', 'font-weight': 600 } },
    { selector: 'node[kind="type"]', style: { 'shape': 'round-tag', 'font-size': 10.5 } },
    { selector: 'node[kind="member"]', style: { 'shape': 'ellipse', 'font-size': 9.5, 'color': T.soft } },
    { selector: 'node[kind="table"]', style: { 'shape': 'barrel', 'font-weight': 600 } },
    { selector: 'node[kind="doc"]', style: { 'border-style': 'dashed', 'color': T.soft } },
    { selector: 'node[?closedKids]', style: { 'border-style': 'double', 'border-width': 5 } },
    { selector: 'node[?test]', style: { 'background-opacity': 0.6, 'color': T.test } },
    // an open block: a frame drawn under its contents, caption inside the top edge
    { selector: 'node[?frame]', style: {
      'background-color': 'data(soft)', 'background-opacity': dark ? 0.14 : 0.28, 'border-width': 2, 'border-style': 'solid',
      'text-valign': 'top', 'text-halign': 'center', 'text-margin-y': 40, 'font-weight': 700, 'text-wrap': 'ellipsis',
      'color': 'data(line)', 'shape': 'round-rectangle', 'label': 'data(name)', 'font-size': 26, 'min-zoomed-font-size': 3,
    } },
    { selector: 'node[kind="cat"]', style: {
      'background-color': T.catBg, 'background-opacity': T.catOp, 'border-color': T.catLine, 'border-style': 'dashed', 'border-width': 2,
      'label': 'data(name)', 'text-wrap': 'wrap', 'text-max-width': 640, 'text-valign': 'top', 'text-halign': 'center',
      'text-margin-y': 70, 'font-size': 26, 'font-weight': 600, 'color': T.soft, 'line-height': 1.4, 'min-zoomed-font-size': 3,
    } },
    { selector: 'edge', style: {
      'width': 'data(width)', 'line-color': 'data(color)', 'target-arrow-color': 'data(color)',
      'target-arrow-shape': 'triangle', 'arrow-scale': 1.1, 'curve-style': 'bezier', 'opacity': 'data(op)',
      'target-distance-from-node': 2, 'z-index-compare': 'manual', 'z-index': 15,
    } },
    // layered view: the exact orthogonal route ELK computed (see layeredLayout)
    { selector: 'edge.routed', style: {
      'curve-style': 'segments', 'edge-distances': 'endpoints',
      'segment-weights': 'data(sw)', 'segment-distances': 'data(sd)', 'segment-radius': 10, 'radius-type': 'arc-radius',
      'source-endpoint': 'data(sp)', 'target-endpoint': 'data(tp)', 'target-distance-from-node': 0,
    } },
    // drawn by the arrow layer (edgelayer.js) unless highlighted
    { selector: 'edge.onlayer', style: { 'display': 'none' } },
    { selector: 'edge.onlayer.hl, edge.onlayer.hover', style: { 'display': 'element' } },
    { selector: 'edge:active, edge.hover', style: { 'width': 4, 'opacity': 1, 'z-index': 30, 'label': 'data(lbl)', 'font-size': 12, 'font-weight': 600, 'color': T.tfg, 'text-background-color': T.tbg, 'text-background-opacity': 0.92, 'text-background-padding': 3 } },
    { selector: 'edge.tree', style: { 'target-arrow-shape': 'none', 'width': 2, 'opacity': 0.55 } },
    { selector: 'edge.overlay', style: { 'display': 'none' } },
    { selector: 'edge.overlay.shown', style: { 'display': 'element', 'curve-style': 'bezier', 'width': 2.6, 'opacity': 0.95 } },
    { selector: 'edge.implied', style: { 'display': 'none' } },
    { selector: 'edge.implied.shown', style: { 'display': 'element', 'line-style': 'dashed', 'line-dash-pattern': [3, 5] } },
    { selector: 'edge.cochange', style: { 'line-style': 'dashed', 'line-dash-pattern': [8, 6], 'target-arrow-shape': 'none' } },
    { selector: 'edge.db', style: { 'line-style': 'dotted', 'target-arrow-shape': 'square', 'arrow-scale': 0.9 } },
    // Dimming must not use 'opacity' on nodes: on compound nodes it invalidates the style cache of
    // every descendant (quadratic — seconds per click on big diagrams). Fade the parts instead.
    { selector: 'edge.dim', style: { 'opacity': 0.08 } },
    { selector: 'node.dim', style: { 'background-opacity': 0.12, 'border-opacity': 0.15, 'text-opacity': 0.15, 'background-image-opacity': 0.12 } },
    { selector: 'edge.hl', style: {
      'opacity': 1, 'z-index': 25, 'label': 'data(lbl)', 'font-size': 12, 'font-weight': 600, 'color': T.tfg,
      'text-background-color': T.tbg, 'text-background-opacity': 0.92, 'text-background-padding': 3,
      'text-background-shape': 'round-rectangle',
    } },
    // selection veil (see dimAllBut): the canvas colour over everything, the kept blocks above it
    { selector: 'node.veil', style: {
      'shape': 'rectangle', 'background-color': T.canvas, 'background-opacity': 0.78, 'border-width': 0, 'label': '',
      'background-image': 'none', 'events': 'no', 'z-index': 22,
    } },
    { selector: 'node.lift', style: { 'z-index': 26 } },
    { selector: 'edge.lift', style: { 'z-index': 23 } },
    { selector: 'node:selected', style: { 'border-width': 4, 'border-color': T.sel } },
    { selector: '.changed', style: { 'underlay-color': '#f0b429', 'underlay-opacity': 0.45, 'underlay-padding': 7 } },
    { selector: '.planhl', style: { 'underlay-color': '#f0b429', 'underlay-opacity': 0.4, 'underlay-padding': 9 } },
    { selector: '.found', style: { 'underlay-color': T.found, 'underlay-opacity': 0.35, 'underlay-padding': 10 } },
  ];
}

const cy = cytoscape({
  container: $('#cy'),
  wheelSensitivity: 0.25,
  // Full screen resolution (pixelRatio 1 on a 125 % screen made every diagram blurry). While the view
  // moves, a snapshot of the full-resolution picture is moved instead of redrawing 1600 arrows per frame:
  // panning shifts it pixel for pixel; zooming scales it only until the gesture ends.
  textureOnViewport: true, hideEdgesOnViewport: false, motionBlur: false, pixelRatio: 'auto',
  minZoom: 0.05, maxZoom: 3,
  boxSelectionEnabled: false,
  autoungrabify: true, // blocks are placed by the layout; dragging one would detach it from its frame
  style: buildStyle(),
});

// Camera moves: animated on small diagrams, instant on big ones (every animation frame redraws all cards).
function smoothView(opts) {
  cy.stop(true, true);
  if (cy.elements().length > 250) {
    if (opts.fit) cy.fit(opts.fit.eles, opts.fit.padding);
    else if (opts.center) { if (opts.zoom) cy.zoom(opts.zoom); cy.center(opts.center.eles); }
    return;
  }
  cy.animate({ ...opts, duration: 350, easing: 'ease-out-cubic' });
}

// ---------------------------------------------------------------- graph → visible elements

function testHidden(id) {
  if (S.showTests) return false;
  const M = S.model;
  return M.isTest(id) || M.isTest(M.moduleOf.get(id));
}
function isDocsGroup(id) {
  const n = S.model.node(id);
  return n.kind === 'group' && /doc|док/i.test(id + n.name);
}

function computeVisible() {
  const M = S.model, vis = new Set(), stack = [...M.top];
  while (stack.length) {
    const id = stack.pop();
    if (testHidden(id) || (!S.showDocs && isDocsGroup(id))) continue;
    vis.add(id);
    if (S.expanded.has(id)) for (const c of M.kids(id)) stack.push(c);
  }
  return vis;
}

function makeRep(vis) {
  const M = S.model, memo = new Map();
  return id => {
    if (memo.has(id)) return memo.get(id);
    let x = id;
    while (x && !vis.has(x)) x = M.parent(x);
    memo.set(id, x || null);
    return x || null;
  };
}

// Russian title of a node: its role if known, otherwise its own name.
function titleOf(n) { return n._desc?.ru || n.name; }

// ---- block cards: a clear title, a thin divider, then the plain explanation and the count

const xmlEsc = s => String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
function wrapText(text, maxChars) {
  const words = String(text).split(/\s+/), lines = [];
  let cur = '';
  for (const w of words) {
    if (!cur) cur = w;
    else if ((cur + ' ' + w).length <= maxChars) cur += ' ' + w;
    else { lines.push(cur); cur = w; }
  }
  if (cur) lines.push(cur);
  return lines;
}
const cardCache = new Map();
function cardFor(n, closed) {
  const dark = document.documentElement.dataset.theme === 'dark';
  const container = ['module', 'folder', 'group'].includes(n.kind);
  const big = n.kind === 'module' || n.kind === 'group';
  const T = { fs: big ? 24 : container ? 20 : 16, sub: big ? 15 : 13, body: big ? 16 : 14 };
  const a = n._agg;
  const title = container ? titleOf(n) : n.name;
  const orig = container && n._desc?.ru && n._desc.ru !== n.name ? n.name : null;
  const about = n._desc?.hint || (n.kind === 'file' ? fileAboutQuick(n) : n.summary) || '';
  const count = container
    ? (a.files ? `${a.files} ${plural(a.files, 'файл', 'файла', 'файлов')}` : '')
    : n.kind === 'file' && a.types ? `${a.types} ${plural(a.types, 'класс', 'класса', 'классов')}`
    : n.kind === 'type' && a.members ? `${a.members} ${plural(a.members, 'функция', 'функции', 'функций')}` : '';
  const w = big ? 330 : container ? 290 : n.kind === 'file' ? 260 : 230;
  return svgCard({ key: n.id + '|' + closed, w, title, sub: orig, body: about, foot: count, fs: T.fs, subFs: T.sub, bodyFs: T.body });
}

// A card picture: title (up to 2 lines), optional small subtitle, divider, explanation (up to
// `bodyLines` lines, or a list of `rows`), and a footer. Drawn once as SVG and cached.
function svgCard({ key, w, title, sub, body, rows, foot, fs = 16, subFs = 13, bodyFs = 14, bodyLines = 4, align = 'middle', pill }) {
  const dark = document.documentElement.dataset.theme === 'dark';
  const ck = [key, dark, w, title, sub, body, rows?.join('\u0001'), foot, pill?.text].join('|');
  if (cardCache.has(ck)) return cardCache.get(ck);
  const pad = 14, perTitle = Math.floor((w - 2 * pad) / (fs * 0.56)), perBody = Math.floor((w - 2 * pad) / (bodyFs * 0.52));
  const tl = wrapText(title, perTitle).slice(0, 2);
  const all = body ? wrapText(body, perBody) : [];
  const bl = all.slice(0, bodyLines);
  if (all.length > bodyLines) bl[bodyLines - 1] = bl[bodyLines - 1].replace(/.{0,2}$/, '…');
  const C = dark ? { t: '#f2f4f8', m: '#aab2bf', b: '#d5dae2', d: 'rgba(255,255,255,0.18)' } : { t: '#1c2130', m: '#6b7280', b: '#3d4454', d: 'rgba(0,0,0,0.14)' };
  const x = align === 'start' ? pad : w / 2;
  let y = pad, parts = [];
  if (pill) {
    const pw = pill.text.length * 7.2 + 18;
    parts.push(`<rect x="${align === 'start' ? pad : (w - pw) / 2}" y="${y}" width="${pw}" height="20" rx="10" fill="${pill.color}" fill-opacity="0.22"/><text x="${align === 'start' ? pad + pw / 2 : w / 2}" y="${y + 14.5}" font-size="12" font-weight="700" fill="${pill.color}" text-anchor="middle">${xmlEsc(pill.text)}</text>`);
    y += 26;
  }
  for (const l of tl) { y += fs * 1.05; parts.push(`<text x="${x}" y="${y}" font-size="${fs}" font-weight="700" fill="${C.t}">${xmlEsc(l)}</text>`); y += fs * 0.15; }
  if (sub) { y += subFs * 1.1; parts.push(`<text x="${x}" y="${y}" font-size="${subFs}" fill="${C.m}">${xmlEsc(sub)}</text>`); }
  if (bl.length || rows?.length || foot) { y += 10; parts.push(`<line x1="${pad + 6}" x2="${w - pad - 6}" y1="${y}" y2="${y}" stroke="${C.d}" stroke-width="1.5"/>`); y += 4; }
  for (const l of bl) { y += bodyFs * 1.3; parts.push(`<text x="${x}" y="${y}" font-size="${bodyFs}" fill="${C.b}">${xmlEsc(l)}</text>`); }
  for (const r of rows || []) { y += bodyFs * 1.35; parts.push(`<text x="${pad}" y="${y}" font-size="${bodyFs}" fill="${C.b}" text-anchor="start" font-family="Cascadia Mono, Consolas, monospace">${xmlEsc(r.length > perBody + 4 ? r.slice(0, perBody + 3) + '…' : r)}</text>`); }
  if (foot) { y += subFs * 1.5; parts.push(`<text x="${x}" y="${y}" font-size="${subFs}" font-weight="600" fill="${C.m}">${xmlEsc(foot)}</text>`); }
  const h = Math.round(y + pad + 2);
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}" viewBox="0 0 ${w} ${h}"><g font-family="Segoe UI Variable Text, Segoe UI, sans-serif" text-anchor="${align}">${parts.join('')}</g></svg>`;
  const res = { w, h, svg: 'data:image/svg+xml;utf8,' + encodeURIComponent(svg) };
  cardCache.set(ck, res);
  return res;
}
// cheap description of a file for its card (the full one lives in the tree view)
function fileAboutQuick(n) {
  for (const k of S.model.kids(n.id)) { const t = S.model.node(k); if (t.summary) return t.summary; }
  return '';
}

function nodeLabel(n, closed) {
  const a = n._agg, d = n._desc || {};
  const sub = d.ru && d.ru !== n.name ? n.name : null; // original name, shown under the Russian one
  if (!closed) return d.ru ? `${d.ru}  ·  ${n.name}` : n.name;
  if (n.kind === 'module' || n.kind === 'folder' || n.kind === 'group') {
    const kids = S.model.kids(n.id).length;
    const count = a.files ? `${a.files} ${plural(a.files, 'файл', 'файла', 'файлов')}` : `${kids} ${plural(kids, 'элемент', 'элемента', 'элементов')}`;
    // the block itself says what it is for
    return [titleOf(n), sub, n._desc?.hint, count].filter(Boolean).join('\n');
  }
  if (n.kind === 'file' || n.kind === 'type') return [n.name, d.hint].filter(Boolean).join('\n');
  return n.name;
}

function nodeSize(n) {
  const loc = n._agg.loc || 0;
  // only files and classes print their hint on the block; containers print title + name + count
  const shown = n.kind === 'file' || n.kind === 'type' ? (n._desc?.hint || '').length * 0.85 : 0;
  const text = Math.max(titleOf(n).length, n.name.length, shown);
  switch (n.kind) {
    case 'module': case 'group': case 'folder': {
      // fixed comfortable width; the hint wraps, the height grows with the number of lines
      const big = n.kind !== 'folder', fs = big ? 22 : 19, cw = fs * 0.56;
      const w = Math.max(big ? 300 : 260, Math.min(400, text * cw + 44));
      const perLine = Math.max(10, Math.floor((w - 24) / cw));
      const hintLines = n._desc?.hint ? Math.ceil(n._desc.hint.length / perLine) : 0;
      const lines = 2 + (n._desc?.ru && n._desc.ru !== n.name ? 1 : 0) + hintLines;
      return [w, 26 + lines * fs * 1.3];
    }
    case 'file': return [Math.max(130 + Math.min(80, Math.sqrt(loc) * 1.5), text * 7 + 26), n._desc?.hint ? 52 : 40];
    case 'type': return [Math.max(110, text * 6.8 + 26), n._desc?.hint ? 48 : 36];
    case 'table': return [Math.max(110, n.name.length * 7.5 + 34), 50];
    default: return [Math.max(70, n.name.length * 6.4 + 20), 28];
  }
}

function buildElements() {
  if (S.view && S.view !== 'map') return viewElements(S.view);
  const M = S.model, vis = computeVisible(), rep = makeRep(vis);
  const els = [], now = Date.now();
  // depth among visible blocks → drawing order of nested frames (outer below inner)
  const depthOf = id => { let d = 0; for (let p = M.parent(id); p; p = M.parent(p)) if (vis.has(p)) d++; return d; };
  // SVG cards are costly to rasterize the first time; on big levels files/classes get plain labels
  const cardKinds = vis.size <= 250 ? ['module', 'folder', 'group', 'file', 'type', 'table'] : ['module', 'folder', 'group', 'table'];
  for (const id of vis) {
    const n = M.node(id), col = M.colorOf(id);
    const open = S.expanded.has(id) && M.kids(id).some(k => vis.has(k));
    const closed = !open && M.hasKids(id);
    // closed blocks are cards (title / divider / explanation); open ones are frames with a caption.
    // Frames are ordinary nodes sized from the layout — Cytoscape compound nodes were far too slow.
    const card = !open && cardKinds.includes(n.kind) ? cardFor(n, closed) : null;
    const [w, h] = card ? [card.w, card.h] : open ? [10, 10] : nodeSize(n);
    const changed = n.path && [...S.changed].some(([cp, t]) => now - t < 20000 && cp.startsWith(n.path.toLowerCase()));
    els.push({
      group: 'nodes', pannable: true, // blocks are not draggable: dragging one moves the picture
      data: {
        id, name: (open && n._desc?.ru ? `${n._desc.ru}  ·  ${n.name}` : n.name), kind: n.kind,
        label: card || open ? '' : nodeLabel(n, closed), svg: card?.svg, w, h, tw: Math.max(20, w - 14),
        fill: col.fill, line: col.line, soft: col.soft, closedKids: closed, test: M.isTest(id),
        frame: open, z: open ? 1 + Math.min(8, depthOf(id)) : 20,
      },
      classes: changed ? 'changed' : '',
    });
  }

  // roll edges up to the nearest visible ancestor of each endpoint
  const agg = new Map();
  for (const e of M.edges) {
    const cat = EDGE_CAT[e.kind];
    if (!S.cats[cat]) continue;
    const a = rep(e.source), b = rep(e.target);
    if (!a || !b || a === b || M.isUnder(a, b) || M.isUnder(b, a)) continue;
    const [s, t] = cat === 'cochange' && a > b ? [b, a] : [a, b];
    const key = `${cat}|${s}|${t}`;
    let x = agg.get(key);
    if (!x) agg.set(key, x = { s, t, cat, w: 0, n: 0, kinds: new Set() });
    x.w += e.weight || 1; x.n++; x.kinds.add(e.kind);
  }
  // Top-level blocks with no arrow at all are gathered into captioned category frames,
  // so it is clear what they are and why they stand apart.
  const linked = new Set();
  for (const x of agg.values()) if (x.cat !== 'cochange') { linked.add(x.s); linked.add(x.t); }
  const frames = new Map();
  for (const el of els) {
    const id = el.data.id;
    if (!M.top.includes(id) || S.expanded.has(id) || linked.has(id)) continue;
    const key = M.categoryOf(id);
    if (!frames.has(key)) frames.set(key, []);
    frames.get(key).push(el);
  }
  for (const [key, list] of frames) {
    const [, title, about] = CATEGORIES.find(c => c[0] === key);
    const fid = 'cat:' + key;
    els.push({ group: 'nodes', pannable: true, data: { id: fid, kind: 'cat', frame: true, z: 1, name: `${title}\n${about}`, label: '', w: 10, h: 10, tw: 520, fill: 'transparent', line: 'transparent', soft: 'transparent' } });
    for (const el of list) el.data.inCat = fid;
  }

  let list = [...agg.values()];
  if (list.length > MAX_EDGES) list = list.sort((x, y) => y.w - x.w).slice(0, MAX_EDGES);
  // Transitive reduction of "uses" arrows: if A→B→…→C exists, the direct A→C adds no
  // information to the picture and only crosses other lines. It is hidden until A or C is selected.
  const implied = new Set();
  if (S.simplify) {
    const succ = new Map();
    for (const x of list) if (x.cat === 'code') { if (!succ.has(x.s)) succ.set(x.s, []); succ.get(x.s).push(x.t); }
    const reachableWithout = (from, to) => {
      const seen = new Set([from]), stack = (succ.get(from) || []).filter(v => v !== to);
      while (stack.length) {
        const v = stack.pop();
        if (v === to) return true;
        if (seen.has(v)) continue;
        seen.add(v);
        for (const w of succ.get(v) || []) if (!seen.has(w)) stack.push(w);
      }
      return false;
    };
    for (const x of list) if (x.cat === 'code' && reachableWithout(x.s, x.t)) implied.add(x);
  }
  const maxW = {};
  for (const x of list) maxW[x.cat] = Math.max(maxW[x.cat] || 1, x.w);
  for (const x of list) {
    const r = Math.sqrt(x.w / maxW[x.cat]);
    els.push({
      group: 'edges',
      data: {
        id: `${x.cat}|${x.s}|${x.t}`, source: x.s, target: x.t, cat: x.cat, w: x.w, n: x.n,
        // one thickness for every arrow: strength is the "×N" label, not the width
        kinds: [...x.kinds].join(','), width: 2.4, op: 0.55 + r * 0.35,
        // an arrow has the colour of the block it starts from
        color: M.colorOf(x.s).line, lbl: `×${x.w}`,
      },
      classes: x.cat + (implied.has(x) ? ' implied' : ''),
    });
  }
  S.edgesTotal = agg.size;
  if (S.heat) applyHeat(els, S.heat);
  return els;
}

// Visible blocks inside (or equal to) a block, by the project tree — not by Cytoscape parents.
function blocksUnder(id) {
  const M = S.model;
  return cy.nodes().filter(x => M.byId.has(x.id()) && M.isUnder(x.id(), id));
}

// ---------------------------------------------------------------- render

// The new picture is laid out first and swapped in as one batch: adding the elements and then moving and
// re-routing all of them after the layout restyled every element twice (hundreds of ms on big levels).
// The old picture stays on screen while ELK works.
function render({ fit = false, anchor = null } = {}) {
  if (!S.model) return;
  const fresh = cy.elements().empty();
  let els = buildElements();
  if (S.focus && els.some(e => e.data.id === S.focus)) els = focusElements(els, S.focus);
  // the plan has no arrows to route: a simple packing is instant and fills the screen better
  const job = S.view === 'plan'
    ? packedLayout(els, fit || fresh)
    : layeredLayout(els, { fit: fit || fresh });
  job.then(ok => {
    if (!ok) return;
    if (fit || fresh) cy.fit(diagramNodes(), 40);
    else if (anchor && cy.getElementById(anchor).nonempty()) {
      // after opening/closing a block, show that block together with its neighbours
      const inner = blocksUnder(anchor);
      smoothView({ fit: { eles: inner.union(inner.neighborhood().nodes()), padding: 60 } });
    }
    applySelection();
  });
  updateCrumbs();
  renderLegend();
}

// «Фокус»: only the block, what is inside it, its arrows and the blocks at their other ends (with frames).
function focusElements(els, focus) {
  const M = S.model, keep = new Set();
  const under = id => M.byId.has(id) && M.isUnder(id, focus);
  for (const e of els) if (e.group === 'nodes' && under(e.data.id)) keep.add(e.data.id);
  for (const e of els) if (e.group === 'edges' && (keep.has(e.data.source) || keep.has(e.data.target))) {
    keep.add(e.data.id); keep.add(e.data.source); keep.add(e.data.target);
  }
  const present = new Set(els.map(e => e.data.id));
  for (const id of [...keep]) if (M.byId.has(id)) for (const a of M.ancestors(id)) if (present.has(a)) keep.add(a);
  return els.filter(e => keep.has(e.data.id));
}

// ELK runs in a Web Worker: a big layout (hundreds of blocks) must never freeze the window.
// A newer layout request kills the worker that is still busy with an outdated one.
let elk = new ELK({ workerUrl: 'vendor/elk-worker.min.js' });
let elkBusy = false;
function restartElk() { try { elk.terminateWorker(); } catch { } elk = new ELK({ workerUrl: 'vendor/elk-worker.min.js' }); }
function setBusy(on, text) {
  const el = $('#busy');
  if (!on) { clearTimeout(setBusy.t); el.classList.add('hidden'); return; }
  el.querySelector('span').textContent = text;
  clearTimeout(setBusy.t);
  setBusy.t = setTimeout(() => el.classList.remove('hidden'), 250); // quick layouts don't flash it
}
let layoutSeq = 0;

// Puts a laid-out picture on screen a few hundred elements per frame — frames, then blocks, then arrows.
// The first draw of an element is what costs (Cytoscape styles and rasterizes it): 1100 blocks at once froze
// the window for 300–500 ms, in parts every frame stays short. Returns false if a newer layout took over.
// The part size adapts to the measured frame time (target ≈ 30 ms per frame).
async function swapIn(els, seq, fit) {
  const order = [
    ...els.filter(e => e.group === 'nodes' && e.data.frame), ...els.filter(e => e.group === 'nodes' && !e.data.frame),
    ...els.filter(e => e.group === 'edges'),
  ];
  const frame = () => new Promise(r => requestAnimationFrame(() => setTimeout(r, 0))); // after the frame is drawn
  let size = 120, i = 0;
  while (i < order.length) {
    const part = order.slice(i, i + size), t0 = performance.now();
    cy.batch(() => {
      if (i === 0) { cy.elements().remove(); if (fit) fitBox(els); }
      cy.add(part);
    });
    i += part.length;
    if (i >= order.length) break;
    await frame();
    if (seq !== layoutSeq) return false;
    const ms = Math.max(1, performance.now() - t0);
    size = Math.max(40, Math.min(800, Math.round(size * 30 / ms)));
  }
  return true;
}
// the view that cy.fit(…, 40) would give for elements that are not on the diagram yet
function fitBox(els, pad = 40) {
  let x1 = Infinity, y1 = Infinity, x2 = -Infinity, y2 = -Infinity;
  for (const e of els) if (e.group === 'nodes' && e.position) {
    const w = (e.data.w || 0) / 2, h = (e.data.h || 0) / 2;
    x1 = Math.min(x1, e.position.x - w); x2 = Math.max(x2, e.position.x + w);
    y1 = Math.min(y1, e.position.y - h); y2 = Math.max(y2, e.position.y + h);
  }
  if (!Number.isFinite(x1)) return;
  const z = Math.max(cy.minZoom(), Math.min(cy.maxZoom(), Math.min((cy.width() - 2 * pad) / Math.max(1, x2 - x1), (cy.height() - 2 * pad) / Math.max(1, y2 - y1))));
  cy.viewport({ zoom: z, pan: { x: cy.width() / 2 - z * (x1 + x2) / 2, y: cy.height() / 2 - z * (y1 + y2) / 2 } });
}

async function packedLayout(els, fit) {
  const seq = ++layoutSeq;
  if (elkBusy) restartElk(); // an outdated ELK layout must not land on top of this one
  elkBusy = true;
  packLayout(els);
  const done = await swapIn(els, seq, fit);
  if (seq !== layoutSeq || !done) return false;
  EdgeLayer.clear();
  elkBusy = false; setBusy(false);
  return true;
}

// ELK places the blocks AND routes every arrow orthogonally around them, with separate lanes for
// parallel arrows. Open blocks become frames whose size ELK computes; the routes are drawn exactly.
// Works on the element list (not on the diagram), then puts the finished picture on screen in one batch.
async function layeredLayout(els, { fit = false } = {}) {
  const seq = ++layoutSeq;
  const M = S.model;
  const byId = new Map(els.map(e => [e.data.id, e]));
  const isCls = (e, c) => (' ' + (e.classes || '') + ' ').includes(' ' + c + ' ');
  // blocks without arrows sit in category frames placed beside the graph (placeFrames), not by ELK
  const loose = id => { const d = byId.get(id).data; return d.kind === 'cat' || (d.inCat && byId.has(d.inCat)); };
  const nodes = els.filter(e => e.group === 'nodes' && !loose(e.data.id));
  const inLayout = new Set(nodes.map(e => e.data.id));
  const routed = els.filter(e => e.group === 'edges' && !isCls(e, 'cochange') && !isCls(e, 'implied') && !isCls(e, 'overlay')
    && inLayout.has(e.data.source) && inLayout.has(e.data.target));
  const view = S.view || 'map';
  const aspect = (cy.width() / Math.max(1, cy.height())).toFixed(2);

  // hierarchy for ELK: data.pid in the extra views, otherwise the project tree
  // (nearest ancestor that is on the diagram)
  const kidsOf = new Map(), roots = [];
  for (const e of nodes) {
    const id = e.data.id;
    let p;
    if (e.data.pid !== undefined) p = e.data.pid;
    else { p = M.byId.has(id) ? M.parent(id) : null; while (p && !inLayout.has(p)) p = M.parent(p); }
    if (p && inLayout.has(p)) { if (!kidsOf.has(p)) kidsOf.set(p, []); kidsOf.get(p).push(id); } else roots.push(id);
  }
  const toElk = id => {
    const kids = kidsOf.get(id);
    if (kids?.length) return { id, children: kids.map(toElk), layoutOptions: { 'elk.padding': '[top=64,left=36,bottom=36,right=36]' } };
    const d = byId.get(id).data;
    return { id, width: d.w + 6, height: d.h + 6 };
  };
  const graph = {
    id: '__root',
    layoutOptions: {
      'elk.algorithm': 'layered', 'elk.direction': view === 'tree' ? 'RIGHT' : 'DOWN',
      'elk.hierarchyHandling': 'INCLUDE_CHILDREN', 'elk.edgeRouting': 'ORTHOGONAL',
      'elk.layered.spacing.nodeNodeBetweenLayers': view === 'tree' ? 90 : 120, 'elk.spacing.nodeNode': view === 'tree' ? 26 : 80,
      'elk.layered.spacing.edgeNodeBetweenLayers': 30, 'elk.spacing.edgeNode': 28,
      'elk.spacing.edgeEdge': 16, 'elk.layered.spacing.edgeEdgeBetweenLayers': 16,
      'elk.spacing.componentComponent': 120,
      'elk.layered.nodePlacement.strategy': 'BRANDES_KOEPF', 'elk.layered.nodePlacement.bk.fixedAlignment': 'BALANCED',
      'elk.layered.crossingMinimization.strategy': 'LAYER_SWEEP',
      'elk.layered.mergeEdges': false, 'elk.layered.unnecessaryBendpoints': false,
      'elk.separateConnectedComponents': true, 'elk.aspectRatio': aspect,
    },
    children: roots.map(toElk),
    edges: routed.map(e => ({ id: e.data.id, sources: [e.data.source], targets: [e.data.target] })),
  };
  let res;
  if (elkBusy) restartElk();
  elkBusy = true;
  setBusy(true, `Раскладываю схему: ${nodes.length} ${plural(nodes.length, 'блок', 'блока', 'блоков')}…`);
  try { res = await elk.layout(graph); }
  catch (err) {
    if (seq !== layoutSeq) return false; // cancelled by a newer layout
    elkBusy = false; setBusy(false);
    console.error('elk', err); toast('Не удалось разложить схему: ' + (err.message || err), 5000); return false;
  }
  if (seq !== layoutSeq) return false; // a newer layout started meanwhile

  // elkjs 0.9 ignores elk.json.*Coords: child shapes are relative to their parent and edge
  // sections to their "container" node, so convert everything to absolute coordinates here.
  const boxes = new Map([['__root', { x: 0, y: 0, width: 0, height: 0 }]]);
  const walk = (list, ox, oy) => (list || []).forEach(c => {
    const abs = { x: ox + c.x, y: oy + c.y, width: c.width, height: c.height };
    boxes.set(c.id, abs);
    walk(c.children, abs.x, abs.y);
  });
  walk(res.children, 0, 0);
  const shift = (p, o) => ({ x: p.x + o.x, y: p.y + o.y });
  const centre = id => { const b = boxes.get(id); return { x: b.x + b.width / 2, y: b.y + b.height / 2 }; };

  for (const [id, b] of boxes) {
    const e = byId.get(id);
    if (!e) continue;
    if (e.data.frame) Object.assign(e.data, { w: b.width, h: b.height, tw: Math.max(20, b.width - 40) });
    e.position = centre(id);
  }
  placeFrames(els, byId, boxes);

  // segments are expressed relative to the line between the two endpoints
  // elkjs puts an edge into the `edges` of the lowest node containing both ends, not only into the root:
  // reading res.edges alone left every arrow inside an open block unrouted (drawn as a plain curve)
  const allEdges = [], collect = n => { for (const e of n.edges || []) allEdges.push(e); for (const c of n.children || []) collect(c); };
  collect(res);
  const layerRoutes = [];
  for (const re of allEdges) {
    const sec = re.sections?.[0], e = byId.get(re.id);
    if (!sec || !e || !boxes.has(e.data.source) || !boxes.has(e.data.target)) continue;
    const sc = centre(e.data.source), tc = centre(e.data.target);
    const o = boxes.get(re.container || '__root') || boxes.get('__root');
    const S0 = shift(sec.startPoint, o), T0 = shift(sec.endPoint, o), bends = (sec.bendPoints || []).map(p => shift(p, o));
    const dx = T0.x - S0.x, dy = T0.y - S0.y, len2 = dx * dx + dy * dy || 1, len = Math.sqrt(len2);
    const w = [], d = [];
    for (const p of bends) {
      const px = p.x - S0.x, py = p.y - S0.y;
      w.push(((px * dx + py * dy) / len2).toFixed(4));
      d.push(((dx * py - dy * px) / len).toFixed(2));
    }
    Object.assign(e.data, {
      sp: `${(S0.x - sc.x).toFixed(1)}px ${(S0.y - sc.y).toFixed(1)}px`,
      tp: `${(T0.x - tc.x).toFixed(1)}px ${(T0.y - tc.y).toFixed(1)}px`,
      sw: w.length ? w.join(' ') : '0.5', sd: d.length ? d.join(' ') : '0',
    });
    e.classes = (e.classes || '') + ' routed onlayer';
    layerRoutes.push(layerRoute(e, [S0, ...bends, T0]));
  }
  // stays "busy" until the last part is on screen, so nobody centres on a half-built picture
  const done = await swapIn(els, seq, fit);
  if (seq !== layoutSeq || !done) return false;
  EdgeLayer.set(layerRoutes);
  elkBusy = false; setBusy(false);
  return true;
}

// An arrow for the arrow layer, styled like the Cytoscape edge it replaces (see the edge rules in buildStyle).
function layerRoute(e, pts) {
  const cls = ' ' + e.classes + ' ', has = c => cls.includes(' ' + c + ' ');
  let x1 = Infinity, y1 = Infinity, x2 = -Infinity, y2 = -Infinity;
  for (const p of pts) { x1 = Math.min(x1, p.x); x2 = Math.max(x2, p.x); y1 = Math.min(y1, p.y); y2 = Math.max(y2, p.y); }
  const tree = has('tree');
  return {
    id: e.data.id, pts, x1, y1, x2, y2, color: e.data.color,
    width: tree ? 2 : e.data.width ?? 2.4, op: tree ? 0.55 : e.data.op ?? 0.85,
    dash: has('cochange') ? [8, 6] : has('db') ? [1.5, 4] : null,
    arrow: tree || has('cochange') ? 'none' : has('db') ? 'square' : 'triangle',
  };
}

// Category frames (blocks without arrows) stack beside a tall graph or under a wide one;
// inside a frame the blocks form a grid of up to three columns; the frame is sized around them.
function placeFrames(els, byId, boxes) {
  const frames = els.filter(e => e.group === 'nodes' && e.data.kind === 'cat');
  if (!frames.length) return;
  let bb = null;
  for (const [id, b] of boxes) if (id !== '__root' && byId.has(id)) {
    bb = bb || { x1: Infinity, y1: Infinity, x2: -Infinity, y2: -Infinity };
    bb.x1 = Math.min(bb.x1, b.x); bb.y1 = Math.min(bb.y1, b.y); bb.x2 = Math.max(bb.x2, b.x + b.width); bb.y2 = Math.max(bb.y2, b.y + b.height);
  }
  const empty = !bb;
  bb = bb ? { ...bb, w: bb.x2 - bb.x1, h: bb.y2 - bb.y1 } : { x1: 0, x2: 0, y1: 0, y2: 0, w: 0, h: 0 };
  const order = CATEGORIES.map(c => 'cat:' + c[0]);
  const LABEL = 90, GAP = 150, PAD = 30;
  const kidsOf = new Map(frames.map(f => [f.data.id, []]));
  for (const e of els) if (e.group === 'nodes' && kidsOf.has(e.data.inCat)) kidsOf.get(e.data.inCat).push(e);
  const specs = frames.sort((a, b) => order.indexOf(a.data.id) - order.indexOf(b.data.id)).map(f => {
    const kids = kidsOf.get(f.data.id).sort((a, b) => (a.data.name || '').localeCompare(b.data.name || ''));
    const cols = Math.min(3, kids.length);
    const colW = Math.max(...kids.map(k => k.data.w)) + 36;
    const rowH = Math.max(...kids.map(k => k.data.h)) + 36;
    return { f, kids, cols, colW, rowH, w: Math.max(cols * colW, 560), h: Math.ceil(kids.length / cols) * rowH + LABEL };
  });
  // beside the graph (a column of frames) or under it (a row) — whichever gives the bigger zoom
  const zoomFor = (W, H) => Math.min(cy.width() / Math.max(1, W), cy.height() / Math.max(1, H));
  const sideW = bb.w + GAP + Math.max(...specs.map(s => s.w));
  const sideH = Math.max(bb.h, specs.reduce((t, s) => t + s.h + GAP, -GAP));
  const downW = Math.max(bb.w, specs.reduce((t, s) => t + s.w + GAP, -GAP));
  const downH = bb.h + GAP + Math.max(...specs.map(s => s.h));
  const side = empty || zoomFor(sideW, sideH) >= zoomFor(downW, downH);
  let fx = side ? (empty ? 0 : bb.x2 + GAP) : bb.x1, fy = side ? bb.y1 : bb.y2 + GAP;
  for (const s of specs) {
    const x0 = fx + (s.w - s.cols * s.colW) / 2, y0 = fy + LABEL;
    s.kids.forEach((k, i) => { k.position = { x: x0 + (i % s.cols) * s.colW + s.colW / 2, y: y0 + Math.floor(i / s.cols) * s.rowH + s.rowH / 2 }; });
    Object.assign(s.f.data, { w: s.w + 2 * PAD, h: s.h + PAD, tw: s.w });
    s.f.position = { x: fx + s.w / 2, y: fy + (s.h + PAD) / 2 - PAD / 2 };
    if (side) fy += s.h + GAP; else fx += s.w + GAP;
  }
}

cy.on('mouseover', 'edge', e => e.target.addClass('hover'));
cy.on('mouseout', 'edge', e => e.target.removeClass('hover'));

// Everything on the diagram except the selection veil.
const diagramNodes = () => cy.nodes('[kind != "veil"]');

// Dimming everything but the selection: one translucent «veil» node over the whole diagram, and the kept
// elements lifted above it. Toggling a class on 2000 elements made Cytoscape restyle and re-rasterize all of
// them (≈250 ms on «Классы Ядра»); now only the kept few change. Frames stay under the veil as faint context.
function dimAllBut(keepIds) {
  cy.batch(() => {
    for (const id of keepIds) {
      const e = cy.getElementById(id);
      if (e.nonempty() && e.isNode() && !e.data('frame')) e.addClass('lift');
    }
    const bb = diagramNodes().boundingBox(), m = 4000;
    const veil = { w: bb.w + 2 * m, h: bb.h + 2 * m };
    const v = cy.getElementById('__veil');
    if (v.empty()) cy.add({ group: 'nodes', data: { id: '__veil', kind: 'veil', ...veil, z: 22, tw: 20, label: '', fill: 'transparent', line: 'transparent', soft: 'transparent' }, position: { x: bb.x1 + bb.w / 2, y: bb.y1 + bb.h / 2 }, classes: 'veil' });
    else { v.data(veil); v.position({ x: bb.x1 + bb.w / 2, y: bb.y1 + bb.h / 2 }); }
  });
  EdgeLayer.setKeep(keepIds);
}

function applySelection() {
  cy.batch(() => {
    cy.elements('.hl, .planhl, .shown, .lift').removeClass('hl planhl shown lift');
    cy.nodes(':selected').unselect();
    cy.getElementById('__veil').remove();
  });
  EdgeLayer.setKeep(null);
  cy.edges('.dep').remove();
  const sel = S.selected;
  if (!sel) return;
  if (S.view && S.view !== 'map') {
    // plan / database / tree: the block, its arrows and their other ends; plan→code arrows and the
    // tree's dependency arrows exist only while something is selected
    const n = cy.getElementById(sel);
    if (n.empty()) return;
    if (S.view === 'tree') { const deps = treeDependencyEdges(sel); if (deps.length) cy.add(deps); }
    if (S.view === 'file' && FV.extraEdges?.length) {
      // the weaker arrows of a big file: only the selected function's ones, while it is selected
      const mine = FV.extraEdges.filter(e => e.data.source === sel || e.data.target === sel);
      if (mine.length) cy.add(mine.map(e => ({ ...e, data: { ...e.data }, classes: e.classes + ' overlay dep' })));
    }
    n.select();
    const edges = n.connectedEdges();
    const keepIds = new Set([sel]);
    edges.forEach(e => { keepIds.add(e.id()); keepIds.add(e.source().id()); keepIds.add(e.target().id()); });
    dimAllBut(keepIds);
    cy.batch(() => { edges.addClass('hl'); edges.filter('.overlay').addClass('shown'); });
    return;
  }
  const rep = makeRep(new Set(cy.nodes().map(n => n.id())));
  if (sel.startsWith('roadmap:') || S.model.rmById.has(sel)) {
    const item = S.model.rmById.get(sel);
    if (!item) return;
    const ids = [...new Set(roadmapLinks(item).map(rep).filter(Boolean))];
    if (!ids.length) return;
    const keep = cy.collection(ids.map(id => cy.getElementById(id)));
    const keepIds = new Set(ids);
    dimAllBut(keepIds);
    keep.addClass('planhl');
    return;
  }
  const r = rep(sel);
  if (!r) return;
  const n = cy.getElementById(r);
  n.select();
  const inner = blocksUnder(r), innerIds = new Set(inner.map(x => x.id()));
  // the block's links are the arrows that leave or enter it; arrows between its own parts stay as they are
  const edges = inner.connectedEdges(), outside = edges.filter(e => innerIds.has(e.source().id()) !== innerIds.has(e.target().id()));
  const keepIds = new Set(innerIds);
  edges.forEach(e => { keepIds.add(e.id()); keepIds.add(e.source().id()); keepIds.add(e.target().id()); });
  // when the selection is most of the picture (an open module that fills the screen) there is nothing to fade
  if (keepIds.size < 0.6 * cy.elements().length) {
    dimAllBut(keepIds);
    cy.batch(() => edges.filter(e => !outside.has(e)).addClass('lift'));
  }
  // At most the strongest MAX_HL links get the highlight with its «×N» label: an open module selected on a big
  // level has hundreds of incoming arrows (782 for «Ядро»), and labelled arrows are drawn one by one by
  // Cytoscape (a 2-second zoom frame). Unreadable anyway; the full list is in the «Связи» tab.
  const strongest = outside.length > MAX_HL ? outside.sort((a, b) => (b.data('w') || 0) - (a.data('w') || 0)).slice(0, MAX_HL) : outside;
  cy.batch(() => {
    strongest.addClass('hl');
    strongest.filter('.implied').addClass('shown'); // the selected block shows its direct links
  });
}
const MAX_HL = 120;

// ---------------------------------------------------------------- actions

function toggle(id, force) {
  const M = S.model;
  // a leaf (function, class without members, file without classes): show it inside the app
  if (!M.hasKids(id) || M.node(id).kind === 'type') { if (M.node(id)?.path) openFileView(id); return; }
  const open = force ?? !S.expanded.has(id);
  if (open) S.expanded.add(id);
  else { S.expanded.delete(id); for (const x of [...S.expanded]) if (M.isUnder(x, id)) S.expanded.delete(x); }
  saveState();
  render({ anchor: id });
  renderTree();
}

function reveal(id) {
  const M = S.model;
  if (!M.byId.has(id)) return;
  if (S.view === 'tree') {
    // in the tree diagram: open the branch down to the block
    const top = [id, ...M.ancestors(id)].find(x => M.top.includes(x));
    TREE.open.add('tree:root');
    if (top) TREE.open.add('tree:cat:' + M.categoryOf(top));
    for (const a of M.ancestors(id)) TREE.open.add(a);
    render({ anchor: id });
    select(id, { center: true });
    return;
  }
  if (S.view !== 'map') setView('map');
  let changed = false;
  // asking for a hidden test / document turns its layer on
  if (testHidden(id)) { S.showTests = true; changed = true; renderEdgeChips(); }
  if (!S.showDocs && M.ancestors(id).concat(id).some(a => M.byId.has(a) && isDocsGroup(a))) { S.showDocs = true; changed = true; renderEdgeChips(); }
  for (const a of M.ancestors(id)) if (a !== M.systemId && !S.expanded.has(a)) { S.expanded.add(a); changed = true; }
  for (const a of M.ancestors(id)) S.treeOpen.add(a);
  if (S.focus && !cy.getElementById(id).nonempty()) { S.focus = null; changed = true; }
  if (changed) { saveState(); render({ anchor: M.parent(id) }); }
  select(id, { center: true });
  renderTree();
}

function select(id, { center = false } = {}) {
  S.selected = id;
  applySelection();
  renderDetails();
  updateCrumbs();
  if (S.view === 'file') markCode();
  $$('.tree .row').forEach(r => r.classList.toggle('sel', r.dataset.id === id));
  $$('.plan .item').forEach(r => r.classList.toggle('sel', r.dataset.id === id));
  if (center && (S.model.byId.has(id) || cy.getElementById(id).nonempty())) {
    const rep = makeRep(new Set(cy.nodes().map(n => n.id())));
    const n = cy.getElementById(cy.getElementById(id).nonempty() ? id : rep(id) || '');
    if (n.nonempty()) {
      // wait for a running layout, otherwise we would centre on the block's old place
      const go = () => {
        if (elkBusy) { setTimeout(go, 150); return; }
        const cur = cy.getElementById(n.id());
        if (cur.empty()) return;
        smoothView({ center: { eles: cur }, zoom: Math.max(cy.zoom(), 0.8) });
        cur.flashClass('found', 1600);
      };
      go();
    }
  }
}

// A diagram stays readable (and fast) only up to a limit; bigger levels apply to the selected block.
const MAX_BLOCKS = 1500;

function depthSet(d, scope) {
  const M = S.model, set = new Set();
  const levelOf = { module: 1, group: 1, folder: 2, file: 3 };
  const inScope = id => !scope || M.isUnder(id, scope);
  for (const [id, n] of M.byId) {
    const lv = levelOf[n.kind];
    if (!lv || !M.hasKids(id) || !inScope(id)) continue;
    if ((lv === 1 && d >= 2) || (lv === 2 && d >= 3) || (lv === 3 && d >= 4)) set.add(id);
  }
  if (scope) { set.add(scope); for (const a of M.ancestors(scope)) set.add(a); }
  return set;
}

function countVisible(expanded) {
  const keep = S.expanded; S.expanded = expanded;
  const n = computeVisible().size;
  S.expanded = keep;
  return n;
}

function setDepth(d) {
  const M = S.model;
  let scope = null, set = depthSet(d, null);
  if (countVisible(set) > MAX_BLOCKS) {
    // whole project is too big for this level: apply it to the selected block (or its module)
    const sel = S.selected && M.byId.has(S.selected) ? S.selected : null;
    scope = sel ? [sel, ...M.ancestors(sel)].reverse().find(x => M.top.includes(x)) || sel : null;
    if (!scope) {
      toast(`Уровень «${['', 'Модули', 'Папки', 'Файлы', 'Классы'][d]}» для всего проекта — это ${countVisible(set)} блоков, схема станет нечитаемой. Выберите модуль кликом и нажмите кнопку снова.`, 7000);
      return;
    }
    set = depthSet(d, scope);
    toast(`Слишком много блоков для всего проекта — уровень применён к «${titleOf(M.node(scope))}».`, 5000);
  }
  S.expanded = set;
  S.focus = null;
  saveState();
  render({ fit: true });
  $$('#depthSeg button').forEach(b => b.classList.toggle('active', +b.dataset.depth === d));
}

function setFocus(id) {
  S.focus = S.focus === id ? null : id;
  render({ fit: true });
  toast(S.focus ? 'Фокус: только этот элемент и его связи. Esc — выйти.' : 'Фокус снят');
}

function roadmapLinks(item) {
  const M = S.model, out = new Set(item.links || []);
  for (const c of M.rmChildren.get(item.id) || []) for (const l of roadmapLinks(M.rmById.get(c))) out.add(l);
  return [...out].filter(l => M.byId.has(l));
}

// ---------------------------------------------------------------- load

function loadGraph(g, root) {
  const first = S.root !== root || !S.model;
  const prevSel = S.selected;
  S.model = new GraphModel(g);
  S.root = root;
  $('#start').classList.add('hidden');
  if (first) {
    if (!loadState(root)) { S.expanded = new Set(); S.treeOpen = new Set(); }
    S.selected = null; S.focus = null;
    cy.elements().remove();
  }
  for (const id of [...S.expanded]) if (!S.model.byId.has(id)) S.expanded.delete(id);
  if (prevSel && !S.model.byId.has(prevSel) && !S.model.rmById.has(prevSel)) S.selected = null;

  const p = g.project || {};
  $('#projName').textContent = p.name || root;
  $('#projBranch').textContent = [p.gitBranch, p.gitHead?.slice(0, 7)].filter(Boolean).join(' @ ');
  send({ type: 'title', text: `projmap — ${p.name || root}` });
  syncToolbar();
  render({ fit: first, animate: !first });
  renderTree(); renderPlan(); renderDb(); renderHot(); renderDetails();
}

// ---------------------------------------------------------------- crumbs + legend

function updateCrumbs() {
  const M = S.model, el = $('#crumbs');
  if (!M) { el.innerHTML = ''; return; }
  const parts = [`<span data-id="">${esc(M.g.project?.name || 'Проект')}</span>`];
  const sel = S.selected && M.byId.has(S.selected) ? S.selected : null;
  if (sel) for (const a of [...M.ancestors(sel).reverse(), sel]) {
    if (a === M.systemId) continue;
    parts.push('<i>›</i>', `<span data-id="${esc(a)}">${esc(M.node(a).name)}</span>`);
  }
  if (S.focus) parts.push('<i>·</i>', '<span data-unfocus="1">✕ фокус</span>');
  el.innerHTML = parts.join('');
}
$('#crumbs').addEventListener('click', e => {
  const s = e.target.closest('span'); if (!s) return;
  if (s.dataset.unfocus) { setFocus(S.focus); return; }
  if (!s.dataset.id) { select(null); smoothView({ fit: { eles: diagramNodes(), padding: 40 } }); return; }
  reveal(s.dataset.id);
});

let legendOpen = true;
function renderLegend() {
  const M = S.model;
  const mods = M.top.filter(id => cy.getElementById(id).nonempty()).slice(0, 24);
  const line = (style, text) => `<div class="lg"><svg width="44" height="12"><line x1="2" y1="6" x2="34" y2="6" stroke="#cfd6e2" stroke-width="2.5" ${style.replace('arrow', '')}/>${style.includes('arrow') ? '<path d="M34 1 L43 6 L34 11 Z" fill="#cfd6e2"/>' : ''}</svg><span>${text}</span></div>`;
  // the colour list of blocks is the "Структура" tab; the legend only explains how to read lines
  void mods;
  $('#legend').innerHTML = (S.heat && S.view === 'map'
    ? `<div class="lg"><span class="heatbar"></span><span>Подсветка: краснее — ${HEAT_RU[S.heat]}, синее — наоборот</span></div>` : '')
    + `<div class="lh" data-toggle="1">Как читать схему ${legendOpen ? '▾' : '▸'}</div>`
    + (legendOpen ? `
      ${line('arrow', '<b>А → Б</b>: часть А пользуется частью Б')}
      ${line('stroke-dasharray="7 5"', 'правятся вместе (по истории git)')}
      ${line('arrow stroke-dasharray="2 4"', 'работа с базой данных')}
      <div class="lg muted">Цвет стрелки — цвет того, <b>от кого</b> она идёт. Число на стрелке (при наведении) — сколько раз одна часть обращается к другой.</div>
      <div class="lg muted">Если А → Б → В, прямая стрелка А → В не рисуется. <b>Клик по блоку</b> показывает все его связи.</div>
      <div class="lg muted">Двойная рамка — внутри есть ещё: <b>двойной клик</b> раскрывает.</div>
      ${S.edgesTotal > MAX_EDGES ? `<div class="muted">показаны ${MAX_EDGES} сильнейших связей из ${S.edgesTotal}</div>` : ''}` : '');
  try { localStorage.setItem('pm:legend', legendOpen ? '1' : '0'); } catch { }
}
// collapsed by default so it never covers the graph; the same explanation is in the right panel
try { legendOpen = localStorage.getItem('pm:legend') === '1'; } catch { legendOpen = false; }
$('#legend').addEventListener('click', e => {
  if (e.target.closest('[data-toggle]')) { legendOpen = !legendOpen; renderLegend(); return; }
  const li = e.target.closest('.li'); if (li) reveal(li.dataset.id);
});

function renderEdgeChips() {
  const chips = Object.entries(CAT_UI).map(([k, v]) =>
    `<span class="chip ${S.cats[k] ? '' : 'off'}" data-cat="${k}"><span class="sw" style="background:${v.color}"></span>${v.label}</span>`);
  chips.push(`<span class="chip ${S.simplify ? '' : 'off'}" data-flag="simplify" title="Не рисовать стрелку А→В, если уже есть путь А→Б→В. Все связи блока видны при его выборе.">Без лишних стрелок</span>`);
  chips.push(`<span class="chip ${S.showTests ? '' : 'off'}" data-flag="showTests">Тесты</span>`);
  chips.push(`<span class="chip ${S.showDocs ? '' : 'off'}" data-flag="showDocs">Документы</span>`);
  $('#edgeChips').innerHTML = chips.join('');
}
$('#edgeChips').addEventListener('click', e => {
  const c = e.target.closest('.chip'); if (!c) return;
  if (c.dataset.cat) S.cats[c.dataset.cat] = !S.cats[c.dataset.cat];
  if (c.dataset.flag) S[c.dataset.flag] = !S[c.dataset.flag];
  saveState(); renderEdgeChips(); render(); renderTree();
});

function syncToolbar() {
  $$('#hotSeg button').forEach(b => b.classList.toggle('active', b.dataset.sort === S.hotSort));
  $('#btnLive').classList.toggle('on', S.live);
  renderEdgeChips();
}

// ---------------------------------------------------------------- sidebar: tree

function renderTree() {
  const M = S.model, el = $('#tree');
  if (!M) return;
  const q = $('#treeFilter').value.trim().toLowerCase();
  const rows = [];
  const row = (id, depth) => {
    const n = M.node(id), kids = M.kids(id).filter(k => M.node(k).kind !== 'member');
    const open = S.treeOpen.has(id);
    const ct = n.kind === 'file' ? (n._agg.loc ? fmt(n._agg.loc) : '') : n._agg.files ? n._agg.files : kids.length || '';
    rows.push(`<div class="row ${S.selected === id ? 'sel' : ''}" data-id="${esc(id)}" style="padding-left:${4 + depth * 14}px">
      <span class="tw" data-tw="1">${kids.length ? (open ? '▾' : '▸') : ''}</span>
      <span class="dot" style="background:${M.colorOf(id).line};opacity:${n.kind === 'module' || n.kind === 'group' ? 1 : .45}"></span>
      <span class="nm" title="${esc(n._desc?.hint || n.summary || '')}">${esc(titleOf(n))}${n._desc?.ru && n._desc.ru !== n.name ? ` <span class="muted">${esc(n.name)}</span>` : ''}</span><span class="ct">${ct}</span></div>`);
    if (open) for (const k of kids) row(k, depth + 1);
  };
  if (q) {
    let shown = 0;
    for (const [id, n] of M.byId) {
      if (shown > 300) break;
      if (n.kind === 'system' || n.kind === 'member') continue;
      if (!n.name.toLowerCase().includes(q) && !(n.path || '').toLowerCase().includes(q)) continue;
      shown++;
      rows.push(`<div class="row" data-id="${esc(id)}"><span class="dot" style="background:${M.colorOf(id).line}"></span>
        <span class="nm">${esc(n.name)}</span><span class="ct">${esc(KIND_RU[n.kind] || n.kind)}</span></div>`);
    }
  } else for (const [key, title, about] of CATEGORIES) {
    const ids = M.top.filter(id => M.categoryOf(id) === key);
    if (!ids.length) continue;
    const closed = S.catClosed.has(key);
    const files = ids.reduce((s, id) => s + (M.node(id)._agg.files || 0), 0);
    rows.push(`<div class="cat"><div class="cat-h" data-cat="${key}">${closed ? '▸' : '▾'} ${title}<span class="c">${files ? files + ' ф.' : ids.length}</span></div>
      ${closed ? '' : `<div class="cat-d">${about}</div>`}`);
    if (!closed) for (const id of ids) row(id, 0);
    rows.push('</div>');
  }
  el.innerHTML = rows.join('') || '<div class="muted">Ничего не найдено</div>';
}
$('#tree').addEventListener('click', e => {
  const ch = e.target.closest('.cat-h');
  if (ch) { const k = ch.dataset.cat; S.catClosed.has(k) ? S.catClosed.delete(k) : S.catClosed.add(k); renderTree(); return; }
  const r = e.target.closest('.row'); if (!r) return;
  const id = r.dataset.id;
  if (e.target.dataset.tw) { S.treeOpen.has(id) ? S.treeOpen.delete(id) : S.treeOpen.add(id); saveState(); renderTree(); return; }
  reveal(id);
});
$('#tree').addEventListener('dblclick', e => { const r = e.target.closest('.row'); if (r) toggle(r.dataset.id, true); });
$('#treeFilter').addEventListener('input', renderTree);

// ---------------------------------------------------------------- sidebar: plan

function renderPlan() {
  const M = S.model;
  const counts = {};
  for (const r of M.roadmap) counts[r.status] = (counts[r.status] || 0) + 1;
  $('#planChips').innerHTML = Object.keys(STATUS_RU).filter(s => counts[s]).map(s =>
    `<span class="chip ${S.planFilter.has(s) ? '' : 'off'}" data-st="${s}"><span class="dot" style="background:${STATUS_COLOR[s]}"></span>${STATUS_RU[s]} ${counts[s]}</span>`).join('');
  if (!M.roadmap.length) { $('#plan').innerHTML = '<div class="muted">Дорожная карта не найдена (ROADMAP*, TODO*, STATE*, PLAN* в репозитории).</div>'; return; }

  const html = [];
  const visibleItem = r => S.planFilter.has(r.status) || (M.rmChildren.get(r.id) || []).some(c => visibleItem(M.rmById.get(c)));
  const item = (r, depth) => {
    if (!visibleItem(r)) return;
    const kids = M.rmChildren.get(r.id) || [];
    const links = roadmapLinks(r).length;
    if (kids.length && depth === 0) html.push(`<div class="gh">${esc(r.title)}</div>`);
    if (!kids.length || depth > 0 || S.planFilter.has(r.status))
      html.push(`<div class="item ${S.selected === r.id ? 'sel' : ''}" data-id="${esc(r.id)}" style="margin-left:${depth * 10}px">
        <span class="dot" style="background:${STATUS_COLOR[r.status] || STATUS_COLOR.unknown};margin-top:5px"></span>
        <span class="t">${esc(r.title)}</span><span class="n">${links ? links + ' ⟶' : ''}</span></div>`);
    for (const c of kids) item(M.rmById.get(c), depth + 1);
  };
  for (const id of M.rmChildren.get(null) || []) item(M.rmById.get(id), 0);
  $('#plan').innerHTML = html.join('') || '<div class="muted">Нет задач с выбранными статусами</div>';
}
$('#planChips').addEventListener('click', e => {
  const c = e.target.closest('.chip'); if (!c) return;
  const s = c.dataset.st; S.planFilter.has(s) ? S.planFilter.delete(s) : S.planFilter.add(s);
  saveState(); renderPlan();
  if (S.view === 'plan') render({ fit: true });
});
$('#plan').addEventListener('click', e => {
  const it = e.target.closest('.item'); if (!it) return;
  if (S.view !== 'plan') setView('plan');
  select(it.dataset.id, { center: true });
});

// ---------------------------------------------------------------- sidebar: database

function renderDb() {
  const M = S.model, db = M.g.database;
  if (!db || !db.tables?.length) { $('#db').innerHTML = '<div class="muted">База данных не обнаружена.</div>'; return; }
  const tableNode = name => [...M.byId.values()].find(n => n.kind === 'table' && n.name.toLowerCase() === name.toLowerCase());
  $('#db').innerHTML = `<div class="muted" style="margin-bottom:8px">${esc(db.engine)} · ${db.tables.length} ${plural(db.tables.length, 'таблица', 'таблицы', 'таблиц')}</div>`
    + db.tables.map(t => {
      const n = tableNode(t.name);
      const users = n ? (M.edgesOf.get(n.id) || []).filter(e => EDGE_CAT[e.kind] === 'db').length : 0;
      return `<div class="item" data-id="${esc(n?.id || '')}"><span class="dot" style="background:var(--db);margin-top:5px"></span>
        <span class="t">${esc(t.name)}<br><span class="muted">${t.columns.length} ${plural(t.columns.length, 'колонка', 'колонки', 'колонок')}${t.foreignKeys?.length ? ' · ' + t.foreignKeys.length + ' FK' : ''}</span></span>
        <span class="n">${users ? users + ' ⟵' : ''}</span></div>`;
    }).join('')
    + `<h4 class="muted" style="margin-top:14px;font-size:11px">ИСТОЧНИКИ СХЕМЫ</h4>` + (db.sources || []).map(s => `<div class="path" data-path="${esc(s)}">${esc(s)}</div>`).join('');
}
$('#db').addEventListener('click', e => {
  const p = e.target.closest('[data-path]'); if (p) { send({ type: 'open', path: p.dataset.path }); return; }
  const it = e.target.closest('.item'); if (it?.dataset.id) reveal(it.dataset.id);
});

// ---------------------------------------------------------------- sidebar: hot spots

function renderHot() {
  const M = S.model;
  const key = { commits: n => n._agg.commits, complexity: n => n._agg.complexity, loc: n => n._agg.loc, links: n => (n._in || 0) + (n._out || 0) }[S.hotSort];
  const files = [...M.byId.values()].filter(n => n.kind === 'file' && !testHidden(n.id)).sort((a, b) => key(b) - key(a)).slice(0, 120);
  const max = Math.max(1, key(files[0] || { _agg: {} }) || 1);
  $('#hot').innerHTML = `<table class="tbl"><tbody>${files.map(n => `
    <tr class="click" data-id="${esc(n.id)}"><td class="nm" title="${esc(n.path)}">${esc(n.name)}
      <div class="minibar" style="width:${Math.max(2, 100 * key(n) / max)}%;background:${M.colorOf(n.id).line}"></div></td>
      <td class="num">${fmt(key(n))}</td></tr>`).join('')}</tbody></table>`;
}
$('#hotSeg').addEventListener('click', e => {
  const b = e.target.closest('button'); if (!b) return;
  S.hotSort = b.dataset.sort; S.heat = S.hotSort; saveState(); syncToolbar(); renderHot();
  if (S.view !== 'map') setView('map'); else render();
  renderLegend();
});
$('#hot').addEventListener('click', e => { const r = e.target.closest('tr'); if (r) reveal(r.dataset.id); });

// ---------------------------------------------------------------- details panel

function connections(id) {
  const M = S.model, vis = new Set(cy.nodes().map(n => n.id())), rep = makeRep(vis);
  const groups = { out: new Map(), in: new Map(), cochange: new Map(), db: new Map() };
  const under = x => M.isUnder(x, id);
  const seen = new Set();
  const stack = [id];
  while (stack.length) {
    const x = stack.pop();
    for (const e of M.edgesOf.get(x) || []) {
      if (seen.has(e)) continue; seen.add(e);
      const cat = EDGE_CAT[e.kind];
      if (cat === 'plan' || cat === 'package') continue;
      const sIn = under(e.source), tIn = under(e.target);
      if (sIn && tIn) continue;
      const other = sIn ? e.target : e.source;
      if (!M.byId.has(other)) continue;
      // a peer hidden on the graph (e.g. tests switched off) is counted once, as its top-level block
      let peer = rep(other) || [other, ...M.ancestors(other)].find(x => M.top.includes(x)) || other;
      if (M.isUnder(id, peer)) peer = other; // the peer is our own container: show the concrete element
      const g = cat === 'cochange' ? groups.cochange : cat === 'db' ? groups.db : sIn ? groups.out : groups.in;
      let r = g.get(peer);
      if (!r) g.set(peer, r = { peer, w: 0, kinds: new Set(), ev: [] });
      r.w += e.weight || 1; r.kinds.add(e.kind);
      // an empty path means "in the source's own file" (graph.json omits it to stay small)
      for (const v of e.evidence || []) if (r.ev.length < 8) r.ev.push(v.path ? v : { ...v, path: M.node(e.source)?.path || '' });
    }
    stack.push(...M.kids(x));
  }
  return groups;
}

// One category of connections, as its own card with a coloured edge and a plain explanation.
function connGroup(title, about, map, color, peerColor = false) {
  const list = [...map.values()].sort((a, b) => b.w - a.w);
  const M = S.model;
  if (!list.length) return '';
  const max = list[0].w;
  const rows = list.slice(0, 40).map((r, i) => {
    const pn = M.node(r.peer);
    const sub = pn._desc?.ru && pn._desc.ru !== pn.name ? ` <small>${esc(pn.name)}</small>` : '';
    return `
    <div class="conn ${i >= 8 ? 'hidden extra' : ''}" data-peer="${esc(r.peer)}" title="${esc(pn.path || pn.name)}">
      <div class="top"><span class="dot" style="background:${M.colorOf(r.peer).line}"></span>
        <span class="nm">${esc(titleOf(pn))}${sub}</span><span class="w">×${r.w}</span></div>
      <div class="bar" style="width:${Math.max(3, 100 * r.w / max)}%;background:${peerColor ? M.colorOf(r.peer).line : color}"></div>
      ${r.ev.length ? `<div class="ev">${r.ev.map(v => `<div data-path="${esc(v.path)}" data-line="${v.line}">${esc(v.path.split('/').pop())}:${v.line}${v.snippet ? '  ' + esc(v.snippet.trim()) : ''}</div>`).join('')}</div>` : ''}
    </div>`;
  }).join('');
  return `<div class="card grp" style="--gc:${color}"><h3>${title}<span class="c">${list.length}</span></h3>
    <div class="gd">${about}</div>${rows}
    ${list.length > 8 ? `<div class="more" data-more="1">показать ещё ${Math.min(32, list.length - 8)}</div>` : ''}</div>`;
}

function fact(v, label) { return `<div class="fact"><b>${v}</b><span>${label}</span></div>`; }

function tabs(defs) {
  if (!defs.some(([k]) => k === S.dtab)) S.dtab = defs[0][0];
  return `<div class="dtabs">${defs.map(([k, t, c]) =>
    `<button data-dtab="${k}" class="${S.dtab === k ? 'active' : ''}">${t}${c ? `<span class="c">${c}</span>` : ''}</button>`).join('')}</div>`;
}

function renderDetails() {
  const M = S.model, el = $('#details');
  if (!M) { el.innerHTML = ''; return; }
  const id = S.selected;
  if (!id) return renderOverview();
  if (M.rmById.has(id)) return renderRoadmapDetails(M.rmById.get(id));
  const n = M.node(id);
  if (!n) return renderOverview();
  const a = n._agg, m = n.metrics, col = M.colorOf(id);
  const c = connections(id);
  const items = [...new Set((M.rmByNode.get(id) || []).concat(...M.ancestors(id).map(x => M.rmByNode.get(x) || [])))].slice(0, 30);
  const tbl = n.kind === 'table' ? (M.g.database?.tables || []).find(t => t.name.toLowerCase() === n.name.toLowerCase()) : null;
  const linkCount = c.out.size + c.in.size + c.db.size + c.cochange.size;
  const codeNote = n.summary || (n.kind === 'file' ? fileAboutQuick(n) : '');
  const kids = M.kids(id).filter(k => !testHidden(k)).length;

  const head = `<div class="card head" style="--hd:${col.line}">
    <span class="kind">${esc(KIND_RU[n.kind] || n.kind)}</span>
    <h2>${esc(titleOf(n))}</h2>
    ${n._desc?.ru && n._desc.ru !== n.name ? `<div class="orig">${esc(n.name)}</div>` : ''}
    ${n.path ? `<div class="path" data-path="${esc(n.path)}" data-line="${n.line || 0}" title="Открыть в редакторе">${esc(n.path)}${n.line ? ':' + n.line : ''}</div>` : ''}
    <div class="what prose">
      <h4>Что это</h4>
      ${mdLite(n._desc?.hint ? n._desc.hint[0].toUpperCase() + n._desc.hint.slice(1) + '.' : codeNote ? 'Своего описания нет — ниже то, что сказано о нём в проекте.' : 'Описание не найдено — смотрите связи и содержимое.')}
      ${codeNote ? `<h4>${n.kind === 'module' || n.kind === 'folder' ? 'Из документации проекта' : 'Из комментария в коде'}</h4>${mdLite(codeNote)}` : ''}</div>
    <div class="actions">
      ${M.hasKids(id) ? `<button data-act="toggle" class="main">${S.expanded.has(id) ? 'Свернуть на схеме' : 'Раскрыть на схеме'}</button>` : ''}
      <button data-act="focus">${S.focus === id ? 'Показать всё' : 'Показать только его связи'}</button>
      ${n.kind !== 'type' && n.kind !== 'member' ? '<button data-act="tree">Подробно по файлам</button>' : ''}
      ${['file', 'type', 'member'].includes(n.kind) && n.path ? '<button data-act="view" class="main">Открыть файл здесь</button>' : ''}
      ${n.path ? '<button data-act="open">Открыть файл</button><button data-act="reveal">Папка</button>' : ''}
    </div></div>`;

  const T = tabs([
    ['about', 'Обзор'], ['links', 'Связи', linkCount || ''],
    ...(kids ? [['inside', 'Внутри', kids]] : []), ...(items.length ? [['plan', 'План', items.length]] : []),
  ]);

  let body = '';
  if (S.dtab === 'about') {
    const sizeFacts = n.kind === 'file' || n.kind === 'type' || n.kind === 'member'
      ? fact(a.types || a.members || 0, n.kind === 'type' ? 'функций и свойств' : 'классов внутри')
      : fact(a.files, 'файлов внутри');
    body = `<div class="card"><h3>Размер и активность</h3><div class="facts">
        ${n.kind === 'member' ? fact(n.line ? 'строка ' + n.line : '—', 'где в файле') : fact(fmt(a.loc), 'строк кода — объём')}${sizeFacts}
        ${fact(a.commits || 0, n.kind === 'file' ? 'правок за год (git)' : 'правок файлов за год (git)')}${fact(a.last ? ago(a.last) : '—', 'последняя правка')}
        ${fact(a.complexity || '—', 'сложность самой запутанной функции')}${fact(a.authors || '—', 'авторов правок')}
      </div></div>
      <div class="card"><h3>Связи коротко</h3><div class="facts">
        ${fact(c.out.size, 'частей, которыми пользуется')}${fact(c.in.size, 'частей пользуются им')}
        ${fact(c.cochange.size, 'правятся вместе с ним')}${fact(c.db.size, 'связей с базой данных')}
      </div></div>
      ${tbl ? `<div class="card"><h3>Колонки таблицы<span class="c">${tbl.columns.length}</span></h3><table class="tbl">${tbl.columns.map(cl =>
        `<tr><td>${cl.primaryKey ? '🔑 ' : ''}${esc(cl.name)}</td><td class="num">${esc(cl.type)}${cl.notNull ? ' · обязательно' : ''}</td></tr>`).join('')}</table>
        ${tbl.foreignKeys?.length ? `<h3 style="margin-top:12px">Ссылки на другие таблицы</h3>${tbl.foreignKeys.map(f => `<div class="muted">${esc(f.column)} → ${esc(f.refTable)}.${esc(f.refColumn)}</div>`).join('')}` : ''}</div>` : ''}
      ${n.tags.length ? `<div class="card"><h3>Метки</h3><div class="tags">${n.tags.map(t => `<span class="tag">${esc(TAG_RU[t] || t)}</span>`).join('')}</div></div>` : ''}`;
  } else if (S.dtab === 'links') {
    body = (connGroup('Пользуется', 'части, без которых эта не работает (стрелки от неё)', c.out, col.line)
      + connGroup('Кто пользуется им', 'части, которые сломаются, если её поменять неудачно', c.in, CAT_UI.code.color, true)
      + connGroup(n.kind === 'table' ? 'Код, работающий с таблицей' : 'База данных', 'чтение и запись таблиц', c.db, CAT_UI.db.color)
      + connGroup('Правится вместе с', 'по истории git: когда меняли эту часть, обычно меняли и эти', c.cochange, CAT_UI.cochange.color))
      || '<div class="card empty">Связей с другими частями не найдено.</div>';
  } else if (S.dtab === 'inside') {
    body = childTable(id);
  } else if (S.dtab === 'plan') {
    body = `<div class="card"><h3>Задачи плана, которые его касаются<span class="c">${items.length}</span></h3>${items.map(rid => {
      const r = M.rmById.get(rid);
      return `<div class="item" data-rm="${esc(rid)}"><span class="status st-${r.status}">${STATUS_RU[r.status] || r.status}</span><span class="t">${esc(r.title)}</span></div>`;
    }).join('')}</div>`;
  }
  el.innerHTML = head + T + body;
}

const TAG_RU = { test: 'проверки', generated: 'создан автоматически', entrypoint: 'точка запуска', ui: 'интерфейс', hot: 'часто меняется' };

let childSort = { key: 'loc', desc: true };
function childTable(id) {
  const M = S.model;
  const kids = M.kids(id).map(k => M.node(k)).filter(k => !testHidden(k.id));
  if (!kids.length) return '';
  const val = { name: k => titleOf(k).toLowerCase(), loc: k => k._agg.loc, commits: k => k._agg.commits, complexity: k => k._agg.complexity };
  kids.sort((x, y) => {
    const a = val[childSort.key](x), b = val[childSort.key](y);
    return (a < b ? -1 : a > b ? 1 : 0) * (childSort.desc ? -1 : 1);
  });
  const arrow = k => childSort.key === k ? (childSort.desc ? ' ↓' : ' ↑') : '';
  return `<div class="card"><h3>Из чего состоит<span class="c">${kids.length}</span></h3>
    <div class="gd muted" style="font-size:12.5px;margin-bottom:6px">Нажмите на заголовок столбца, чтобы отсортировать.</div>
    <table class="tbl"><thead><tr>
    <th data-sort="name">Название${arrow('name')}</th><th data-sort="loc">Строк${arrow('loc')}</th>
    <th data-sort="commits">Правок${arrow('commits')}</th></tr></thead><tbody>
    ${kids.slice(0, 200).map(k => `<tr class="click" data-id="${esc(k.id)}" title="${esc(k._desc?.hint || k.summary || k.path || '')}"><td class="nm"><span class="dot" style="background:${M.colorOf(k.id).line}"></span> ${esc(titleOf(k))}${k._desc?.ru && k._desc.ru !== k.name ? `<br><span class="muted" style="font-size:11.5px">${esc(k.name)}</span>` : ''}
      ${(() => { const d = k._desc?.hint || (k.kind === 'file' ? fileAboutQuick(k) : k.summary); return d ? `<div class="muted" style="font-size:12px;white-space:normal;line-height:1.35;margin-top:2px">${esc(d)}</div>` : ''; })()}</td>
      <td class="num">${fmt(k._agg.loc)}</td><td class="num">${k._agg.commits || ''}</td></tr>`).join('')}
    </tbody></table></div>`;
}

function renderRoadmapDetails(r) {
  const M = S.model;
  const links = roadmapLinks(r);
  const kids = M.rmChildren.get(r.id) || [];
  const chain = []; for (let p = r._parent; p; p = M.rmById.get(p)._parent) chain.unshift(M.rmById.get(p).title);
  $('#details').innerHTML = `
    <div class="card head" style="--hd:${STATUS_COLOR[r.status] || STATUS_COLOR.unknown}">
      <span class="kind">задача плана</span> <span class="status st-${r.status}">${STATUS_RU[r.status] || r.status}</span>
      ${chain.length ? `<div class="muted" style="margin-top:8px;font-size:12.5px">${chain.map(esc).join(' › ')}</div>` : ''}
      <h2 style="${r.title.length > 90 ? 'font-size:15px;line-height:1.45' : ''}">${esc(r.title)}</h2>
      ${r.source ? `<div class="path" data-path="${esc(r.source.path)}" data-line="${r.source.line}">${esc(r.source.path)}:${r.source.line}</div>` : ''}
      ${r.detail ? `<div class="what prose scroll"><h4>Описание задачи</h4>${mdLite(r.detail)}</div>` : ''}
    </div>
    <div class="card grp" style="--gc:var(--warn)"><h3>Затрагивает в коде<span class="c">${links.length}</span></h3>
      <div class="gd">найдено по упоминаниям в тексте и по коммитам с номером задачи; на схеме подсвечено жёлтым</div>
      ${links.length ? links.slice(0, 60).map(l => {
        const n = M.node(l);
        return `<div class="conn" data-peer="${esc(l)}"><div class="top"><span class="dot" style="background:${M.colorOf(l).line}"></span><span class="nm">${esc(titleOf(n))}${n._desc?.ru && n._desc.ru !== n.name ? ` <small>${esc(n.name)}</small>` : ''}</span><span class="w">${esc((KIND_RU[n.kind] || n.kind).split(' ')[0])}</span></div></div>`;
      }).join('') : '<div class="empty">Связей с кодом не найдено.</div>'}
    </div>
    ${kids.length ? `<div class="card"><h3>Подзадачи<span class="c">${kids.length}</span></h3>${kids.map(k => {
      const ch = M.rmById.get(k);
      return `<div class="item" data-rm="${esc(k)}"><span class="status st-${ch.status}">${STATUS_RU[ch.status] || ch.status}</span><span class="t">${esc(ch.title)}</span></div>`;
    }).join('')}</div>` : ''}`;
}

function renderOverview() {
  const M = S.model, g = M.g, p = g.project || {};
  const count = k => g.nodes.filter(n => n.kind === k).length;
  const st = {}; for (const r of M.roadmap) st[r.status] = (st[r.status] || 0) + 1;
  const total = M.roadmap.length || 1;
  const cats = CATEGORIES.map(([key, title, about]) => {
    const ids = M.top.filter(id => M.categoryOf(id) === key);
    return ids.length ? `<div class="card grp" style="--gc:${M.colorOf(ids[0]).line}"><h3>${title}<span class="c">${ids.length}</span></h3>
      <div class="gd">${about}</div>
      <table class="tbl">${ids.map(id => M.node(id)).sort((a, b) => b._agg.loc - a._agg.loc).map(n => `<tr class="click" data-id="${esc(n.id)}" title="${esc(n._desc?.hint || n.summary || '')}">
        <td class="nm"><span class="dot" style="background:${M.colorOf(n.id).line}"></span> ${esc(titleOf(n))}${n._desc?.ru && n._desc.ru !== n.name ? `<br><span class="muted" style="font-size:11.5px">${esc(n.name)}</span>` : ''}</td>
        <td class="num">${n._agg.files ? n._agg.files + ' ф.' : ''}</td></tr>`).join('')}</table></div>` : '';
  }).join('');
  $('#details').innerHTML = `
    <div class="card head" style="--hd:var(--accent)">
      <span class="kind">проект</span><h2>${esc(p.name)}</h2>
      <div class="path" data-path="" title="Открыть папку">${esc(p.root)}</div>
      <div class="facts" style="margin-top:12px">
        ${fact(fmt(count('file')), 'файлов')}${fact(count('module'), 'крупных частей (модулей)')}
        ${fact(fmt(count('type')), 'классов (блоков кода)')}${fact(count('table') || '—', 'таблиц в базе данных')}
      </div>
      <div class="muted" style="margin-top:10px;font-size:12.5px">Обновлено ${p.scannedAt ? new Date(p.scannedAt).toLocaleString('ru') : '—'}${p.scanMilliseconds ? ` · разбор занял ${(p.scanMilliseconds / 1000).toFixed(0)} с` : ''}</div>
    </div>
    ${M.roadmap.length ? `<div class="card"><h3>Дорожная карта<span class="c">${M.roadmap.length} задач</span></h3>
      <div class="stbar">${Object.keys(STATUS_RU).filter(s => st[s]).map(s => `<i style="width:${100 * st[s] / total}%;background:${STATUS_COLOR[s]}" title="${STATUS_RU[s]}: ${st[s]}"></i>`).join('')}</div>
      <div class="chips">${Object.keys(STATUS_RU).filter(s => st[s]).map(s => `<span class="status st-${s}">${STATUS_RU[s]} ${st[s]}</span>`).join('')}</div>
      <div class="gd muted" style="font-size:12.5px;margin-top:8px">Все задачи — во вкладке «План» слева.</div></div>` : ''}
    ${cats}
    <div class="card"><h3>Как читать схему</h3><ul class="howto">
      <li><b>Стрелка А → Б</b>: часть А пользуется частью Б. Сверху — те, кто пользуется, внизу — то, на чём всё держится.</li>
      <li>Цвет стрелки — цвет блока, <b>от которого</b> она идёт.</li>
      <li>Лишние стрелки скрыты: если А → Б → В, прямая А → В не рисуется. Клик по блоку покажет все его связи.</li>
      <li>Пунктир — части, которые обычно правят вместе; точки — работа с базой данных.</li>
      <li>Блоки в рамках справа не связаны стрелками с программой: это инструменты, проверки, документы.</li>
    </ul></div>
    <div class="card"><h3>Как пользоваться</h3><ul class="howto">
      <li><b>Клик</b> по блоку — подробности здесь и подсветка его связей.</li>
      <li><b>Двойной клик</b> — заглянуть внутрь (папки, файлы, классы).</li>
      <li>Кнопки <b>«Модули / Папки / Файлы / Классы»</b> вверху — глубина сразу для всего проекта.</li>
      <li><b>Ctrl+K</b> — поиск. <b>Esc</b> — сбросить выбор. <b>Правый клик</b> — меню.</li>
    </ul></div>
    ${g.warnings?.length ? `<div class="card"><h3>Что разобрать не удалось<span class="c">${g.warnings.length}</span></h3><div class="warnlist">${g.warnings.slice(0, 30).map(w => `<div>${esc(w.message)}${w.path ? ' — ' + esc(w.path) : ''}</div>`).join('')}</div></div>` : ''}`;
}

$('#details').addEventListener('click', e => {
  const t = e.target;
  const path = t.closest('[data-path]');
  if (path) { send(path.dataset.path ? { type: 'open', path: path.dataset.path, line: +path.dataset.line || 0 } : { type: 'reveal', path: '' }); return; }
  const dt = t.closest('[data-dtab]');
  if (dt) { S.dtab = dt.dataset.dtab; renderDetails(); return; }
  const more = t.closest('[data-more]');
  if (more) { more.parentElement.querySelectorAll('.extra').forEach(x => x.classList.remove('hidden')); more.remove(); return; }
  const th = t.closest('th[data-sort]');
  if (th) { const k = th.dataset.sort; childSort = { key: k, desc: childSort.key === k ? !childSort.desc : k !== 'name' }; renderDetails(); return; }
  const rm = t.closest('[data-rm]'); if (rm) { select(rm.dataset.rm); return; }
  const conn = t.closest('.conn');
  if (conn) {
    // first click shows the evidence lines, the second one jumps to the peer
    if (conn.querySelector('.ev') && !conn.classList.contains('open')) { conn.classList.add('open'); return; }
    reveal(conn.dataset.peer); return;
  }
  const row = t.closest('tr[data-id]'); if (row) { reveal(row.dataset.id); return; }
  const act = t.closest('button[data-act]')?.dataset.act;
  const n = S.model.node(S.selected);
  if (!act || !n) return;
  if (act === 'toggle') toggle(n.id);
  if (act === 'focus') setFocus(n.id);
  if (act === 'view') { openFileView(n.id, n.kind === 'file' ? null : n.id); return; }
  if (act === 'tree') {
    // open the tree diagram down to this block (and one level inside it) and select it there
    const M = S.model, top = [n.id, ...M.ancestors(n.id)].find(x => M.top.includes(x));
    TREE.open.add('tree:root');
    if (top) TREE.open.add('tree:cat:' + M.categoryOf(top));
    for (const a of M.ancestors(n.id)) TREE.open.add(a);
    if (M.hasKids(n.id) && n.kind !== 'type') TREE.open.add(n.id);
    if (countTreeBlocks() > MAX_BLOCKS) TREE.open.delete(n.id);
    S.view = null; setView('tree');
    select(n.id);
    return;
  }
  if (act === 'open') send({ type: 'open', path: n.path, line: n.line || 0 });
  if (act === 'reveal') send({ type: 'reveal', path: n.path });
  renderDetails();
});
$('#details').addEventListener('dblclick', e => { const c = e.target.closest('.conn'); if (c) reveal(c.dataset.peer); });

// ---------------------------------------------------------------- graph events

// selectable = a project block or a plan item; frames of the extra views are only containers
const selectable = id => !!(S.model?.node(id) || S.model?.rmById.has(id));
const nameOf = id => S.model.node(id) ? titleOf(S.model.node(id)) : S.model.rmById.get(id)?.title || cy.getElementById(id).data('name') || id;

// a click on an arrow of the arrow layer (over the background or an open frame) selects where it starts
function tapLayerArrow() {
  const id = EdgeLayer.hovered();
  if (!id) return false;
  const s = cy.getElementById(id).data('source');
  if (s && selectable(s)) select(s);
  return true;
}
cy.on('tap', 'node', e => {
  if (e.target.data('frame') && tapLayerArrow()) return;
  const id = e.target.data('ref') || e.target.id(); // «Файл»: outside blocks point to a project block
  if (selectable(id)) select(id);
});
cy.on('tap', e => { if (e.target === cy) { if (!tapLayerArrow()) select(null); hideCtx(); } });
cy.on('dbltap', 'node', e => {
  const id = e.target.id(), M = S.model;
  if (S.view === 'file') { const ref = e.target.data('ref'); if (ref) openFileView(ref, M.node(ref)?.kind === 'file' ? null : ref); return; }
  if (S.view === 'tree') {
    if (M.node(id)?.kind === 'file') { openFileView(id); return; }
    if (id.startsWith('tree:') || (M.node(id) && M.hasKids(id))) toggleTree(id);
    return;
  }
  if (S.view === 'plan' || S.view === 'db') { if (S.model.node(id)) { setView('map'); reveal(id); } return; }
  if (S.model.node(id)) toggle(id);
});
cy.on('mouseover', 'edge', e => {
  const d = e.target.data();
  const a = `«${nameOf(d.source)}»`, b = `«${nameOf(d.target)}»`;
  $('#cy').title = e.target.hasClass('tree') ? `${b} находится внутри ${a}`
    : e.target.hasClass('planlink') ? `Задача ${a} затрагивает ${b}`
    : d.cat === 'cochange' ? `${a} и ${b} правились вместе в ${d.w} ${plural(d.w, 'коммите', 'коммитах', 'коммитах')}`
    : d.cat === 'db' || e.target.hasClass('db') ? `${a} → ${b}${d.lbl ? ': ' + d.lbl : ''}`
    : `${a} пользуется ${b} — ${d.w || ''} ${plural(d.w || 0, 'обращение', 'обращения', 'обращений')} в коде`;
});
cy.on('mouseout', 'edge', () => { $('#cy').title = ''; });
cy.on('tap', 'edge', e => { const s = e.target.data('source'); if (selectable(s)) select(s); });

// context menu
function hideCtx() { $('#ctx').classList.add('hidden'); }
cy.on('cxttap', 'node', e => {
  const id = e.target.id(), n = S.model.node(id), M = S.model;
  if (!n) return; // category frame
  select(id);
  const opts = [];
  if (M.hasKids(id)) opts.push([S.expanded.has(id) ? 'Свернуть' : 'Раскрыть', () => toggle(id)]);
  if (S.expanded.has(id)) opts.push(['Свернуть всё внутри', () => { for (const x of [...S.expanded]) if (x !== id && M.isUnder(x, id)) S.expanded.delete(x); saveState(); render({ anchor: id }); }]);
  if (M.hasKids(id)) opts.push(['Раскрыть всё внутри', () => {
    const next = new Set(S.expanded), st = [id];
    while (st.length) { const x = st.pop(); if (M.hasKids(x) && M.node(x).kind !== 'type') { next.add(x); st.push(...M.kids(x)); } }
    const n = countVisible(next);
    if (n > MAX_BLOCKS) { toast(`Внутри ${n} блоков — слишком много для одной схемы. Раскрывайте по одной папке или откройте «Подробное древо».`, 6000); return; }
    S.expanded = next; saveState(); render({ anchor: id });
  }]);
  opts.push([S.focus === id ? 'Снять фокус' : 'Фокус на связях', () => setFocus(id)]);
  if (n.path) {
    opts.push(null);
    if (['file', 'type', 'member'].includes(n.kind)) opts.push(['Открыть файл здесь (функции и код)', () => openFileView(id, n.kind === 'file' ? null : id)]);
    opts.push(['Открыть во внешнем редакторе', () => send({ type: 'open', path: n.path, line: n.line || 0 })]);
    opts.push(['Показать в проводнике', () => send({ type: 'reveal', path: n.path })]);
    opts.push(['Копировать путь', () => navigator.clipboard?.writeText(n.path)]);
  }
  const box = $('#ctx');
  box.innerHTML = opts.map((o, i) => o ? `<div class="opt" data-i="${i}">${o[0]}</div>` : '<hr>').join('');
  box._opts = opts;
  const p = e.renderedPosition || e.target.renderedPosition(); // keyboard/emitted events have no pointer
  box.style.left = Math.min(p.x, $('#stage').clientWidth - 220) + 'px';
  box.style.top = Math.min(p.y, $('#stage').clientHeight - 240) + 'px';
  box.classList.remove('hidden');
});
$('#ctx').addEventListener('click', e => { const o = e.target.closest('.opt'); if (!o) return; hideCtx(); $('#ctx')._opts[+o.dataset.i][1](); });
document.addEventListener('click', e => { if (!e.target.closest('#ctx')) hideCtx(); });

// ---------------------------------------------------------------- search

let searchHits = [], searchIdx = 0;
function doSearch(q) {
  const M = S.model, box = $('#searchResults');
  q = q.trim().toLowerCase();
  if (!M || !q) { box.classList.add('hidden'); return; }
  const hits = [];
  const score = (name, path) => {
    const nm = name.toLowerCase();
    if (nm === q) return 100;
    if (nm.startsWith(q)) return 80 - nm.length / 100;
    if (nm.includes(q)) return 60 - nm.length / 100;
    if (path && path.toLowerCase().includes(q)) return 30;
    // camel-case initials: "gvm" → GraphViewModel
    const caps = name.replace(/[^A-Z]/g, '').toLowerCase();
    if (q.length > 1 && caps.startsWith(q)) return 50;
    return 0;
  };
  for (const [id, n] of M.byId) {
    if (n.kind === 'system') continue;
    const s = score(n.name, n.path);
    if (s) hits.push({ id, s: s + (n.kind === 'module' ? 5 : n.kind === 'file' ? 3 : n.kind === 'type' ? 2 : 0), n, kind: KIND_RU[n.kind] || n.kind, sub: n.path || M.node(M.parent(id) || id)?.name });
  }
  for (const r of M.roadmap) {
    const s = score(r.title, '');
    if (s) hits.push({ id: r.id, s: s - 5, n: { name: r.title }, kind: STATUS_RU[r.status] || 'план', sub: r.source?.path });
  }
  searchHits = hits.sort((a, b) => b.s - a.s).slice(0, 40);
  searchIdx = 0;
  box.innerHTML = searchHits.map((h, i) => `<div class="opt ${i === 0 ? 'hl' : ''}" data-i="${i}"><span class="k">${esc(h.kind)}</span><span>${esc(h.n.name)}</span><span class="p">${esc(h.sub || '')}</span></div>`).join('')
    || '<div class="opt muted">Ничего не найдено</div>';
  box.classList.remove('hidden');
}
function pickSearch(i) {
  const h = searchHits[i]; if (!h) return;
  $('#searchResults').classList.add('hidden');
  $('#search').blur();
  if (S.model.rmById.has(h.id)) { switchTab('plan'); select(h.id); } else reveal(h.id);
}
$('#search').addEventListener('input', e => doSearch(e.target.value));
$('#search').addEventListener('focus', e => doSearch(e.target.value));
$('#search').addEventListener('keydown', e => {
  const opts = $$('#searchResults .opt[data-i]');
  if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
    e.preventDefault();
    searchIdx = (searchIdx + (e.key === 'ArrowDown' ? 1 : -1) + opts.length) % Math.max(1, opts.length);
    opts.forEach((o, i) => o.classList.toggle('hl', i === searchIdx));
    opts[searchIdx]?.scrollIntoView({ block: 'nearest' });
  }
  if (e.key === 'Enter') pickSearch(searchIdx);
  if (e.key === 'Escape') { $('#searchResults').classList.add('hidden'); e.target.blur(); }
});
$('#searchResults').addEventListener('mousedown', e => { const o = e.target.closest('.opt[data-i]'); if (o) { e.preventDefault(); pickSearch(+o.dataset.i); } });
$('#search').addEventListener('blur', () => setTimeout(() => $('#searchResults').classList.add('hidden'), 150));

// ---------------------------------------------------------------- toolbar, tabs, keys

function switchTab(t) {
  $$('#sideTabs button').forEach(b => b.classList.toggle('active', b.dataset.tab === t));
  $$('.tabpane').forEach(p => p.classList.toggle('active', p.id === 'tab-' + t));
}
$('#sideTabs').addEventListener('click', e => {
  const b = e.target.closest('button'); if (!b || !S.model) return;
  const t = b.dataset.tab;
  switchTab(t);
  // the side lists drive the picture: plan/db open their diagrams, «Горячие» colours the map
  const heat = t === 'hot' ? S.hotSort : null;
  if (t === 'plan' || t === 'db') { S.heat = null; setView(t); return; }
  if (heat !== S.heat || S.view !== 'map') { S.heat = heat; if (S.view !== 'map') setView('map'); else render(); renderLegend(); }
});
$('#depthSeg').addEventListener('click', e => { const b = e.target.closest('button'); if (b && S.model) setDepth(+b.dataset.depth); });
// re-fit when the window size changes (maximize, second monitor), unless the user is looking at a block
let resizeTimer;
window.addEventListener('resize', () => {
  clearTimeout(resizeTimer);
  resizeTimer = setTimeout(() => { cy.resize(); if (!S.selected) cy.fit(diagramNodes(), 40); }, 200);
});
systemDark.addEventListener('change', () => {
  setTheme(systemDark.matches ? 'dark' : 'light');
  cy.style(buildStyle());
  if (S.model) { render({ animate: false }); renderTree(); renderDetails(); renderHot(); }
});
$('#btnFit').onclick =() => smoothView({ fit: { eles: diagramNodes(), padding: 40 } });
$('#btnRescan').onclick = () => { send({ type: 'rescan' }); showProgress('Сканирование…', 0.02); };
$('#btnLive').onclick = () => { S.live = !S.live; send({ type: 'setLive', on: S.live }); syncToolbar(); toast(S.live ? 'Живое обновление включено' : 'Живое обновление выключено'); };
$('#btnOpen').onclick = () => $('#start').classList.remove('hidden');
$('#btnPick').onclick = () => send({ type: 'pickFolder' });
$('#start').addEventListener('click', e => {
  const it = e.target.closest('.item'); if (it) { send({ type: 'openProject', root: it.dataset.root }); return; }
  if (e.target.id === 'start' && S.model) $('#start').classList.add('hidden');
});

document.addEventListener('keydown', e => {
  const typing = e.target instanceof Element && e.target.matches('input, textarea');
  if ((e.ctrlKey && e.key.toLowerCase() === 'k') || (!typing && e.key === '/')) { e.preventDefault(); $('#search').focus(); $('#search').select(); return; }
  if (typing) return;
  if (e.key === 'Escape') { if (S.focus) setFocus(S.focus); else select(null); hideCtx(); }
  if (e.key === 'f' || e.key === 'F') smoothView({ fit: { eles: diagramNodes(), padding: 40 } });
  if (e.key === 'Enter' && S.selected && S.model.byId.has(S.selected)) toggle(S.selected, true);
  if (e.key === 'Backspace' && S.selected && S.model.byId.has(S.selected)) {
    const p = S.expanded.has(S.selected) ? S.selected : S.model.parent(S.selected);
    if (p && p !== S.model.systemId) { toggle(p, false); select(p); }
  }
  if (e.key >= '1' && e.key <= '4' && S.model) setDepth(+e.key);
});

function renderRecent(items) {
  $('#recent').innerHTML = items.length ? items.map(r => `<div class="item" data-root="${esc(r.root)}"><span class="t"><b>${esc(r.name)}</b><br><span class="muted">${esc(r.root)}</span></span><span class="n">${ago(r.openedAt)}</span></div>`).join('')
    : '<div class="muted">Пока пусто</div>';
}

function showProgress(label, f) {
  $('#progress').classList.remove('hidden');
  $('#progress .bar').style.width = Math.max(2, Math.min(100, (f || 0) * 100)) + '%';
  $('#progress .label').textContent = label;
}
function hideProgress() { $('#progress').classList.add('hidden'); }
let toastTimer;
function toast(text, ms = 2600) {
  const t = $('#toast'); t.textContent = text; t.classList.remove('hidden');
  clearTimeout(toastTimer); toastTimer = setTimeout(() => t.classList.add('hidden'), ms);
}

// fade the "changed" marks after their 20 s window
setInterval(() => {
  if (!S.changed.size) return;
  const now = Date.now();
  let stale = false;
  for (const [p, t] of S.changed) if (now - t > 20000) { S.changed.delete(p); stale = true; }
  if (stale) cy.nodes('.changed').forEach(n => {
    const path = S.model?.node(n.id())?.path?.toLowerCase();
    if (!path || ![...S.changed.keys()].some(p => p.startsWith(path))) n.removeClass('changed');
  });
}, 3000);

// ---------------------------------------------------------------- boot

syncToolbar();
if (host) send({ type: 'ready' });
else {
  // browser dev mode: index.html?graph=<url>
  const url = new URLSearchParams(location.search).get('graph');
  if (url) fetch(url).then(r => r.json()).then(g => loadGraph(g, g.project?.root || url));
  else $('#start').classList.remove('hidden');
}
