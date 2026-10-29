// Pure navigation ladder over canonical graph objects; no drawing limits or DOM.
'use strict';

class GraphProjection {
  constructor(model) {
    this.model = model;
    this._files = new Map();
  }

  actualFileOf(id) { return this.model.actualFileOf(id); }

  /** Real source location, optionally choosing one declaration of a partial type. */
  locationOf(id, { filePath } = {}) {
    const M = this.model, n = M.node(id);
    if (!n) return null;
    const declarations = M.declarationsByNode.get(id) || [];
    const part = filePath && declarations.find(d => M.pathKey(d.path) === M.pathKey(filePath));
    if (filePath && !part && M.pathKey(n.path) !== M.pathKey(filePath)) return null;
    const file = M.node(part ? M.fileAtPath(part.path) : M.actualFileOf(id)) || null;
    const folders = [];
    let module = n.kind === 'module' ? n : null;
    for (let x = file?.id || id; x; x = M.realParent.get(x)) {
      const item = M.node(x);
      if (item?.kind === 'folder') folders.unshift(item);
      if (item?.kind === 'module') module = item;
    }
    return { module, folders, file, path: part?.path || n.path || file?.path || null, line: part?.line ?? n.line ?? null };
  }

  /** Unique physical files; memberships in overlapping features form a union. */
  filesOf(id) {
    if (this._files.has(id)) return new Set(this._files.get(id));
    const M = this.model, n = M.node(id), files = new Set();
    const group = !n && this.group(id);
    if (group) return this.fileSet(group);
    if (n?.kind === 'file') files.add(id);
    else if (n?.kind === 'type') {
      for (const d of M.declarationsByNode.get(id) || []) {
        const file = M.fileAtPath(d.path);
        if (file) files.add(file);
      }
      const file = M.actualFileOf(id);
      if (file) files.add(file);
    } else if (n?.kind === 'member' || n?.kind === 'doc' || n?.kind === 'table') {
      const file = M.actualFileOf(id);
      if (file) files.add(file);
    } else {
      for (const child of M.realChildren.get(id) || []) for (const file of this.filesOf(child)) files.add(file);
    }
    this._files.set(id, files);
    return new Set(files);
  }

  fileSet(options = {}) {
    const M = this.model, filters = options.filters || {}, files = new Set();
    const featureIds = options.featureId ? [options.featureId] : (filters.featureIds || []);
    if (featureIds.length) {
      for (const feature of featureIds) for (const m of M.membershipsByFeature.get(feature) || []) {
        for (const file of this.filesOf(m.nodeId)) files.add(file);
      }
    } else for (const id of M.canonicalIds) if (M.node(id).kind === 'file') files.add(id);
    let active = null;
    if (options.timeFilter) {
      const timeline = M.g.views?.timeline, { from, to } = options.timeFilter;
      const start = from ? Date.parse(from) : -Infinity, end = to ? Date.parse(to) : Infinity;
      if (Number.isNaN(start) || Number.isNaN(end) || start > end) throw new RangeError('Invalid time interval');
      active = new Set();
      for (const commit of timeline?.commits || []) if (Date.parse(commit.at) >= start && Date.parse(commit.at) <= end) {
        for (const index of commit.files) active.add(timeline.files[index]);
      }
    }
    const allowed = filters.fileIds && new Set(filters.fileIds);
    for (const id of files) {
      const n = M.node(id), assignment = M.assignmentsByNode.get(id);
      if ((options.role && assignment?.role !== options.role) || (options.tierId && assignment?.tier !== options.tierId) ||
          (allowed && !allowed.has(id)) || (active && !active.has(id)) ||
          (filters.excludeTags || []).some(tag => n.tags.includes(tag))) files.delete(id);
    }
    return files;
  }

  purposeGroups() {
    const M = this.model;
    return [
      ...[...M.featuresById.values()].map(f => ({ id: f.id, kind: 'feature', name: f.name, featureId: f.id })),
      ...[...M.tiersById.values()].map(t => ({ id: `tier:${t.id}`, kind: 'tier', name: t.name, tierId: t.id })),
      ...[...new Set([...M.assignmentsByNode.values()].map(a => a.role).filter(Boolean))].sort()
        .map(role => ({ id: `role:${role}`, kind: 'role', name: role, role })),
    ];
  }

