// «Время» and «От кнопки» on Map: R13 touched 22 files; the «Слои отдельными PNG» button reaches LayerPngExporter.
const overlaps = `(() => {
  const r = [...document.querySelectorAll('#lv text')].filter(t => t.textContent).map(t => ({ t: t.textContent, b: t.getBoundingClientRect() })).filter(x => x.b.width > 0);
  const bad = [];
  for (let i = 0; i < r.length; i++) for (let j = i + 1; j < r.length; j++) {
    const a = r[i].b, b = r[j].b, ix = Math.min(a.right, b.right) - Math.max(a.left, b.left), iy = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
    if (ix > 1 && iy > 1) bad.push(r[i].t + ' × ' + r[j].t);
  }
  return { texts: r.length, badCount: bad.length, bad: bad.slice(0, 6) };
})()`;
export default async page => {
  const res = {};
  await page.eval(`setView('links')`); await page.wait(`LV.scene`);
  await page.eval(`document.querySelector('[data-lv-mode="time"]').click()`); await page.wait(`LV.scene?.kind === 'time'`);
  res.time = await page.eval(overlaps);
  res.weeks = await page.eval(`svTimeData(LV.data).cols.length`);
  await page.shot('tp-time');
  await page.eval(`document.querySelector('#details [data-sv-stage="R13"]').click()`); await page.sleep(200);
  res.r13 = await page.eval(`document.querySelector('#details .what').innerText`);
  res.timeSel = await page.eval(overlaps);
  await page.shot('tp-time-r13');
  await page.eval(`document.querySelector('[data-lv-mode="path"]').click()`); await page.wait(`LV.scene?.kind === 'path'`);
  await page.eval(`(() => { const i = document.querySelector('#details [data-sv-q]'); i.value = 'PNG'; i.dispatchEvent(new Event('input', { bubbles: true })); })()`);
  await page.sleep(100);
  res.pngButtons = await page.eval(`[...document.querySelectorAll('#details [data-sv-start]')].map(x => x.textContent).slice(0, 5)`);
  await page.eval(`[...document.querySelectorAll('#details [data-sv-start]')].find(x => /отдельными PNG/.test(x.textContent))?.click()`);
  await page.wait(`SV_PATH.start`);
  res.steps = await page.eval(`svPathSteps(LV.data, SV_PATH.start).map(s => S.model.node(s.id).name + (s.possible ? '?' : '')).slice(0, 14)`);
  res.reachesExporter = await page.eval(`svPathSteps(LV.data, SV_PATH.start).some(s => /LayerPngExporter/.test(S.model.node(s.id).path || ''))`);
  res.path = await page.eval(overlaps);
  await page.shot('tp-path');
  return res;
};
