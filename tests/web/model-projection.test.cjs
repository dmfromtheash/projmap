const test = require('node:test');
const assert = require('node:assert/strict');
const { graph, setup, GraphModel } = require('./fixtures.cjs');
const flatten = groups => groups.flatMap(g => g.relations);

test('additive indexes retain declarations, overlapping memberships, sources and all kinds', () => {
  const { model: m } = setup();
  assert.equal(m.declarationsByPath.get('a/src/deep/editor.part.cs').length, 3);
  assert.equal(m.membershipsByNode.get('file:part').length, 2);
  assert.equal(m.edgesByKind.size, 9);
  assert.ok(m.edgesByKind.has('xamlEvent'));
  assert.ok(!m.edges.some(e => e.kind === 'xamlEvent')); // Legacy drawing behavior unchanged.
  assert.equal(m.fileOf('member:export'), 'file:a'); // Legacy public lookup is unchanged.
  assert.equal(m.actualFileOf('member:export'), 'file:part');
  assert.equal(m.fileAtPath('A\\src\\deep\\Editor.Part.cs'), 'file:part');
  assert.deepEqual([...m.nameIndex.get('экспорт')], ['member:export']);
  const sources = m.sourcesFor(m.explanationsById.get('member:export').displayName, 'member:export');
  assert.equal(sources[0].path, 'a/src/deep/Editor.Part.cs');
  assert.equal(sources[0].declarations[0].line, 7);
});

test('location and code routes follow a partial member physical file and skip class for TS functions', () => {
  const { projection: p } = setup();
  const location = p.locationOf('member:export');
  assert.equal(location.module.id, 'mod:a');
  assert.deepEqual(location.folders.map(n => n.id), ['folder:a', 'folder:deep']);
  assert.equal(location.file.id, 'file:part'); assert.equal(location.line, 7);
  assert.deepEqual(p.routeTo('member:export').map(n => n.id), ['sys', 'mod:a', 'folder:a', 'folder:deep', 'file:part', 'type:editor', 'member:export']);
  assert.equal(p.routeTo('member:export').at(-2).path, 'a/src/deep/Editor.Part.cs');
  assert.deepEqual(p.routeTo('member:get').map(n => n.kind), ['system', 'module', 'folder', 'file', 'member']);
  assert.deepEqual(p.routeTo('mod:a').map(n => n.id), ['sys', 'mod:a']);
  assert.equal(p.routeTo('doc:other').at(-1).id, 'doc:other');
});

test('purpose ladder keeps real modules/folders and unique shown/total counts', () => {
  const g = graph(), parents = g.nodes.map(n => [n.id, n.parentId]);
  const { model: m, projection: p } = setup(g);
  assert.ok(p.children('sys', { route: 'purpose' }).some(n => n.kind === 'feature'));
  assert.deepEqual(p.children('feature:export').map(n => n.id).sort(), ['mod:a', 'mod:b']);
  const options = { route: 'purpose', featureId: 'feature:export' };
  const folders = p.children('mod:a', options);
  assert.deepEqual(folders.map(n => n.id), ['folder:a']);
  const deep = p.children('folder:a', options)[0];
  assert.deepEqual([deep.id, deep.shownFiles, deep.totalFiles, deep.partial], ['folder:deep', 1, 3, true]);
  assert.deepEqual(p.children('folder:deep', options).map(n => n.id), ['file:part']);
  assert.equal(p.children('folder:deep').length, 3); // Show whole folder.
  assert.deepEqual([...p.fileSet({ filters: { featureIds: ['feature:export', 'feature:edit'] } })].sort(), ['file:part', 'file:ts2']);
  assert.deepEqual(p.routeTo('member:export', options).map(n => n.kind), ['system', 'feature', 'module', 'folder', 'folder', 'file', 'type', 'member']);
  assert.ok(p.children('role:view').some(n => n.id === 'mod:a'));
  assert.ok(p.children('tier:frontend').some(n => n.id === 'mod:a'));
  assert.deepEqual(g.nodes.map(n => [n.id, n.parentId]), parents);
  assert.equal(m.parent('file:part'), 'folder:deep');
});