  group(id) { return this.purposeGroups().find(g => g.id === id) || null; }

  item(id, options = {}, shown = this.fileSet(options)) {
    const M = this.model, n = M.node(id), location = this.locationOf(id, { filePath: options.filePath });
    if (!n) return null;
    const total = this.filesOf(id);
    const shownFiles = [...total].filter(file => shown.has(file)).length;
    return { id, kind: n.kind, subKind: n.subKind, name: n.name,
      path: location?.path || n.path || null, line: location?.line ?? n.line ?? null,
      shownFiles, totalFiles: total.size, partial: shownFiles < total.size };
  }

  /** Immediate steps only: purpose group -> real module/folder -> file -> type/member. */
  children(scope = this.model.systemId, options = {}) {
    const M = this.model, group = this.group(scope);
    if (group) options = { ...options, ...group, route: 'purpose' };
    if (!group && scope === M.systemId && options.route === 'purpose') return this.purposeGroups();
    if (group) scope = M.systemId;
    const n = M.node(scope);
    if (!n) return [];
    const shown = this.fileSet(options);
    let ids;
    if (n.kind === 'file') {
      // Include all partial declarations, even when the canonical parent is another file.
      ids = (M.nodesByFile.get(scope) || []).filter(id => {
        const kind = M.node(id).kind, parent = M.node(M.realParent.get(id));
        return (kind === 'type' && parent?.kind === 'file') || (kind === 'member' && parent?.kind === 'file');
      });
      options = { ...options, filePath: n.path };
    } else if (n.kind === 'type') {
      ids = M.realChildren.get(scope) || [];
      options = { ...options, filePath: options.scopeFilePath || options.filePath };
      if (options.filePath) {
        const file = M.fileAtPath(options.filePath);
        ids = ids.filter(id => this.filesOf(id).has(file));
      }
    } else if (n.kind === 'member') ids = [];
    else ids = (M.realChildren.get(scope) || []).filter(id => ['module', 'folder', 'file'].includes(M.node(id).kind));
    return [...new Set(ids)].filter(id => [...this.filesOf(id)].some(file => shown.has(file)))
      .map(id => this.item(id, options, shown)).sort((a, b) => a.kind.localeCompare(b.kind) || a.name.localeCompare(b.name) || a.id.localeCompare(b.id));
  }

  routeTo(id, options = {}) {
    const M = this.model, group = this.group(id);
    if (group) return [this.item(M.systemId), group].filter(Boolean);
    if (!M.canonicalIds.has(id)) throw new RangeError(`Unknown canonical node: ${id}`);
    const location = this.locationOf(id, { filePath: options.filePath });
    if (!location) throw new RangeError(`No declaration of ${id} in ${options.filePath}`);
    const physical = [];
    const inCode = ['type', 'member'].includes(M.node(id).kind);
    for (let x = inCode ? location.file?.id : id; x; x = M.realParent.get(x)) physical.unshift(x);
    // Semantic ownership below a file, physical ownership above it.
    const code = [];
    if (M.node(id).kind === 'type' || M.node(id).kind === 'member') {
      for (let x = id; x && ['type', 'member'].includes(M.node(x)?.kind); x = M.realParent.get(x)) code.unshift(x);
    }
    const shown = this.fileSet(options);
    const result = [...new Set([...physical, ...code])].map(x => {
      const kind = M.node(x).kind;
      return this.item(x, { ...options, filePath: ['type', 'member'].includes(kind) ? location.file?.path : undefined }, shown);
    });
    if (options.route === 'purpose' && id !== M.systemId) {
      const groupId = options.featureId || (options.role && `role:${options.role}`) || (options.tierId && `tier:${options.tierId}`);
      const step = this.group(groupId);
      if (step) result.splice(result[0]?.kind === 'system' ? 1 : 0, 0, step);
    }
    return result;
  }

