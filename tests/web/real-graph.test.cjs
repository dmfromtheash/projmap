const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const { GraphModel, GraphProjection, NavigationState } = require('./fixtures.cjs');
const flatten = groups => groups.flatMap(g => g.relations);

// Large, private source graphs are never committed. Supply a CLI scan explicitly (see NAVIGATION.md).
const mapPath = process.env.PROJMAP_MAP_GRAPH;
let graph, model, projection;
if (mapPath) {
  graph = JSON.parse(fs.readFileSync(mapPath, 'utf8'));
  model = new GraphModel(graph); projection = new GraphProjection(model);
}
const map = (name, fn) => test(`Map: ${name}`, { skip: !mapPath && 'Set PROJMAP_MAP_GRAPH to the isolated CLI scan' }, fn);

map('scan totals and complete indexes', t => {
  const nodes = model.canonicalIds.size, edges = model.allEdges.length;
  t.diagnostic(`${nodes} nodes, ${edges} edges, ${graph.warnings.length} warnings`);
  // Map itself keeps changing, so the scan is compared with itself, not with one historical snapshot:
  // every node and edge of graph.json must be reachable through the model indexes.
  assert.equal(graph.warnings.length, 0);
  // The model adds a viewer-only folder for loose root files to graph.nodes; compare with the file as scanned.
  const raw = JSON.parse(fs.readFileSync(mapPath, 'utf8'));
  assert.equal(nodes, new Set(raw.nodes.map(n => n.id)).size);
  assert.equal(edges, raw.edges.length);
  for (const kind of ['call', 'reference', 'implement', 'projectRef', 'cochange'])
    assert.ok((model.edgesByKind.get(kind)?.length || 0) > 0, kind);
  const byKind = new Map();
  for (const e of graph.edges) byKind.set(e.kind, (byKind.get(e.kind) || 0) + 1);
  for (const [kind, count] of byKind) assert.equal(model.edgesByKind.get(kind)?.length || 0, count, kind);
  for (const kind of new Set(graph.edges.map(e => e.kind))) assert.ok(model.edgesByKind.has(kind));
  for (const [name, indexes] of Object.entries(graph.views.explanations.nameIndex)) {
    for (const index of indexes) assert.ok(model.nameIndex.get(name.toLowerCase()).has(graph.views.explanations.nodes[index].nodeId));
  }
});

map('EditorViewModel part vs all files, and actual declaring file of every member', t => {
  const type = graph.nodes.find(n => n.kind === 'type' && n.name === 'EditorViewModel');
  const whole = projection.wholeBlockAllFiles(type.id);
  const path = 'src/Map.Presentation/ViewModels/EditorViewModel.PosterExport.cs';
  const part = projection.thisPartInFile(type.id, path);
  assert.ok(whole.files.length >= 2); assert.ok(whole.members.length > part.members.length);
  assert.ok(part.members.some(n => n.name === 'ExportLayersAsync'));
  assert.ok(part.members.every(n => n.path === path));
  for (const n of graph.nodes.filter(n => n.kind === 'member' && n.parentId === type.id)) {
    assert.equal(model.node(projection.actualFileOf(n.id)).path, n.path);
  }
  const oneMore = whole.files.find(f => f.path !== path);
  assert.ok(projection.thisPartInFile(type.id, oneMore.path).members.every(n => n.path === oneMore.path));
  t.diagnostic(`${whole.files.length} files, ${whole.members.length} direct members; PosterExport: ${part.members.length} members`);
});

