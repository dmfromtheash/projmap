// The novice layer in the window (plan steps 4 and 6): a short card beside the chosen block on the
// classic «Схема» and in «Связи», honest marks of where each statement comes from, a glossary,
// «Где лежит» and «Назад». Selection history lives in NavigationState (navigation.js); the card
// only reads the model and the projection (projection.js) and never changes what views draw.
'use strict';

const NV = { nav: null, proj: null, last: null, back: false, open: true };

// Where a statement comes from, in the owner's words; the order is also the strength of the source.
const NV_SOURCE = {
  comment: ['comment', 'из комментария'], markdown: ['doc', 'из документа'], git: ['history', 'по истории изменений'],
  handlerLabel: ['code', 'найдено в коде'], declaration: ['code', 'по устройству кода'], tier: ['code', 'по устройству кода'],
};
const NV_GUESS = ['guess', 'предположение по имени'];

const NV_TERMS = {
  block: ['Блок кода', 'Именованная часть программы, где собраны связанные данные и действия. Частый вид — класс. Один блок может лежать в нескольких файлах.'],
  action: ['Действие', 'Отдельная операция в коде: «сохранить», «экспортировать». Программисты называют это методом или функцией.'],
  file: ['Файл', 'Лист с текстом программы, настройками или описанием. Лежит в папке; в одном файле бывает несколько блоков кода.'],
  folder: ['Папка', 'Собирает файлы одной темы. «Где лежит» показывает настоящий путь.'],
  module: ['Модуль', 'Крупная часть программы со своей ролью: окна, хранение, рисование. Обычно это отдельный проект внутри решения.'],
  tier: ['Ярус', 'Части с похожей ролью: интерфейс показывает кнопки, хранение записывает файлы. Если ярус угадан по папке, это предположение.'],
  uses: ['«Пользуется»', 'Обращается к другой части: запускает её действие или берёт её данные.'],
  users: ['«Им пользуются»', 'Другие части обращаются к этой. После её изменения их стоит проверить; сама связь поломку не предсказывает.'],
  marks: ['Откуда известно', 'Зелёная метка — сказано автором в комментарии или документе. Синяя — найдено по устройству кода или истории. Жёлтая — только предположение по имени, его стоит проверить.'],
};
const NV_KIND_TERM = { member: 'action', type: 'block', file: 'file', folder: 'folder', module: 'module' };

function nvMark(claim, nodeId) {
  const kinds = (S.model.sourcesFor?.(claim, nodeId) || []).map(s => s.kind);
  for (const k of ['comment', 'markdown', 'git', 'handlerLabel']) if (kinds.includes(k)) return NV_SOURCE[k];
  return claim.confidence === 'guessed' ? NV_GUESS : NV_SOURCE.declaration;
}
const nvTag = ([cls, text]) => `<span class="nv-tag nv-${cls}">${text}</span>`;
const nvTerm = (key, label) => `<button class="nv-term" data-nv-term="${key}" title="Что это?">${esc(label)}</button>`;

/** The one chosen node of the visible view, or null where the card does not apply. */
function nvSelected() {
  if (!S.model) return null;
  if (S.view === 'links') {
    if (['time', 'path', 'problems'].includes(LV.mode) && !LV.stack.length) return null;
    const id = LV.sel || LV.stack[LV.stack.length - 1]?.node;
    return id && S.model.byId.has(id) ? id : null;
  }
  if (S.view && S.view !== 'map') return null;
  return S.selected && S.model.byId.has(S.selected) ? S.selected : null;
}

/** Screen box of the chosen block inside #stage, from the picture that is drawn now. */
function nvAnchor(id) {
  const stage = $('#stage').getBoundingClientRect();
  if (S.view === 'links') {
    const els = LV.scene?.blockEls;
    const el = els && (els.get('mid' + id) || els.get(id) || [...els].find(([k]) => k.endsWith(id))?.[1]);
    if (!el) return null;
    const r = el.getBoundingClientRect();
    return { x: r.left - stage.left, y: r.top - stage.top, w: r.width, h: r.height };
  }
  const rep = makeRep(new Set(cy.nodes().map(n => n.id())))(id);
  const n = rep && cy.getElementById(rep);
  if (!n || n.empty()) return null;
  const b = n.renderedBoundingBox(), c = $('#cy').getBoundingClientRect();
  return { x: b.x1 + c.left - stage.left, y: b.y1 + c.top - stage.top, w: b.w, h: b.h };
}

