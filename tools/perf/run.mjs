#!/usr/bin/env node
// projmap performance run: launches a published projmap.exe (see app.mjs), walks the heavy scenarios and
// measures layout time, time to picture, frames while panning/zooming, long tasks, canvas sharpness and JS
// memory. Several launches, medians reported in perf.md / perf.json.
//
//   node tools/perf/run.mjs --exe <projmap.exe> --root <repo> [--runs 3] [--port 9344] [--out <dir>] [--home <dir>]
//                           [--only modules,files,...] [--label before] [--profile files,classes]
import { mkdir, writeFile, access, stat } from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { App, sleep, checkPaths, prepareHome } from './app.mjs';
import { settle, freePoint } from './probe.mjs';

const argv = process.argv.slice(2), opt = {};
for (let i = 0; i < argv.length; i++) {
  if (!/^--(exe|root|runs|port|out|home|only|label|profile)$/.test(argv[i]) || argv[i + 1] === undefined) throw new Error(`Unknown/missing option ${argv[i]}`);
  opt[argv[i].slice(2)] = argv[++i];
}
if (!opt.exe || !opt.root) throw new Error('--exe and --root are required');
opt.exe = path.resolve(opt.exe); opt.root = path.resolve(opt.root);
opt.runs = Number(opt.runs || 3); opt.port = Number(opt.port || 9344);
opt.label = opt.label || 'run';
const scratch = process.env.PROJMAP_SCRATCH || path.join(os.tmpdir(), 'projmap-perf');
opt.out = path.resolve(opt.out || path.join(scratch, `perf-${opt.label}-${new Date().toISOString().replace(/[:.]/g, '-')}`));
opt.home = path.resolve(opt.home || path.join(scratch, 'perf-home'));
checkPaths(opt);
await access(opt.exe); await access(opt.root);
await mkdir(opt.out, { recursive: true });
const cacheFile = await prepareHome(opt.home, opt.root);
const median = xs => { const v = xs.filter(x => Number.isFinite(x)).sort((a, b) => a - b); return v.length ? v[Math.floor((v.length - 1) / 2)] : null; };

let app = null;
for (const sig of ['SIGINT', 'SIGTERM']) process.on(sig, () => { void (app?.kill() ?? Promise.resolve()).then(() => process.exit(130)); });
// allBlocks(): the diagram without the selection veil (newer builds), all nodes in older ones
const ev = (js, t) => app.ev(js.replaceAll('allBlocks()', `(typeof diagramNodes === 'function' ? diagramNodes() : cy.nodes())`), t);

// ---------------------------------------------------------------- measurements

const find = {
  core: `(() => { const M = S.model; return M.top.find(id => M.node(id).kind === 'module' && (M.node(id)._desc?.ru === 'Ядро' || /\\.Core$/i.test(M.node(id).name))) })()`,
  bigFile: name => `(() => [...S.model.byId.values()].filter(n => n.kind === 'file' && n.name === ${JSON.stringify(name)}).sort((a, b) => b._agg.loc - a._agg.loc)[0]?.id)()`,
};

// transition: run `js` and wait for the picture; frames and long tasks are recorded across it
async function transition(js) {
  const r = await ev(`(async () => { __perf.start(); window.__t0 = performance.now(); ${js}; return await ${settle('window.__t0')}; })()`);
  await sleep(200);
  const f = await ev(`__perf.stop()`);
  return { layoutMs: r.layoutMs, paintMs: r.paintMs, nodes: r.nodes, edges: r.edges, timedOut: r.timedOut, blockMs: f.worstMs, longMaxMs: f.longMaxMs, longTasks: f.longTasks };
}

const mouse = (type, x, y, extra = {}) => app.send('Input.dispatchMouseEvent', { type, x, y, ...extra });

