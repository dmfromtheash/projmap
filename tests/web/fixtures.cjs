const { GraphModel } = require('../../src/ProjMap.App/web/model.js');
const { GraphProjection } = require('../../src/ProjMap.App/web/projection.js');
const { NavigationState } = require('../../src/ProjMap.App/web/navigation.js');

function graph() {
  const node = (id, kind, name, parentId, path, extra = {}) => ({ id, kind, name, parentId, path, ...extra });
  return {
    project: { name: 'Demo', root: 'read-only' },
    nodes: [
      node('sys', 'system', 'Demo'),
      node('mod:a', 'module', 'UI', 'sys', 'a'), node('mod:b', 'module', 'Core', 'sys', 'b'),
      node('folder:a', 'folder', 'src', 'mod:a', 'a/src'),
      node('folder:deep', 'folder', 'deep', 'folder:a', 'a/src/deep'),
      node('folder:b', 'folder', 'src', 'mod:b', 'b/src'),
      node('file:a', 'file', 'Editor.cs', 'folder:deep', 'a/src/deep/Editor.cs'),
      node('file:part', 'file', 'Editor.Part.cs', 'folder:deep', 'a/src/deep/Editor.Part.cs'),
      node('file:other', 'file', 'Other.cs', 'folder:deep', 'a/src/deep/Other.cs'),
      node('file:ts1', 'file', 'route.ts', 'folder:a', 'a/src/route.ts', { language: 'TypeScript' }),
      node('file:ts2', 'file', 'route.ts', 'folder:b', 'b/src/route.ts', { language: 'TypeScript' }),
      node('type:editor', 'type', 'Editor', 'file:a', 'a/src/deep/Editor.cs', { subKind: 'class', line: 2,
        declarations: [{ path: 'a/src/deep/Editor.cs', line: 2, endLine: 20 },
          { path: 'a/src/deep/Editor.Part.cs', line: 3, endLine: 30 }] }),
      node('member:open', 'member', 'Open', 'type:editor', 'a/src/deep/Editor.cs', { subKind: 'method', line: 4 }),
      node('member:export', 'member', 'Export', 'type:editor', 'a/src/deep/Editor.Part.cs', { subKind: 'method', line: 7,
        declarations: [{ path: 'a/src/deep/Editor.Part.cs', line: 7, endLine: 12 }] }),
      node('type:nested', 'type', 'Nested', 'type:editor', 'a/src/deep/Editor.Part.cs', { subKind: 'struct', line: 14 }),
      node('member:get', 'member', 'GET', 'file:ts1', 'a/src/route.ts', { subKind: 'function', line: 8 }),
      node('member:get2', 'member', 'GET', 'file:ts2', 'b/src/route.ts', { subKind: 'function', line: 9 }),
      node('group:docs', 'group', 'Docs', 'sys'),
      node('doc:other', 'doc', 'Guide', 'group:docs', 'a/src/deep/Other.cs', { line: 1 }),
    ],
    edges: [
      { id: 'call:open->export', kind: 'call', source: 'member:open', target: 'member:export', weight: 2,
        evidence: [{ path: '', line: 5, snippet: 'Export()' }] },
      { kind: 'reference', source: 'member:export', target: 'member:get2', evidence: [{ path: 'a/src/deep/Editor.Part.cs', line: 9 }] },
      { kind: 'import', source: 'file:ts1', target: 'file:ts2', weight: 1 },
      { kind: 'cochange', source: 'file:ts1', target: 'file:ts2', weight: 3 },
      { kind: 'xamlEvent', source: 'file:a', target: 'member:export', evidence: [{ line: 2 }] },
      { kind: 'binding', source: 'file:a', target: 'member:open' },
      { kind: 'futureKind', source: 'member:open', target: 'member:get2' },
      { kind: 'package', source: 'mod:a', target: 'pkg:React' },
      { kind: 'mentions', source: 'roadmap:task', target: 'member:export' },
    ],
    roadmap: [{ id: 'roadmap:task', title: 'Export task', source: { path: 'PLAN.md', line: 5 } }],
    views: {
      tiers: [{ id: 'frontend', name: 'UI' }],
      assignments: [{ nodeId: 'file:a', tier: 'frontend', role: 'view' },
        { nodeId: 'file:part', tier: 'frontend', role: 'view' }],
      features: [{ id: 'feature:export', name: 'Export' }, { id: 'feature:edit', name: 'Edit' }],
      memberships: [
        { featureId: 'feature:export', nodeId: 'file:part', role: 'implementation' },
        { featureId: 'feature:export', nodeId: 'member:export', role: 'entry' },
        { featureId: 'feature:export', nodeId: 'file:ts2', role: 'implementation' },
        { featureId: 'feature:edit', nodeId: 'file:part', role: 'implementation' },
      ],
      explanations: { nodes: [{ nodeId: 'member:export', displayName: { text: 'Экспорт', confidence: 'derived', sourceIds: [0] } }],
        nameIndex: { 'экспорт': [0], 'export': [0] }, sources: [{ kind: 'declaration', reference: 'self' }] },
      timeline: { files: ['file:a', 'file:part'], commits: [
        { at: '2026-10-27T11:00:00Z', files: [1] }, { at: '2026-10-20T11:00:00Z', files: [0] },
      ] },
    },
  };
}
function setup(g = graph()) { const model = new GraphModel(g); return { model, projection: new GraphProjection(model) }; }
module.exports = { graph, setup, GraphModel, GraphProjection, NavigationState };
