// «Файл»: one source file inside the app — a diagram of its functions (who calls whom, which
// fields/properties they use, what they call outside the file and who calls them) next to the code.
'use strict';

const FV = { file: null, focus: null, text: null, textFor: null, prevView: 'map' };
const SUB_KIND_RU = { method: 'функция', ctor: 'конструктор', property: 'свойство', field: 'поле', event: 'событие', function: 'функция',
  class: 'класс', record: 'запись (данные)', struct: 'структура', interface: 'интерфейс', enum: 'перечисление', delegate: 'делегат' };
const MAX_OUTSIDE = 24;
const MAX_FILE_EDGES = 400;

function openFileView(id, focus = null) {
  const M = S.model, file = M.byId.has(id) ? (M.node(id).kind === 'file' ? id : M.fileOf(id)) : null;
  if (!file) { toast('Для этого блока нет файла'); return; }
  if (S.view !== 'file') FV.prevView = S.view || 'map';
  FV.file = file;
  FV.focus = focus || (id !== file ? id : null);
  const btn = $('#viewSeg [data-view="file"]');
  btn.textContent = 'Файл: ' + M.node(file).name;
  btn.classList.remove('hidden');
  S.view = null; // force setView to rebuild
  setView('file');
  loadCode(file);
  if (FV.focus) setTimeout(() => select(FV.focus), 50);
  if (FV.hiddenEdges) toast(`В файле много связей: показаны ${MAX_FILE_EDGES} самых сильных, остальные ${FV.hiddenEdges} — при выборе функции.`, 6000);
}

function closeFileView() {
  $('#viewSeg [data-view="file"]').classList.add('hidden');
  setView(FV.prevView === 'file' ? 'map' : FV.prevView);
}

// ---- diagram of the file

// functions/properties written in this file (by their own path, so partial classes work)
function fileMembers(fid) {
  const M = S.model, path = M.node(fid).path;
  if (!M._membersByPath) {
    M._membersByPath = new Map();
    for (const n of M.byId.values()) if (n.kind === 'member' && n.path) {
      if (!M._membersByPath.has(n.path)) M._membersByPath.set(n.path, []);
      M._membersByPath.get(n.path).push(n);
    }
    for (const list of M._membersByPath.values()) list.sort((a, b) => (a.line || 0) - (b.line || 0));
  }
  return M._membersByPath.get(path) || [];
}

