// Launching and driving a published projmap.exe for measurements: --test mode, own CDP port, own PROJMAP_HOME
// (never the owner's state), the window rectangle watched every second (never on the primary display when a
// second one exists). Shared by run.mjs (the scenario table) and lab.mjs (one-off experiments).
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { mkdir, access, copyFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { CDP } from '../e2e/cdp.mjs';
import { install, settle } from './probe.mjs';

const dir = path.dirname(fileURLToPath(import.meta.url));
const probeScript = path.join(dir, '..', 'e2e', 'window-probe.ps1');
export const sleep = ms => new Promise(r => setTimeout(r, ms));
const exec = promisify(execFile);
const now = () => performance.now();

export function checkPaths({ exe, root, out, home }) {
  if (/src[\\/]ProjMap.App[\\/]bin/i.test(exe)) throw new Error('Publish a separate executable; src/ProjMap.App/bin is forbidden');
  for (const p of [out, home].filter(Boolean)) {
    const rel = path.relative(root, p);
    if (rel === '' || (!rel.startsWith('..') && !path.isAbsolute(rel))) throw new Error('Output and home must be outside the scanned repository');
  }
}

// the owner's cached graph of this repo is copied into the private home once (warm start)
export async function prepareHome(home, root) {
  await mkdir(path.join(home, 'cache'), { recursive: true });
  const hash = createHash('sha256').update(root.toLowerCase()).digest('hex').slice(0, 16).toUpperCase();
  const cacheFile = path.join(home, 'cache', hash + '.json');
  try { await access(cacheFile); } catch {
    try { await copyFile(path.join(process.env.LOCALAPPDATA, 'projmap', 'cache', hash + '.json'), cacheFile); } catch { }
  }
  return cacheFile;
}

export class App {
  constructor({ exe, root, home, port = 9344 }) {
    Object.assign(this, { exe, root, home, port });
    this.child = null; this.cdp = null; this.monitor = null; this.unsafe = null; this.lastProbe = null;
    this.errors = []; this.warnings = new Map();
  }

  async launch() {
    const pre = JSON.parse((await exec('powershell.exe', ['-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', probeScript, '-Once'], { windowsHide: true, timeout: 20000 })).stdout.trim());
    const single = pre.screens.length === 1;
    if (!single && !pre.screens.some(s => !s.primary)) throw new Error('No non-primary display');
    const t0 = now();
    this.child = spawn(this.exe, [this.root, '--test', '--cdp-port', String(this.port)], {
      cwd: path.dirname(this.exe), windowsHide: true, stdio: 'ignore', env: { ...process.env, PROJMAP_HOME: this.home },
    });
    this.watch(single);
    while (!this.cdp) {
      if (this.unsafe) throw new Error('SAFETY ABORT');
      if (now() - t0 > 60000) throw new Error('CDP target timeout');
      try {
        const targets = await (await fetch(`http://127.0.0.1:${this.port}/json`, { signal: AbortSignal.timeout(1500) })).json();
        const t = targets.find(t => t.type === 'page' && t.url.startsWith('https://projmap.local/'));
        if (t) this.cdp = await CDP.connect(t.webSocketDebuggerUrl);
      } catch { }
      if (!this.cdp) await sleep(100);
    }
    const cdp = this.cdp;
    await cdp.send('Runtime.enable'); await cdp.send('Performance.enable');
    cdp.on('Runtime.exceptionThrown', p => this.errors.push(p.exceptionDetails.exception?.description || p.exceptionDetails.text));
    cdp.on('Runtime.consoleAPICalled', p => {
      if (!['warning', 'error', 'log'].includes(p.type)) return;
      const text = p.args.map(a => a.value ?? a.description).join(' ');
      if (text.startsWith('[perf]')) console.log('  [page]', text.slice(0, 2000));
      else if (p.type !== 'log') this.warnings.set(text.slice(0, 160), (this.warnings.get(text.slice(0, 160)) || 0) + 1);
    });
    while (!await cdp.evaluate(`typeof S!=='undefined'&&typeof cy!=='undefined'`).catch(() => false)) await sleep(50);
    await this.ev(install);
    while (!await this.ev(`!!S.model`)) { if (now() - t0 > 120000) throw new Error('graph never loaded'); await sleep(50); }
    const first = await this.ev(settle('performance.now()'));
    return { loadMs: Math.round(now() - t0), window: this.lastProbe?.rect, single, first };
  }

  watch(single) {
    this.monitor = spawn('powershell.exe', ['-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', probeScript,
      '-ProcessId', String(this.child.pid), ...(single ? ['-AllowSingleDisplay'] : [])], { windowsHide: true, stdio: ['ignore', 'pipe', 'ignore'] });
    let buf = '';
    this.monitor.stdout.on('data', c => {
      buf += c; const lines = buf.split(/\r?\n/); buf = lines.pop();
      for (const l of lines) try { const p = JSON.parse(l); this.lastProbe = p; if (p.rect && !p.safe) { this.unsafe = p; void this.kill(); } } catch { }
    });
  }

  async ev(js, timeout = 120000) {
    if (this.unsafe) throw new Error('SAFETY ABORT: window on the primary display ' + JSON.stringify(this.unsafe.rect));
    return this.cdp.evaluate(js, timeout);
  }
  send(method, params, timeout) { return this.cdp.send(method, params, timeout); }
  settle(t0Expr) { return this.ev(settle(t0Expr)); }
  async heapMb() { const m = await this.cdp.send('Performance.getMetrics'); return Math.round((m.metrics.find(x => x.name === 'JSHeapUsedSize')?.value || 0) / 1048576); }

  // the background rescan (cached start) delivers its result; then the viewer settles
  async waitScan() {
    const t = now();
    while (now() - t < 240000) {
      const s = await this.ev(`({ hidden: document.querySelector('#progress').classList.contains('hidden'), g: __perf.hostGraph })`);
      const done = s.g.find(x => !x.cached);
      if (done && s.hidden) { await this.settle(); return done.ms || null; }
      await sleep(250);
    }
    throw new Error('background scan did not finish in 240 s');
  }

  async kill() {
    try { this.cdp?.close(); } catch { }
    this.cdp = null;
    if (this.child?.pid && this.child.exitCode === null) await exec('taskkill.exe', ['/PID', String(this.child.pid), '/T', '/F'], { windowsHide: true }).catch(() => { });
    this.monitor?.kill(); this.monitor = null; this.child = null;
  }
}
