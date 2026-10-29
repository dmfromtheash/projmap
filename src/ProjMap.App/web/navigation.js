// Shared, serializable navigation; camera state is independent for each view.
'use strict';

class NavigationState {
  constructor(model, initial = {}) {
    this.model = model;
    const Projection = typeof module !== 'undefined' ? require('./projection.js').GraphProjection : GraphProjection;
    this.projection = new Projection(model);
    this._listeners = new Map();
    this._history = [];
    this._cameras = {};
    this._state = { selectedId: null, scope: model.systemId, route: 'location', level: 'overview',
      timeFilter: null, featureId: null, role: null, tierId: null, filters: {}, selectedLink: null,
      filePath: null, scopeFilePath: null, view: null, routePath: [] };
    this._state = this._target(initial);
  }

  _copy(value) { return value == null ? value : JSON.parse(JSON.stringify(value)); }
  get state() { return this._copy(this._state); }
  get selectedId() { return this._state.selectedId; }
  get scope() { return this._state.scope; }
  get canBack() { return this._history.length > 0; }
  get cameraByView() { return this._copy(this._cameras); }
  cameraFor(view) { return this._copy(this._cameras[view] ?? null); }

  on(event, fn) {
    if (!this._listeners.has(event)) this._listeners.set(event, new Set());
    this._listeners.get(event).add(fn);
    return () => this._listeners.get(event)?.delete(fn);
  }

  emit(event, payload) {
    for (const fn of this._listeners.get(event) || []) fn(this._copy(payload));
  }

  _change(reason, status) { this.emit('change', { reason, state: this.state, cameraByView: this.cameraByView, status }); }

  _target(target) {
    if (typeof target === 'string') target = { selectedId: target, scope: target };
    target = this._copy(target);
    // id is a convenience alias, always a canonical graph node ID.
    if (Object.hasOwn(target, 'id')) { target.selectedId = target.id; delete target.id; }
    if (Object.hasOwn(target, 'selectedId') && !Object.hasOwn(target, 'scope') && target.selectedId) target.scope = target.selectedId;
    const next = { ...this._state, ...target };
    const scopeGroup = this.projection.group(next.scope);
    if (scopeGroup && Object.hasOwn(target, 'scope')) {
      next.route = 'purpose';
      next.featureId = scopeGroup.featureId || null;
      next.role = scopeGroup.role || null;
      next.tierId = scopeGroup.tierId || null;
    }
    const changedSelection = Object.hasOwn(target, 'selectedId') && next.selectedId !== this._state.selectedId;
    if (changedSelection && !Object.hasOwn(target, 'selectedLink')) next.selectedLink = null;
    if (changedSelection && !Object.hasOwn(target, 'filePath')) next.filePath = null;
    if (!['purpose', 'location'].includes(next.route)) throw new RangeError('Invalid route');
    if (next.selectedId != null && !this.model.canonicalIds.has(next.selectedId)) throw new RangeError(`Unknown canonical node: ${next.selectedId}`);
    if (next.scope != null && !this.model.canonicalIds.has(next.scope) && !this.projection.group(next.scope)) throw new RangeError(`Unknown scope: ${next.scope}`);
    if (next.featureId != null && !this.model.featuresById.has(next.featureId)) throw new RangeError(`Unknown feature: ${next.featureId}`);
    if (next.selectedLink != null) {
      const { source, target: end, kind } = next.selectedLink;
      if (!(this.model.relationsOf.get(source) || []).some(e => e.target === end && e.kind === kind)) throw new RangeError('Unknown source relation');
    }
    if (next.selectedId) {
      const location = this.projection.locationOf(next.selectedId, { filePath: next.filePath });
      if (!location) throw new RangeError('Selected part is not declared in that file');
      next.filePath = location.file?.path || null;
    } else next.filePath = null;
    const scopeKind = this.model.node(next.scope)?.kind;
    if (['type', 'member'].includes(scopeKind)) {
      next.scopeFilePath = Object.hasOwn(target, 'scopeFilePath') ? target.scopeFilePath :
        (next.scope === next.selectedId ? next.filePath : (next.scope === this._state.scope ? this._state.scopeFilePath : null));
    } else next.scopeFilePath = null;
    next.routePath = next.scope ? this.projection.routeTo(next.scope, { ...next,
      filePath: next.scopeFilePath || undefined }) : [];
    // Save location data separately: it remains useful when this ID disappears in a rescan.
    const location = next.selectedId && this.projection.locationOf(next.selectedId, { filePath: next.filePath });
    next.location = location ? { path: location.path, line: location.line,
      folders: location.folders.map(n => ({ id: n.id, path: n.path })) } : null;
    return next;
  }

