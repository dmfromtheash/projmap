// Five more pictures of the same tiers × features data, inside the «Схема связей» area (linkview.js):
// «Дерево», «Плитки», «Солнце» — the hierarchy program → tier → feature → file (size = lines of code);
// «Созвездие» — feature blocks around a circle, grouped by tier, links bundled through the middle;
// «Потоки» — ribbons from tiers to features (width = files). Each zooms (wheel, drag, F) and opens inside:
// a click goes one level down, a file opens in «Связи» with its classes and functions, the crumbs go back.
'use strict';

const SV_MODES = [['links', 'Связи'], ['tree', 'Дерево'], ['tiles', 'Плитки'], ['sun', 'Солнце'], ['stars', 'Созвездие'], ['flows', 'Потоки']];
const SV_FONT = '12.5px "Segoe UI Variable Text", "Segoe UI", sans-serif';
const SV_FONT_B = '600 13px "Segoe UI Variable Text", "Segoe UI", sans-serif';
const svOpenTree = new Set(['root']);

// program → tiers → features → files, with values (lines of code) summed upwards
function svHier(D) {
  const M = S.model;
  const rows = LV_ROWS.filter(t => [...D.cells.values()].some(c => c.tier === t) && (t !== 'tests' || LV.showTests) && (t !== 'docs' || LV.showDocs));
  const colIds = [...D.cols, LV_REST, LV_OTHER];
  const root = { id: 'root', name: 'Вся программа', depth: 0, children: [] };
  for (const t of rows) {
    const tier = { id: 'tier:' + t, tier: t, name: lvTierName(t), depth: 1, parent: root, children: [] };
    for (const c of colIds) {
      const cell = D.cells.get(t + '|' + c);
      if (!cell) continue;
      const cn = { id: cell.key, cell: cell.key, tier: t, name: lvColName(c), depth: 2, parent: tier, children: [] };
      for (const f of cell.files) {
        const n = M.node(f);
        cn.children.push({ id: f, file: f, tier: t, name: n.name, depth: 3, parent: cn, value: Math.max(1, n.metrics?.loc || 1), guess: ['guess', 'unknown'].includes(D.assign.get(f)?.decision) });
      }
      cn.children.sort((a, b) => b.value - a.value);
      tier.children.push(cn);
    }
    root.children.push(tier);
  }
  const sum = n => n.value = n.children ? n.children.reduce((s, c) => s + sum(c), 0) || 1 : n.value;
  sum(root);
  for (const n of [root, ...root.children]) n.children.sort((a, b) => b.value - a.value);
  return root;
}
function svFind(root, id) { const st = [root]; while (st.length) { const n = st.pop(); if (n.id === id) return n; if (n.children) st.push(...n.children); } return null; }
function svFocus(root) {
  const top = LV.stack[LV.stack.length - 1];
  return top?.cell ? svFind(root, top.cell) : top?.tier ? svFind(root, 'tier:' + top.tier) : root;
}
// one level down from a hierarchy node: a file opens in «Связи» with its classes
function svOpen(n) {
  if (n.file) { LV.stack = lvPathTo(n.file); LV.mode = 'links'; }
  else if (n.cell) LV.stack = [{ tier: n.tier }, { cell: n.cell }];
  else if (n.tier) LV.stack = [{ tier: n.tier }];
  else LV.stack = [];
  LV.sel = null;
  lvRender();
}
const svTip = n => `<b>${esc(n.file ? S.model.node(n.file).path : n.cell ? `${lvTierName(n.tier)} · ${n.name}` : n.name)}</b><span>${n.file ? `${fmt(n.value)} строк` : `${n.children.length} ${plural(n.children.length, 'часть', 'части', 'частей')} · ${fmt(n.value)} строк кода`}${n.guess ? ' · ярус — предположение' : ''}<br>нажмите — ${n.file ? 'классы и связи файла' : 'заглянуть внутрь'}</span>`;
function svHover(el, n) {
  el.addEventListener('mousemove', e => lvTip(e, svTip(n)));
  el.addEventListener('mouseleave', lvTipHide);
  el.addEventListener('click', e => { e.stopPropagation(); lvTipHide(); svOpen(n); });
  el.style.cursor = 'pointer';
}
function svText(parent, x, y, text, width, attrs = {}) {
  if (width < 24) return null;
  const s = lvFit(text, width, attrs.bold ? SV_FONT_B : SV_FONT);
  if (s === '…' || s.length < 2) return null;
  const t = lvEl('text', { x, y, 'font-size': attrs.bold ? 13 : 12.5, 'font-weight': attrs.bold ? 600 : null, fill: attrs.fill || 'var(--text)', 'text-anchor': attrs.anchor, transform: attrs.transform, 'pointer-events': 'none' }, parent);
  t.textContent = s;
  return t;
}
const svArea = () => { const h = $('#lv'); return { aw: Math.max(400, (h?.clientWidth || 1300) - 60), ah: Math.max(300, (h?.clientHeight || 900) - 110) }; };