function buildFileElements() {
  const M = S.model, fid = FV.file, els = [];
  if (!fid || !M.byId.has(fid)) return [];
  const fpath = M.node(fid).path;
  const inFile = id => M.node(id)?.path === fpath || M.isUnder(id, fid);
  const col = M.colorOf(fid);
  const memberCard = (n) => svgCard({
    key: 'fm:' + n.id, w: 250, title: n.name + (n.subKind === 'method' || n.subKind === 'ctor' || n.subKind === 'function' ? '()' : ''),
    sub: SUB_KIND_RU[n.subKind] || n.subKind || '', body: n.summary || '', bodyLines: 2, fs: 15, bodyFs: 12,
    foot: `строка ${n.line || '?'}${n.metrics?.complexity > 1 ? ' · сложность ' + n.metrics.complexity : ''}`,
  });
  const shown = new Set();
  // Group by the file the code is really written in: a partial class spread over several files has
  // its node in one of them, but each function knows its own file.
  const byType = new Map();
  for (const m of fileMembers(fid)) {
    const t = M.parent(m.id);
    const key = M.node(t)?.kind === 'type' ? t : '';
    if (!byType.has(key)) byType.set(key, []);
    byType.get(key).push(m);
  }
  for (const t of M.kids(fid).map(k => M.node(k))) if (t.kind === 'type' && !byType.has(t.id)) byType.set(t.id, []);
  for (const [tid, members] of byType) {
    const t = tid ? M.node(tid) : null;
    if (t && members.length) {
      const partial = t.path !== M.node(fid).path ? ` (часть; класс объявлен в ${t.path.split('/').pop()})` : '';
      els.push(frameNode(tid, `${t.name} · ${SUB_KIND_RU[t.subKind] || 'тип'}${partial}`, null, col));
      shown.add(tid);
      for (const m of members) { els.push(cardNode(m.id, 'fn', memberCard(m), col, tid, { name: m.name })); shown.add(m.id); }
    } else if (t) {
      els.push(cardNode(tid, 'fn', memberCard(t), col, null, { name: t.name })); shown.add(tid);
    } else for (const m of members) { els.push(cardNode(m.id, 'fn', memberCard(m), col, null, { name: m.name })); shown.add(m.id); }
  }
  if (!shown.size) {
    els.push(cardNode(fid, 'file', svgCard({ key: 'fv:empty:' + fid, w: 360, title: M.node(fid).name, body: 'Функции и классы в этом файле не распознаны — смотрите код справа.' }), col, null));
    return els;
  }
  // links of the file's functions: inside the file member→member; outside rolled to the other class
  const outsideOf = id => [id, ...M.ancestors(id)].find(x => ['type', 'file', 'table'].includes(M.node(x)?.kind)) || id;
  const inner = new Map(), out = new Map(), inn = new Map();
  const bump = (map, key, w, data) => { const r = map.get(key) || { ...data, w: 0 }; r.w += w; map.set(key, r); };
  for (const id of shown) for (const e of M.edgesOf.get(id) || []) {
    const cat = EDGE_CAT[e.kind];
    if (cat !== 'code' && cat !== 'db') continue;
    const fromHere = e.source === id, other = fromHere ? e.target : e.source;
    if (!M.byId.has(other)) continue;
    if (inFile(other)) {
      if (!fromHere) continue; // counted once, from the source side
      const t = shown.has(other) ? other : [...M.ancestors(other)].find(a => shown.has(a));
      if (t && t !== id) bump(inner, `${id}|${t}`, e.weight || 1, { s: id, t, kind: e.kind });
    } else {
      const o = outsideOf(other);
      bump(fromHere ? out : inn, `${id}|${o}`, e.weight || 1, { s: fromHere ? id : o, t: fromHere ? o : id, kind: e.kind, o });
    }
  }
  const topOutside = map => {
    const per = new Map();
    for (const r of map.values()) per.set(r.o, (per.get(r.o) || 0) + r.w);
    return new Set([...per].sort((a, b) => b[1] - a[1]).slice(0, MAX_OUTSIDE).map(x => x[0]));
  };
  const outKeep = topOutside(out), inKeep = topOutside(inn);
  const extCard = o => {
    const n = M.node(o), f = M.fileOf(o);
    return svgCard({ key: 'fx:' + o, w: 240, title: n.name, sub: n.kind === 'type' ? `${SUB_KIND_RU[n.subKind] || 'тип'} · ${M.node(f)?.name || ''}` : KIND_RU[n.kind], fs: 14, bodyFs: 12,
      body: n._desc?.hint || n.summary || '', bodyLines: 2 });
  };
  if (outKeep.size) els.push(frameNode('fv:out', 'Чем пользуется снаружи (двойной клик — открыть тот файл)', null, NEUTRAL()));
  if (inKeep.size) els.push(frameNode('fv:in', 'Кто пользуется этим файлом', null, NEUTRAL()));
  const extAdded = new Set();
  const addExt = (o, frame) => {
    const key = frame + '|' + o;
    if (extAdded.has(key)) return frame + ':' + o;
    extAdded.add(key);
    els.push(cardNode(frame + ':' + o, 'ext', extCard(o), M.colorOf(o), frame, { name: M.node(o).name, ref: o }));
    return frame + ':' + o;
  };
  const edgeEl = (s, t, r) => edge(`fv|${s}|${t}`, s, t, EDGE_CAT[r.kind] === 'db' ? 'db' : 'code',
    { color: M.colorOf(r.s).line || col.line, lbl: `×${r.w}`, w: r.w, cat: EDGE_CAT[r.kind], kinds: r.kind });
  for (const r of inner.values()) els.push(edgeEl(r.s, r.t, r));
  for (const r of out.values()) if (outKeep.has(r.o)) els.push(edgeEl(r.s, addExt(r.o, 'fv:out'), r));
  for (const r of inn.values()) if (inKeep.has(r.o)) els.push(edgeEl(addExt(r.o, 'fv:in'), r.t, r));
  const result = dedupeEdges(els);
  // A huge file (hundreds of functions, thousands of links) can't be read as one web and takes ELK
  // seconds: lay out the strongest links, keep the rest hidden until a function is selected.
  // The rest are not even put on the diagram (1400 hidden arrows still cost ~80 ms to draw the first time):
  // applySelection adds the selected function's ones.
  const edges = result.filter(e => e.group === 'edges').sort((a, b) => b.data.w - a.data.w);
  FV.extraEdges = edges.slice(MAX_FILE_EDGES);
  FV.hiddenEdges = FV.extraEdges.length;
  if (!FV.hiddenEdges) return result;
  const extra = new Set(FV.extraEdges);
  return result.filter(e => !extra.has(e));
}

// ---- code pane

