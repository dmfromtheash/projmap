#!/usr/bin/env node
// One-off experiments against a live projmap window: launches it like run.mjs, waits for the background scan,
// then runs an experiment module `export default async (app, { sleep, out }) => { ... }` and prints what it returns.
//
//   node tools/perf/lab.mjs --exe <projmap.exe> --root <repo> --script <experiment.mjs> [--no-scan-wait]
import path from 'node:path';
import os from 'node:os';
import { mkdir } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import { App, sleep, checkPaths, prepareHome } from './app.mjs';

const argv = process.argv.slice(2), opt = {};
for (let i = 0; i < argv.length; i++) {
  if (argv[i] === '--no-scan-wait') { opt.noScanWait = true; continue; }
  if (!/^--(exe|root|script|port|home)$/.test(argv[i]) || argv[i + 1] === undefined) throw new Error(`Unknown/missing option ${argv[i]}`);
  opt[argv[i].slice(2)] = argv[++i];
}
const scratch = process.env.PROJMAP_SCRATCH || path.join(os.tmpdir(), 'projmap-perf');
opt.exe = path.resolve(opt.exe); opt.root = path.resolve(opt.root);
opt.home = path.resolve(opt.home || path.join(scratch, 'perf-home'));
const out = path.join(scratch, 'lab-' + new Date().toISOString().replace(/[:.]/g, '-'));
checkPaths({ ...opt, out });
await mkdir(out, { recursive: true });
await prepareHome(opt.home, opt.root);
const experiment = (await import(pathToFileURL(path.resolve(opt.script)).href)).default;
const app = new App({ exe: opt.exe, root: opt.root, home: opt.home, port: Number(opt.port || 9345) });
try {
  const l = await app.launch();
  console.log(`window ${JSON.stringify(l.window)}, picture after ${l.loadMs} ms`);
  if (!opt.noScanWait) console.log(`scan ${await app.waitScan()} ms`);
  const r = await experiment(app, { sleep, out });
  console.log(JSON.stringify(r, null, 2));
  if (app.errors.length) console.log('errors:', [...new Set(app.errors)].slice(0, 5));
} finally { await app.kill(); }
