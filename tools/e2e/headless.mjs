#!/usr/bin/env node
// The viewer in an invisible Chrome (--headless=new, own temporary profile): no window, nothing on the owner's screens.
// Serves src/ProjMap.App/web with a stand-in for the WebView2 host, hands it a graph.json, runs a scenario module
// `export default async (page) => { ... }` and writes its screenshots. Node 22 built-ins only.
//
//   node tools/e2e/headless.mjs --graph <graph.json> --script <scenario.mjs> --out <dir> [--size 2048x1100] [--chrome <path>]
//
// page: eval(expr), wait(expr, ms), shot(name), size(w, h), click(x, y), wheel(x, y, dy), move(x, y), key(k), errors[], sent[]
import http from 'node:http';
import path from 'node:path';
import os from 'node:os';
import { readFile, mkdir, writeFile, rm, mkdtemp } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { CDP } from './cdp.mjs';

const argv = process.argv.slice(2), opt = { size: '2048x1100', chrome: 'C:/Program Files/Google/Chrome/Application/chrome.exe' };
for (let i = 0; i < argv.length; i++) {
  if (!/^--(graph|script|out|size|chrome|root)$/.test(argv[i]) || argv[i + 1] === undefined) throw new Error(`Unknown/missing option ${argv[i]}`);
  opt[argv[i].slice(2)] = argv[++i];
}
for (const k of ['graph', 'script', 'out']) if (!opt[k]) throw new Error(`--${k} is required`);
const web = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../src/ProjMap.App/web');
const out = path.resolve(opt.out);
await mkdir(out, { recursive: true });
const graph = await readFile(path.resolve(opt.graph));
const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.json': 'application/json' };
const stub = `<script>window.chrome={webview:{postMessage:m=>(window.__sent||(window.__sent=[])).push(m),addEventListener:(t,f)=>{window.__host=d=>f({data:d})}}};</script>`;

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://x');
  try {
    if (url.pathname.startsWith('/graph.json')) { res.writeHead(200, { 'content-type': 'application/json' }); res.end(graph); return; }
    const rel = url.pathname === '/' ? 'index.html' : decodeURIComponent(url.pathname.slice(1));
    const file = path.resolve(web, rel);
    if (!file.startsWith(web + path.sep)) { res.writeHead(403); res.end(); return; }
    let body = await readFile(file);
    if (rel === 'index.html') body = Buffer.from(body.toString('utf8').replace('<head>', '<head>' + stub));
    res.writeHead(200, { 'content-type': types[path.extname(file)] || 'application/octet-stream' }); res.end(body);
  } catch { res.writeHead(404); res.end(); }
});
await new Promise(r => server.listen(0, '127.0.0.1', r));
const httpPort = server.address().port;

const profile = await mkdtemp(path.join(os.tmpdir(), 'projmap-headless-'));
const [w, h] = opt.size.split('x').map(Number);
const chrome = spawn(opt.chrome, ['--headless=new', `--user-data-dir=${profile}`, '--remote-debugging-port=0', '--no-first-run',
  '--no-default-browser-check', '--hide-scrollbars', `--window-size=${w},${h}`, 'about:blank'], { stdio: ['ignore', 'ignore', 'pipe'] });
const wsUrl = await new Promise((resolve, reject) => {
  let buf = '';
  chrome.stderr.on('data', d => { buf += d; const m = buf.match(/DevTools listening on (ws:\S+)/); if (m) resolve(m[1]); });
  setTimeout(() => reject(new Error('Chrome did not start')), 20000).unref();
});
const port = new URL(wsUrl).port;
const sleep = ms => new Promise(r => setTimeout(r, ms));
let cdp;
try {
  const list = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  cdp = await CDP.connect(list.find(t => t.type === 'page').webSocketDebuggerUrl);
  const errors = [];
  cdp.on('Runtime.exceptionThrown', p => errors.push(p.exceptionDetails?.exception?.description || p.exceptionDetails?.text));
  cdp.on('Runtime.consoleAPICalled', p => { if (p.type === 'error') errors.push(p.args.map(a => a.value ?? a.description).join(' ')); });
  await cdp.send('Runtime.enable'); await cdp.send('Page.enable');
  const size = async (W, H) => cdp.send('Emulation.setDeviceMetricsOverride', { width: W, height: H, deviceScaleFactor: 1, mobile: false });
  await size(w, h);
  await cdp.send('Page.navigate', { url: `http://127.0.0.1:${httpPort}/` });
  const evalJs = async expr => {
    const r = await cdp.send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true });
    if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text);
    return r.result.value;
  };
  const wait = async (expr, ms = 30000) => {
    const t0 = Date.now();
    for (;;) { if (await evalJs(`!!(${expr})`).catch(() => false)) return Date.now() - t0; if (Date.now() - t0 > ms) throw new Error('wait timeout: ' + expr); await sleep(100); }
  };
  await wait('window.__host');
  await evalJs(`__host({ type: 'graph', version: 1, root: ${JSON.stringify(opt.root || 'C:\\project')}, cached: false })`);
  const loadMs = await wait('S.model');
  const input = (type, x, y, extra = {}) => cdp.send('Input.dispatchMouseEvent', { type, x, y, button: 'left', clickCount: 1, ...extra });
  const page = {
    eval: evalJs, wait, sleep, errors, size, loadMs,
    sent: () => evalJs('window.__sent || []'),
    shot: async name => {
      const r = await cdp.send('Page.captureScreenshot', { format: 'png' });
      const file = path.join(out, name + '.png'); await writeFile(file, Buffer.from(r.data, 'base64')); return file;
    },
    move: (x, y) => input('mouseMoved', x, y, { button: 'none' }),
    click: async (x, y) => { await input('mouseMoved', x, y, { button: 'none' }); await input('mousePressed', x, y); await input('mouseReleased', x, y); },
    wheel: (x, y, deltaY) => cdp.send('Input.dispatchMouseEvent', { type: 'mouseWheel', x, y, deltaX: 0, deltaY }),
    key: k => cdp.send('Input.dispatchKeyEvent', { type: 'keyDown', key: k }).then(() => cdp.send('Input.dispatchKeyEvent', { type: 'keyUp', key: k })),
  };
  const scenario = (await import(pathToFileURL(path.resolve(opt.script)).href)).default;
  const result = await scenario(page);
  console.log(JSON.stringify({ loadMs, result, errors: [...new Set(errors)] }, null, 2));
} finally {
  try { await cdp?.send('Browser.close', {}, 3000); } catch { }
  chrome.kill();
  server.close();
  await sleep(500);
  await rm(profile, { recursive: true, force: true }).catch(() => { });
}