// drag the picture (on an empty spot if there is one; blocks are pannable in newer builds)
async function pan() {
  await ev(`(cy.fit(allBlocks(), 40), 0)`); await app.settle();
  let p = await ev(freePoint);
  if (!p) {
    // one big frame fills the screen: step back a little so there is empty space around it
    await ev(`(cy.zoom({ level: cy.zoom() * 0.6, renderedPosition: { x: cy.width() / 2, y: cy.height() / 2 } }), 0)`); await app.settle();
    p = await ev(freePoint);
  }
  if (!p) p = await ev(`(() => { const r = document.querySelector('#cy').getBoundingClientRect(); return { x: r.x + r.width * 0.3, y: r.y + r.height * 0.4, w: r.width, h: r.height, onNode: true }; })()`);
  const before = await ev(`cy.pan()`);
  await ev(`__perf.start()`);
  await mouse('mouseMoved', p.x, p.y);
  await mouse('mousePressed', p.x, p.y, { button: 'left', buttons: 1, clickCount: 1 });
  // a loop that ends where it started, so every scenario pans the same distance
  const R = Math.min(p.w, p.h) * 0.25;
  let mid = null;
  for (let i = 1; i <= 90; i++) {
    const a = i / 90 * Math.PI * 2;
    await mouse('mouseMoved', p.x + (1 - Math.cos(a)) * R, p.y + Math.sin(a) * R * 0.5, { button: 'left', buttons: 1 });
    if (i === 45) mid = await ev(`cy.pan()`);
    else await sleep(16);
  }
  await mouse('mouseReleased', p.x, p.y, { button: 'left', buttons: 0, clickCount: 1 });
  await sleep(700);
  const f = await ev(`__perf.stop()`);
  f.moved = Math.hypot(mid.x - before.x, mid.y - before.y) > 5;
  return f;
}

// wheel in then out over the middle of the canvas
async function zoom() {
  await ev(`(cy.fit(allBlocks(), 40), 0)`); await app.settle();
  const r = await ev(`(() => { const b = document.querySelector('#cy').getBoundingClientRect(); return { x: b.x + b.width / 2, y: b.y + b.height / 2 }; })()`);
  const z0 = await ev(`cy.zoom()`);
  let mid = z0;
  await ev(`__perf.start()`);
  for (let i = 0; i < 48; i++) {
    await app.send('Input.dispatchMouseEvent', { type: 'mouseWheel', x: r.x, y: r.y, deltaX: 0, deltaY: i < 24 ? -120 : 120 });
    if (i === 23) mid = await ev(`cy.zoom()`); else await sleep(33);
  }
  await sleep(700);
  const f = await ev(`__perf.stop()`);
  f.moved = Math.abs(mid - z0) > 1e-6;
  return f;
}

// CPU profile → .cpuprofile file (opens in Edge/Chrome DevTools) + the top functions by own time
async function saveProfile(name) {
  const { profile } = await app.send('Profiler.stop', {}, 60000);
  await writeFile(path.join(opt.out, name + '.cpuprofile'), JSON.stringify(profile));
  const self = new Map(), byId = new Map(profile.nodes.map(n => [n.id, n]));
  profile.samples.forEach((id, k) => {
    const f = byId.get(id).callFrame, key = `${f.functionName || '(anon)'} ${f.url.split('/').pop()}:${f.lineNumber + 1}`;
    self.set(key, (self.get(key) || 0) + (profile.timeDeltas[k] || 0) / 1000);
  });
  const top = [...self].sort((a, b) => b[1] - a[1]).slice(0, 25).map(([k, ms]) => `${ms.toFixed(0).padStart(6)} ms  ${k}`);
  await writeFile(path.join(opt.out, name + '.top.txt'), top.join('\n'));
  console.log(`  profile ${name}:\n    ` + top.slice(0, 12).join('\n    '));
}

// two pictures per scenario: the whole diagram and a close-up at 150 % on the biggest block
async function shots(key) {
  const out = [];
  const snap = async name => {
    await app.settle(); await sleep(300);
    const r = await app.send('Page.captureScreenshot', { format: 'png' }, 20000);
    const file = `${key}-${name}.png`; await writeFile(path.join(opt.out, file), Buffer.from(r.data, 'base64')); out.push(file);
  };
  await ev(`(cy.fit(allBlocks(), 40), 0)`); await snap('fit');
  await ev(`(() => { const n = allBlocks().filter(n => !n.data('frame')).max(n => n.width() * n.height()).ele; if (n) { cy.zoom(1.5); cy.center(n); } return 0; })()`); await snap('zoom150');
  return out;
}