// ---------------------------------------------------------------- «Плитки»: squarified treemap, two levels at a time

function svSquarify(items, x, y, w, h) {
  const out = [], total = items.reduce((s, i) => s + i.value, 0) || 1;
  let rest = items.map(i => ({ i, a: i.value / total * w * h })), X = x, Y = y, W = w, H = h;
  while (rest.length) {
    const side = Math.min(W, H);
    let row = [rest[0]], worst = Infinity;
    for (let k = 1; k <= rest.length; k++) {
      const r = rest.slice(0, k), s = r.reduce((a, b) => a + b.a, 0), mx = Math.max(...r.map(q => q.a)), mn = Math.min(...r.map(q => q.a));
      const wv = Math.max(side * side * mx / (s * s), s * s / (side * side * mn));
      if (wv > worst) break;
      worst = wv; row = r;
    }
    const s = row.reduce((a, b) => a + b.a, 0), thick = s / side;
    let off = 0;
    for (const q of row) {
      const len = q.a / thick;
      out.push(W >= H ? { n: q.i, x: X, y: Y + off, w: thick, h: len } : { n: q.i, x: X + off, y: Y, w: len, h: thick });
      off += len;
    }
    if (W >= H) { X += thick; W -= thick; } else { Y += thick; H -= thick; }
    rest = rest.slice(row.length);
  }
  return out;
}
function svTiles(D) {
  const root = svHier(D), focus = svFocus(root), { aw, ah } = svArea();
  const W = aw, H = ah;
  return { kind: 'tiles', W, H, custom: g => {
    for (const t of svSquarify(focus.children, 0, 0, W, H)) {
      const n = t.n, x = t.x + 2, y = t.y + 2, w = t.w - 4, h = t.h - 4;
      if (w < 2 || h < 2) continue;
      const box = lvEl('g', {}, g);
      lvEl('rect', { x, y, width: w, height: h, rx: 8, style: `fill:${lvColor(n.tier)};fill-opacity:.10;stroke:${lvColor(n.tier)};stroke-opacity:.6` }, box);
      svHover(box, n);
      const head = h > 40 && w > 60 ? 24 : 0;
      if (head) svText(box, x + 10, y + 17, n.name + `  ${fmt(n.value)}`, w - 20, { bold: true, fill: lvColor(n.tier) });
      if (!n.children || h - head < 20) continue;
      for (const u of svSquarify(n.children, x + 4, y + head + 2, w - 8, h - head - 6)) {
        const m = u.n, ux = u.x + 1.5, uy = u.y + 1.5, uw = u.w - 3, uh = u.h - 3;
        if (uw < 2 || uh < 2) continue;
        const el = lvEl('g', {}, g);
        lvEl('rect', { x: ux, y: uy, width: uw, height: uh, rx: 5, style: `fill:var(--panel);stroke:${lvColor(m.tier)};stroke-opacity:.8${m.guess ? ';stroke-dasharray:4 3' : ''}` }, el);
        svHover(el, m);
        if (uh >= 20) svText(el, ux + 7, uy + 15, m.name, uw - 12);
        if (uh >= 38) svText(el, ux + 7, uy + 31, `${fmt(m.value)} строк`, uw - 12, { fill: 'var(--muted)' });
      }
    }
  } };
}

