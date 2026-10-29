// Arrow layer: the routed arrows of the diagram drawn on one canvas above Cytoscape's, grouped by colour.
// Cytoscape spends ~25 µs per edge per full redraw; with 1600 routed arrows that was 75 % of the 60–110 ms
// frame at the start and end of every pan/zoom gesture. Here the whole set is a few strokes (≈3 ms), redrawn
// as vectors on every view change, so arrows stay sharp while zooming too.
// The Cytoscape edges stay in the graph (connectedEdges, selection) with display:none; an arrow that gets
// highlighted (.hl / .hover) is drawn by Cytoscape again, with its label, and skipped here.
'use strict';

const EdgeLayer = (() => {
  const canvas = document.createElement('canvas');
  canvas.id = 'edgeLayer';
  canvas.style.cssText = 'position:absolute;inset:0;pointer-events:none;z-index:1';
  let routes = [], byId = new Map(), keep = null, queued = false, hover = null;

  function attach() { if (!canvas.parentNode) $('#cy').appendChild(canvas); }

  // routes: [{ id, pts: [{x,y}…] in model coordinates, color, width, op, dash: [] | null, arrow: 'triangle'|'square'|'none' }]
  function set(list) { routes = list; byId = new Map(list.map(r => [r.id, r])); keep = null; hover = null; redraw(); }
  function clear() { set([]); }
  // selection: arrows outside `ids` are drawn faded (as if under the selection veil); null = nothing selected
  function setKeep(ids) { keep = ids; redraw(); }
  function has(id) { return byId.has(id); }

  function redraw() {
    if (queued) return;
    queued = true;
    requestAnimationFrame(() => { queued = false; draw(); });
  }

  function draw() {
    attach();
    const w = cy.width(), h = cy.height(), pr = window.devicePixelRatio || 1;
    if (canvas.width !== Math.round(w * pr) || canvas.height !== Math.round(h * pr)) {
      canvas.width = Math.round(w * pr); canvas.height = Math.round(h * pr);
      canvas.style.width = w + 'px'; canvas.style.height = h + 'px';
    }
    const ctx = canvas.getContext('2d');
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    if (!routes.length) return { groups: 0, drawn: 0 };
    const z = cy.zoom(), pan = cy.pan();
    ctx.setTransform(pr * z, 0, 0, pr * z, pr * pan.x, pr * pan.y);
    // what is on screen, in model coordinates (with a margin for arrowheads)
    const m = 20 / z, vx1 = -pan.x / z - m, vy1 = -pan.y / z - m, vx2 = (w - pan.x) / z + m, vy2 = (h - pan.y) / z + m;
    // one stroke per (colour, width, alpha, dash): alpha in steps of 0.05, otherwise every arrow is its own group
    // (each arrow has its own opacity) and the GPU gets thousands of separate draws
    const groups = new Map();
    let drawn = 0;
    for (const r of routes) {
      const e = cy.getElementById(r.id);
      if (e.empty() || e.hasClass('hl') || e.hasClass('hover')) continue; // Cytoscape draws those itself
      if (r.x2 < vx1 || r.x1 > vx2 || r.y2 < vy1 || r.y1 > vy2) continue;
      const alpha = Math.round((keep && !keep.has(r.id) ? r.op * 0.22 : r.op) * 20) / 20;
      // a dash shorter than ~2 screen pixels is invisible anyway and costs the GPU millions of tiny segments
      const dash = r.dash && r.dash[0] * z >= 2 ? r.dash : null;
      const key = `${r.color}|${r.width}|${alpha}|${dash ? dash.join(',') : ''}`;
      let g = groups.get(key);
      if (!g) groups.set(key, g = { color: r.color, width: r.width, alpha, dash, list: [] });
      g.list.push(r); drawn++;
    }
    ctx.lineJoin = 'round'; ctx.lineCap = 'butt';
    // far out, lines thinner than a pixel and heads of a pixel or two are slow to rasterize and invisible
    const minW = 1 / z, heads = r => r.arrow !== 'none' && arrowLen(r) * z >= 3;
    for (const g of groups.values()) {
      ctx.globalAlpha = g.alpha; ctx.strokeStyle = g.color; ctx.fillStyle = g.color; ctx.lineWidth = Math.max(g.width, minW);
      ctx.setLineDash(g.dash || []);
      ctx.beginPath();
      for (const r of g.list) {
        const p = r.pts, end = shorten(p, heads(r) ? arrowLen(r) : 0);
        ctx.moveTo(p[0].x, p[0].y);
        for (let i = 1; i < p.length - 1; i++) ctx.lineTo(p[i].x, p[i].y);
        ctx.lineTo(end.x, end.y);
      }
      ctx.stroke();
      ctx.setLineDash([]);
      ctx.beginPath();
      for (const r of g.list) if (heads(r)) head(ctx, r);
      ctx.fill();
    }
    ctx.globalAlpha = 1;
    return { groups: groups.size, drawn };
  }

  const arrowLen = r => r.width * 3.6;
  // the line stops where the arrowhead begins, so the head stays sharp
  function shorten(p, len) {
    const a = p[p.length - 2], b = p[p.length - 1], dx = b.x - a.x, dy = b.y - a.y, d = Math.hypot(dx, dy) || 1;
    return len ? { x: b.x - dx / d * Math.min(len, d), y: b.y - dy / d * Math.min(len, d) } : b;
  }
  function head(ctx, r) {
    const p = r.pts, a = p[p.length - 2], b = p[p.length - 1];
    const dx = b.x - a.x, dy = b.y - a.y, d = Math.hypot(dx, dy) || 1, ux = dx / d, uy = dy / d;
    const L = arrowLen(r), W = r.width * (r.arrow === 'square' ? 1.4 : 1.9);
    if (r.arrow === 'square') {
      const cx = b.x - ux * L / 2, cy0 = b.y - uy * L / 2;
      ctx.moveTo(cx - ux * L / 2 - uy * W, cy0 - uy * L / 2 + ux * W);
      ctx.lineTo(cx + ux * L / 2 - uy * W, cy0 + uy * L / 2 + ux * W);
      ctx.lineTo(cx + ux * L / 2 + uy * W, cy0 + uy * L / 2 - ux * W);
      ctx.lineTo(cx - ux * L / 2 + uy * W, cy0 - uy * L / 2 - ux * W);
      ctx.closePath();
      return;
    }
    ctx.moveTo(b.x, b.y);
    ctx.lineTo(b.x - ux * L - uy * W, b.y - uy * L + ux * W);
    ctx.lineTo(b.x - ux * L + uy * W, b.y - uy * L - ux * W);
    ctx.closePath();
  }

  // the arrow under the pointer (model coordinates), within a few screen pixels
  function hit(pos) {
    const tol = 6 / cy.zoom();
    let best = null, bestD = tol;
    for (const r of routes) {
      if (pos.x < r.x1 - tol || pos.x > r.x2 + tol || pos.y < r.y1 - tol || pos.y > r.y2 + tol) continue;
      const e = cy.getElementById(r.id);
      if (e.empty() || (keep && !keep.has(r.id))) continue;
      for (let i = 1; i < r.pts.length; i++) {
        const d = segDist(pos, r.pts[i - 1], r.pts[i]);
        if (d < bestD) { bestD = d; best = r.id; }
      }
    }
    return best;
  }
  function segDist(p, a, b) {
    const dx = b.x - a.x, dy = b.y - a.y, l2 = dx * dx + dy * dy;
    const t = l2 ? Math.max(0, Math.min(1, ((p.x - a.x) * dx + (p.y - a.y) * dy) / l2)) : 0;
    return Math.hypot(p.x - (a.x + t * dx), p.y - (a.y + t * dy));
  }

  // pointer over a layer arrow behaves like pointer over a Cytoscape edge (tooltip, highlight, click)
  cy.on('mousemove', e => {
    if (e.target !== cy && !e.target.data?.('frame')) { if (hover) leave(); return; }
    const id = routes.length ? hit(e.position) : null;
    if (id === hover) return;
    if (hover) leave();
    if (id) { hover = id; cy.getElementById(id).emit('mouseover'); $('#cy').style.cursor = 'pointer'; }
  });
  function leave() { const e = cy.getElementById(hover); hover = null; $('#cy').style.cursor = ''; if (e.nonempty()) e.emit('mouseout'); redraw(); }
  cy.on('mouseout', () => { if (hover) leave(); });
  cy.on('viewport resize', redraw);
  cy.on('class', 'edge', e => { if (byId.has(e.target.id())) redraw(); });

  return { set, clear, setKeep, has, redraw, drawNow: draw, hit: pos => hit(pos), hovered: () => hover };
})();
