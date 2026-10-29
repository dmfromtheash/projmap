// «Время» (weeks of work, stages, what a stage touched) and «От кнопки» (a possible path from a button down through
// the tiers), inside the «Схема связей» area. Data: graph.views.timeline (ViewsScanner) and the code edges.
'use strict';

// ---------------------------------------------------------------- «Время»

const SV_TIME = { sel: null }; // { week: 'yyyy-mm-dd' } | { stage: key }
const svWeek = iso => { const d = new Date(iso); const day = (d.getDay() + 6) % 7; d.setHours(0, 0, 0, 0); d.setDate(d.getDate() - day); return d; };
const svDay = d => `${String(d.getDate()).padStart(2, '0')}.${String(d.getMonth() + 1).padStart(2, '0')}`;
const svKey = d => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;

function svTimeData(D) {
  const T = D.v.timeline;
  if (!T) return null;
  if (SV_TIME.dataFor === T) return SV_TIME.data;
  const weeks = new Map();
  for (const c of T.commits) {
    const w = svWeek(c.at), k = svKey(w);
    let x = weeks.get(k);
    if (!x) weeks.set(k, x = { key: k, start: w, commits: [], files: new Set(), stages: new Set() });
    x.commits.push(c);
    for (const i of c.files) x.files.add(T.files[i]);
  }
  const stageOf = new Map();
  for (const s of T.stages) for (const id of s.commitIds) (stageOf.get(id) || stageOf.set(id, []).get(id)).push(s.key);
  for (const w of weeks.values()) for (const c of w.commits) for (const k of stageOf.get(c.id) || []) w.stages.add(k);
  // columns in time order; a run of empty weeks becomes one narrow «перерыв» column
  const keys = [...weeks.keys()].sort(), cols = [];
  for (let i = 0; i < keys.length; i++) {
    const w = weeks.get(keys[i]);
    if (i) {
      const prev = weeks.get(keys[i - 1]), gapDays = Math.round((w.start - prev.start) / 864e5);
      if (gapDays > 7) cols.push({ gap: true, from: new Date(+prev.start + 7 * 864e5), to: new Date(+w.start - 864e5), weeks: gapDays / 7 - 1 });
    }
    cols.push(w);
  }
  SV_TIME.dataFor = T;
  return SV_TIME.data = { T, weeks, cols };
}

