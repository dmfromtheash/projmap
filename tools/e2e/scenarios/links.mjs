// «Схема связей» on Map: overview, hover, inside a block, inside a file, narrow window; text overlap and timing.
const overlaps = `(() => {
  const els = [...document.querySelectorAll('#lv text')].filter(t => t.textContent && t.getClientRects().length);
  const r = els.map(t => ({ t: t.textContent, b: t.getBoundingClientRect() })).filter(x => x.b.width > 0);
  const bad = [];
  for (let i = 0; i < r.length; i++) for (let j = i + 1; j < r.length; j++) {
    const a = r[i].b, b = r[j].b, ix = Math.min(a.right, b.right) - Math.max(a.left, b.left), iy = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
    if (ix > 1 && iy > 1) bad.push(r[i].t + ' × ' + r[j].t);
  }
  // text running out of its block
  for (const g of document.querySelectorAll('#lv .blk')) {
    const box = g.querySelector('rect.b').getBoundingClientRect();
    for (const t of g.querySelectorAll('text')) { const b = t.getBoundingClientRect(); if (b.right > box.right + 0.5 || b.left < box.left - 0.5) bad.push('out of block: ' + t.textContent); }
  }
  return { texts: r.length, bad: bad.slice(0, 12), badCount: bad.length };
})()`;
const blockAt = expr => `(() => { const b = LV.scene.blocks.find(${expr}); if (!b) return null; const el = LV.scene.blockEls.get(b.side ? b.side + b.id : b.id); const r = el.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2, id: b.id, name: b.name }; })()`;
export default async page => {
  const res = {};
  let t = Date.now();
  await page.eval(`setView('links')`);
  await page.wait(`LV.scene && document.querySelector('#lv svg')`);
  res.overviewMs = Date.now() - t;
  res.overview = await page.eval(`({ blocks: LV.scene.blocks.length, links: LV.scene.links.length, rows: LV.scene.bands.map(b => b.name), k: LV.t.k })`);
  res.overviewText = await page.eval(overlaps);
  await page.shot('1-overview');
  const hex = await page.eval(blockAt(`b => b.tier === 'frontend' && b.name === 'Гексы'`));
  res.hex = hex;
  if (hex) {
    await page.move(hex.x, hex.y); await page.sleep(150);
    res.hoverShown = await page.eval(`LV.scene.linkEls.filter(x => x.path.style.display !== 'none').length`);
    await page.shot('2-hover-hex');
    t = Date.now();
    await page.click(hex.x, hex.y); await page.wait(`LV.stack.length === 1`);
    res.insideMs = Date.now() - t;
    res.inside = await page.eval(`({ title: LV.scene.title, counts: LV.scene.counts, blocks: LV.scene.blocks.length, links: LV.scene.links.length })`);
    res.insideText = await page.eval(overlaps);
    await page.shot('3-inside-hex-ui');
    const file = await page.eval(blockAt(`b => b.side === 'mid'`));
    res.file = file;
    if (file) {
      await page.click(file.x, file.y); await page.wait(`LV.stack.length === 2`);
      res.inFile = await page.eval(`({ title: LV.scene.title, counts: LV.scene.counts })`);
      res.fileText = await page.eval(overlaps);
      await page.shot('4-inside-file');
      const type = await page.eval(blockAt(`b => b.side === 'mid' && b.drill`));
      if (type) { await page.click(type.x, type.y); await page.wait(`LV.stack.length === 3`); res.inType = await page.eval(`({ title: LV.scene.title, counts: LV.scene.counts })`); await page.shot('5-inside-class'); }
      res.details = await page.eval(`document.querySelector('#details h2')?.textContent`);
    }
    // wheel zoom and the way back
    await page.wheel(700, 500, -400); await page.sleep(100);
    res.zoomK = await page.eval(`LV.t.k`);
    await page.key('Escape'); await page.sleep(100);
    res.afterEsc = await page.eval(`LV.stack.length`);
    await page.eval(`lvBack(0)`);
  }
  await page.size(1280, 760); await page.eval(`dispatchEvent(new Event('resize')); lvFitView()`);
  await page.sleep(200);
  res.narrowText = await page.eval(overlaps);
  res.headerOverflow = await page.eval(`(() => { const t = document.querySelector('#top'); return t.scrollWidth > t.clientWidth + 1; })()`);
  await page.shot('6-overview-narrow');
  await page.eval(`document.documentElement.dataset.theme = 'dark'`); await page.sleep(100);
  await page.size(2048, 1100); await page.eval(`lvFitView()`); await page.sleep(100);
  await page.shot('7-overview-dark');
  return res;
};
