// Novice layer (plan steps 4 and 6) on Map: the card beside the chosen block on «Схема» and in «Связи»,
// honest marks, «Где лежит» → the folder and «Назад» to the same object, the glossary; text overlap.
const ACT = 'member:module:src/Map.Presentation/Map.Presentation.csproj:M:Map.Presentation.ViewModels.EditorViewModel.ExportLayersAsync';
const card = `(() => {
  const c = document.querySelector('#nvCard');
  if (!c || c.classList.contains('hidden')) return { shown: false };
  const r = c.getBoundingClientRect(), st = document.querySelector('#stage').getBoundingClientRect();
  // text boxes inside the card must not overlap each other or run out of the card
  const leaves = [...c.querySelectorAll('*')].filter(e => e.children.length === 0 && e.textContent.trim() && e.getClientRects().length);
  const bad = [];
  for (let i = 0; i < leaves.length; i++) {
    const a = leaves[i].getBoundingClientRect();
    if (a.right > r.right + .5 || a.left < r.left - .5) bad.push('out: ' + leaves[i].textContent.slice(0, 40));
    for (let j = i + 1; j < leaves.length; j++) {
      const b = leaves[j].getBoundingClientRect(), ix = Math.min(a.right, b.right) - Math.max(a.left, b.left), iy = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
      if (ix > 1 && iy > 1 && !leaves[i].contains(leaves[j]) && !leaves[j].contains(leaves[i])) bad.push(leaves[i].textContent.slice(0, 30) + ' × ' + leaves[j].textContent.slice(0, 30));
    }
  }
  return { shown: true, id: c.dataset.id, inStage: r.left >= st.left && r.right <= st.right + .5 && r.top >= st.top && r.bottom <= st.bottom + .5,
    title: c.querySelector('.nv-plain')?.textContent, code: c.querySelector('code')?.textContent,
    tags: [...c.querySelectorAll('.nv-tag')].map(t => t.textContent), where: [...c.querySelectorAll('.nv-where button')].map(b => b.textContent),
    back: !!c.querySelector('[data-nv-back]'), bad };
})()`;
export default async page => {
  const res = {};
  // «Схема»: the action from the PNG button
  await page.eval(`reveal(${JSON.stringify(ACT)})`);
  await page.wait(`!document.querySelector('#nvCard').classList.contains('hidden')`, 20000);
  await page.sleep(600);
  res.mapCard = await page.eval(card);
  res.passport = await page.eval(`[...document.querySelectorAll('#details .nv-pass .nv-tag')].map(t => t.textContent)`);
  await page.shot('1-map-card');
  // «Где лежит» → its folder, then «Назад» returns to the same action
  const folder = await page.eval(`(() => { const b = [...document.querySelectorAll('#nvCard .nv-where button')]; const f = b[b.length - 2]; f?.click(); return f?.textContent; })()`);
  await page.sleep(900);
  res.afterWhere = { folder, selected: await page.eval(`S.selected`) };
  await page.shot('2-map-folder');
  await page.eval(`document.querySelector('#nvCard [data-nv-back]')?.click()`);
  await page.sleep(900);
  res.afterBack = await page.eval(`S.selected`);
  res.backOk = res.afterBack === ACT;
  // «Связи»: the same action, the card beside the middle block
  await page.eval(`setView('links'); LV.stack = lvPathTo(${JSON.stringify(ACT)}); lvRender()`);
  await page.wait(`LV.scene && document.querySelector('#lv svg')`);
  await page.sleep(600);
  res.linksCard = await page.eval(card);
  await page.shot('3-links-card');
  // glossary from a dotted word
  await page.eval(`document.querySelector('#nvCard [data-nv-term]')?.click()`);
  await page.sleep(300);
  res.glossary = await page.eval(`(() => { const g = document.querySelector('#nvGloss'); return g && !g.classList.contains('hidden') ? g.querySelector('dt.on')?.textContent : null; })()`);
  await page.shot('4-glossary');
  await page.eval(`document.querySelector('[data-nv-gloss-close]')?.click()`);
  // dark theme and a narrow window
  await page.eval(`setTheme('dark')`); await page.size(1280, 800); await page.eval(`lvRender({ keepView: true })`); await page.sleep(500);
  res.narrow = await page.eval(card);
  await page.shot('5-dark-narrow');
  return res;
};