function svTime(D) {
  const TD = svTimeData(D);
  if (!TD) return { kind: 'time', W: 600, H: 100, custom: g => { lvEl('text', { x: 0, y: 40, class: 'empty' }, g).textContent = 'Нет истории git: проект не в git или окно истории пустое.'; } };
  const colW = 96, gapW = 52, barH = 340, top = 30, chipRows = 4;
  const maxFiles = Math.max(1, ...TD.cols.filter(c => !c.gap).map(c => c.files.size));
  const sel = SV_TIME.sel, selStage = sel?.stage ? TD.T.stages.find(s => s.key === sel.stage) : null;
  const byId = new Map(TD.T.commits.map(c => [c.id, c]));
  const stageWeeks = new Set(selStage ? selStage.commitIds.filter(id => byId.has(id)).map(id => svKey(svWeek(byId.get(id).at))) : []);
  let x = 0;
  const placed = TD.cols.map(c => { const p = { c, x, w: c.gap ? gapW : colW }; x += p.w + 10; return p; });
  return { kind: 'time', W: x, H: top + barH + 34 + chipRows * 22 + 10, custom: g => {
    for (const { c, x, w } of placed) {
      if (c.gap) {
        const el = lvEl('g', {}, g);
        lvEl('rect', { x, y: top, width: w, height: barH, rx: 8, style: 'fill:none;stroke:var(--line2);stroke-dasharray:5 4' }, el);
        svText(el, x + w / 2, top + barH + 20, 'пауза', w + 6, { anchor: 'middle', fill: 'var(--muted)' });
        el.addEventListener('mousemove', e => lvTip(e, `<b>Перерыв ${svDay(c.from)} – ${svDay(c.to)}</b><span>${Math.round(c.weeks)} нед. без коммитов в этой ветке</span>`));
        el.addEventListener('mouseleave', lvTipHide);
        continue;
      }
      const el = lvEl('g', { style: 'cursor:pointer' }, g);
      const on = sel?.week === c.key || stageWeeks.has(c.key), dim = sel && !on;
      const byTier = new Map();
      for (const f of c.files) { const t = D.assign.get(f)?.tier || 'unclassified'; byTier.set(t, (byTier.get(t) || 0) + 1); }
      const h = barH * c.files.size / maxFiles;
      let y = top + barH;
      lvEl('rect', { x, y: top, width: w, height: barH, rx: 8, style: `fill:${on ? 'var(--sel)' : 'transparent'}` }, el);
      for (const [t, n] of [...byTier].sort((a, b) => LV_ROWS.indexOf(a[0]) - LV_ROWS.indexOf(b[0]))) {
        const hh = h * n / c.files.size; y -= hh;
        lvEl('rect', { x: x + 8, y, width: w - 16, height: Math.max(0.5, hh), style: `fill:${lvColor(t)};fill-opacity:${dim ? 0.25 : 0.85}` }, el);
      }
      svText(el, x + w / 2, y - 6, `${c.files.size}`, w, { anchor: 'middle', bold: true });
      svText(el, x + w / 2, top + barH + 20, svDay(c.start), w, { anchor: 'middle', fill: 'var(--muted)' });
      // stage keys under the week, wrapped inside the column; what does not fit is counted
      // stages linked explicitly (a progress table) first, then by key
      const linkRank = k => TD.T.stages.find(s => s.key === k)?.link === 'explicit' ? 0 : 1;
      const keys = [...c.stages].sort((a, b) => linkRank(a) - linkRank(b) || a.localeCompare(b, 'ru', { numeric: true }));
      let row = 0, cx = x, shown = 0;
      lvCanvas.font = SV_FONT;
      for (const k of keys) {
        const kw = Math.min(w, lvCanvas.measureText(k).width + 16);
        if (cx + kw > x + w) { row++; cx = x; }
        if (row >= chipRows - 1 && keys.length - shown > 1) break;
        if (row >= chipRows) break;
        const cy = top + barH + 34 + row * 22;
        const chip = lvEl('g', { style: 'cursor:pointer' }, el);
        lvEl('rect', { x: cx, y: cy, width: kw - 4, height: 18, rx: 9, style: `fill:${selStage?.key === k ? 'var(--accent)' : 'var(--panel2)'};stroke:var(--line)` }, chip);
        svText(chip, cx + 6, cy + 13, k, kw - 9, { fill: selStage?.key === k ? '#fff' : 'var(--text2)' });
        chip.addEventListener('click', e => { e.stopPropagation(); SV_TIME.sel = selStage?.key === k ? null : { stage: k }; lvRender({ keepView: true }); });
        cx += kw; shown++;
      }
      if (shown < keys.length) svText(el, cx + 2, top + barH + 34 + row * 22 + 13, `+${keys.length - shown}`, x + w - cx - 2, { fill: 'var(--muted)' });
      el.addEventListener('mousemove', e => lvTip(e, `<b>Неделя с ${svDay(c.start)}</b><span>${c.commits.length} ${plural(c.commits.length, 'коммит', 'коммита', 'коммитов')} · ${c.files.size} ${plural(c.files.size, 'файл', 'файла', 'файлов')} изменено${keys.length ? ' · этапы: ' + esc(keys.join(', ')) : ''}<br>нажмите — что именно</span>`));
      el.addEventListener('mouseleave', lvTipHide);
      el.addEventListener('click', e => { e.stopPropagation(); lvTipHide(); SV_TIME.sel = sel?.week === c.key ? null : { week: c.key }; lvRender({ keepView: true }); });
    }
  } };
}

// files grouped by tier, each a row that opens the file inside «Связи»
function svFileRows(D, files) {
  const by = new Map();
  for (const f of files) { if (!S.model.byId.has(f)) continue; const t = D.assign.get(f)?.tier || 'unclassified'; (by.get(t) || by.set(t, []).get(t)).push(f); }
  return [...by].sort((a, b) => LV_ROWS.indexOf(a[0]) - LV_ROWS.indexOf(b[0])).map(([t, fs]) =>
    `<div class="card"><h3 style="color:${lvColor(t)}">${esc(lvTierName(t))}<span class="c">${fs.length}</span></h3>${lvList(fs.sort().map(f => ({ tier: t, name: S.model.node(f).name, title: S.model.node(f).path, attr: `data-lv-node="${esc(f)}"` })))}</div>`).join('');
}

