// Switching between the diagram modes: map, hierarchy tree, plan, database.
'use strict';

const VIEW_HINT = {
  map: 'Двойной клик — раскрыть/свернуть · клик — подробности · правый клик — меню',
  tree: 'Двойной клик — раскрыть ветку · клик по файлу — стрелки его зависимостей',
  plan: 'Клик по задаче — какие части кода она затрагивает · фильтр статусов — слева, вкладка «План»',
  db: 'Таблицы, связи между ними и файлы, которые их читают или пишут · двойной клик по файлу — на схему',
  file: 'Функции файла: стрелка — вызов или обращение к полю/свойству · клик — показать код · двойной клик по внешнему — открыть тот файл',
};

function setView(v) {
  if (!S.model) return;
  if (S.view === v) return;
  S.view = v;
  $$('#viewSeg button').forEach(b => b.classList.toggle('active', b.dataset.view === v));
  // depth and edge-layer controls only make sense on the map
  $('#depthSeg').classList.toggle('hidden', v !== 'map');
  $('#edgeChips').classList.toggle('hidden', v !== 'map');
  $('#hint').textContent = VIEW_HINT[v];
  $('#stage').classList.toggle('with-code', v === 'file');
  $('#code').classList.toggle('hidden', v !== 'file');
  if (v === 'file' && !FV.file) { S.view = null; setView('map'); return; }
  if (v === 'plan' || v === 'db') switchTab(v);
  else if (document.querySelector('#sideTabs .active')?.dataset.tab !== 'hot') switchTab('tree');
  S.focus = null;
  cy.elements().remove();
  cy.resize(); // the code pane changes the canvas width
  render({ fit: true });
}

$('#viewSeg').addEventListener('click', e => { const b = e.target.closest('button'); if (b) setView(b.dataset.view); });
