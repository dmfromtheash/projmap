// Indexed, read-only view over graph.json (contract: src/ProjMap.Core/GraphModel.cs).
'use strict';

const EDGE_CAT = {
  projectRef: 'code', reference: 'code', call: 'code', dispatch: 'code', inherit: 'code', implement: 'code', import: 'code',
  cochange: 'cochange', dbRead: 'db', dbWrite: 'db', mentions: 'plan', package: 'package',
};
const EDGE_RU = {
  projectRef: 'зависит', reference: 'использует', call: 'использует', inherit: 'построен на',
  implement: 'построен на', dispatch: 'возможная реализация', import: 'подключает', cochange: 'меняются вместе', dbRead: 'читает из базы',
  dbWrite: 'записывает в базу', mentions: 'упоминается', package: 'внешняя библиотека',
};
const KIND_RU = {
  system: 'проект', module: 'модуль (крупная часть программы)', folder: 'папка', file: 'файл',
  type: 'класс (блок кода)', member: 'функция или свойство', table: 'таблица базы данных',
  doc: 'документ', group: 'раздел', roadmap: 'задача плана',
};
const STATUS_RU = { done: 'готово', active: 'в работе', planned: 'в плане', deferred: 'отложено', blocked: 'ждёт / блок', unknown: 'без статуса' };
const KIND_ORDER = { group: 0, module: 1, folder: 2, file: 3, type: 4, member: 5, table: 6, doc: 7 };