function svTimeDetails(D, el) {
  const TD = svTimeData(D);
  if (!TD) { el.innerHTML = '<div class="card"><h3>Время</h3><p class="why">Нет истории git.</p></div>'; return; }
  const sel = SV_TIME.sel;
  const commitRows = cs => `<div class="lvlist">${cs.map(c => `<div class="r" title="${esc(c.id)}"><span class="nm">${esc(c.subject)}</span><span class="n">${esc(c.at.slice(0, 10))}</span></div>`).join('') || '<p class="why">—</p>'}</div>`;
  if (sel?.week && TD.weeks.has(sel.week)) {
    const w = TD.weeks.get(sel.week);
    el.innerHTML = `<div class="card head"><span class="kind">неделя</span><h2>С ${svDay(w.start)}</h2>
      <div class="what prose"><p>${w.commits.length} ${plural(w.commits.length, 'коммит', 'коммита', 'коммитов')}, изменено ${w.files.size} ${plural(w.files.size, 'файл', 'файла', 'файлов')}.</p></div></div>
      <div class="card"><h3>Коммиты</h3>${commitRows(w.commits)}</div>${svFileRows(D, [...w.files])}`;
    return;
  }
  const s = sel?.stage && TD.T.stages.find(x => x.key === sel.stage);
  if (s) {
    const files = new Set(), commits = TD.T.commits.filter(c => s.commitIds.includes(c.id));
    for (const c of commits) for (const i of c.files) files.add(TD.T.files[i]);
    el.innerHTML = `<div class="card head"><span class="kind">этап</span><h2>${esc(s.key)} · ${esc(s.title)}</h2>
      <div class="what prose"><p>Статус: ${esc(STATUS_RU[s.status] || s.status)}. ${s.link === 'explicit' ? 'Коммиты названы рядом с этапом в документах проекта (таблица прогресса).' : s.link === 'subject' ? 'Коммиты найдены по ключу этапа в их заголовках.' : 'Коммиты этапа не найдены.'}</p>
      <p>Что затронул этап: <b>${files.size} ${plural(files.size, 'файл', 'файла', 'файлов')}</b> в ${commits.length} ${plural(commits.length, 'коммите', 'коммитах', 'коммитах')}.</p></div></div>
      <div class="card"><h3>Коммиты</h3>${commitRows(commits)}</div>${svFileRows(D, [...files])}`;
    return;
  }
  const withCommits = TD.T.stages.filter(x => x.commitIds.length);
  el.innerHTML = `<div class="card head"><span class="kind">вид</span><h2>Время</h2>
    <div class="what prose"><p>Столбец — неделя работы: высота — сколько файлов изменено, цвета — на каких ярусах. Пунктир — пауза без коммитов. Под неделей — этапы плана, чьи коммиты в ней.</p>
    <p>Нажмите на неделю — что в ней менялось; на этап — что он затронул.</p><p class="why">${esc(TD.T.coverage)}.</p></div></div>
    <div class="card"><h3>Этапы с коммитами<span class="c">${withCommits.length}</span></h3><div class="lvlist">${withCommits.map(x => `<div class="r" data-sv-stage="${esc(x.key)}"><span class="nm">${esc(x.key)} · ${esc(x.title)}</span><span class="n">${x.commitIds.length}</span></div>`).join('')}</div></div>`;
}

// ---------------------------------------------------------------- «От кнопки»

const SV_PATH = { start: null, sel: null, q: '', depth: 5, max: 60 };
const SV_PATH_KINDS = new Set(['call', 'reference', 'xamlEvent', 'binding']);

function svPathIndex(D) {
  if (D.pathIndex) return D.pathIndex;
  const out = new Map(), impl = new Map();
  for (const e of S.model.g.edges) {
    if (SV_PATH_KINDS.has(e.kind)) (out.get(e.source) || out.set(e.source, []).get(e.source)).push(e);
    // calling a contract may run any of its implementations: each is a possible next step
    if (e.kind === 'implement' || e.kind === 'inherit') (impl.get(e.target) || impl.set(e.target, []).get(e.target)).push(e.source);
  }
  return D.pathIndex = { out, impl };
}

