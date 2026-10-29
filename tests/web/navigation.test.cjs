const test = require('node:test');
const assert = require('node:assert/strict');
const { graph, setup, GraphModel, NavigationState } = require('./fixtures.cjs');

test('related navigation and Back restore exact selection, link, path, filters and cameras', () => {
  const { model } = setup(), nav = new NavigationState(model);
  nav.go({ id: 'member:open', filters: { excludeTags: ['test'] }, featureId: 'feature:export', route: 'purpose',
    timeFilter: { from: '2026-10-01T00:00:00Z' }, level: 'code', view: 'links',
    selectedLink: { source: 'member:open', target: 'member:export', kind: 'call' } });
  nav.setCamera('links', { x: 100, y: 200, zoom: 2 });
  nav.setCamera('tree', { scroll: 19 });
  const original = nav.state, cameras = nav.cameraByView;
  nav.go('member:export', 'related');
  assert.equal(nav.selectedId, 'member:export'); assert.equal(nav.state.filePath, 'a/src/deep/Editor.Part.cs');
  nav.go({ scope: 'folder:deep', selectedId: 'folder:deep', filters: {} }, 'location');
  nav.setCamera('links', { x: 0 });
  nav.back(); assert.equal(nav.selectedId, 'member:export');
  assert.equal(nav.back().status, 'ok');
  assert.deepEqual(nav.state, original); assert.deepEqual(nav.cameraByView, cameras);
  assert.equal(nav.canBack, true);
});

test('view changes preserve the shared context, with independent cameras', () => {
  const { model } = setup(), nav = new NavigationState(model, { id: 'member:export', level: 'code' });
  nav.setCamera('tree', { x: 1 }); nav.setCamera('links', { x: 2 });
  nav.setView('tree'); nav.setView('links');
  assert.equal(nav.selectedId, 'member:export'); assert.equal(nav.state.filePath, 'a/src/deep/Editor.Part.cs');
  assert.equal(nav.state.level, 'code'); assert.deepEqual(nav.cameraFor('tree'), { x: 1 });
  nav.back(); assert.equal(nav.state.view, 'tree');
});

test('up follows the purpose route through real file/folders/module and feature', () => {
  const { model } = setup(), nav = new NavigationState(model, { id: 'member:export', route: 'purpose', featureId: 'feature:export' });
  const scopes = [nav.scope];
  for (let i = 0; i < 7; i++) { assert.equal(nav.up().status, 'ok'); scopes.push(nav.scope); }
  assert.deepEqual(scopes, ['member:export', 'type:editor', 'file:part', 'folder:deep', 'folder:a', 'mod:a', 'feature:export', 'sys']);
  assert.equal(nav.up(), null);
  assert.ok(nav.projection.children(nav.scope, nav.state).some(n => n.kind === 'feature'));
  nav.back(); assert.equal(nav.scope, 'feature:export'); assert.equal(nav.selectedId, null);
});

test('change events unsubscribe; snapshots and camera values cannot mutate state', () => {
  const { model } = setup(), nav = new NavigationState(model);
  let count = 0;
  const off = nav.on('change', e => { count++; e.state.filters.modified = true; });
  nav.go('file:ts1'); off(); nav.go('file:ts2');
  assert.equal(count, 1); assert.equal(nav.state.filters.modified, undefined);
  const state = nav.state; state.selectedId = 'wrong'; assert.equal(nav.selectedId, 'file:ts2');
  const camera = { x: 9 }; nav.setCamera('tree', camera); camera.x = 0;
  assert.equal(nav.cameraFor('tree').x, 9);
});

test('purpose scope selects its group; selection and scope retain independent partial paths', () => {
  const { model } = setup(), nav = new NavigationState(model);
  nav.go({ scope: 'feature:export' });
  assert.equal(nav.state.featureId, 'feature:export'); assert.equal(nav.state.route, 'purpose');
  nav.go('mod:a'); nav.up(); assert.equal(nav.scope, 'feature:export');
  nav.go({ id: 'type:editor', filePath: 'a/src/deep/Editor.Part.cs' });
  const route = nav.state.routePath;
  nav.go({ selectedId: 'member:get2', scope: 'type:editor' });
  assert.equal(nav.state.filePath, 'b/src/route.ts');
  assert.equal(nav.state.scopeFilePath, 'a/src/deep/Editor.Part.cs');
  assert.deepEqual(nav.state.routePath, route);
  assert.deepEqual(nav.projection.children(nav.scope, nav.state).filter(n => n.kind === 'member').map(n => n.id), ['member:export']);
  nav.go({ selectedId: null });
  assert.equal(nav.scope, 'type:editor'); assert.deepEqual(nav.state.routePath, route);
});

test('invalid renderer IDs, features, links and part paths fail atomically', () => {
  const { model } = setup(), nav = new NavigationState(model);
  for (const target of ['rectangle:1', 'feature:export', { id: 'member:export', filePath: 'b/src/route.ts' },
    { id: 'member:export', featureId: 'feature:missing' },
    { id: 'member:export', selectedLink: { source: 'file:a', target: 'file:ts2', kind: 'call' } }]) {
    assert.throws(() => nav.go(target), RangeError);
    assert.equal(nav.canBack, false); assert.equal(nav.selectedId, null);
  }
  assert.equal(nav.back(), null);
});

test('rescan reports deletion, keeps tombstone and proposes nearest surviving folder, never namesake', () => {
  const { model } = setup(), nav = new NavigationState(model, { id: 'member:export' });
  const g = graph();
  g.nodes = g.nodes.filter(n => !['member:export', 'folder:deep'].includes(n.id));
  g.nodes.find(n => n.id === 'file:part').parentId = 'folder:a';
  g.nodes.push({ id: 'member:namesake', kind: 'member', name: 'Export', path: 'b/src/route.ts', parentId: 'file:ts2' });
  const result = nav.restore(new GraphModel(g));
  assert.equal(result.status, 'missing'); assert.equal(result.selectedId, 'member:export');
  assert.equal(result.path, 'a/src/deep/Editor.Part.cs'); assert.equal(result.suggestedFolderId, 'folder:a');
  assert.equal(nav.selectedId, 'member:export');
  assert.equal(nav.go(result.suggestedFolderId, 'accept-suggestion').status, 'ok');
  assert.equal(nav.back().status, 'missing'); assert.equal(nav.selectedId, 'member:export');
});

test('rescan with unchanged IDs preserves filters and chosen partial declaration', () => {
  const { model } = setup(), nav = new NavigationState(model, { id: 'type:editor', filePath: 'a/src/deep/Editor.Part.cs', filters: { excludeTags: ['test'] } });
  assert.equal(nav.restore(new GraphModel(graph())).status, 'ok');
  assert.equal(nav.state.filePath, 'a/src/deep/Editor.Part.cs');
  assert.deepEqual(nav.state.filters, { excludeTags: ['test'] });
  const g = graph(); g.nodes.find(n => n.id === 'type:editor').declarations.pop();
  assert.equal(nav.restore(new GraphModel(g)).missingPart, true);
});

test('rescan reports a disappeared selected relation even when both nodes survive', () => {
  const { model } = setup(), nav = new NavigationState(model, { id: 'member:open',
    selectedLink: { source: 'member:open', target: 'member:export', kind: 'call' } });
  const g = graph(); g.edges = g.edges.filter(e => e.kind !== 'call');
  assert.equal(nav.restore(new GraphModel(g)).missingLink, true);
  assert.equal(nav.go('file:ts1').status, 'ok');
});
