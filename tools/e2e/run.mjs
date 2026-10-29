#!/usr/bin/env node
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { mkdir, readFile, writeFile, rename, rm, access, copyFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { CDP } from './cdp.mjs';
import { install, state, discover, point } from './page.mjs';

const dir = path.dirname(fileURLToPath(import.meta.url));
const scratch = process.env.PROJMAP_SCRATCH || path.join(process.env.TEMP || '.', 'projmap-e2e');
// the window under test keeps all its state (cache, recent list, WebView profile) here, never in the owner's store
const home = path.join(scratch, 'e2e-home');
const argv = process.argv.slice(2), opt = {};
for (let i=0;i<argv.length;i++) {
  if(argv[i]==='--help') { console.log('node tools/e2e/run.mjs --exe <projmap.exe> --root <repo> [--port 9333] [--out <dir>] [--runs 2] [--budget-seconds 600]'); process.exit(0); }
  if(argv[i]==='--allow-single-display'){opt['allow-single-display']=true;continue;}
  if(!/^--(exe|root|port|out|runs|budget-seconds)$/.test(argv[i])||!argv[i+1])throw new Error(`Unknown/missing option ${argv[i]}`);
  opt[argv[i].slice(2)]=argv[++i];
}
if(!opt.exe||!opt.root)throw new Error('--exe and --root are required');
opt.exe=path.resolve(opt.exe); opt.root=path.resolve(opt.root);
opt.port=Number(opt.port||9333); opt.runs=Number(opt.runs||2); opt.budget=Number(opt['budget-seconds']||600)*1000;
if(!Number.isInteger(opt.port)||opt.port<1024||opt.port>65535||![1,2].includes(opt.runs)||!Number.isFinite(opt.budget)||opt.budget<60000)throw new Error('Invalid port, runs (1 or 2), or budget (minimum 60 s)');
opt.out=path.resolve(opt.out||path.join(dir,'results',new Date().toISOString().replace(/[:.]/g,'-')));
const relative=path.relative(opt.root,opt.out);
if(relative===''||(!relative.startsWith('..')&&!path.isAbsolute(relative)))throw new Error('Output must be outside the scanned repository');
if(/src[\\/]ProjMap.App[\\/]bin/i.test(opt.exe))throw new Error('Publish a separate executable; src/ProjMap.App/bin is forbidden');
await access(opt.exe); await access(opt.root); await mkdir(opt.out,{recursive:true}); await mkdir(home,{recursive:true});
const sleep=ms=>new Promise(r=>setTimeout(r,ms)), now=()=>performance.now(), exec=promisify(execFile);
const ps=(args,timeout=20000)=>exec('powershell.exe',['-NoLogo','-NoProfile','-ExecutionPolicy','Bypass',...args],{windowsHide:true,timeout,maxBuffer:4*1024*1024});
const exists=async p=>{try{await access(p);return true}catch{return false}};
const report={started:new Date().toISOString(),options:opt,runs:[],notes:[
  'Inputs use CDP mouse/keyboard; graph events use cy.$id(id).emit as requested. No source files or scanned repository files are modified.',
  'Cold means no target graph cache and a fresh WebView test profile. Warm preserves the graph cache and profile produced by cold.',
  'Settle requires a stable lexical layoutSeq (also checks window.layoutSeq), hidden progress/busy and stopped layouts for 500 ms; cached graph is accepted only for the initial load measurement.',
  'Unchanged operations are FAIL only when there is a concrete expected postcondition; inherently idempotent commands may legitimately leave state unchanged.',
  'Host inspection: MainForm.cs:45 (MainForm constructor) falls back to Screen.PrimaryScreen when no secondary display exists. Repro: launch with --test on a one-monitor desktop. This is a static safety finding, not a measured UI failure; the harness blocks that launch.',
]};
let active=null, session=null, stopping=false, deadline=Infinity, recovering=false;
const backups=[];
async function preserve(p) {
  const backup=p+`.e2e-backup-${process.pid}`; const had=await exists(p);
  if(had)await rename(p,backup); backups.push({p,backup,had});
}
async function restore() {
  for(const b of backups.reverse()) {
    let error;
    for(let attempt=0;attempt<10;attempt++)try{await rm(b.p,{force:true,recursive:true});if(b.had)await rename(b.backup,b.p);error=null;break;}catch(e){error=e;await sleep(500);}
    if(error)throw new Error(`Restore failed; original retained at ${b.backup}: ${error.message}`);
  }
}
async function preflight() {
  const p=JSON.parse((await ps(['-File',path.join(dir,'window-probe.ps1'),'-Once'])).stdout.trim());
  report.preflight=p;
  const single=opt['allow-single-display']&&p.screens.length===1;
  if(!single&&!p.screens.some(s=>!s.primary))throw new Error('SAFETY ABORT: no non-primary display; host would fall back to primary (use --allow-single-display only when the laptop panel is the only display)');
  try { const r=await fetch(`http://127.0.0.1:${opt.port}/json`,{signal:AbortSignal.timeout(1000)}); if(r.ok)throw new Error('PORT_IN_USE'); }
  catch(e){if(e.message==='PORT_IN_USE')throw new Error('CDP port already in use');}
}

async function start(run) {
  const actionLog=path.join(opt.out,`${run.cache}-actions.log`);
  const child=spawn(opt.exe,[opt.root,'--test','--cdp-port',String(opt.port),'--action-log',actionLog],{cwd:path.dirname(opt.exe),windowsHide:true,stdio:'ignore',env:{...process.env,PROJMAP_HOME:home}});
  const s={child,actionLog,run,cdp:null,monitor:null,safe:false,closed:false,monitorAt:now(),heartbeat:null,freeze:null,probes:[],lastShell:null};
  session=s; run.launchAt=new Date().toISOString(); run.pid=child.pid;
  (run.launches??=[]).push({pid:child.pid,at:run.launchAt,recovery:recovering});
  child.on('error',e=>{s.launchError=e.message;});
  child.on('exit',(code,signal)=>{s.exited=true;if(!s.closed){const event={type:'crash',code,signal,action:active?.id,at:new Date().toISOString()};run.events.push(event); if(active)active.crash=event;}});
  const mon=spawn('powershell.exe',['-NoLogo','-NoProfile','-ExecutionPolicy','Bypass','-File',path.join(dir,'window-probe.ps1'),'-ProcessId',String(child.pid),...(opt['allow-single-display']?['-AllowSingleDisplay']:[])],{windowsHide:true,stdio:['ignore','pipe','pipe']});
  s.monitor=mon; let buffer='';
  mon.stdout.on('data',chunk=>{
    buffer+=chunk; const lines=buffer.split(/\r?\n/);buffer=lines.pop();
    for(const line of lines)try{
      const p=JSON.parse(line);s.monitorAt=now();s.probe=p;s.probes.push({...p,action:active?.id});
      if(p.rect){if(!p.safe){s.unsafe=p;run.events.push({type:'SAFETY_ABORT',...p,action:active?.id});void kill(s);}else s.safe=true;}
      if(p.responding===false)run.events.push({type:'notResponding',at:p.at,action:active?.id});
    }catch{}
  });
  mon.stderr.on('data',c=>{s.monitorError=(s.monitorError||'')+c;});
  const startAt=now();
  while(!s.safe){if(s.unsafe||s.exited||s.launchError||now()-startAt>20000)throw new Error(`Window safety verification failed: ${s.launchError||s.monitorError||JSON.stringify(s.probe)}`);await sleep(100);}
  run.window=s.probe;
  while(!s.cdp){
    if(s.exited||s.unsafe||now()-startAt>45000)throw new Error('CDP target timeout or unsafe/exited window');
    try{
      const targets=await(await fetch(`http://127.0.0.1:${opt.port}/json`,{signal:AbortSignal.timeout(1500)})).json();
      const target=targets.find(t=>t.type==='page'&&t.url.startsWith('https://projmap.local/'));
      if(target){run.target={id:target.id,url:target.url};s.cdp=await CDP.connect(target.webSocketDebuggerUrl);}
    }catch{} if(!s.cdp)await sleep(200);
  }
  s.cdp.on('Runtime.exceptionThrown',p=>{
    const d=p.exceptionDetails,event={type:'exception',at:new Date().toISOString(),action:active?.id,text:d.exception?.description||d.text,url:d.url,line:d.lineNumber+1,stack:d.stackTrace};run.events.push(event);if(active)active.errors.push(event);
  });
  s.cdp.on('Runtime.consoleAPICalled',p=>{if(p.type==='error'){const event={type:'console.error',at:new Date().toISOString(),action:active?.id,text:p.args.map(a=>a.value??a.description).join(' '),stack:p.stackTrace};run.events.push(event);if(active)active.errors.push(event);}});
  await s.cdp.send('Runtime.enable'); await s.cdp.send('Page.enable');
  s.heartbeat=setInterval(()=>heartbeat(s),250);
  // User scripts are deferred until the viewer globals exist.
  while(!await s.cdp.evaluate(`typeof S!=='undefined'&&typeof cy!=='undefined'`)){await sleep(200);if(now()-startAt>60000)throw new Error('Viewer globals absent');}
  await s.cdp.evaluate(install);
  return s;
}
function heartbeat(s) {
  if(s.closed||!s.cdp)return;
  const sent=now(), action=active;
  // Keep the 250 ms cadence even during a freeze, with bounded pending requests.
  if(s.cdp.pending.size>200)return;
  s.cdp.send('Runtime.evaluate',{expression:'1',returnByValue:true},45000).then(()=>{
    const ms=now()-sent;s.run.heartbeats.push({at:Math.round(sent),ms:Math.round(ms),action:action?.id});
    if(ms>1000){const f={type:'freeze',start:new Date(Date.now()-ms).toISOString(),end:new Date().toISOString(),ms:Math.round(ms),action:action?.id};s.run.events.push(f);if(action)action.maxFreezeMs=Math.max(action.maxFreezeMs,f.ms);}
  }).catch(e=>{if(!s.closed){s.run.events.push({type:'heartbeatTimeout',ms:Math.round(now()-sent),action:action?.id,text:e.message});if(action)action.maxFreezeMs=Math.max(action.maxFreezeMs,Math.round(now()-sent));}});
  const oldest=Math.min(...[...s.cdp.pending.values()].map(p=>p.sentAt));
  if(now()-oldest>30000&&!s.longFreeze){
    s.longFreeze=true;s.run.events.push({type:'freezeOver30s',action:active?.id,at:new Date().toISOString()});
    if(active){active.maxFreezeMs=Math.max(active.maxFreezeMs,Math.round(now()-oldest));active.failures.push('UI freeze exceeded 30 s; waiting for recovery or restarting');}
  }
}
async function kill(s) {
  if(!s||s.closed)return;s.closed=true;clearInterval(s.heartbeat);
  if(s.child.pid&&!s.exited)await exec('taskkill.exe',['/PID',String(s.child.pid),'/T','/F'],{windowsHide:true}).catch(()=>{});
  // A crashed parent may leave its browser tree behind. Check creation time to avoid PID reuse.
  const roots=new Map(s.probes.flatMap(p=>p.children||[]).map(p=>[p.pid,p]));
  for(const p of roots.values()){
    const check=await ps(['-Command',`$p=Get-CimInstance Win32_Process -Filter 'ProcessId = ${Number(p.pid)}'; if($p -and $p.CreationDate.ToUniversalTime().ToString('o') -eq '${p.created}') { 'owned' }`]).catch(()=>({stdout:''}));
    if(check.stdout.trim()==='owned')await exec('taskkill.exe',['/PID',String(p.pid),'/T','/F'],{windowsHide:true}).catch(()=>{});
  }
  s.cdp?.close();s.monitor?.kill();
  (s.run.processProbes??=[]).push(...s.probes);s.run.cleanup={at:new Date().toISOString(),pid:s.child.pid,treeKillRequested:true,trackedBrowserRoots:[...roots.values()]};
  if(await exists(s.actionLog)){s.run.actionLog=(s.run.actionLog||'')+await readFile(s.actionLog,'utf8');await rm(s.actionLog,{force:true});}
  if(session===s)session=null;
}
function guard(){if(!session||session.exited||session.closed)throw new Error('Process exited; restart required');if(session.unsafe)throw new Error('SAFETY ABORT');if(now()-session.monitorAt>6000)throw new Error('Safety/process probe stale');}
async function ev(js,timeout=45000){guard();return session.cdp.evaluate(js,timeout);}
async function readState(){return ev(state);}
async function settle(timeout=45000,{initial=false}={}) {
  if(!recovering)timeout=Math.max(1000,Math.min(timeout,deadline-now()));
  const t=now();let stableAt=t,lastKey=null,last=null;
  while(now()-t<timeout){
    last=await readState();
    const key=JSON.stringify([last.seq,last.windowSeq,last.progress,last.progressWidth,last.progressText,last.busy,last.layoutActive,last.layouts]);
    if(key!==lastKey){lastKey=key;stableAt=now();}
    const idle=initial?last.loaded&&(!last.progress||last.cached):!last.progress&&!last.busy&&!last.layoutActive;
    if(idle&&now()-stableAt>=500)return last;
    await sleep(100);
  }
  throw new Error(`Settle timeout ${timeout} ms; state=${JSON.stringify(last)}`);
}
const meaningful=s=>JSON.stringify(s&&Object.fromEntries(Object.entries(s).filter(([k])=>!['progressText','toast','messages'].includes(k))));
async function action(name,selector,fn,expected=null,config={}) {
  if(!recovering&&now()>deadline)throw new Error('Run time budget exhausted; remaining scenarios are untested');
  const run=session.run,a={id:run.actions.length+1,name,selector,preceding:run.actions.slice(-3).map(x=>({id:x.id,name:x.name,selector:x.selector})),started:new Date().toISOString(),maxFreezeMs:0,errors:[],failures:[]};
  run.actions.push(a);active=a;const t=now();
  try {
    a.before=await readState();const m0=a.before.messages;
    const result=await fn();
    if(result?.skip){a.status='SKIP';a.reason=result.skip;return a;}
    a.after=await settle(config.timeout||45000,config);a.settleMs=Math.round(now()-t);a.changed=meaningful(a.before)!==meaningful(a.after);
    a.shellActions=await ev(`__e2e.messages.slice(${m0}).filter(m=>m.type==='testAction')`);
    if(expected){const failure=await expected(a.before,a.after);if(failure)a.failures.push(failure);}
    if(config.mustChange&&!a.changed&&!a.shellActions.length)a.failures.push('Nothing happened although a state change was expected');
    // the first open waits for a full scan when there is no cache (tens of seconds by design);
    // what matters there is that the window never freezes, which the watchdog measures separately
    // a rescan takes as long as the scan itself (tens of seconds); the window stays usable meanwhile
    if(a.settleMs>5000&&!config.initial&&!/btnRescan/.test(selector))a.failures.push(`Settle > 5 s (${a.settleMs} ms)`);
  } catch(e) {
    a.failures.push(e.message);a.settleMs=Math.round(now()-t);a.harnessError=e.message;
    if(/timeout|exited|disconnected|probe stale/i.test(e.message)&&!/SAFETY/.test(e.message)&&now()<deadline){
      a.recovery={started:new Date().toISOString()};recovering=true;
      try{await kill(session);await sleep(1500);await start(run);await settle(120000,{initial:true});await settle(120000);a.recovery.status='restarted';}
      catch(error){a.recovery.status='failed';a.recovery.error=error.message;}
      finally{recovering=false;}
    }
  }
  finally {
    // Allow the last queued watchdog response to be associated before finalizing.
    await sleep(30);
    if(a.maxFreezeMs>1000)a.failures.push(`UI freeze > 1 s (${a.maxFreezeMs} ms)`);
    if(a.errors.length)a.failures.push(...a.errors.map(e=>`${e.type}: ${e.text}`));
    if(a.crash)a.failures.push(`Process crash (${a.crash.code})`);
    a.status=a.status|| (a.failures.length?'FAIL':'PASS');a.ms=Math.round(now()-t);active=null;
    console.log(`${run.cache} #${a.id} ${a.status} ${a.ms} ms ${name}${a.failures.length?' :: '+a.failures.join('; ').slice(0,240):''}`);
  }
  return a;
}
async function click(c) {
  const shell=await ev(`(() => {
    const d=${JSON.stringify(c.data||{})},n=S.model?.node(S.selected);let type=null,rel=null,line=0;
    if(${JSON.stringify(c.selector)}==='#btnOpen'||${JSON.stringify(c.selector)}==='#btnPick'){type='pickFolder';rel='';}
    else if(d.act==='open'||d.act==='reveal'){type=d.act;rel=n?.path;line=n?.line||0;}
    else if(d.open!==undefined){type='open';rel=d.open;line=+d.line||0;}
    else if(d.path!==undefined){type=d.path?'open':'reveal';rel=d.path;line=+d.line||0;}
    if(!type||rel===null||rel===undefined)return null;
    const last=__e2e.messages.filter(m=>m.type==='testAction').at(-1);
    const full=type==='pickFolder'?'':S.root+'\\\\'+String(rel).replaceAll('/','\\\\');
    const arg=type==='open'?full+':'+line:full;
    return last?.action===type&&last?.arg===arg?{skip:'Skipped repeated logged shell action: '+type+' '+arg}:null;
  })()`);
  if(shell)return shell;
  const p=await ev(point(c));if(!p)return {skip:'Control no longer visible at its centre (fresh discovery required)'};
  await session.cdp.send('Input.dispatchMouseEvent',{type:'mouseMoved',...p});
  await session.cdp.send('Input.dispatchMouseEvent',{type:'mousePressed',button:'left',clickCount:1,...p});
  await session.cdp.send('Input.dispatchMouseEvent',{type:'mouseReleased',button:'left',clickCount:1,...p});
}
async function find(selector){return (await ev(discover)).find(c=>c.selector===selector||c.ref===selector||Object.entries(c.data).some(([k,v])=>selector===`[data-${k}="${v}"]`));}
const covered=new Set();
async function clickSelector(selector,label=selector,expected=null){const c=await find(selector);if(!c){session.run.coverage.push({selector,status:'absent/occluded',phase:label});return null;}covered.add(c.key);return action(label,c.selector,()=>click(c),expected);}
async function key(key,modifiers=0){const code=key.length===1?(key>='0'&&key<='9'?'Digit'+key:'Key'+key.toUpperCase()):key;const vk=key==='Enter'?13:key==='Escape'?27:key==='Backspace'?8:key.toUpperCase().charCodeAt(0);for(const type of ['keyDown','keyUp'])await session.cdp.send('Input.dispatchKeyEvent',{type,key,code,modifiers,windowsVirtualKeyCode:vk,nativeVirtualKeyCode:vk});}
async function screenshot(label){
  guard();if(!session.safe||session.probe?.safe!==true)throw new Error('Screenshot blocked: window safety not confirmed');
  const filename=`${session.run.cache}-${label}.png`;const r=await session.cdp.send('Page.captureScreenshot',{format:'png',captureBeyondViewport:false},15000);await writeFile(path.join(opt.out,filename),Buffer.from(r.data,'base64'));session.run.screenshots.push(filename);
}
async function graphEvent(id,event) {
  return ev(`(() => {const n=cy.$id(${JSON.stringify(id)});if(!n.length)return {skip:'Node no longer on graph'};n.emit({type:${JSON.stringify(event)},position:n.position(),renderedPosition:{x:120,y:100}});return {id:n.id(),kind:n.data('kind')};})()`);
}
async function ensureNode(id){await ev(`reveal(${JSON.stringify(id)})`);await settle();}
async function generic(phase,deadline) {
  let count=0;
  while(now()<deadline){
    guard();const all=await ev(discover);const c=all.find(c=>!covered.has(c.key));
    if(!c)break;covered.add(c.key);count++;
    const a=await action(`${phase}: ${c.text||c.selector}`,c.selector,()=>click(c),null,{mustChange:c.toggle});
    if(c.toggle&&a.status!=='SKIP'&&!a.shellActions?.length){
      // Explore controls revealed by the first toggle before closing it again.
      const children=(await ev(discover)).filter(x=>!covered.has(x.key)&&x.region===c.region&&!x.toggle);
      for(const x of children.slice(0,12)){if(now()>deadline)break;covered.add(x.key);await action(`${phase}: ${x.text||x.selector}`,x.selector,()=>click(x));}
      const fresh=(await ev(discover)).find(x=>x.selector===c.selector);
      if(fresh)await action(`${phase}: toggle again ${c.text}`,fresh.selector,()=>click(fresh),null,{mustChange:true});
    }
    if(a.harnessError&&/timeout|exited|stale/i.test(a.harnessError))break;
  }
  const remaining=(await ev(discover)).filter(c=>!covered.has(c.key));session.run.coverage.push({phase,clicked:count,remainingVisible:remaining});
}
async function scenarios(run,deadline) {
  await action('Initial graph (cached allowed)','S.model; #progress',async()=>{},null,{initial:true,timeout:120000});
  run.loadMs=Math.round(now()-run.startedAt);await screenshot('loaded');
  await action('Wait for background scan to finish','#progress.hidden',async()=>{},null,{timeout:120000});
  run.scanReadyMs=Math.round(now()-run.startedAt);
  // Deliberately exercise the expensive paths before exploratory enumeration.
  for(const depth of [1,2,3,4,1]){
    await clickSelector(`[data-depth="${depth}"]`,`Stress depth ${depth}`);
    for(const layout of ['organic','layered'])await clickSelector(`[data-layout="${layout}"]`,`Stress depth ${depth}, layout ${layout}`,(b,a)=>a.layout===layout?null:`Expected layout ${layout}, got ${a.layout}`);
    if(now()>deadline)break;
  }
  await screenshot('after-depth-stress');
  // Choose up to ten real model nodes, first one per kind; skip synthetic category frames.
  const nodes=await ev(`(() => {const ns=[...S.model.byId.values()].filter(n=>n.kind!=='system');const out=[],seen=new Set();for(const n of ns)if(!seen.has(n.kind)){seen.add(n.kind);out.push({id:n.id,kind:n.kind,name:n.name,hasKids:S.model.hasKids(n.id)})}for(const n of ns)if(out.length<10&&!out.some(x=>x.id===n.id))out.push({id:n.id,kind:n.kind,name:n.name,hasKids:S.model.hasKids(n.id)});return out.slice(0,10)})()`);
  run.graphNodes=nodes;
  for(const n of nodes){
    if(now()>deadline)break;
    await action(`Prepare ${n.kind}: ${n.name}`,`reveal(${JSON.stringify(n.id)})`,()=>ev(`reveal(${JSON.stringify(n.id)})`));
    await action(`Graph tap ${n.kind}: ${n.name}`,`cy.$id(${JSON.stringify(n.id)}).emit('tap')`,()=>graphEvent(n.id,'tap'),(b,a)=>a.selected===n.id?null:'Tap did not select node');
    const first=await action(`Graph expand ${n.kind}: ${n.name}`,`cy.$id(${JSON.stringify(n.id)}).emit('dbltap')`,()=>graphEvent(n.id,'dbltap'),
      // a class (or anything without children) opens the «Файл» view instead of expanding on the map
      async(b,a)=>{if(n.kind==='type'||!n.hasKids){const v=await ev(`S.view`);if(v==='file'){await ev(`(closeFileView(),0)`);await settle();return null;}return n.hasKids?'Double click on a class did not open the file view':null;}
        return b.expanded.includes(n.id)===a.expanded.includes(n.id)?'Expandable node did not toggle':null;});
    if(!first.shellActions?.length)await action(`Graph collapse ${n.kind}: ${n.name}`,`cy.$id(${JSON.stringify(n.id)}).emit('dbltap')`,()=>graphEvent(n.id,'dbltap'));
    else run.coverage.push({node:n.id,event:'second dbltap',status:'skipped duplicate logged shell action'});
    await action(`Context menu ${n.kind}: ${n.name}`,`cy.$id(${JSON.stringify(n.id)}).emit('cxttap')`,()=>graphEvent(n.id,'cxttap'),(b,a)=>a.ctx?null:'Context menu did not open');
    const opts=await ev(`[...document.querySelectorAll('#ctx .opt')].map(e=>({text:e.textContent.trim(),act:e.dataset.a}))`);
    for(const entry of opts){
      await action(`Context ${n.kind} ${n.name}: ${entry.text}`,'#ctx .opt',async()=>{
        await ensureNode(n.id);await graphEvent(n.id,'cxttap');
        const c=await ev(`(() => {const e=[...document.querySelectorAll('#ctx .opt')].find(e=>e.textContent.trim()===${JSON.stringify(entry.text)});if(!e)return null;if(!e.dataset.e2eRef)e.dataset.e2eRef=String(++__e2e.next);return {ref:e.dataset.e2eRef,selector:'#ctx .opt',text:e.textContent.trim()}})()`);
        return c?click(c):{skip:'Context option changed after graph state change'};
      });
      await clickSelector('[data-view="map"]','Return to map after context');
    }
    await key('Escape');
  }
  await action('Edge hover','cy.edges().first().emit("mouseover")',()=>ev(`(() => {const e=cy.edges().first();if(!e.length)return {skip:'No visible edges'};e.emit('mouseover');return e.id()})()`),(b,a)=>a.title?null:'Edge hover did not show tooltip');
  await ev(`(cy.edges().emit('mouseout'), 0)`); // never return a Cytoscape collection over CDP (cyclic)
  // Exercise input using the actual CDP keyboard and text insertion commands.
  await action('Keyboard Ctrl+K','Ctrl+K',()=>key('k',2),(b,a)=>a.active==='search'?null:'Ctrl+K did not focus #search');
  const query=await ev(`[...S.model.byId.values()].find(n=>n.kind==='file')?.name||'Map'`);
  await action(`Search query ${query}`,'#search',()=>session.cdp.send('Input.insertText',{text:query}),(b,a)=>a.search?null:'Search results did not appear');
  await action('Keyboard search Enter','Enter',()=>key('Enter'),(b,a)=>a.selected?null:'Search Enter did not select result');
  for(const k of ['Escape','f','Enter','Backspace','1','2','3','4'])await action(`Keyboard ${k}`,k,()=>key(k));
  await clickSelector('[data-depth="1"]','Restore depth 1');
  // Diagram modes (the tree, plan and database are Cytoscape diagrams; nodes are driven by events).
  const viewIs=v=>(b,a)=>ev(`S.view`).then(x=>x===v?null:`view is ${x}, expected ${v}`);
  for(const v of ['tree','plan','db']){
    await clickSelector(`[data-view="${v}"]`,`View ${v}`);
    const ids=await ev(`cy.nodes().filter(n=>!n.data('frame')).map(n=>n.id()).slice(0,6)`);
    for(const id of ids){
      await action(`${v}: tap ${id.slice(-40)}`,'cy tap',()=>ev(`(cy.getElementById(${JSON.stringify(id)}).emit('tap'),0)`));
      await action(`${v}: dbltap ${id.slice(-40)}`,'cy dbltap',()=>ev(`(cy.getElementById(${JSON.stringify(id)}).nonempty()&&cy.getElementById(${JSON.stringify(id)}).emit('dbltap'),0)`));
      if(await ev(`S.view`)!==v)await clickSelector(`[data-view="${v}"]`,`Back to ${v}`);
    }
    await screenshot(`view-${v}`);
  }
  // «Файл»: functions of a file + its code, for a few big C# files
  const files=await ev(`[...S.model.byId.values()].filter(n=>n.kind==='file'&&n.language==='C#').sort((a,b)=>b._agg.loc-a._agg.loc).slice(0,3).map(n=>({id:n.id,name:n.name}))`);
  for(const f of files){
    await action(`File view ${f.name}`,'openFileView',()=>ev(`(openFileView(${JSON.stringify(f.id)}),0)`),
      async()=>{for(let i=0;i<40;i++){if(await ev(`document.querySelectorAll('#codeBody .cl').length>0&&!elkBusy`))return await ev(`S.view`)==='file'?null:'not in file view';await sleep(250);}return 'code did not load';});
    const fns=await ev(`cy.nodes('[kind="fn"]').map(n=>n.id()).slice(0,4)`);
    for(const id of fns)await action(`File ${f.name}: select function`,'cy tap fn',()=>ev(`(cy.getElementById(${JSON.stringify(id)}).emit('tap'),0)`),
      ()=>ev(`document.querySelectorAll('#codeBody .cl.on').length`).then(n=>n>0?null:'selected function not highlighted in code'));
    await action(`File ${f.name}: double click a code line`,'#codeBody .cl',()=>ev(`(()=>{const l=document.querySelectorAll('#codeBody .cl')[30];l&&l.dispatchEvent(new MouseEvent('dblclick',{bubbles:true}));return 0})()`));
    const ext=await ev(`cy.nodes('[kind="ext"]').map(n=>n.id())[0]||null`);
    if(ext)await action(`File ${f.name}: open an outside file`,'cy dbltap ext',()=>ev(`(cy.getElementById(${JSON.stringify(ext)}).emit('dbltap'),0)`),viewIs('file'));
    await clickSelector('#codeOpen',`File ${f.name}: external editor`);
    await screenshot(`file-${f.name}`);
  }
  // closing returns to the view the file was opened from
  await clickSelector('#codeClose','Close file view',()=>ev(`S.view`).then(x=>x!=='file'?null:'still in file view'));
  await clickSelector('[data-view="map"]','Return map after file view');
  await clickSelector('[data-view="map"]','Return map');
  for(const tab of ['tree','plan','db','hot']){
    await clickSelector(`[data-tab="${tab}"]`,`Side tab ${tab}`);
    await generic(`DOM ${tab}`,Math.min(deadline,now()+45000));
  }
  await generic('DOM remaining',Math.min(deadline,now()+60000));
  // Stress buttons are repeated after exploration because prior actions can change state.
  await clickSelector('#btnRescan','Rescan',null);
  const live=await clickSelector('#btnLive','● Живо off/on',(b,a)=>b.live!==a.live?null:'Live toggle did not change S.live');
  if(live)await clickSelector('#btnLive','● Живо toggle again',(b,a)=>b.live!==a.live?null:'Live toggle did not change S.live');
  await screenshot('final');
}
function severity(a){return a.crash?5:a.errors.length?4:a.maxFreezeMs>30000?3:a.maxFreezeMs>1000?2:1;}
function cell(s){return String(s??'').replace(/\|/g,'\\|').replace(/\r?\n/g,'<br>');}
async function saveReport(){
  for(const r of report.runs)for(const a of r.actions){
    a.maxFreezeMs=Math.max(a.maxFreezeMs,...r.heartbeats.filter(h=>h.action===a.id).map(h=>h.ms>1000?h.ms:0),0);
    if(a.maxFreezeMs>1000&&!a.failures.some(f=>f.startsWith('UI freeze')))a.failures.push(`UI freeze > 1 s (${a.maxFreezeMs} ms)`);
    if(a.failures.length)a.status='FAIL';
  }
  report.finished=new Date().toISOString();
  const fails=report.runs.flatMap(r=>r.actions.filter(a=>a.status==='FAIL').map(a=>({run:r.cache,...a}))).sort((a,b)=>severity(b)-severity(a)||b.maxFreezeMs-a.maxFreezeMs||b.ms-a.ms);
  report.summary={actions:report.runs.reduce((n,r)=>n+r.actions.length,0),failures:fails.length,crashes:report.runs.flatMap(r=>r.events).filter(e=>e.type==='crash').length,complete:report.runs.length===opt.runs&&report.runs.every(r=>r.complete)};
  const status=report.runs.every(r=>r.blocked)?'BLOCKED':report.summary.complete?'COMPLETED':'PARTIAL';
  let md=`# projmap desktop E2E\n\nStarted: ${report.started}; finished: ${report.finished}.\n\nExecutable: \`${opt.exe}\`\n\nScanned root (read only): \`${opt.root}\`\n\n**${status}** — ${report.summary.actions} actions, ${fails.length} measured FAIL actions, ${report.summary.crashes} crashes.\n\n`;
  if(report.preflight)md+='Preflight (active Win32 desktop):\n\n```json\n'+JSON.stringify(report.preflight,null,2)+'\n```\n\n';
  if(report.runs.some(r=>r.blocked))md+='Both requested runs were blocked before launching a window. No cold/warm behaviour or freeze duration has been measured.\n\n';
  md+='| Run | Load ms | Scan ready ms | Actions | FAIL | Window rectangle |\n|---|---:|---:|---:|---:|---|\n';
  for(const r of report.runs)md+=`| ${r.cache} | ${r.loadMs??''} | ${r.scanReadyMs??''} | ${r.actions.length} | ${r.actions.filter(a=>a.status==='FAIL').length} | ${cell(JSON.stringify(r.window?.rect))} |\n`;
  md+='\n## FAIL sorted by severity\n\n| Run / action | Control / selector | Settle ms | Max freeze ms | Failure |\n|---|---|---:|---:|---|\n';
  for(const a of fails)md+=`| ${a.run} #${a.id} | ${cell(a.name)} — ${cell(a.selector)} | ${a.settleMs} | ${a.maxFreezeMs} | ${cell(a.failures.join('; '))} |\n`;
  if(!fails.length)md+=`| — | ${report.summary.actions?'No measured failing actions':'No UI actions executed; behaviour untested'} | | | |\n`;
  for(const r of report.runs){
    md+=`\n## ${r.cache}: full action ledger\n\n${r.fatal?'Run error: '+r.fatal+'\n\n':''}| # | Status | Action / selector | Settle ms | Max freeze ms | Errors / crash |\n|---:|---|---|---:|---:|---|\n`;
    for(const a of r.actions)md+=`| ${a.id} | ${a.status} | ${cell(a.name)} — ${cell(a.selector)} | ${a.settleMs??a.ms} | ${a.maxFreezeMs} | ${cell(a.failures.join('; ')||a.reason||'')} |\n`;
    md+='\n### Exact failure contexts\n\n';
    for(const a of r.actions.filter(a=>a.status==='FAIL'))md+=`- **#${a.id} ${a.name}**. Preceding: ${a.preceding.map(p=>`#${p.id} ${p.name}`).join(' → ')}. Selected before: \`${a.before?.selected}\`; expanded: ${a.before?.expanded.length}; layout: ${a.before?.layout}; view: ${a.before?.view}. ${a.failures.join('; ')}\n`;
    md+='\n### Screenshots\n\n'+r.screenshots.map(f=>`- [${f}](${f})`).join('\n')+'\n';
    md+='\n### Shell action log (test mode)\n\n```text\n'+(r.actionLog||'(none)')+'\n```\n';
    md+='\n### Coverage / limitations\n\n```json\n'+JSON.stringify(r.coverage,null,2)+'\n```\n';
  }
  md+='\n## Method and limits\n\n'+report.notes.map(n=>'- '+n).join('\n')+'\n';
  if(report.fatal)md+='\nFatal harness error: '+report.fatal+'\n';
  await writeFile(path.join(opt.out,'report.json'),JSON.stringify(report,null,2));await writeFile(path.join(opt.out,'report.md'),md);
  await mkdir(scratch,{recursive:true});await copyFile(path.join(opt.out,'report.md'),path.join(scratch,'e2e-report.md'));
}
async function shutdown(){
  if(stopping)return;stopping=true;
  try{await kill(session);await restore();}catch(e){report.cleanupError=e.stack;report.fatal??=e.stack;}
  await saveReport();
}
for(const sig of ['SIGINT','SIGTERM'])process.on(sig,()=>{report.fatal=`Interrupted ${sig}`;void shutdown().then(()=>process.exit(130));});
try {
  report.build={};
  for(const name of ['projmap.exe','projmap.dll']){
    const file=path.join(path.dirname(opt.exe),name);
    if(await exists(file))report.build[name]=createHash('sha256').update(await readFile(file)).digest('hex');
  }
  await preflight();
  const appStore=home;
  const hash=createHash('sha256').update(opt.root.toLowerCase()).digest('hex').slice(0,16).toUpperCase();
  for(const p of [path.join(appStore,'cache',hash+'.json'),path.join(appStore,'webview-test'),path.join(appStore,'recent.json')])await preserve(p);
  // The hashes above identify the published payload, independent of parallel source edits.
  for(let i=0;i<opt.runs;i++){
    covered.clear();const run={cache:i===0?'cold':'warm',startedAt:now(),actions:[],events:[],heartbeats:[],coverage:[],screenshots:[],complete:false};report.runs.push(run);
    try{deadline=now()+opt.budget;await start(run);await scenarios(run,deadline);run.complete=true;}
    catch(e){run.fatal=e.stack;console.error(e.stack);}
    finally{await kill(session);await saveReport();}
    await sleep(1500);
  }
}catch(e){
  report.fatal=e.stack;console.error(e.stack);
  if(report.runs.length===0)for(let i=0;i<opt.runs;i++)report.runs.push({cache:i===0?'cold':'warm',actions:[],events:[],heartbeats:[],coverage:[{status:'BLOCKED before launch',reason:e.message}],screenshots:[],complete:false,fatal:e.message,blocked:true});
}
finally{await shutdown();}
console.log(`REPORT ${path.join(opt.out,'report.md')}`);
process.exitCode=report.summary?.complete?(report.summary.failures?1:0):2;