function svPathSteps(D, start) {
  const M = S.model, { out, impl } = svPathIndex(D);
  const steps = new Map([[start, { id: start, depth: 0, from: null, possible: false }]]), queue = [start];
  while (queue.length && steps.size < SV_PATH.max) {
    const id = queue.shift(), s = steps.get(id);
    if (s.depth >= SV_PATH.depth) continue;
    const next = [];
    for (const e of [...(out.get(id) || [])].sort((a, b) => (b.weight || 1) - (a.weight || 1))) {
      const t = M.node(e.target);
      if (!t || t.kind !== 'member' || !['method', 'ctor', 'handler', 'function'].includes(t.subKind)) continue; // fields and properties are not steps
      next.push([e.target, false]);
      for (const i of impl.get(e.target) || []) next.push([i, true]);
    }
    // test doubles implement the same contracts: they are steps only when «Проверки» is on
    for (let i = next.length - 1; i >= 0; i--) {
      const tier = D.assign.get(lvFileOf(next[i][0]))?.tier;
      if (tier === 'tests' && !LV.showTests || tier === 'docs' && !LV.showDocs) next.splice(i, 1);
    }
    for (const [t, possible] of next) {
      if (steps.has(t) || steps.size >= SV_PATH.max) continue;
      steps.set(t, { id: t, depth: s.depth + 1, from: id, possible });
      queue.push(t);
    }
  }
  return [...steps.values()];
}

function svPath(D) {
  const M = S.model;
  if (!SV_PATH.start || !M.byId.has(SV_PATH.start))
    return { kind: 'path', W: 760, H: 120, custom: g => { lvEl('text', { x: 0, y: 30, class: 'empty' }, g).textContent = 'Выберите кнопку справа — покажу, через какие ярусы проходит её работа.'; } };
  const steps = svPathSteps(D, SV_PATH.start);
  const tierOf = id => D.assign.get(lvFileOf(id))?.tier || 'unclassified';
  const cols = LV_ROWS.filter(t => steps.some(s => tierOf(s.id) === t));
  const colW = 260, bw = 236, bh = 46, rowH = 56, head = 44;
  const used = new Map();
  for (const s of steps) { const key = cols.indexOf(tierOf(s.id)) + ':' + s.depth; s.k = used.get(key) || 0; used.set(key, s.k + 1); s.x = cols.indexOf(tierOf(s.id)) * colW + (colW - bw) / 2; }
  // each step depth gets as many lines as its fullest tier column needs
  const lines = new Map();
  for (const [key, n] of used) { const d = +key.split(':')[1]; lines.set(d, Math.max(lines.get(d) || 0, n)); }
  const depthY = new Map(); let y = head;
  for (const d of [...lines.keys()].sort((a, b) => a - b)) { depthY.set(d, y); y += lines.get(d) * rowH + 16; }
  for (const s of steps) s.y = depthY.get(s.depth) + s.k * rowH;
  const pos = new Map(steps.map(s => [s.id, s])), H = y;
  return { kind: 'path', W: cols.length * colW, H, custom: g => {
    cols.forEach((t, i) => {
      lvEl('rect', { x: i * colW + 4, y: 0, width: colW - 8, height: H, rx: 12, style: `fill:${lvColor(t)};fill-opacity:.06;stroke:${lvColor(t)};stroke-opacity:.3` }, g);
      svText(g, i * colW + 16, 28, lvTierName(t), colW - 30, { bold: true, fill: lvColor(t) });
    });
    const lg = lvEl('g', {}, g);
    for (const s of steps) if (s.from) {
      const a = pos.get(s.from), ax = a.x + bw / 2, ay = a.y + bh, bx = s.x + bw / 2, by = s.y, m = (ay + by) / 2;
      lvEl('path', { d: `M${ax},${ay} C${ax},${m} ${bx},${m} ${bx},${by - 2}`, style: `fill:none;stroke:var(--line2);stroke-width:1.6${s.possible ? ';stroke-dasharray:5 4' : ''}` }, lg);
    }
    for (const s of steps) {
      const n = M.node(s.id), el = lvEl('g', { style: 'cursor:pointer' }, g), t = tierOf(s.id);
      lvEl('rect', { x: s.x, y: s.y, width: bw, height: bh, rx: 10, style: `fill:${SV_PATH.sel === s.id ? 'var(--sel)' : 'var(--panel)'};stroke:${lvColor(t)};stroke-width:${s.depth ? 1.5 : 3}${s.possible ? ';stroke-dasharray:5 3' : ''}` }, el);
      svText(el, s.x + 12, s.y + 19, (s.depth ? '' : '▶ ') + (n.subKind === 'handler' ? n.name : `${M.node(n._parent)?.name || ''}.${n.name}`), bw - 22, { bold: true });
      svText(el, s.x + 12, s.y + 36, `шаг ${s.depth}${s.possible ? ' · возможный' : ''} · ${M.node(lvFileOf(s.id))?.name || ''}`, bw - 22, { fill: 'var(--muted)' });
      el.addEventListener('mousemove', e => lvTip(e, `<b>${esc(n.name)}</b><span>${esc(n.path || '')}${n.line ? ':' + n.line : ''}${s.possible ? '<br>возможный шаг: вызов идёт через интерфейс, это одна из его реализаций' : ''}<br>нажмите — подробности и «идти отсюда»</span>`));
      el.addEventListener('mouseleave', lvTipHide);
      el.addEventListener('click', e => { e.stopPropagation(); lvTipHide(); SV_PATH.sel = s.id; lvRender({ keepView: true }); });
    }
  } };
}