// ---------------------------------------------------------------- «Солнце»: rings around the focus, three levels

function svSun(D) {
  const root = svHier(D), focus = svFocus(root), { aw, ah } = svArea();
  const R = Math.max(220, Math.min(aw, ah) / 2), r0 = Math.min(90, R * 0.22), ring = (R - r0) / 3, C = R + 4;
  const arc = (a0, a1, r1, r2) => {
    const large = a1 - a0 > Math.PI ? 1 : 0, p = (a, r) => `${C + r * Math.sin(a)},${C - r * Math.cos(a)}`;
    if (a1 - a0 >= 2 * Math.PI - 1e-6) a1 = a0 + 2 * Math.PI - 1e-4;
    return `M${p(a0, r2)} A${r2},${r2} 0 ${large} 1 ${p(a1, r2)} L${p(a1, r1)} A${r1},${r1} 0 ${large} 0 ${p(a0, r1)} z`;
  };
  return { kind: 'sun', W: 2 * C, H: 2 * C, custom: g => {
    const draw = (n, a0, a1, level) => {
      if (level > 3 || !n.children) return;
      let a = a0;
      for (const c of n.children) {
        const da = (a1 - a0) * c.value / n.value, r1 = r0 + (level - 1) * ring, r2 = r1 + ring - 1.5;
        if (da > 0.002) {
          const el = lvEl('g', {}, g);
          lvEl('path', { d: arc(a + 0.002, a + da - 0.002, r1, r2), style: `fill:${lvColor(c.tier)};fill-opacity:${0.5 - level * 0.1};stroke:var(--canvas);stroke-width:1${c.guess ? ';fill-opacity:.16' : ''}` }, el);
          svHover(el, c);
          // a label only where it fits: along the radius inside the ring, the segment wide enough for one line
          const mid = a + da / 2, rm = r1 + ring / 2;
          if (da * rm >= 15) {
            const deg = mid * 180 / Math.PI, flip = mid > Math.PI;
            const x = C + rm * Math.sin(mid), y = C - rm * Math.cos(mid);
            svText(el, x, y + 4, c.name, ring - 14, { anchor: 'middle', transform: `rotate(${flip ? deg + 90 : deg - 90} ${x} ${y})` });
          }
          draw(c, a, a + da, level + 1);
        }
        a += da;
      }
    };
    draw(focus, 0, 2 * Math.PI, 1);
    const center = lvEl('g', { style: 'cursor:pointer' }, g);
    lvEl('circle', { cx: C, cy: C, r: r0 - 4, style: 'fill:var(--panel);stroke:var(--line2)' }, center);
    svText(center, C, C - 2, focus.name, 2 * r0 - 24, { anchor: 'middle', bold: true });
    svText(center, C, C + 16, `${fmt(focus.value)} строк`, 2 * r0 - 24, { anchor: 'middle', fill: 'var(--muted)' });
    if (LV.stack.length) center.addEventListener('click', e => { e.stopPropagation(); lvBack(); });
  } };
}

// ---------------------------------------------------------------- «Дерево»: branches open on click