async function loadCode(fid) {
  const M = S.model, n = M.node(fid);
  $('#codePath').textContent = n.path;
  $('#codeBody').innerHTML = '<div class="muted" style="padding:16px">Загружаю…</div>';
  if (FV.textFor !== fid) {
    try {
      const r = await fetch('/file?p=' + encodeURIComponent(n.path));
      FV.text = r.ok ? await r.text() : null;
    } catch { FV.text = null; }
    FV.textFor = fid;
  }
  if (FV.file !== fid) return;
  if (FV.text == null) { $('#codeBody').innerHTML = '<div class="muted" style="padding:16px">Файл не удалось прочитать.</div>'; return; }
  const lang = (n.language || '').toLowerCase();
  const lines = FV.text.replace(/\r/g, '').split('\n');
  // a 4000-line file highlighted in one go froze the window for half a second: the first screen now, the rest
  // a few hundred lines per frame (the diagram is being built at the same time)
  const html = (from, to) => lines.slice(from, to).map((l, k) => { const i = from + k; return `<div class="cl" data-line="${i + 1}"><span class="ln">${i + 1}</span><span class="cx">${highlight(l, lang) || ' '}</span></div>`; }).join('');
  const body = $('#codeBody'), FIRST = 200, PART = 400;
  body.innerHTML = html(0, FIRST);
  markCode();
  for (let i = FIRST; i < lines.length; i += PART) {
    await new Promise(r => requestAnimationFrame(() => setTimeout(r, 0)));
    if (FV.file !== fid || FV.textFor !== fid) return; // another file was opened meanwhile
    body.insertAdjacentHTML('beforeend', html(i, i + PART));
  }
  if (lines.length > FIRST) markCode();
}

// Lines of the selected function/class: from its line to the line before the next one in the file.
function markCode() {
  const M = S.model, body = $('#codeBody');
  body.querySelectorAll('.cl.on').forEach(x => x.classList.remove('on'));
  const id = S.selected, path = M.node(FV.file)?.path;
  if (!id || !M.byId.has(id) || !FV.file || M.node(id).path !== path) return;
  const n = M.node(id);
  if (!n.line) return;
  // ends where the next function/class of this file starts
  const starts = [...fileMembers(FV.file), ...M.kids(FV.file).map(k => M.node(k))]
    .filter(x => x.path === path && x.line > n.line && !M.isUnder(x.id, id)).map(x => x.line).sort((a, b) => a - b);
  const end = Math.min((starts[0] || n.line + 40) - 1, n.line + 400);
  for (let l = n.line; l <= end; l++) body.querySelector(`.cl[data-line="${l}"]`)?.classList.add('on');
  const first = body.querySelector(`.cl[data-line="${n.line}"]`);
  if (first) body.scrollTop = first.offsetTop - 60;
}

const KW = {
  'c#': 'abstract as async await base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach get goto if implicit in init int interface internal is lock long namespace new null object operator out override params private protected public readonly record ref required return sbyte sealed set short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile when where while with yield',
  javascript: 'async await break case catch class const continue debugger default delete do else export extends false finally for from function if import in instanceof let new null of return static super switch this throw true try typeof undefined var void while with yield',
  python: 'and as assert async await break class continue def del elif else except False finally for from global if import in is lambda None nonlocal not or pass raise return self True try while with yield',
};
KW.typescript = KW.javascript + ' interface type enum implements private public protected readonly declare namespace abstract as';
const kwSets = Object.fromEntries(Object.entries(KW).map(([k, v]) => [k, new Set(v.split(' '))]));

// A light single-line highlighter: comments, strings, numbers, keywords, Capitalized names.
function highlight(line, lang) {
  const kw = kwSets[lang] || kwSets['c#'];
  const comment = lang === 'python' ? '#' : '//';
  let out = '', i = 0;
  const e = s => esc(s);
  while (i < line.length) {
    const rest = line.slice(i);
    if (rest.startsWith(comment) || rest.startsWith('/*') || rest.trimStart().startsWith('*') && i === line.length - rest.trimStart().length) { out += `<span class="tc">${e(rest)}</span>`; break; }
    const str = rest.match(/^(\$?@?"(?:[^"\\]|\\.)*"?|'(?:[^'\\]|\\.)*'?|`[^`]*`?)/);
    if (str) { out += `<span class="ts">${e(str[0])}</span>`; i += str[0].length; continue; }
    const num = rest.match(/^\d[\d_.xXa-fA-F]*[fFdDmMuUlL]*/);
    if (num && !/\w/.test(line[i - 1] || '')) { out += `<span class="tn">${e(num[0])}</span>`; i += num[0].length; continue; }
    const word = rest.match(/^[A-Za-z_][\w]*/);
    if (word) {
      const w = word[0];
      out += kw.has(w) ? `<span class="tk">${w}</span>` : /^[A-Z]/.test(w) ? `<span class="tt">${w}</span>` : e(w);
      i += w.length; continue;
    }
    out += e(line[i]); i++;
  }
  return out;
}

$('#codeOpen').onclick = () => { const n = S.model?.node(FV.file); if (n) send({ type: 'open', path: n.path, line: S.model.node(S.selected)?.line || 0 }); };
$('#codeClose').onclick = () => closeFileView();
$('#codeBody').addEventListener('dblclick', e => {
  // double click on a line: the function that contains it
  const l = +e.target.closest('.cl')?.dataset.line; if (!l) return;
  const M = S.model;
  const path = M.node(FV.file).path;
  const owner = [...fileMembers(FV.file), ...M.kids(FV.file).map(k => M.node(k))].filter(x => x.path === path && x.line <= l)
    .sort((a, b) => b.line - a.line)[0];
  if (owner && cy.getElementById(owner.id).nonempty()) select(owner.id, { center: true });
});