  go(target, reason = 'go') {
    const next = this._target(target); // Validate before changing either state or history.
    this._history.push({ state: this.state, cameras: this.cameraByView });
    this._state = next;
    const status = this._status();
    this._change(reason, status);
    return status;
  }

  back() {
    const previous = this._history.pop();
    if (!previous) return null;
    this._state = previous.state;
    this._cameras = previous.cameras;
    const status = this._status();
    this._change('back', status);
    return status;
  }

  up() {
    const route = this._state.routePath;
    if (route.length < 2) return null;
    const parent = route[route.length - 2], group = this.projection.group(parent.id);
    return this.go({ scope: parent.id, selectedId: group ? null : parent.id,
      filePath: ['type', 'member'].includes(parent.kind) ? parent.path : null,
      selectedLink: null }, 'up');
  }

  setView(view) { return this.go({ view }, 'view'); }

  setCamera(view, camera) {
    this._cameras[view] = this._copy(camera);
    this._change('camera', this._status());
  }

  _status() {
    const M = this.model, state = this._state, missingIds = new Set();
    if (state.selectedId && !M.canonicalIds.has(state.selectedId)) missingIds.add(state.selectedId);
    if (state.scope && !M.canonicalIds.has(state.scope) && !this.projection.group(state.scope)) missingIds.add(state.scope);
    if (state.featureId && !M.featuresById.has(state.featureId)) missingIds.add(state.featureId);
    const file = state.filePath && M.fileAtPath(state.filePath);
    const missingPart = !!state.selectedId && M.canonicalIds.has(state.selectedId) && !!state.filePath &&
      (!file || !this.projection.locationOf(state.selectedId, { filePath: state.filePath }));
    const missingScopePart = !!state.scopeFilePath && M.canonicalIds.has(state.scope) &&
      !this.projection.locationOf(state.scope, { filePath: state.scopeFilePath });
    const link = state.selectedLink;
    const missingLink = !!link && !(M.relationsOf.get(link.source) || []).some(e => e.kind === link.kind && e.target === link.target);
    const ancestors = state.location?.folders?.length ? state.location.folders : state.routePath.filter(n => n.kind === 'folder');
    const folder = [...ancestors].reverse().find(n => M.node(n.id)?.kind === 'folder');
    const missing = !!(missingIds.size || missingPart || missingScopePart || missingLink);
    return { status: missing ? 'missing' : 'ok', selectedId: state.selectedId,
      path: state.location?.path || state.filePath, missingIds: [...missingIds], missingPart, missingScopePart, missingLink,
      suggestedFolderId: missing ? folder?.id || null : null, suggestedFolderPath: missing ? folder?.path || null : null };
  }

  /** Preserve missing selection as a tombstone; accepting the suggested folder is an explicit go(). */
  restore(newModel) {
    this.model = newModel;
    const Projection = this.projection.constructor;
    this.projection = new Projection(newModel);
    const status = this._status();
    if (status.status === 'ok') this._state = this._target({});
    this._change('restore', status);
    return status;
  }
}

if (typeof module !== 'undefined') module.exports = { NavigationState };