function svTree(D) {
  const root = svHier(D);
  const rowH = 28, colW = 250, nodes = [];
  let row = 0;
  const place = n => {
    const open = svOpenTree.has(n.id) && n.children;
    if (!open) { n.y = row++ * rowH; }
    else { for (const c of n.children) place(c); n.y = (n.children[0].y + n.children[n.children.length - 1].y) / 2; }
    n.x = n.depth * colW; n.open = !!open;
    nodes.push(n);
  };
  place(root);
  const H = Math.max(rowH, row * rowH), W = (Math.max(...nodes.map(n => n.depth)) + 1) * colW;
  return { kind: 'tree', W, H, custom: g => {
    const lines = lvEl('g', {}, g);
    for (const n of nodes) if (n.open) for (const c of n.children)
      lvEl('path', { d: `M${n.x + 6},${n.y} C${n.x + colW / 2},${n.y} ${c.x - colW / 2},${c.y} ${c.x - 6},${c.y}`, style: `fill:none;stroke:${lvColor(c.tier)};stroke-opacity:.45;stroke-width:${1 + 4 * Math.sqrt(c.value / root.value)}` }, lines);
    for (const n of nodes) {
      const el = lvEl('g', { style: 'cursor:pointer' }, g);
      lvEl('circle', { cx: n.x, cy: n.y, r: n.file ? 4 : 6, style: `fill:${n.open || n.file ? 'var(--panel)' : lvColor(n.tier || 'frontend')};stroke:${lvColor(n.tier || 'frontend')};stroke-width:2` }, el);
      const label = n.file ? n.name : `${n.name}${n.children && !n.open ? `  +${n.children.length}` : ''}`;
      // an open branch has its label on the left, so its own lines never run through it
      svText(el, n.open ? n.x - 12 : n.x + 12, n.y + 4, label, n.open ? Math.max(120, colW * 0.45) : colW - 30,
        { bold: !n.file, anchor: n.open ? 'end' : null, fill: n.tier && !n.file ? lvColor(n.tier) : 'var(--text)' })?.setAttribute('style', 'paint-order:stroke;stroke:var(--canvas);stroke-width:4px;stroke-linejoin:round');
      el.addEventListener('mousemove', e => lvTip(e, svTip(n)));
      el.addEventListener('mouseleave', lvTipHide);
      el.addEventListener('click', e => {
        e.stopPropagation(); lvTipHide();
        if (n.file) { svOpen(n); return; }
        if (svOpenTree.has(n.id)) svOpenTree.delete(n.id); else svOpenTree.add(n.id);
        lvRender({ keepView: true });
      });
    }
  } };
}

// ---------------------------------------------------------------- «Созвездие»: blocks on a circle, links through the middle