test('partial part vs whole block include only the correct declarations and members', () => {
  const { projection: p } = setup();
  const part = p.thisPartInFile('type:editor', 'a/src/deep/Editor.Part.cs');
  const whole = p.wholeBlockAllFiles('type:editor');
  assert.equal(part.declarations.length, 1); assert.equal(whole.declarations.length, 2);
  assert.deepEqual(part.members.map(n => n.id), ['member:export']);
  assert.deepEqual(part.types.map(n => n.id), ['type:nested']);
  assert.equal(whole.members.length, 2); assert.equal(whole.files.length, 2);
  assert.ok(p.children('file:part').some(n => n.id === 'type:editor'));
  assert.deepEqual(p.thisPartInFile('type:editor', 'missing.cs').members, []);
  assert.equal(p.thisPartInFile('type:editor', 'missing.cs').file, null);
});

test('time, file and tag filters constrain all views through the same file set', () => {
  const { model: m, projection: p } = setup();
  m.node('file:part').tags.push('test');
  const timeFilter = { from: '2026-10-26T00:00:00Z', to: '2026-10-28T00:00:00Z' };
  assert.deepEqual([...p.fileSet({ timeFilter })], ['file:part']);
  assert.equal(p.fileSet({ timeFilter, filters: { excludeTags: ['test'] } }).size, 0);
  assert.equal(p.fileSet({ filters: { fileIds: ['file:ts1'] } }).size, 1);
  assert.throws(() => p.fileSet({ timeFilter: { from: 'bad' } }), /interval/);
});

test('full named relation lists retain evidence and individual edge kinds in both directions', () => {
  const { projection: p } = setup();
  const outgoing = flatten(p.linksOf('member:open', { direction: 'out' }));
  const call = outgoing.find(e => e.kind === 'call');
  assert.equal(call.source.name, 'Open'); assert.equal(call.target.name, 'Export');
  assert.equal(call.target.displayName, 'Экспорт');
  assert.deepEqual(call.evidence, [{ path: 'a/src/deep/Editor.cs', line: 5, snippet: 'Export()' }]);
  assert.ok(flatten(p.linksOf('member:export', { direction: 'in' })).some(e => e.kind === 'xamlEvent'));
  assert.equal(flatten(p.linksOf('file:ts2', { direction: 'out' }))[0].kind, 'cochange');
  const groups = p.linksBetween('mod:a', 'mod:b');
  assert.deepEqual(groups.map(g => g.kind), ['cochange', 'futureKind', 'import', 'reference']);
  assert.equal(flatten(groups).length, 4);
  assert.ok(flatten(groups).every(e => !Object.hasOwn(e, 'combinedStrength')));
  assert.equal(flatten(p.linksOf('mod:a')).find(e => e.kind === 'package').target.name, 'pkg:React');
  assert.equal(flatten(p.linksOf('member:export')).find(e => e.kind === 'mentions').source.name, 'Export task');
  assert.throws(() => p.linksOf('file:a', { direction: 'sideways' }), /direction/);
});

test('container aggregates locate partial members in their actual file and expand source relations', () => {
  const { projection: p } = setup();
  const links = flatten(p.linksBetween('file:part', 'mod:b'));
  assert.ok(links.some(e => e.source.id === 'member:export'));
  assert.ok(!flatten(p.linksBetween('file:a', 'mod:b')).some(e => e.source.id === 'member:export'));
  assert.equal(flatten(p.linksBetween('mod:b', 'file:part', { direction: 'in' })).length, links.length);
  assert.ok(flatten(p.linksBetween('feature:export', 'mod:b')).some(e => e.source.id === 'member:export'));
  assert.ok(flatten(p.linksBetween('sys', 'pkg:React')).some(e => e.source.id === 'mod:a'));
});

test('dispatch is a possible code target with direction, evidence and edge-kind filtering', () => {
  const { EDGE_CAT, EDGE_RU } = require('../../src/ProjMap.App/web/model.js');
  const g = graph();
  const evidence = [{ path: 'a/src/deep/Editor.Part.cs', line: 7, snippet: 'Export()' }];
  g.edges.push({ kind: 'dispatch', source: 'member:open', target: 'member:export', weight: 1, evidence });
  const { model: m, projection: p } = setup(g);
  assert.equal(EDGE_CAT.dispatch, 'code');
  assert.equal(EDGE_RU.dispatch, 'возможная реализация');
  assert.equal(m.edgesByKind.get('dispatch').length, 1);
  assert.ok(m.edges.some(e => e.kind === 'dispatch'));
  const outgoing = flatten(p.linksOf('member:open', { direction: 'out' })).filter(e => e.kind === 'dispatch');
  assert.equal(outgoing.length, 1); assert.equal(outgoing[0].target.id, 'member:export');
  assert.deepEqual(outgoing[0].evidence, evidence);
  assert.equal(flatten(p.linksOf('member:export', { direction: 'in' })).filter(e => e.kind === 'dispatch').length, 1);
  assert.ok(m.edges.filter(e => EDGE_CAT[e.kind] === 'code').some(e => e.kind === 'dispatch'));
  assert.ok(!m.edges.filter(e => EDGE_CAT[e.kind] === 'cochange').some(e => e.kind === 'dispatch'));
});

