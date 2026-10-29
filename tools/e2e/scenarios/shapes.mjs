// The five shape views on Map: each drawn, text overlap, one click inside, timing.
const overlaps = `(() => {
  const r = [...document.querySelectorAll('#lv text')].filter(t => t.textContent).map(t => ({ t: t.textContent, b: t.getBoundingClientRect(), rot: !!t.getAttribute('transform') })).filter(x => x.b.width > 0);
  const bad = [];
  for (let i = 0; i < r.length; i++) for (let j = i + 1; j < r.length; j++) {
    if (r[i].rot || r[j].rot) continue; // rotated labels: checked by eye on the screenshot (their boxes are not tight)
    const a = r[i].b, b = r[j].b, ix = Math.min(a.right, b.right) - Math.max(a.left, b.left), iy = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
    if (ix > 1 && iy > 1) bad.push(r[i].t + ' × ' + r[j].t);
  }
  return { texts: r.length, badCount: bad.length, bad: bad.slice(0, 6) };
})()`;
export default async page => {
  const res = {};
  await page.eval(`setView('links')`); await page.wait(`LV.scene`);
  for (const mode of ['tree', 'tiles', 'sun', 'stars', 'flows']) {
    const t0 = Date.now();
    await page.eval(`document.querySelector('[data-lv-mode="${mode}"]').click()`);
    await page.wait(`LV.scene?.kind === '${mode}'`);
    const r = { ms: Date.now() - t0, text: await page.eval(overlaps), k: await page.eval('LV.t.k') };
    await page.shot('sv-' + mode);
    // one click on the first clickable shape that is not the centre
    const target = await page.eval(`(() => { const el = [...document.querySelectorAll('#lv g[style*="cursor"], #lv path[style*="cursor"]')].find(e => e.getBoundingClientRect().width > 20); if (!el) return null; const b = el.getBoundingClientRect(); return { x: b.left + Math.min(30, b.width / 2), y: b.top + Math.min(20, b.height / 2) }; })()`);
    if (target) {
      await page.click(target.x, target.y); await page.sleep(300);
      r.after = await page.eval(`({ mode: LV.mode, kind: LV.scene.kind, stack: LV.stack.length, crumbs: [...document.querySelectorAll('#lvBar .crumb')].map(c => c.textContent) })`);
      r.afterText = await page.eval(overlaps);
      await page.shot('sv-' + mode + '-in');
    }
    await page.eval(`LV.stack = []; LV.mode = '${mode}'; lvRender()`);
    res[mode] = r;
  }
  return res;
};