function nvCardHtml(id) {
  const M = S.model, n = M.node(id), ex = M.explanationsById?.get(id);
  const plain = ex?.displayName?.text || titleOf(n);
  const kindKey = NV_KIND_TERM[n.kind];
  const kind = esc(KIND_RU[n.kind]?.split(' (')[0] || n.kind);
  const lines = [];
  if (ex?.description) lines.push(`<div class="nv-claim">${nvTag(nvMark(ex.description, id))}<span>${esc(ex.description.text)}</span></div>`);
  const said = (ex?.claims || []).find(c => nvMark(c, id)[0] === 'comment' || nvMark(c, id)[0] === 'doc');
  if (said) {
    const text = said.text.replace(/^(В комментарии автора|Комментарий к [^:]+):\s*/, '');
    lines.push(`<div class="nv-claim">${nvTag(nvMark(said, id))}<q>${esc(text.length > 220 ? text.slice(0, 217) + '…' : text)}</q></div>`);
  }
  const count = dir => (NV.proj?.linksOf(id, { direction: dir }) || [])
    .filter(g => !['cochange', 'mentions'].includes(g.kind)).reduce((s, g) => s + g.relations.length, 0);
  const loc = NV.proj?.locationOf(id);
  const steps = loc ? [loc.module, ...(loc.folders || []), loc.file].filter(Boolean) : [];
  const where = steps.length
    ? `<div class="nv-where">${steps.map(s => `<button data-nv-go="${esc(s.id)}" title="${esc(s.path || s.name)}">${esc(s.name)}</button>`).join('<i>›</i>')}${loc.line ? `<span class="muted">строка ${loc.line}</span>` : ''}</div>`
    : '';
  return `<div class="nv-head"><div class="nv-plain">${esc(plain)}</div><button class="nv-x" data-nv-close title="Скрыть карточку">✕</button></div>
    <div class="nv-coderow"><code>${esc(n.name)}</code> ${kindKey ? nvTerm(kindKey, kind) : kind}</div>
    ${ex?.displayName && ex.displayName.text !== n.name ? `<div class="nv-sub">${nvTag(nvMark(ex.displayName, id))} <span class="muted">так названо по словам из имени</span></div>` : ''}
    ${lines.join('')}
    <div class="nv-count muted">${nvTerm('uses', 'Пользуется')}: ${count('out')} · ${nvTerm('users', 'Им пользуются')}: ${count('in')}</div>
    ${where ? `<div class="nv-label">Где лежит</div>${where}` : ''}
    <div class="nv-act"><button data-nv-more class="main">Подробнее</button>${NV.nav?.canBack ? '<button data-nv-back title="Назад к прежнему выбору (Alt+←)">← Назад</button>' : ''}${nvTerm('marks', 'Откуда известно?')}</div>`;
}

function nvPlace() {
  const card = $('#nvCard'), id = nvSelected();
  if (!id || !NV.open || $('#code') && !$('#code').classList.contains('hidden')) { card.classList.add('hidden'); return; }
  const a = nvAnchor(id);
  if (!a) { card.classList.add('hidden'); return; }
  if (card.dataset.id !== id || card.dataset.back !== String(NV.nav?.canBack)) {
    card.innerHTML = nvCardHtml(id);
    card.dataset.id = id; card.dataset.back = String(NV.nav?.canBack);
  }
  card.classList.remove('hidden');
  const stage = $('#stage'), W = stage.clientWidth, H = stage.clientHeight, cw = card.offsetWidth, ch = card.offsetHeight;
  const top = 48, clampX = x => Math.max(8, Math.min(x, W - cw - 8)), clampY = y => Math.max(top, Math.min(y, H - ch - 8));
  // Other blocks of the picture the card should not cover (the «Связи» columns); the chosen block too.
  const st = stage.getBoundingClientRect(), obstacles = [a];
  if (S.view === 'links') for (const el of LV.scene?.blockEls?.values() || []) {
    const r = el.getBoundingClientRect();
    obstacles.push({ x: r.left - st.left, y: r.top - st.top, w: r.width, h: r.height });
  }
  const cover = (x, y) => obstacles.reduce((s, o) => s + Math.max(0, Math.min(x + cw, o.x + o.w) - Math.max(x, o.x))
    * Math.max(0, Math.min(y + ch, o.y + o.h) - Math.max(y, o.y)), 0);
  // Under, beside, above the block, or a free corner when the block is larger than the screen.
  const cx = a.x + a.w / 2 - cw / 2;
  const places = [[cx, a.y + a.h + 12], [a.x + a.w + 14, a.y], [a.x - cw - 14, a.y], [cx, a.y - ch - 12],
    [W - cw - 8, top], [8, top], [W - cw - 8, H - ch - 8], [8, H - ch - 8]]
    .map(([x, y]) => [clampX(x), clampY(y)]);
  let best = places[0], least = Infinity;
  for (const [x, y] of places) { const c = cover(x, y); if (c < least - 1) { least = c; best = [x, y]; } }
  card.style.left = best[0] + 'px';
  card.style.top = best[1] + 'px';
}

/** Records a new choice in the shared history (not while going back). */
function nvTrack() {
  const id = nvSelected();
  if (!NV.nav || !id || id === NV.last) { nvPlace(); return; }
  if (!NV.back) {
    try { NV.nav.go({ selectedId: id, scope: NV.nav.state.scope, view: S.view }); } catch { /* a viewer-only id */ }
  }
  NV.last = id; NV.back = false; NV.open = true;
  nvPlace();
}

