// In-page probes for the perf harness. Read-only: they observe the viewer, never replace its functions.

// Frame recorder (requestAnimationFrame intervals) + long tasks (PerformanceObserver), and a
// listener for host messages so the scan time of the background rescan is captured.
export const install = `(() => {
  if (window.__perf) return true;
  const P = window.__perf = { rec: null, long: [], hostGraph: [] };
  try {
    new PerformanceObserver(list => { for (const e of list.getEntries()) P.long.push({ start: e.startTime, ms: e.duration }); })
      .observe({ type: 'longtask', buffered: true });
  } catch { P.noLongtask = true; }
  window.chrome?.webview?.addEventListener('message', e => {
    if (e.data?.type === 'graph' || e.data?.type === 'unchanged') P.hostGraph.push({ at: performance.now(), type: e.data.type, cached: !!e.data.cached, ms: e.data.ms || 0 });
  });
  P.start = () => {
    const r = P.rec = { t0: performance.now(), frames: [], on: true };
    const tick = t => { if (!r.on) return; r.frames.push(t); requestAnimationFrame(tick); };
    requestAnimationFrame(tick);
    return true;
  };
  P.stop = () => {
    const r = P.rec; if (!r) return null; r.on = false;
    const t1 = performance.now(), f = r.frames, d = [];
    for (let i = 1; i < f.length; i++) d.push(f[i] - f[i - 1]);
    const sorted = [...d].sort((a, b) => a - b), q = p => sorted.length ? sorted[Math.min(sorted.length - 1, Math.floor(p * sorted.length))] : 0;
    const span = f.length > 1 ? f[f.length - 1] - f[0] : t1 - r.t0;
    const long = P.long.filter(l => l.start + l.ms >= r.t0 && l.start <= t1);
    return {
      ms: Math.round(t1 - r.t0), frames: f.length,
      fps: f.length > 1 ? +(1000 * (f.length - 1) / span).toFixed(1) : 0,
      // the gap between the start of recording and the first frame counts as a frame too
      worstMs: +Math.max(f.length ? f[0] - r.t0 : t1 - r.t0, ...d, 0).toFixed(1),
      p95Ms: +q(0.95).toFixed(1), over17: d.filter(x => x > 17.5).length, over50: d.filter(x => x > 50).length,
      longTasks: long.length, longMaxMs: Math.round(Math.max(0, ...long.map(l => l.ms))), longTotalMs: Math.round(long.reduce((s, l) => s + l.ms, 0)),
    };
  };
  return true;
})()`;

// Resolves when the viewer is idle: no layout running, no busy overlay, the layout counter unchanged
// for 300 ms, file code loaded in the «Файл» view; then two frames so the picture is on screen.
// Returns the time (ms since t0) at which the idle state began and when the picture was painted.
export const settle = (t0Expr = 'performance.now()', timeout = 60000) => `(async () => {
  const t0 = ${t0Expr};
  const busyEl = document.querySelector('#busy');
  const idle = () => !elkBusy && busyEl.classList.contains('hidden')
    && (S.view !== 'file' || document.querySelector('#codeBody .cl'));
  let seq = layoutSeq, since = null;
  while (performance.now() - t0 < ${timeout}) {
    await new Promise(r => setTimeout(r, 16));
    if (layoutSeq !== seq) { seq = layoutSeq; since = null; }
    if (idle()) { if (since === null) since = performance.now(); if (performance.now() - since >= 300) break; }
    else since = null;
  }
  const doneAt = since ?? performance.now(), waitEnd = performance.now();
  await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)));
  // the 300 ms stability wait is not part of the time to the picture
  return { layoutMs: Math.round(doneAt - t0), paintMs: Math.round(doneAt - t0 + performance.now() - waitEnd), nodes: cy.nodes().length, edges: cy.edges().length,
    timedOut: performance.now() - t0 >= ${timeout} };
})()`;

// A point on the diagram canvas that is free of blocks (so a drag pans), in CSS pixels.
export const freePoint = `(() => {
  const r = document.querySelector('#cy').getBoundingClientRect();
  // frames are nodes too: pressing on any node does not pan, so avoid them all
  const boxes = cy.nodes('[kind != "veil"]').map(n => n.renderedBoundingBox({ includeLabels: false, includeOverlays: false, includeUnderlays: false }));
  const free = (x, y) => !boxes.some(b => x >= b.x1 && x <= b.x2 && y >= b.y1 && y <= b.y2);
  for (let fy = 0.5; fy < 0.95; fy += 0.07) for (const fx of [0.5, 0.4, 0.6, 0.3, 0.7, 0.2, 0.8]) {
    const x = r.width * fx, y = r.height * fy;
    if (free(x, y)) return { x: r.x + x, y: r.y + y, w: r.width, h: r.height };
  }
  // after a fit with 40 px padding the left margin is empty
  for (let y = r.height * 0.5; y < r.height - 10; y += 20) if (free(12, y)) return { x: r.x + 12, y: r.y + y, w: r.width, h: r.height };
  const big = cy.nodes().map(n => ({ id: n.id(), b: n.renderedBoundingBox() })).sort((a, b) => b.b.w * b.b.h - a.b.w * a.b.h).slice(0, 3);
  console.warn('[perf] no free point', JSON.stringify({ w: r.width, h: r.height, big }));
  return null;
})()`;
