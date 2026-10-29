// Meaningful protocol tests without opening any browser or desktop window.
import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { CDP } from './cdp.mjs';
import { point, state, discover } from './page.mjs';

class Socket extends EventTarget {
  sent=[];
  send(raw){this.sent.push(JSON.parse(raw));}
  reply(value){this.dispatchEvent(new MessageEvent('message',{data:JSON.stringify(value)}));}
  close(){this.dispatchEvent(new Event('close'));}
}
test('out-of-order CDP replies preserve request identity and events',async()=>{
  const ws=new Socket(),cdp=new CDP(ws),events=[];cdp.on('Runtime.exceptionThrown',p=>events.push(p));
  const first=cdp.send('A'),second=cdp.send('B');
  ws.reply({id:2,result:{value:'second'}});ws.reply({method:'Runtime.exceptionThrown',params:{exceptionDetails:{text:'boom'}}});ws.reply({id:1,result:{value:'first'}});
  assert.deepEqual(await first,{value:'first'});assert.deepEqual(await second,{value:'second'});assert.equal(events[0].exceptionDetails.text,'boom');assert.equal(cdp.pending.size,0);cdp.close();
});
test('request timeout rejects, releases state, and ignores late replies',async()=>{
  const ws=new Socket(),cdp=new CDP(ws);await assert.rejects(cdp.send('Slow',{},15),/Slow timeout 15 ms/);assert.equal(cdp.pending.size,0);ws.reply({id:1,result:{}});cdp.close();
});
test('disconnect rejects outstanding commands',async()=>{
  const ws=new Socket(),cdp=new CDP(ws),p=cdp.send('Waiting');ws.close();await assert.rejects(p,/disconnected/);assert.equal(cdp.pending.size,0);
});
test('evaluation exceptions surface exact description',async()=>{
  const ws=new Socket(),cdp=new CDP(ws),p=cdp.evaluate('bad()');ws.reply({id:1,result:{exceptionDetails:{text:'Uncaught',exception:{description:'TypeError: exact failure'}}}});await assert.rejects(p,/TypeError: exact failure/);cdp.close();
});
test('page probe compiles with lexical S/cy/layoutSeq (not window properties)',()=>{
  const document={querySelector:()=>null,querySelectorAll:()=>[],activeElement:null};
  const context=vm.createContext({document,window:{},getComputedStyle:()=>({display:'none'})});
  vm.runInContext('const S={model:{},expanded:new Set(),treeOpen:new Set(),catClosed:new Set(),planFilter:new Set()}; let layoutSeq=7;',context);
  const r=vm.runInContext(state,context);assert.equal(r.loaded,true);assert.equal(r.seq,7);assert.equal(r.windowSeq,null);new vm.Script(discover);
});
test('control centre uses current DOM and refuses occluded input',()=>{
  const el={disabled:false,closest:()=>null,getBoundingClientRect:()=>({x:10,y:20,width:80,height:40}),contains:()=>false};
  const doc={querySelector:selector=>{assert.equal(selector,'[data-e2e-ref="3"]');return el;},elementFromPoint:()=>el};
  const context=vm.createContext({document:doc});const js=point({ref:'3',selector:'#button',text:'button'});
  assert.equal(JSON.stringify(vm.runInContext(js,context)),JSON.stringify({x:50,y:40}));doc.elementFromPoint=()=>({});assert.equal(vm.runInContext(js,context),null);
});