test('drawing limits cannot truncate names or links (150 partners and all evidence)', () => {
  const g = graph();
  for (let i = 0; i < 150; i++) {
    g.nodes.push({ id: `many:${i}`, kind: 'member', name: `LongPartnerName${i}`, parentId: 'file:ts2', path: 'b/src/route.ts' });
    g.edges.push({ kind: 'call', source: 'member:export', target: `many:${i}`, evidence: Array.from({ length: 9 }, (_, line) => ({ line: line + 1 })) });
  }
  const { model: m, projection: p } = setup(g);
  assert.equal(flatten(p.linksOf('member:export', { direction: 'out' })).filter(e => e.kind === 'call').length, 150);
  assert.ok(m.nameIndex.has('longpartnername149'));
  assert.equal(flatten(p.linksOf('member:export')).find(e => e.target.id === 'many:149').evidence.length, 9);
});

test('legacy graphs and compact edges work without optional novice metadata', () => {
  const g = graph(); delete g.views;
  delete g.nodes.find(n => n.id === 'type:editor').declarations;
  const { projection: legacy } = setup(g);
  assert.equal(legacy.actualFileOf('member:export'), 'file:part');
  const compact = graph(); compact.compact = 1; compact.edgeKinds = ['xamlEvent'];
  compact.edges = [[0, compact.nodes.findIndex(n => n.id === 'file:a'), compact.nodes.findIndex(n => n.id === 'member:export'), 2, [['', 42, 'Click']]]];
  const { projection: p } = setup(compact);
  const link = flatten(p.linksOf('file:a'))[0];
  assert.equal(link.kind, 'xamlEvent'); assert.equal(link.evidence[0].line, 42);
  assert.equal(link.evidence[0].path, 'a/src/deep/Editor.cs');
});

test('loose root-file drawing wrapper never enters canonical routes or selections', () => {
  const g = { nodes: [{ id: 's', kind: 'system', name: 'P' }, ...['a', 'b'].map(id => ({ id, kind: 'file', name: id, path: `${id}.txt`, parentId: 's' }))], edges: [] };
  const { model: m, projection: p } = setup(g);
  assert.equal(m.parent('a'), 'virtual:root-files');
  assert.deepEqual(p.routeTo('a').map(n => n.id), ['s', 'a']);
  assert.throws(() => p.routeTo('virtual:root-files'), /canonical/);
});

test('compact explanation text tables preserve profiles, confidence, source references and complete names', () => {
  const g = graph();
  const index = g.nodes.findIndex(n => n.id === 'member:export');
  g.views.explanations = { compact: 1, schemaVersion: 1, language: 'ru', texts: [
    'Экспорт', 'Описание', 'Комментарий', 'declaration', 'self', 'comment', 'a/src/deep/Editor.Part.cs', 'hash',
  ], nodes: [[index, [0, 1, [0]], [1, 2, [1]], [[2, 0, [1]]]]],
  sources: [[3, null, null, null, null, null, null, 4], [5, 6, 7, 12, 7]], nameIndex: { 'экспорт': [0] } };
  const { model: m, projection: p } = setup(g);
  const profile = m.explanationsById.get('member:export');
  assert.equal(profile.displayName.text, 'Экспорт'); assert.equal(profile.displayName.confidence, 'derived');
  assert.equal(profile.description.confidence, 'guessed'); assert.equal(profile.claims[0].confidence, 'known');
  assert.equal(m.sourcesFor(profile.displayName, profile.nodeId)[0].declarations[0].line, 7);
  assert.equal(m.sourcesFor(profile.claims[0], profile.nodeId)[0].sourceHash, 'hash');
  assert.deepEqual([...m.nameIndex.get('экспорт')], ['member:export']);
  assert.equal(flatten(p.linksOf('member:open')).find(e => e.kind === 'call').target.displayName, 'Экспорт');
});

test('duplicate route.ts names retain their complete distinct IDs and paths', () => {
  const { model: m, projection: p } = setup();
  assert.deepEqual([...m.nameIndex.get('route.ts')].sort(), ['file:ts1', 'file:ts2']);
  assert.notEqual(p.locationOf('member:get').path, p.locationOf('member:get2').path);
});
