export const controls = 'button,.chip,.seg button,.tabs button,.dtabs button,[data-cat],[data-flag],[data-depth],[data-layout],[data-view],[data-act],[data-dtab],.tree .row,.cat-h,.item,tr.click,.tv-row,.tv-chip';

// Read-only probes plus harness-owned DOM ids; application functions are never replaced.
export const install = `(() => {
  window.__e2e = { next: 0, messages: [], mutations: 0, layouts: 0, layoutActive: false };
  new MutationObserver(() => __e2e.mutations++).observe(document.body, {subtree:true,childList:true,attributes:true,characterData:true});
  window.chrome?.webview?.addEventListener('message', e => __e2e.messages.push({at:Date.now(), ...e.data}));
  if(typeof cy !== 'undefined') { cy.on('layoutstart',()=>{__e2e.layoutActive=true;__e2e.layouts++}); cy.on('layoutstop',()=>{__e2e.layoutActive=false}); }
  return true;
})()`;

export const state = `(() => {
  const s = typeof S==='undefined'?{}:S, c = typeof cy==='undefined'?null:cy;
  const p = document.querySelector('#progress'), b=document.querySelector('#busy');
  const visible = e => !!e && !e.classList.contains('hidden') && getComputedStyle(e).display!=='none';
  return { loaded:!!s.model, root:s.root, selected:s.selected, focus:s.focus, view:s.view||'map', layout:s.layout,
    expanded:s.expanded?[...s.expanded].sort():[], treeOpen:s.treeOpen?[...s.treeOpen].sort():[], live:s.live,
    cats:s.cats, showTests:s.showTests, showDocs:s.showDocs, dtab:s.dtab, hotSort:s.hotSort,
    planFilter:s.planFilter?[...s.planFilter].sort():[], catClosed:s.catClosed?[...s.catClosed].sort():[],
    seq:typeof layoutSeq==='undefined'?null:layoutSeq, windowSeq:window.layoutSeq??null,
    busy:visible(b), layoutActive:window.__e2e?.layoutActive||false,
    progress:visible(p), progressText:p?.textContent, progressWidth:p?.querySelector('.bar')?.style.width,
    cached:/кэш|cache/i.test(p?.textContent||''), nodes:c?.nodes().length||0, edges:c?.edges().length||0,
    pan:c?.pan(), zoom:c?.zoom(), ctx:visible(document.querySelector('#ctx')),
    search:visible(document.querySelector('#searchResults')), active:document.activeElement?.id,
    tabs:[...document.querySelectorAll('.tabs button.active,.dtabs button.active,.seg button.active')].map(e=>[e.textContent,e.dataset]),
    details:document.querySelector('#details')?.textContent?.slice(0,2500),
    tv:typeof TV==='undefined'?null:{open:[...TV.open],card:[...TV.card],scope:TV.scope,q:TV.q},
    messages:window.__e2e?.messages.length||0, layouts:window.__e2e?.layouts||0,
    toast:document.querySelector('#toast')?.textContent,
    title:document.querySelector('#cy')?.title,
    rects:[...document.querySelectorAll('#ctx .opt')].map(e=>e.textContent) };
})()`;

export const discover = `(() => {
  const visible = el => {
    const r=el.getBoundingClientRect(),s=getComputedStyle(el);
    if(r.width<2||r.height<2||s.visibility==='hidden'||s.display==='none'||el.disabled||el.closest('.hidden'))return false;
    const x=r.x+r.width/2,y=r.y+r.height/2;
    if(x<0||y<0||x>=innerWidth||y>=innerHeight)return false;
    const hit=document.elementFromPoint(x,y);return hit===el||el.contains(hit);
  };
  return [...document.querySelectorAll(${JSON.stringify(controls)})].filter(visible).map(el=>{
    if(!el.dataset.e2eRef)el.dataset.e2eRef=String(++__e2e.next);
    const data=Object.fromEntries(Object.entries(el.dataset).filter(([k])=>!k.startsWith('e2e')));
    const region=el.closest('[id]')?.id||el.parentElement?.className||'body';
    const selector=el.id?'#'+CSS.escape(el.id):Object.keys(data).length?el.tagName.toLowerCase()+Object.entries(data).map(([k,v])=>'[data-'+k.replace(/[A-Z]/g,c=>'-'+c.toLowerCase())+'='+JSON.stringify(v)+']').join(''):'[data-e2e-ref='+JSON.stringify(el.dataset.e2eRef)+']';
    const text=(el.innerText||el.title||'').trim().replace(/\\s+/g,' ').slice(0,160);
    const toggle=el.matches('.chip,.cat-h,.tv-row,[data-cat],[data-flag],[data-act="toggle"],#btnLive')||el.getAttribute('aria-pressed')!==null;
    return {ref:el.dataset.e2eRef, selector, key:region+'|'+selector+'|'+text, text, data, toggle, region};
  });
})()`;

export function point(control) {
  return `(() => {
    let el=document.querySelector('[data-e2e-ref='+${JSON.stringify(JSON.stringify(control.ref))}+']');
    if(!el)el=[...document.querySelectorAll(${JSON.stringify(control.selector)})].find(e=>(e.innerText||e.title||'').trim().replace(/\\s+/g,' ').slice(0,160)===${JSON.stringify(control.text)});
    if(!el||el.closest('.hidden')||el.disabled)return null;
    const r=el.getBoundingClientRect(),x=r.x+r.width/2,y=r.y+r.height/2,h=document.elementFromPoint(x,y);
    return r.width>1&&r.height>1&&(h===el||el.contains(h))?{x,y}:null;
  })()`;
}
