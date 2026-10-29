// The downloaded release exe as a user would use it: start without a project (start screen), «Открыть папку проекта…»,
// the chosen folder opens and gets scanned, then the block diagrams. Test mode keeps the window on the laptop display
// (never the primary one; a Win32 probe kills it otherwise) and its own PROJMAP_HOME. Screenshots are CDP viewport shots.
//   node release-check.mjs <projmap.exe> <project root> <out dir>
import path from 'node:path';
import { mkdir, writeFile, rm } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { App, sleep } from '../perf/app.mjs';
import { CDP } from './cdp.mjs';

const [exe, root, out] = process.argv.slice(2);
const home = path.join(out, 'home');
await rm(home, { recursive: true, force: true });
await mkdir(out, { recursive: true });
const app = new App({ exe, root: '', home, port: 9361 });
const log = [];
const step = (s, extra = {}) => { const e = { t: new Date().toISOString().slice(11, 19), step: s, ...extra }; log.push(e); console.log(JSON.stringify(e)); };
try {
  // start like a double click: no folder argument
  app.child = spawn(exe, ['--test', '--cdp-port', String(app.port)], { cwd: path.dirname(exe), windowsHide: true, stdio: 'ignore', env: { ...process.env, PROJMAP_HOME: home } });
  app.watch(false);
  const t0 = Date.now();
  while (!app.cdp) {
    if (app.unsafe) throw new Error('SAFETY ABORT');
    if (Date.now() - t0 > 60000) throw new Error('CDP timeout');
    try {
      const targets = await (await fetch(`http://127.0.0.1:${app.port}/json`, { signal: AbortSignal.timeout(1500) })).json();
      const t = targets.find(t => t.type === 'page' && t.url.startsWith('https://projmap.local/'));
      if (t) app.cdp = await CDP.connect(t.webSocketDebuggerUrl);
    } catch { }
    if (!app.cdp) await sleep(200);
  }
  await app.cdp.send('Runtime.enable');
  app.cdp.on('Runtime.exceptionThrown', p => app.errors.push(p.exceptionDetails.exception?.description || p.exceptionDetails.text));
  const shot = async name => { const r = await app.cdp.send('Page.captureScreenshot', { format: 'png' }); await writeFile(path.join(out, name + '.png'), Buffer.from(r.data, 'base64')); };
  while (!await app.ev(`!!document.querySelector('#start') && !document.querySelector('#start').classList.contains('hidden')`).catch(() => false)) await sleep(200);
  step('start screen', { window: app.lastProbe?.rect, safe: app.lastProbe?.safe });
  await shot('1-start-screen');
  // «Открыть папку проекта…»: in test mode the host only logs the native dialog; the folder it would return is then
  // opened through the same host message the dialog result uses (OpenProject)
  await app.ev(`document.querySelector('#btnPick').click()`);
  await sleep(500);
  const testActions = await app.ev(`[...document.querySelectorAll('#toast')].map(t => t.textContent)`);
  step('pick folder clicked', { hostReply: testActions });
  await app.ev(`window.chrome.webview.postMessage({ type: 'openProject', root: ${JSON.stringify(root)} })`);
  step('folder chosen', { root });
  const t1 = Date.now();
  while (!await app.ev(`!!S.model && document.querySelector('#progress').classList.contains('hidden')`).catch(() => false)) {
    if (Date.now() - t1 > 240000) throw new Error('scan never finished');
    await sleep(500);
  }
  step('scanned and shown', { seconds: Math.round((Date.now() - t1) / 1000), nodes: await app.ev(`S.model.g.nodes.length`), title: await app.ev(`document.querySelector('#projName').textContent`) });
  await sleep(3000);
  await shot('2-schema');
  await app.ev(`setView('links')`); await sleep(800);
  step('links view', await app.ev(`({ tiers: LV.scene.bands?.map(b => b.name), blocks: LV.scene.blocks.length, features: LV.data.v.features.map(f => f.name) })`));
  await shot('3-links');
  for (const m of ['tiles', 'flows', 'time', 'problems']) {
    await app.ev(`LV.stack = []; LV.mode = '${m}'; lvRender()`); await sleep(500);
    await shot('4-' + m);
  }
  await app.ev(`LV.mode = 'links'; const c = [...LV.data.cells.values()].sort((a, b) => b.files.length - a.files.length).find(c => c.tier === 'backend') || [...LV.data.cells.values()][0]; LV.stack = [{ cell: c.key }]; lvRender()`); await sleep(500);
  step('inside a block', await app.ev(`({ title: LV.scene.title, counts: LV.scene.counts })`));
  await shot('5-inside');
  step('done', { errors: [...new Set(app.errors)].slice(0, 5), lastWindow: app.lastProbe?.rect, safe: app.lastProbe?.safe });
} catch (e) { step('FAILED', { error: String(e), errors: app.errors.slice(0, 5) }); }
finally {
  await app.kill();
  await writeFile(path.join(out, 'log.json'), JSON.stringify(log, null, 1));
  await rm(home, { recursive: true, force: true }).catch(() => { });
}
