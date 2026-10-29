// «Проблемы» inside the «Схема связей» area: dead code, cycles, tier violations and hot files from graph.problems
// (ProblemsAnalyzer). A column per kind, a block per problem; a click explains it on the right, and each node of
// the problem opens inside «Связи» with its links.
'use strict';

const SV_PROB = { sel: null, kind: null };
const SV_PROB_KINDS = [
  ['tierViolation', 'Нарушения ярусов', 'код нижнего яруса зависит от верхнего (например, правила — от интерфейса)'],
  ['moduleCycle', 'Циклы модулей', 'модули зависят друг от друга по кругу'],
  ['fileCycle', 'Циклы файлов', 'файлы обращаются друг к другу по кругу'],
  ['deadCode', 'Похоже на мёртвый код', 'ни откуда не вызывается и не используется'],
  ['hotFile', 'Горячие файлы', 'часто меняются и сложные — стоит проверить в первую очередь'],
];
const SV_PROB_SHOW = 24;

function svProbItems(D) {
  const P = S.model.g.problems;
  if (!P) return null;
  return { P, by: new Map(SV_PROB_KINDS.map(([k]) => [k, P.items.map((it, i) => ({ ...it, i })).filter(it => it.kind === k)])) };
}
function svProbTitle(it) {
  const M = S.model, name = id => M.node(id)?.name || id.split(/[:/]/).pop();
  if (it.kind === 'tierViolation') return `${name(it.nodeIds[0])} → ${name(it.nodeIds[1] ?? it.nodeIds[0])}`;
  if (it.kind === 'moduleCycle' || it.kind === 'fileCycle') return `${it.nodeIds.length}: ${it.nodeIds.slice(0, 3).map(name).join(' → ')}${it.nodeIds.length > 3 ? ' …' : ''}`;
  const n = M.node(it.nodeIds[0]);
  return n?.kind === 'member' ? `${M.node(n._parent)?.name || ''}.${n.name}` : name(it.nodeIds[0]);
}

function svProblems(D) {
  const PI = svProbItems(D);
  if (!PI) return { kind: 'problems', W: 760, H: 100, custom: g => { lvEl('text', { x: 0, y: 30, class: 'empty' }, g).textContent = 'В этой схеме ещё нет отчёта о проблемах — нажмите ⟳ «Пересканировать».'; } };
  const colW = 280, bw = 256, bh = 46, gy = 10, head = 64;
  const kinds = SV_PROB_KINDS.filter(([k]) => (PI.P.counts?.[k] ?? PI.by.get(k).length) > 0 || PI.by.get(k).length);
  const rows = Math.max(1, ...kinds.map(([k]) => Math.min(SV_PROB_SHOW, PI.by.get(k).length)));
  const H = head + rows * (bh + gy) + 34;
  return { kind: 'problems', W: Math.max(1, kinds.length) * colW, H, custom: g => {
    if (!kinds.length) { lvEl('text', { x: 0, y: 30, class: 'empty' }, g).textContent = 'Проблем не найдено.'; return; }
    kinds.forEach(([k, name, sub], c) => {
      const x = c * colW, items = PI.by.get(k), total = PI.P.counts?.[k] ?? items.length;
      const hd = lvEl('g', { style: 'cursor:pointer' }, g);
      lvEl('rect', { x: x + 4, y: 0, width: colW - 8, height: H, rx: 12, style: `fill:var(--panel2);stroke:var(--line)` }, hd);
      svText(hd, x + 16, 26, k === 'hotFile' && total > items.length ? `${name} · ${items.length} из ${total}` : `${name} · ${total}`, colW - 30, { bold: true });
      svText(hd, x + 16, 46, sub, colW - 30, { fill: 'var(--muted)' });
      hd.addEventListener('click', e => { e.stopPropagation(); SV_PROB.kind = k; SV_PROB.sel = null; lvDetails(); });
      items.slice(0, SV_PROB_SHOW).forEach((it, r) => {
        const y = head + r * (bh + gy), bx = x + (colW - bw) / 2, tier = D.assign.get(lvFileOf(it.nodeIds[0]))?.tier || 'unclassified';
        const el = lvEl('g', { style: 'cursor:pointer' }, g);
        lvEl('rect', { x: bx, y, width: bw, height: bh, rx: 10, style: `fill:${SV_PROB.sel === it.i ? 'var(--sel)' : 'var(--panel)'};stroke:${it.severity === 'warn' ? 'var(--warn)' : lvColor(tier)};stroke-width:${it.severity === 'warn' ? 2 : 1.3}` }, el);
        svText(el, bx + 12, y + 19, svProbTitle(it), bw - 22, { bold: true });
        svText(el, bx + 12, y + 36, it.score != null ? `${it.reason} · ${it.score}` : it.reason, bw - 22, { fill: 'var(--muted)' });
        el.addEventListener('mousemove', e => lvTip(e, `<b>${esc(svProbTitle(it))}</b><span>${esc(it.reason)}<br>нажмите — подробности</span>`));
        el.addEventListener('mouseleave', lvTipHide);
        el.addEventListener('click', e => { e.stopPropagation(); lvTipHide(); SV_PROB.sel = it.i; SV_PROB.kind = null; lvRender({ keepView: true }); });
      });
      if (total > SV_PROB_SHOW) svText(g, x + 16, head + rows * (bh + gy) + 16, `и ещё ${total - Math.min(SV_PROB_SHOW, items.length)} — нажмите на заголовок`, colW - 30, { fill: 'var(--muted)' });
    });
  } };
}