function svStars(D) {
  const root = svHier(D), cells = root.children.flatMap(t => t.children);
  const n = cells.length, gaps = root.children.length;
  const R = Math.max(300, (n + gaps * 2) * 22 / (2 * Math.PI)), lab = 190, C = R + lab;
  const pos = new Map();
  let k = 0;
  for (const t of root.children) { for (const c of t.children) pos.set(c.id, { c, a: (k++ + 0.5) / (n + gaps * 2) * 2 * Math.PI }); k += 2; }
  const P = (a, r) => [C + r * Math.sin(a), C - r * Math.cos(a)];
  const links = D.cellLinks.filter(l => pos.has(l.a) && pos.has(l.b));
  const maxW = Math.max(1, ...links.map(l => l.w));
  return { kind: 'stars', W: 2 * C, H: 2 * C, custom: g => {
    const lg = lvEl('g', {}, g), dots = new Map(), els = [];
    links.forEach((l, i) => {
      const A = pos.get(l.a), B = pos.get(l.b), [x1, y1] = P(A.a, R - 10), [x2, y2] = P(B.a, R - 10);
      // bundle: the control point is pulled to the centre, less for neighbours on the circle
      const same = A.c.tier === B.c.tier, [cx, cy] = P((A.a + B.a) / 2 + (Math.abs(A.a - B.a) > Math.PI ? Math.PI : 0), same ? R * 0.6 : R * 0.12);
      const p = lvEl('path', { d: `M${x1},${y1} Q${cx},${cy} ${x2},${y2}`, style: `fill:none;stroke:${lvColor(A.c.tier)};stroke-width:${0.8 + 4 * Math.sqrt(l.w / maxW)};stroke-opacity:${i < 120 ? 0.35 : 0}` }, lg);
      p.addEventListener('mousemove', e => lvTip(e, `<b>${esc(lvTierName(A.c.tier))} · ${esc(A.c.name)} → ${esc(lvTierName(B.c.tier))} · ${esc(B.c.name)}</b><span>${l.w} ${plural(l.w, 'обращение', 'обращения', 'обращений')}</span>`));
      p.addEventListener('mouseleave', lvTipHide);
      els.push({ l, p, base: i < 120 ? 0.35 : 0 });
    });
    for (const t of root.children) { // the tier's arc
      const as = t.children.map(c => pos.get(c.id).a), a0 = Math.min(...as) - 0.02, a1 = Math.max(...as) + 0.02;
      const [x0, y0] = P(a0, R + 4), [x1, y1] = P(a1, R + 4);
      lvEl('path', { d: `M${x0},${y0} A${R + 4},${R + 4} 0 ${a1 - a0 > Math.PI ? 1 : 0} 1 ${x1},${y1}`, style: `fill:none;stroke:${lvColor(t.tier)};stroke-width:5;stroke-linecap:round` }, g);
      const am = (a0 + a1) / 2, [tx, ty] = P(am, R - 34);
      svText(g, tx, ty + 4, t.name, Math.max(60, (a1 - a0) * (R - 34) - 10), { anchor: 'middle', bold: true, fill: lvColor(t.tier) })?.setAttribute('style', 'paint-order:stroke;stroke:var(--canvas);stroke-width:5px;stroke-linejoin:round');
    }
    for (const { c, a } of pos.values()) {
      const el = lvEl('g', { style: 'cursor:pointer' }, g), [x, y] = P(a, R + 14), deg = a * 180 / Math.PI, flip = a > Math.PI;
      lvEl('circle', { cx: P(a, R)[0], cy: P(a, R)[1], r: 4 + Math.min(6, Math.sqrt(c.children.length)), style: `fill:${lvColor(c.tier)}` }, el);
      svText(el, x, y + 4, `${c.name} · ${c.children.length}`, lab - 24, { anchor: flip ? 'end' : 'start', transform: `rotate(${flip ? deg + 90 : deg - 90} ${x} ${y})` });
      el.addEventListener('mouseenter', () => { for (const x of els) { const on = x.l.a === c.id || x.l.b === c.id; x.p.style.strokeOpacity = on ? 0.9 : 0.04; } });
      el.addEventListener('mouseleave', () => { for (const x of els) x.p.style.strokeOpacity = x.base; lvTipHide(); });
      el.addEventListener('mousemove', e => lvTip(e, svTip(c)));
      el.addEventListener('click', e => { e.stopPropagation(); lvTipHide(); LV.stack = [{ cell: c.id }]; LV.mode = 'links'; lvRender(); });
    }
  } };
}

// ---------------------------------------------------------------- «Потоки»: tiers on the left, features on the right