const SCENARIOS = [
  { key: 'modules', name: 'Модули (весь проект)', js: `setView('map'); select(null); setDepth(1)`, move: true },
  { key: 'folders', name: 'Папки (весь проект)', js: `setView('map'); select(null); setDepth(2)`, move: true },
  { key: 'files', name: 'Файлы (весь проект)', js: `setView('map'); select(null); setDepth(3)`, move: true },
  { key: 'classes', name: 'Классы «Ядра»', pre: `setView('map'); select(null); setDepth(1)`, js: `select(${find.core}); setDepth(4)`, move: true },
  { key: 'file', name: 'Файл: SceneRenderer.cs', js: `openFileView(${find.bigFile('SceneRenderer.cs')})`, move: true },
  { key: 'plan', name: 'План', js: `setView('plan')`, move: true },
  { key: 'tree', name: 'Подробное древо', js: `setView('tree')`, move: true },
  { key: 'select', name: 'Выбор блока (уровень «Файлы»)', pre: `setView('map'); select(null); setDepth(3)`,
    js: `select([...S.model.byId.values()].filter(n => n.kind === 'file' && cy.getElementById(n.id).nonempty()).sort((a, b) => ((b._in||0)+(b._out||0)) - ((a._in||0)+(a._out||0)))[0].id)` },
  { key: 'expand', name: 'Раскрыть «Ядро»', pre: `setView('map'); select(null); setDepth(1)`, js: `toggle(${find.core}, true)` },
  { key: 'collapse', name: 'Свернуть «Ядро»', js: `toggle(${find.core}, false)` },
];
const only = opt.only ? new Set(opt.only.split(',')) : null;
const profiled = new Set((opt.profile || '').split(',').filter(Boolean));

// ---------------------------------------------------------------- runs

const runs = [], errors = [], warnings = new Map();
let unsafe = null;
for (let i = 0; i < opt.runs; i++) {
  const run = { i, scenarios: {}, heapPeakMb: 0 };
  runs.push(run);
  app = new App({ exe: opt.exe, root: opt.root, home: opt.home, port: opt.port });
  try {
    Object.assign(run, await app.launch());
    console.log(`run ${i + 1}: window ${JSON.stringify(run.window)}; picture after ${run.loadMs} ms`);
    run.scanMs = await app.waitScan();
    console.log(`run ${i + 1}: background scan ${run.scanMs} ms`);
    for (const s of SCENARIOS) {
      if (only && !only.has(s.key)) continue;
      if (s.pre) { await ev(`(() => { ${s.pre}; return 0 })()`); await app.settle(); }
      const prof = profiled.has(s.key) && i === 0;
      if (prof) { await app.send('Profiler.enable'); await app.send('Profiler.setSamplingInterval', { interval: 200 }); await app.send('Profiler.start'); }
      const r = { ...(await transition(s.js)) };
      if (prof) await saveProfile(`${s.key}-transition`);
      if (s.move) {
        if (prof) await app.send('Profiler.start');
        r.pan = await pan(); r.zoom = await zoom();
        if (prof) await saveProfile(`${s.key}-move`);
        if (i === 0) r.shots = await shots(s.key);
      }
      // sharpness: how many canvas pixels Cytoscape draws per screen pixel (1.0 = full resolution)
      r.quality = await ev(`(() => { const c = [...document.querySelectorAll('#cy canvas')].find(c => c.clientWidth > 0); return { dpr: devicePixelRatio, canvasRatio: c ? +(c.width / c.clientWidth / devicePixelRatio).toFixed(2) : null }; })()`);
      r.heapMb = await app.heapMb(); run.heapPeakMb = Math.max(run.heapPeakMb, r.heapMb);
      run.scenarios[s.key] = r;
      console.log(`  ${s.name}: layout ${r.layoutMs} ms, block ${r.blockMs} ms, ${r.nodes} nodes` + (r.pan ? `; pan ${r.pan.fps} fps worst ${r.pan.worstMs}${r.pan.moved ? '' : ' (did not move)'}; zoom ${r.zoom.fps} fps worst ${r.zoom.worstMs}` : ''));
    }
    // leave the saved view at «Модули» so the next launch starts the same way
    await ev(`(setView('map'), select(null), setDepth(1), 0)`); await app.settle();
  } catch (e) { run.error = e.stack; console.error(e.stack); }
  finally {
    errors.push(...app.errors);
    for (const [k, n] of app.warnings) warnings.set(k, (warnings.get(k) || 0) + n);
    unsafe ??= app.unsafe;
    await app.kill(); await sleep(2000);
  }
  if (unsafe) break;
}

// ---------------------------------------------------------------- report

