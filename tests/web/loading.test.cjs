const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { graph } = require('./fixtures.cjs');
const web = path.resolve(__dirname, '../../src/ProjMap.App/web');

test('classic browser scripts expose the same API and perform no DOM work', () => {
  const context = vm.createContext({ input: graph() });
  Object.defineProperty(context, 'document', { get() { throw new Error('DOM access in pure code'); } });
  for (const file of ['glossary.js', 'model.js', 'projection.js', 'navigation.js']) {
    vm.runInContext(fs.readFileSync(path.join(web, file), 'utf8'), context, { filename: file });
  }
  const result = vm.runInContext(`(() => {
    const model = new GraphModel(input), projection = new GraphProjection(model), nav = new NavigationState(model);
    nav.go('member:export');
    return [nav.selectedId, projection.actualFileOf(nav.selectedId), projection.linksOf(nav.selectedId).length];
  })()`, context);
  assert.deepEqual(Array.from(result), ['member:export', 'file:part', 4]);
});

test('index loads model, projection and navigation before views; resource wildcard ships both new files', () => {
  const html = fs.readFileSync(path.join(web, 'index.html'), 'utf8');
  const scripts = [...html.matchAll(/<script src="([^"]+)"/g)].map(m => m[1]);
  assert.ok(scripts.indexOf('model.js') < scripts.indexOf('projection.js'));
  assert.ok(scripts.indexOf('projection.js') < scripts.indexOf('navigation.js'));
  assert.ok(scripts.indexOf('navigation.js') < scripts.indexOf('app.js'));
  const project = fs.readFileSync(path.join(web, '../ProjMap.App.csproj'), 'utf8');
  assert.ok(project.includes('<EmbeddedResource Include="web\\**\\*"'));
});