function svProblemsDetails(D, el) {
  const PI = svProbItems(D), M = S.model;
  if (!PI) { el.innerHTML = '<div class="card"><h3>Проблемы</h3><p class="why">Нужен новый скан: нажмите ⟳.</p></div>'; return; }
  const nodeRows = ids => lvList(ids.filter(id => M.byId.has(id)).map(id => ({ tier: D.assign.get(lvFileOf(id))?.tier || 'unclassified', name: svProbTitle({ kind: 'deadCode', nodeIds: [id] }), title: M.node(id).path, attr: `data-lv-node="${esc(id)}"` })));
  const it = SV_PROB.sel != null ? PI.P.items[SV_PROB.sel] : null;
  if (it) {
    const kind = SV_PROB_KINDS.find(k => k[0] === it.kind);
    const ev = (it.edges || []).flatMap(e => (e.evidence || []).map(v => `${v.path}:${v.line}${v.snippet ? ' — ' + v.snippet : ''}`)).slice(0, 8);
    el.innerHTML = `<div class="card head"><span class="kind">${esc(kind?.[1] || it.kind)}</span><h2>${esc(svProbTitle(it))}</h2>
      <div class="what prose"><p>${esc(it.reason.replace(/\.+$/, ''))}.</p>${it.rule ? `<p class="why">Правило: ${esc(it.rule)}</p>` : ''}</div></div>
      <div class="card"><h3>Что затронуто<span class="c">${it.nodeIds.length}</span></h3>${nodeRows(it.nodeIds)}</div>
      ${ev.length ? `<div class="card"><h3>Где в коде</h3><ul class="why">${ev.map(x => `<li>${esc(x)}</li>`).join('')}</ul></div>` : ''}`;
    return;
  }
  if (SV_PROB.kind) {
    const kind = SV_PROB_KINDS.find(k => k[0] === SV_PROB.kind), items = PI.by.get(SV_PROB.kind);
    el.innerHTML = `<div class="card head"><span class="kind">проблемы</span><h2>${esc(kind[1])}</h2><div class="what prose"><p>${esc(kind[2])}.</p></div></div>
      <div class="card"><h3>Все<span class="c">${items.length}</span></h3><div class="lvlist">${items.slice(0, 300).map(x => `<div class="r" data-sv-prob="${x.i}" title="${esc(x.reason)}"><span class="nm">${esc(svProbTitle(x))}</span></div>`).join('')}</div></div>`;
    return;
  }
  el.innerHTML = `<div class="card head"><span class="kind">вид</span><h2>Проблемы</h2>
    <div class="what prose"><p>Что стоит проверить в коде: части, нарушающие порядок ярусов, круговые зависимости, код, который ни откуда не вызывается, и файлы, которые часто меняются и при этом сложны.</p>
    <p>Это подсказки по коду, а не приговор: метод, который вызывает библиотека или окно (например, обработчик кнопки), не считается мёртвым.</p></div></div>
    <div class="card"><h3>Сколько найдено</h3><div class="facts">${SV_PROB_KINDS.map(([k, n]) => fact(PI.P.counts?.[k] ?? PI.by.get(k).length, n.toLowerCase())).join('')}</div></div>`;
}

$('#details').addEventListener('click', e => {
  if (S.view !== 'links') return;
  const p = e.target.closest('[data-sv-prob]');
  if (p) { e.stopPropagation(); SV_PROB.sel = +p.dataset.svProb; SV_PROB.kind = null; lvRender({ keepView: true }); }
}, true);

SV_MODES.push(['problems', 'Проблемы']);
SV_BUILD.problems = svProblems;
