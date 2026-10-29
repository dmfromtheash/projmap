export default async page => {
  await page.eval(`setView('links')`); await page.wait(`LV.scene`);
  await page.eval(`document.querySelector('[data-lv-mode="problems"]').click()`); await page.wait(`LV.scene?.kind === 'problems'`);
  const bad = await page.eval(`(() => { const r = [...document.querySelectorAll('#lv text')].map(t => t.getBoundingClientRect()).filter(b => b.width > 0); let n = 0; for (let i = 0; i < r.length; i++) for (let j = i + 1; j < r.length; j++) { const a = r[i], b = r[j]; if (Math.min(a.right, b.right) - Math.max(a.left, b.left) > 1 && Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top) > 1) n++; } return n; })()`);
  await page.shot('pr-0');
  await page.eval(`SV_PROB.sel = 0; lvRender({ keepView: true })`);
  const det = await page.eval(`document.querySelector('#details h2')?.textContent`);
  await page.shot('pr-1');
  return { bad, det };
};