map('PNG button -> ExportLayersAsync -> folder -> Back restores exact ID, path and link', () => {
  const entry = graph.views.entryPoints.find(e => e.label === 'Слои отдельными PNG…');
  const handler = entry.handlerIds[0];
  const relation = flatten(projection.linksOf(handler, { direction: 'out' })).find(e => e.kind === 'call' && e.target.name === 'ExportLayersAsync');
  assert.ok(relation);
  const nav = new NavigationState(model, { id: handler, view: 'links', filters: { excludeTags: ['generated'] },
    selectedLink: { source: relation.source.id, target: relation.target.id, kind: relation.kind } });
  const original = nav.state;
  nav.go(relation.target.id, 'related'); const action = nav.state;
  const location = projection.locationOf(nav.selectedId);
  assert.equal(location.line, 47); assert.match(location.file.path, /EditorViewModel\.PosterExport\.cs$/);
  nav.go(location.folders.at(-1).id, 'location');
  nav.back(); assert.deepEqual(nav.state, action);
  nav.back(); assert.deepEqual(nav.state, original); assert.equal(nav.state.filePath, entry.path);
});

map('overlapping features count each physical file once', t => {
  const featureIds = [...model.featuresById.keys()];
  const expected = new Set(); let memberships = 0;
  for (const m of graph.views.memberships) {
    for (const file of projection.filesOf(m.nodeId)) { expected.add(file); memberships++; }
  }
  assert.ok(memberships > expected.size);
  assert.deepEqual(projection.fileSet({ filters: { featureIds } }), expected);
  t.diagnostic(`${memberships} file contributions -> ${expected.size} unique files across ${featureIds.length} features`);
});

map('full links exceed drawing limits and container aggregates retain every source relation', t => {
  const [id, edges] = [...model.relationsOf].filter(([id]) => model.canonicalIds.has(id)).sort((a, b) => b[1].length - a[1].length)[0];
  assert.ok(edges.length > 60);
  assert.equal(flatten(projection.linksOf(id)).length, edges.length);
  const member = graph.nodes.find(n => n.name === 'ExportLayersAsync');
  const file = projection.actualFileOf(member.id), mod = projection.locationOf(member.id).module.id;
  const groups = projection.linksBetween(file, mod, { direction: 'both' });
  assert.ok(flatten(groups).some(e => e.source.id === member.id || e.target.id === member.id));
  assert.ok(groups.every(g => g.relations.every(e => e.kind === g.kind)));
  t.diagnostic(`${edges.length} complete relations for ${model.node(id).name}`);
});

map('removed real member reports its exact path and proposes the surviving folder', () => {
  const member = graph.nodes.find(n => n.name === 'ExportLayersAsync');
  const nav = new NavigationState(model, { id: member.id });
  const old = projection.locationOf(member.id);
  const next = new GraphModel({ ...graph, nodes: graph.nodes.filter(n => n.id !== member.id).map(n => ({ ...n })),
    edges: graph.edges.filter(e => e.source !== member.id && e.target !== member.id) });
  const result = nav.restore(next);
  assert.equal(result.status, 'missing'); assert.equal(result.selectedId, member.id);
  assert.equal(result.path, member.path); assert.equal(result.suggestedFolderId, old.folders.at(-1).id);
  assert.equal(nav.selectedId, member.id);
});

test('ensi: identical route.ts filenames keep distinct source IDs and physical paths', {
  skip: !process.env.PROJMAP_ENSI_GRAPH && 'C:\\Projects\\ensi-bot absent, or PROJMAP_ENSI_GRAPH not supplied',
}, t => {
  const g = JSON.parse(fs.readFileSync(process.env.PROJMAP_ENSI_GRAPH, 'utf8'));
  const m = new GraphModel(g), p = new GraphProjection(m);
  const routes = g.nodes.filter(n => n.kind === 'file' && n.name === 'route.ts');
  assert.ok(routes.length >= 2);
  const [a, b] = routes;
  assert.notEqual(a.id, b.id); assert.notEqual(a.path, b.path);
  const nav = new NavigationState(m, { id: a.id }), before = nav.state;
  nav.go(b.id); assert.equal(nav.state.filePath, b.path); nav.back(); assert.deepEqual(nav.state, before);
  assert.equal(p.locationOf(a.id).file.id, a.id); assert.equal(p.locationOf(b.id).file.id, b.id);
  t.diagnostic(`${routes.length} distinct route.ts files`);
});