function svPathDetails(D, el) {
  const M = S.model;
  const handlers = [...new Map([...D.entries].flatMap(([f, ids]) => ids.map(id => [id, f]))).entries()].filter(([id]) => M.byId.has(id));
  const sel = SV_PATH.sel && M.byId.has(SV_PATH.sel) ? M.node(SV_PATH.sel) : null;
  const q = SV_PATH.q.toLowerCase();
  const list = handlers.filter(([id, f]) => !q || M.node(id).name.toLowerCase().includes(q) || (D.feats.get(f)?.name || '').toLowerCase().includes(q)).slice(0, 80);
  el.innerHTML = `<div class="card head"><span class="kind">вид</span><h2>От кнопки</h2>
    <div class="what prose"><p>Что происходит после нажатия: шаг за шагом вниз, колонки — ярусы. Пунктир — <b>возможный</b> шаг: вызов идёт через общий интерфейс, и выполниться может любая его реализация. Это путь по коду, а не запись настоящего запуска; показаны первые ${SV_PATH.max} шагов до глубины ${SV_PATH.depth}.</p></div></div>
    ${sel ? `<div class="card"><h3>Выбранный шаг</h3><p class="why"><b>${esc(sel.name)}</b><br>${esc(sel.path || '')}${sel.line ? ':' + sel.line : ''}</p>
      <div class="actions"><button data-sv-start="${esc(sel.id)}" class="main">Идти отсюда</button><button data-lv-node="${esc(sel.id)}">Внутри</button>${sel.path ? `<button data-sv-open="${esc(sel.id)}">В редакторе</button>` : ''}</div></div>` : ''}
    <div class="card lvedit"><h3>Кнопки<span class="c">${handlers.length}</span></h3>
      <input data-sv-q placeholder="Найти кнопку или функцию…" value="${esc(SV_PATH.q)}">
      <div class="lvlist">${list.map(([id, f]) => `<div class="r" data-sv-start="${esc(id)}" title="${esc(M.node(id).path)}"><span class="nm">${esc(M.node(id).name)}</span><span class="n">${esc(D.feats.get(f)?.name || '')}</span></div>`).join('')}</div></div>`;
}

$('#details').addEventListener('click', e => {
  if (S.view !== 'links') return;
  const st = e.target.closest('[data-sv-stage]');
  if (st) { e.stopPropagation(); SV_TIME.sel = { stage: st.dataset.svStage }; lvRender({ keepView: true }); return; }
  const sp = e.target.closest('[data-sv-start]');
  if (sp) { e.stopPropagation(); SV_PATH.start = sp.dataset.svStart; SV_PATH.sel = null; LV.mode = 'path'; LV.stack = []; lvRender(); return; }
  const so = e.target.closest('[data-sv-open]');
  if (so) { e.stopPropagation(); const n = S.model.node(so.dataset.svOpen); send({ type: 'open', path: n.path, line: n.line || 0 }); }
}, true);
$('#details').addEventListener('input', e => {
  if (S.view !== 'links' || !e.target.matches('[data-sv-q]')) return;
  SV_PATH.q = e.target.value;
  const at = e.target.selectionStart;
  lvDetails();
  const input = $('#details [data-sv-q]'); input.focus(); input.setSelectionRange(at, at);
});

SV_MODES.push(['time', 'Время'], ['path', 'От кнопки']);
Object.assign(SV_BUILD, { time: svTime, path: svPath });
