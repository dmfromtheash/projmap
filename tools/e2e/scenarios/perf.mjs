// Speed of the new views on Map: time to picture per view, and frames during wheel zoom and drag (rAF + long tasks).
export default async page => {
  await page.eval(`window.__lt = []; new PerformanceObserver(l => { for (const e of l.getEntries()) __lt.push(e.duration); }).observe({ entryTypes: ['longtask'] })`);
  const res = {};
  const measureGesture = async () => {
    await page.eval(`window.__f = []; (function loop(t) { __f.push(t); if (__f.length < 400) requestAnimationFrame(loop); })(performance.now())`);
    for (let i = 0; i < 20; i++) { await page.wheel(800, 500, i % 2 ? 120 : -120); await page.sleep(30); }
    await page.move(700, 500);
    for (let i = 0; i < 20; i++) { await page.sleep(16); await page.eval(`void 0`); }
    await page.sleep(300);
    return page.eval(`(() => { const d = __f.slice(1).map((t, i) => t - __f[i]); const worst = Math.max(...d); const fps = 1000 / (d.reduce((a, b) => a + b, 0) / d.length); return { fps: Math.round(fps), worstFrame: Math.round(worst) }; })()`);
  };
  await page.eval(`setView('links')`); await page.wait(`LV.scene`);
  for (const m of ['links', 'tree', 'tiles', 'sun', 'stars', 'flows', 'time', 'path']) {
    await page.eval(`__lt.length = 0`);
    const t0 = await page.eval(`performance.now()`);
    await page.eval(`LV.stack = []; LV.mode = '${m}'; ${m === 'path' ? "SV_PATH.start = [...LV.data.entries.values()][0][0];" : ''} lvRender()`);
    const ms = Math.round(await page.eval(`new Promise(r => requestAnimationFrame(() => r(performance.now())))`) - t0);
    res[m] = { toPicture: ms, block: Math.round(await page.eval(`Math.max(0, ...__lt)`)), ...(await measureGesture()) };
  }
  // drill into a block and a file
  const t0 = await page.eval(`performance.now()`);
  await page.eval(`LV.mode = 'links'; LV.stack = [{ cell: [...LV.data.cells.keys()][0] }]; lvRender()`);
  res.insideBlock = Math.round(await page.eval(`new Promise(r => requestAnimationFrame(() => r(performance.now())))`) - t0);
  res.memoryMB = Math.round(await page.eval(`performance.memory ? performance.memory.usedJSHeapSize / 1048576 : 0`));
  return res;
};