function nvBack() {
  if (!NV.nav?.canBack) return;
  NV.back = true;
  const status = NV.nav.back();
  const id = NV.nav.state.selectedId;
  if (!id || !S.model.byId.has(id)) { NV.back = false; if (status?.status === 'missing') toast('Прежний объект исчез после пересканирования.'); return; }
  if (S.view === 'links') { LV.stack = lvPathTo(id); lvRender(); }
  else reveal(id);
}

function nvGlossary(key) {
  let g = $('#nvGloss');
  if (!g) {
    g = Object.assign(document.createElement('aside'), { id: 'nvGloss' });
    document.querySelector('main').appendChild(g);
  }
  g.innerHTML = `<div class="nv-head"><b>Словарь</b><button class="nv-x" data-nv-gloss-close title="Закрыть">✕</button></div>
    <div class="muted">Схема остаётся на месте. Подчёркнутые слова в карточке открывают этот словарь.</div>
    <dl>${Object.entries(NV_TERMS).map(([k, [t, d]]) => `<dt class="${k === key ? 'on' : ''}">${t}</dt><dd>${d}</dd>`).join('')}</dl>`;
  g.classList.remove('hidden');
  g.querySelector('dt.on')?.scrollIntoView({ block: 'center' });
}

/** «Что это» in the details panel: every statement with its own mark and source. */
function nvPassport() {
  const id = S.view === 'links' ? nvSelected() : S.selected;
  const el = $('#details'), M = S.model;
  const ex = id && M?.explanationsById?.get(id);
  if (!ex || el.querySelector('.nv-pass')) return;
  const rows = [ex.description, ...(ex.claims || [])].filter(Boolean).map(c => {
    const src = (M.sourcesFor?.(c, id) || []).find(s => s.path);
    return `<div class="nv-claim">${nvTag(nvMark(c, id))}<span>${esc(c.text)}</span>${src ? `<small class="path" data-path="${esc(src.path)}" data-line="${src.line || 0}">${esc(src.path.split('/').pop())}${src.line ? ':' + src.line : ''}</small>` : ''}</div>`;
  }).join('');
  const card = document.createElement('div');
  card.className = 'card nv-pass';
  card.innerHTML = `<h3>Откуда известно: ${esc(M.node(id).name)} ${nvTerm('marks', '?')}</h3>${rows}`;
  const head = el.querySelector('.card.head') || el.querySelector('.card');
  head ? head.after(card) : el.prepend(card);
}

(function nvInstall() {
  const card = Object.assign(document.createElement('div'), { id: 'nvCard', className: 'hidden' });
  $('#stage').appendChild(card);
  const tools = document.querySelector('#top .tools');
  const btn = Object.assign(document.createElement('button'), { id: 'btnGloss', textContent: 'Словарь', title: 'Что значат слова на схеме' });
  tools.insertBefore(btn, tools.firstChild.nextSibling);
  btn.addEventListener('click', () => nvGlossary());

  // The views stay as they are; the layer listens to them through their own entry points.
  const wrap = (name, after) => {
    const original = window[name];
    window[name] = function (...args) { const r = original.apply(this, args); try { after(...args); } catch (e) { console.error(e); } return r; };
  };
  wrap('loadGraph', () => {
    if (NV.nav) NV.nav.restore(S.model); else NV.nav = new NavigationState(S.model);
    NV.proj = new GraphProjection(S.model); NV.last = null;
    $('#nvCard').dataset.id = '';
    nvTrack();
  });
  wrap('select', () => nvTrack());
  wrap('applySelection', () => nvPlace());
  wrap('lvDetails', () => nvTrack());
  wrap('lvApply', () => nvPlace());
  wrap('renderDetails', () => nvPassport());
  wrap('lvDetails', () => nvPassport());
  cy.on('viewport', () => nvPlace());
  window.addEventListener('resize', () => nvPlace());

  card.addEventListener('click', e => {
    const go = e.target.closest('[data-nv-go]');
    if (go) { const id = go.dataset.nvGo; if (S.view === 'links') { LV.stack = lvPathTo(id); LV.sel = null; lvRender(); } else reveal(id); return; }
    if (e.target.closest('[data-nv-back]')) { nvBack(); return; }
    if (e.target.closest('[data-nv-close]')) { NV.open = false; nvPlace(); return; }
    if (e.target.closest('[data-nv-more]')) {
      const d = $('#details'); d.scrollTop = 0;
      (d.querySelector('.nv-pass') || d.firstElementChild)?.classList.add('nv-flash');
      setTimeout(() => d.querySelector('.nv-flash')?.classList.remove('nv-flash'), 1400);
    }
  });
  document.addEventListener('click', e => {
    const t = e.target.closest('[data-nv-term]');
    if (t) { e.stopPropagation(); nvGlossary(t.dataset.nvTerm); return; }
    if (e.target.closest('[data-nv-gloss-close]')) $('#nvGloss')?.classList.add('hidden');
  }, true);
  document.addEventListener('keydown', e => {
    if (e.altKey && e.key === 'ArrowLeft' && !e.target.closest('input, textarea, select')) { e.preventDefault(); nvBack(); }
    if (e.key === 'Escape') $('#nvGloss')?.classList.add('hidden');
  });
})();