class GraphModel {
  constructor(g) {
    // Preserve scanner containment before the legacy viewer groups loose root files.
    this.canonicalIds = new Set(g.nodes.map(n => n.id));
    this.realParent = new Map(g.nodes.map(n => [n.id, n.parentId || null]));
    // compact form from the host (GraphJson.SerializeCompact): edge ends are node indexes
    if (g.compact) {
      const id = x => typeof x === 'number' ? g.nodes[x].id : x;
      g.edges = g.edges.map(a => ({
        kind: g.edgeKinds[a[0]], source: id(a[1]), target: id(a[2]), weight: a[3],
        evidence: a[4] ? a[4].map(v => ({ path: v[0], line: v[1], snippet: v[2] })) : [],
      }));
    }
    this.g = g;
    this.byId = new Map();
    this.children = new Map();
    this.byPath = new Map();
    // Loose files at the repo root (.editorconfig, global.json…) go into one folder so the
    // top level shows only modules, folders and groups.
    const sysNode = g.nodes.filter(n => n.kind === 'system');
    if (sysNode.length === 1) {
      const loose = g.nodes.filter(n => n.parentId === sysNode[0].id && n.kind === 'file');
      if (loose.length > 1) {
        const vid = 'virtual:root-files';
        g.nodes.push({ id: vid, kind: 'folder', name: 'Файлы в корне', parentId: sysNode[0].id, metrics: {}, tags: [] });
        for (const n of loose) n.parentId = vid;
      }
    }
    for (const n of g.nodes) {
      n.metrics = n.metrics || {};
      n.tags = n.tags || [];
      this.byId.set(n.id, n);
      if (n.path && n.kind === 'file') this.byPath.set(this.pathKey(n.path), n.id);
    }
    const roots = [];
    for (const n of g.nodes) {
      const p = n.parentId && this.byId.has(n.parentId) ? n.parentId : null;
      n._parent = p;
      if (p) { if (!this.children.has(p)) this.children.set(p, []); this.children.get(p).push(n.id); }
      else roots.push(n.id);
    }
    const cmp = (a, b) => {
      const x = this.byId.get(a), y = this.byId.get(b);
      return (KIND_ORDER[x.kind] ?? 9) - (KIND_ORDER[y.kind] ?? 9) || x.name.localeCompare(y.name);
    };
    for (const list of this.children.values()) list.sort(cmp);
    roots.sort(cmp);

    // A single "system" root is the project itself: its children are the top level.
    const sys = roots.filter(id => this.byId.get(id).kind === 'system');
    this.systemId = sys.length === 1 ? sys[0] : null;
    this.top = this.systemId ? [...(this.children.get(this.systemId) || []), ...roots.filter(r => r !== this.systemId)] : roots;
    // a group with nothing inside (e.g. "Roadmap": its items live in the side panel) is not drawn
    this.top = this.top.filter(id => this.byId.get(id).kind !== 'group' || this.children.has(id));

    // depth + module + aggregated metrics
    this.depth = new Map();
    this.moduleOf = new Map();
    const walk = (id, d, mod) => {
      const n = this.byId.get(id);
      this.depth.set(id, d);
      const m = n.kind === 'module' || (d === 0 && n.kind !== 'system') ? id : mod;
      this.moduleOf.set(id, m);
      const agg = { loc: 0, files: 0, types: 0, members: 0, complexity: 0, commits: 0 };
      let last = n.metrics?.lastChange || null, authors = n.metrics?.authors || 0;
      for (const c of this.children.get(id) || []) {
        const a = walk(c, d + 1, m);
        for (const k in agg) agg[k] = k === 'complexity' ? Math.max(agg[k], a[k]) : agg[k] + a[k];
        if (a.last && (!last || a.last > last)) last = a.last;
        authors = Math.max(authors, a.authors || 0);
      }
      const own = n.metrics;
      if (n.kind === 'file') { agg.files = 1; agg.loc = own.loc || agg.loc; agg.commits = own.commits || 0; }
      if (n.kind === 'type') agg.types += 1;
      if (n.kind === 'member') agg.members += 1;
      if (n.kind === 'member' || n.kind === 'type') agg.complexity = Math.max(agg.complexity, own.complexity || 0);
      if (n.kind === 'file' && own.complexity) agg.complexity = Math.max(agg.complexity, own.complexity);
      // only a function's own number is a real "complexity"; classes/files store sums, so they use
      // the max of their functions (or their own value when members were not scanned)
      const leaf = n.kind === 'member' || !(this.children.get(id) || []).length;
      n._agg = {
        loc: own.loc || agg.loc, files: own.files || agg.files, types: own.types || agg.types,
        members: own.members || agg.members,
        // "complexity" always means the most tangled single function inside, never a sum
        complexity: leaf ? Math.max(own.complexity || 0, agg.complexity) : agg.complexity,
        commits: n.kind === 'file' ? (own.commits || 0) : (own.commits || agg.commits),
        last, authors,
      };
      return n._agg;
    };
    for (const r of roots) walk(r, r === this.systemId ? -1 : 0, null);

    // edges
    this.edges = (g.edges || []).filter(e => EDGE_CAT[e.kind]);
    this.edgesOf = new Map();
    const touch = (id, e) => { if (!this.edgesOf.has(id)) this.edgesOf.set(id, []); this.edgesOf.get(id).push(e); };
    for (const e of this.edges) { touch(e.source, e); touch(e.target, e); }

    // fan-in / fan-out per file (code edges only)
    for (const e of this.edges) {
      if (EDGE_CAT[e.kind] !== 'code') continue;
      const a = this.fileOf(e.source), b = this.fileOf(e.target);
      if (!a || !b || a === b) continue;
      const na = this.byId.get(a), nb = this.byId.get(b);
      na._out = (na._out || 0) + 1; nb._in = (nb._in || 0) + 1;
    }

    // roadmap
    this.roadmap = g.roadmap || [];
    this.rmById = new Map(this.roadmap.map(r => [r.id, r]));
    this.rmChildren = new Map();
    for (const r of this.roadmap) {
      // titles come straight from Markdown: drop emphasis/code marks and link syntax
      r.title = r.title.replace(/\*\*|__|`/g, '').replace(/\[([^\]]+)\]\([^)]*\)/g, '$1').trim();
      const p = r.parentId && this.rmById.has(r.parentId) ? r.parentId : null;
      r._parent = p;
      if (!this.rmChildren.has(p)) this.rmChildren.set(p, []);
      this.rmChildren.get(p).push(r.id);
      r.links = r.links || [];
    }
    for (const e of this.edges) if (e.kind === 'mentions' && this.rmById.has(e.source)) {
      const r = this.rmById.get(e.source);
      if (!r.links.includes(e.target)) r.links.push(e.target);
    }
    this.rmByNode = new Map();
    for (const r of this.roadmap) for (const l of r.links) {
      if (!this.byId.has(l)) continue;
      if (!this.rmByNode.has(l)) this.rmByNode.set(l, []);
      this.rmByNode.get(l).push(r.id);
    }

    // plain-Russian role of every node
    const pname = g.project?.name;
    for (const n of g.nodes) n._desc = typeof describe === 'function' ? describe(n, pname) : { ru: null, hint: null };

    // colour: every top-level block gets its own clearly different hue (big modules first get
    // the most distinct ones); children are shades of their parent so families stay readable.
    const HUES = [210, 28, 135, 330, 52, 265, 175, 0, 95, 300, 195, 15, 155, 240, 75, 350, 120, 285];
    const tops = [...this.top].sort((a, b) => (this.byId.get(b)._agg.loc || 0) - (this.byId.get(a)._agg.loc || 0));
    this.hue = new Map();
    tops.forEach((id, i) => {
      const n = this.byId.get(id);
      const h = n.kind === 'group' && /data|баз/i.test(id + n.name) ? 172 : HUES[i % HUES.length] + Math.floor(i / HUES.length) * 7;
      this.hue.set(id, { h, s: this.isTest(id) ? 22 : 72 });
    });
    const shade = (id, h, s, depth) => {
      this.kids(id).forEach((k, i) => {
        const hk = h + ((i % 7) - 3) * 6;
        this.hue.set(k, { h: hk, s: Math.max(18, s - 6), d: depth });
        shade(k, hk, Math.max(18, s - 6), depth + 1);
      });
    };
    for (const id of this.top) { const { h, s } = this.hue.get(id); shade(id, h, s, 1); }
    this.indexNavigation();
  }

  /** Additive indexes: unlike legacy drawing indexes, these retain every edge kind/name. */
  indexNavigation() {
    const append = (map, key, value) => {
      if (!map.has(key)) map.set(key, []);
      map.get(key).push(value);
    };
    this.realChildren = new Map();
    this.declarationsByPath = new Map();
    this.declarationsByNode = new Map();
    this.nodesByFile = new Map();
    for (const id of this.canonicalIds) {
      const n = this.node(id), parent = this.realParent.get(id);
      if (parent) append(this.realChildren, parent, id);
      const declarations = n.declarations?.length ? n.declarations :
        (n.path && (n.kind === 'type' || n.kind === 'member') ? [{ path: n.path, line: n.line }] : []);
      this.declarationsByNode.set(id, declarations);
      const files = new Set();
      for (const d of declarations) {
        append(this.declarationsByPath, this.pathKey(d.path), { nodeId: id, ...d });
        const file = this.fileAtPath(d.path);
        if (file) files.add(file);
      }
      const file = this.actualFileOf(id);
      if (file) files.add(file);
      for (const fid of files) append(this.nodesByFile, fid, id);
    }
    const views = this.g.views || {}, explanations = this.explanationData(views.explanations);
    this.featuresById = new Map((views.features || []).map(f => [f.id, f]));
    this.tiersById = new Map((views.tiers || []).map(t => [t.id, t]));
    this.assignmentsByNode = new Map((views.assignments || []).map(a => [a.nodeId, a]));
    this.membershipsByNode = new Map();
    this.membershipsByFeature = new Map();
    for (const m of views.memberships || []) {
      append(this.membershipsByNode, m.nodeId, m);
      append(this.membershipsByFeature, m.featureId, m);
    }
    this.sources = explanations.sources || [];
    this.explanationsById = new Map((explanations.nodes || []).map(n => [n.nodeId, n]));
    this.nameIndex = new Map();
    const name = (key, id) => {
      if (!key) return;
      key = key.toLowerCase();
      if (!this.nameIndex.has(key)) this.nameIndex.set(key, new Set());
      this.nameIndex.get(key).add(id);
    };
    for (const id of this.canonicalIds) {
      const n = this.node(id);
      name(n.name, id); name(n.path, id);
    }
    for (const [key, indexes] of Object.entries(explanations.nameIndex || {})) {
      for (const index of indexes) {
        const id = explanations.nodes?.[index]?.nodeId;
        if (this.canonicalIds.has(id)) name(key, id);
      }
    }
    this.allEdges = this.g.edges || [];
    this.edgesByKind = new Map();
    this.relationsOf = new Map();
    for (const e of this.allEdges) {
      append(this.edgesByKind, e.kind, e);
      append(this.relationsOf, e.source, e);
      if (e.target !== e.source) append(this.relationsOf, e.target, e);
    }
  }

  pathKey(path) { return (path || '').replace(/\\/g, '/').toLowerCase(); }
  fileAtPath(path) { return this.byPath.get(this.pathKey(path)) || null; }

  /** SerializeCompact interns explanation text/sources independently of compact edges. */
  explanationData(info) {
    if (!info) return {};
    if (!info.compact) return info;
    const text = index => index == null ? null : info.texts[index];
    const claim = row => ({ text: text(row[0]), confidence: ['known', 'derived', 'guessed'][row[1]], sourceIds: row[2] });
    return { ...info,
      nodes: info.nodes.map(row => ({ nodeId: typeof row[0] === 'number' ? this.g.nodes[row[0]].id : row[0],
        displayName: claim(row[1]), description: claim(row[2]), claims: row[3]?.map(claim) })),
      sources: info.sources.map(row => ({ kind: text(row[0]), path: text(row[1]), line: row[2], endLine: row[3],
        sourceHash: text(row[4]), commitId: text(row[5]), ruleId: text(row[6]), reference: text(row[7]), text: text(row[8]) })),
    };
  }

  /** Shared source-file lookup. Partial members use their own path, never their type's file. */
  actualFileOf(id) {
    const n = this.node(id);
    if (!n) return null;
    if (n.kind === 'file') return id;
    if (n.path) return this.fileAtPath(n.path); // An unavailable own file is not another part.
    const declaration = n.declarations?.[0];
    if (declaration) return this.fileAtPath(declaration.path);
    for (let p = this.realParent.get(id); p; p = this.realParent.get(p)) {
      if (this.node(p)?.kind === 'file') return p;
    }
    return null;
  }

  sourcesFor(claim, nodeId) {
    return (claim?.sourceIds || []).map(index => {
      const source = this.sources[index];
      if (!source) return null;
      if (source.kind !== 'declaration' || source.reference !== 'self') return { ...source };
      const n = this.node(nodeId);
      return { ...source, nodeId, path: n?.path, line: n?.line, declarations: this.declarationsByNode.get(nodeId) || [] };
    }).filter(Boolean);
  }

  colorOf(id) {
    const c = this.hue.get(id) || { h: 220, s: 12, d: 0 };
    const d = Math.min(3, c.d || 0);
    if (document.documentElement.dataset.theme === 'dark') return {
      line: `hsl(${c.h},${Math.round(c.s * 0.85)}%,${68 - d * 4}%)`,
      fill: `hsl(${c.h},${Math.round(c.s * 0.4)}%,${25 + d * 2}%)`,
      soft: `hsl(${c.h},${Math.round(c.s * 0.5)}%,40%)`, // drawn with background-opacity (Cytoscape drops hsla alpha)
    };
    return {
      line: `hsl(${c.h},${Math.round(c.s * 0.8)}%,${40 + d * 3}%)`,
      fill: `hsl(${c.h},${Math.round(c.s * 0.9)}%,${93 - d}%)`,
      soft: `hsl(${c.h},${Math.round(c.s * 0.7)}%,80%)`,
    };
  }

  /** Plain-language category of a top-level block, used to group the sidebar. */
  categoryOf(id) {
    const n = this.byId.get(id), key = (n.path || n.name || '').toLowerCase();
    if (n.kind === 'group') return /data|баз/i.test(id + n.name) ? 'data' : 'docs';
    if (this.isTest(id) || /^tests?(\/|$)/.test(key)) return 'tests';
    if (/^(tools|experiments?|scripts|samples|benchmarks?)(\/|$)/.test(key) || /performance|livecheck|benchmark/i.test(n.name)) return 'tools';
    if (n.kind === 'module') return 'app';
    if (/^(docs?|\.github|\.claude|\.vscode|\.codex)(\/|$)|^virtual:/.test(key) || id.startsWith('virtual:')) return 'docs';
    return 'data';
  }

  node(id) { return this.byId.get(id); }
  kids(id) { return this.children.get(id) || []; }
  parent(id) { return this.byId.get(id)?._parent || null; }
  hasKids(id) { return (this.children.get(id) || []).length > 0; }

  ancestors(id) {
    const out = [];
    for (let p = this.parent(id); p; p = this.parent(p)) out.push(p);
    return out;
  }
  isUnder(id, anc) {
    for (let x = id; x; x = this.parent(x)) if (x === anc) return true;
    return false;
  }
  fileOf(id) {
    for (let x = id; x; x = this.parent(x)) { const n = this.byId.get(x); if (n?.kind === 'file' || n?.kind === 'table') return x; }
    return null;
  }
  descendantsCount(id) { return this.byId.get(id)?._agg?.files ?? 0; }
  isTest(id) { return this.byId.get(id)?.tags?.includes('test'); }
}

if (typeof module !== 'undefined') module.exports = { GraphModel, EDGE_CAT, EDGE_RU, KIND_RU };