  thisPartInFile(typeId, filePath) {
    const M = this.model;
    if (M.node(typeId)?.kind !== 'type') throw new RangeError('Expected a type ID');
    const declarations = (M.declarationsByNode.get(typeId) || []).filter(d => M.pathKey(d.path) === M.pathKey(filePath));
    const file = M.fileAtPath(filePath);
    const exists = declarations.length > 0 || (!M.node(typeId).declarations?.length && M.actualFileOf(typeId) === file);
    return { typeId, file: exists ? M.node(file) || null : null, declarations,
      members: exists ? this.children(typeId, { filePath }).filter(n => n.kind === 'member') : [],
      types: exists ? this.children(typeId, { filePath }).filter(n => n.kind === 'type') : [] };
  }

  wholeBlockAllFiles(typeId) {
    const M = this.model;
    if (M.node(typeId)?.kind !== 'type') throw new RangeError('Expected a type ID');
    return { typeId, files: [...this.filesOf(typeId)].map(id => M.node(id)),
      declarations: [...(M.declarationsByNode.get(typeId) || [])],
      members: this.children(typeId).filter(n => n.kind === 'member'), types: this.children(typeId).filter(n => n.kind === 'type') };
  }

  endpoint(id) {
    const M = this.model, n = M.node(id), roadmap = M.rmById.get(id);
    return { id, name: n?.name || roadmap?.title || id,
      displayName: M.explanationsById.get(id)?.displayName?.text || n?.name || roadmap?.title || id,
      path: n?.path || roadmap?.source?.path || null, line: n?.line ?? roadmap?.source?.line ?? null };
  }

  _groupLinks(edges) {
    const groups = new Map();
    for (const e of edges) {
      if (!groups.has(e.kind)) groups.set(e.kind, []);
      const source = this.endpoint(e.source), target = this.endpoint(e.target);
      groups.get(e.kind).push({ id: e.id || `${e.kind}:${e.source}->${e.target}`, kind: e.kind,
        source, target, weight: e.weight, strength: e.strength,
        evidence: (e.evidence || []).map(v => ({ ...v, path: v.path || source.path || '' })) });
    }
    return [...groups].sort(([a], [b]) => a.localeCompare(b)).map(([kind, relations]) => ({ kind, relations }));
  }

  /** Full direct name list. Co-change participates on either side in every direction. */
  linksOf(id, { direction = 'both' } = {}) {
    if (!['in', 'out', 'both'].includes(direction)) throw new RangeError('Invalid direction');
    return this._groupLinks((this.model.relationsOf.get(id) || []).filter(e =>
      e.kind === 'cochange' || direction === 'both' || (direction === 'in' ? e.target === id : e.source === id)));
  }

  /** Expand a drawn container-to-container aggregate back to original relations, by kind. */
  linksBetween(sourceId, targetId, { direction = 'out' } = {}) {
    if (!['in', 'out', 'both'].includes(direction)) throw new RangeError('Invalid direction');
    const M = this.model;
    const matcher = container => {
      const node = M.node(container);
      const files = this.filesOf(container);
      return id => {
        if (id === container) return true;
        if (['type', 'member'].includes(node?.kind)) {
          for (let x = M.realParent.get(id); x; x = M.realParent.get(x)) if (x === container) return true;
          return false;
        }
        if (files.has(M.actualFileOf(id))) return true;
        // Container-level project/package edges need real containment as well.
        if (['system', 'module', 'folder'].includes(M.node(id)?.kind)) {
          for (let x = M.realParent.get(id); x; x = M.realParent.get(x)) if (x === container) return true;
        }
        return false;
      };
    };
    const source = matcher(sourceId), target = matcher(targetId);
    return this._groupLinks(M.allEdges.filter(e => {
      const out = source(e.source) && target(e.target);
      const into = source(e.target) && target(e.source);
      return e.kind === 'cochange' || direction === 'both' ? out || into : direction === 'in' ? into : out;
    }));
  }
}

if (typeof module !== 'undefined') module.exports = { GraphProjection };