function svFlows(D) {
  const root = svHier(D), { aw, ah } = svArea();
  const cells = root.children.flatMap(t => t.children);
  const feats = [...new Set(cells.map(c => c.name))];
  const fv = new Map(feats.map(f => [f, cells.filter(c => c.name === f).reduce((s, c) => s + c.children.length, 0)]));
  feats.sort((a, b) => fv.get(b) - fv.get(a));
  const total = cells.reduce((s, c) => s + c.children.length, 0) || 1;
  const H = Math.max(ah, 600), gap = 8, bar = 16, W = Math.max(aw, 900), lx = 200, rx = W - 220;
  const scaleL = (H - gap * (root.children.length - 1)) / total, scaleR = (H - gap * (feats.length - 1)) / total;
  return { kind: 'flows', W, H, custom: g => {
    const left = new Map(), right = new Map();
    let y = 0;
    for (const t of root.children) { const h = t.children.reduce((s, c) => s + c.children.length, 0) * scaleL; left.set(t.tier, { y, h, off: 0 }); y += h + gap; }
    y = 0;
    for (const f of feats) { const h = fv.get(f) * scaleR; right.set(f, { y, h, off: 0 }); y += h + gap; }
    const rib = lvEl('g', {}, g), ribs = [];
    for (const t of root.children) for (const c of t.children) {
      const L = left.get(t.tier), Rr = right.get(c.name), w1 = c.children.length * scaleL, w2 = c.children.length * scaleR;
      const y1 = L.y + L.off, y2 = Rr.y + Rr.off; L.off += w1; Rr.off += w2;
      const m = (lx + bar + rx) / 2;
      const p = lvEl('path', { d: `M${lx + bar},${y1} C${m},${y1} ${m},${y2} ${rx},${y2} L${rx},${y2 + w2} C${m},${y2 + w2} ${m},${y1 + w1} ${lx + bar},${y1 + w1} z`,
        style: `fill:${lvColor(t.tier)};fill-opacity:.28;cursor:pointer` }, rib);
      p.addEventListener('mousemove', e => { p.style.fillOpacity = 0.6; lvTip(e, svTip(c)); });
      p.addEventListener('mouseleave', () => { p.style.fillOpacity = 0.28; lvTipHide(); });
      p.addEventListener('click', e => { e.stopPropagation(); lvTipHide(); LV.stack = [{ cell: c.id }]; LV.mode = 'links'; lvRender(); });
      ribs.push({ p, tier: t.tier, feat: c.name });
    }
    const hl = pred => { for (const r of ribs) r.p.style.fillOpacity = pred ? (pred(r) ? 0.6 : 0.06) : 0.28; };
    for (const t of root.children) {
      const L = left.get(t.tier), el = lvEl('g', { style: 'cursor:pointer' }, g);
      lvEl('rect', { x: lx, y: L.y, width: bar, height: Math.max(1, L.h), rx: 3, style: `fill:${lvColor(t.tier)}` }, el);
      if (L.h >= 14) svText(el, lx - 10, L.y + L.h / 2 + 4, `${t.name} · ${t.children.reduce((s, c) => s + c.children.length, 0)}`, lx - 20, { anchor: 'end', bold: true, fill: lvColor(t.tier) });
      el.addEventListener('mouseenter', () => hl(r => r.tier === t.tier)); el.addEventListener('mouseleave', () => hl(null));
    }
    for (const f of feats) {
      const Rr = right.get(f), el = lvEl('g', {}, g);
      lvEl('rect', { x: rx, y: Rr.y, width: bar, height: Math.max(1, Rr.h), rx: 3, style: 'fill:var(--text2)' }, el);
      if (Rr.h >= 14) svText(el, rx + bar + 10, Rr.y + Rr.h / 2 + 4, `${f} · ${fv.get(f)}`, W - rx - bar - 14);
      el.addEventListener('mouseenter', () => hl(r => r.feat === f)); el.addEventListener('mouseleave', () => hl(null));
    }
  } };
}

const SV_HELP = {
  links: 'Большие цветные полосы — <b>ярусы</b> программы: от того, что видит человек, до хранения и движка. Блоки — <b>функции</b> на этом ярусе; одна функция стоит на одной линии на всех ярусах. Стрелка — код одного блока обращается к коду другого; чем толще, тем больше обращений.',
  tree: 'Программа ветвится на <b>ярусы</b>, ярусы — на <b>функции</b>, функции — на файлы. Толщина ветки — сколько в ней кода. Нажмите на ветку — откроется или закроется; на файл — его классы и связи.',
  tiles: 'Площадь плитки — сколько в ней кода. Большие плитки — <b>ярусы</b>, внутри — <b>функции</b>. Нажмите — шаг внутрь до файлов; файл откроется с классами и связями.',
  sun: 'В центре — то, что вы смотрите; первое кольцо — <b>ярусы</b>, второе — <b>функции</b>, внешнее — файлы. Ширина сектора — сколько кода. Нажмите на сектор — он станет центром; на центр — шаг назад.',
  stars: 'По кругу — блоки «ярус · функция», цветная дуга объединяет блоки одного яруса. Линия — код одного блока обращается к другому. Наведите на точку — останутся только её связи; нажмите — что внутри.',
  flows: 'Слева — <b>ярусы</b>, справа — <b>функции</b>. Ширина ленты — сколько файлов функции лежит на этом ярусе. Наведите на ярус или функцию — их ленты; нажмите на ленту — файлы этого блока и их связи.',
};

const SV_BUILD = { tree: svTree, tiles: svTiles, sun: svSun, stars: svStars, flows: svFlows };