const graphBytes = await stat(cacheFile).then(s => s.size).catch(() => null);
const agg = {};
for (const s of SCENARIOS) {
  const rs = runs.map(r => r.scenarios[s.key]).filter(Boolean);
  if (!rs.length) continue;
  const m = f => median(rs.map(f));
  agg[s.key] = {
    name: s.name, canvasRatio: m(r => r.quality?.canvasRatio), shots: rs.find(r => r.shots)?.shots,
    nodes: m(r => r.nodes), edges: m(r => r.edges), layoutMs: m(r => r.layoutMs), paintMs: m(r => r.paintMs), blockMs: m(r => r.blockMs), longMaxMs: m(r => r.longMaxMs),
    ...(s.move ? {
      panFps: m(r => r.pan.fps), panWorst: m(r => r.pan.worstMs), panP95: m(r => r.pan.p95Ms), panLong: m(r => r.pan.longMaxMs), panMoved: rs.every(r => r.pan.moved),
      zoomFps: m(r => r.zoom.fps), zoomWorst: m(r => r.zoom.worstMs), zoomP95: m(r => r.zoom.p95Ms), zoomLong: m(r => r.zoom.longMaxMs),
    } : {}),
  };
}
const summary = {
  label: opt.label, exe: opt.exe, root: opt.root, runs: runs.length, okRuns: runs.filter(r => !r.error).length,
  loadMs: median(runs.map(r => r.loadMs)), scanMs: median(runs.map(r => r.scanMs)), heapPeakMb: median(runs.map(r => r.heapPeakMb)),
  graphMb: graphBytes ? +(graphBytes / 1048576).toFixed(1) : null, scenarios: agg, errors: [...new Set(errors)].slice(0, 20),
  consoleWarnings: [...warnings].sort((a, b) => b[1] - a[1]).slice(0, 10).map(([text, n]) => ({ n, text })),
  consoleWarningsTotal: [...warnings.values()].reduce((a, b) => a + b, 0),
  windows: runs.map(r => r.window), safety: unsafe ? 'ABORT' : 'ok',
};
const ok = (v, lim, less = true) => v == null ? '—' : (less ? v < lim : v >= lim) ? `${v}` : `**${v}**`;
let md = `# projmap perf — ${opt.label}\n\n${new Date().toISOString()} · ${summary.okRuns}/${summary.runs} запусков · медианы · \`${opt.root}\`\n\n`;
md += `Загрузка окна до картинки: ${summary.loadMs} мс · фоновый скан: ${summary.scanMs} мс · граф: ${summary.graphMb} МБ · пик JS-памяти: ${summary.heapPeakMb} МБ\n\n`;
md += `Жирным — не уложилось в цель (FPS ≥ 58, худший кадр < 50 мс, блокировка < 200 мс, чёткость 1.0). «не сдвинулась» — перетаскивание не двигало схему.\n\n`;
md += '| Сценарий | Блоков | Связей | Раскладка, мс | До картинки, мс | Блокировка, мс | Пан FPS | Пан худший, мс | Зум FPS | Зум худший, мс | Длинная задача при движении, мс | Чёткость холста |\n|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|\n';
for (const a of Object.values(agg)) {
  const panFps = a.panFps == null ? '' : a.panMoved ? ok(a.panFps, 58, false) : 'не сдвинулась';
  md += `| ${a.name} | ${a.nodes} | ${a.edges} | ${a.layoutMs} | ${a.paintMs} | ${ok(a.blockMs, 200)} | ${panFps} | ${a.panWorst == null || !a.panMoved ? '' : ok(a.panWorst, 50)} | ${a.zoomFps == null ? '' : ok(a.zoomFps, 58, false)} | ${a.zoomWorst == null ? '' : ok(a.zoomWorst, 50)} | ${a.panLong == null ? '' : Math.max(a.panLong, a.zoomLong)} | ${a.canvasRatio == null ? '' : ok(a.canvasRatio, 1, false)} |\n`;
}
md += `\nПредупреждений в консоли окна: ${summary.consoleWarningsTotal}${summary.consoleWarnings.length ? '\n\n' + summary.consoleWarnings.map(w => `- ×${w.n} ${w.text}`).join('\n') : ''}\n`;
md += `\nСнимки (первый запуск): ${Object.values(agg).flatMap(a => a.shots || []).join(', ')}\n`;
if (summary.errors.length) md += `\nОшибки в окне:\n\n${summary.errors.map(e => '- ' + e.split('\n')[0]).join('\n')}\n`;
for (const r of runs) if (r.error) md += `\nЗапуск ${r.i + 1} прерван: ${r.error.split('\n')[0]}\n`;
await writeFile(path.join(opt.out, 'perf.json'), JSON.stringify({ summary, runs }, null, 2));
await writeFile(path.join(opt.out, 'perf.md'), md);
console.log(md);
console.log('REPORT ' + path.join(opt.out, 'perf.md'));
process.exitCode = summary.okRuns === summary.runs && !unsafe ? 0 : 2;
