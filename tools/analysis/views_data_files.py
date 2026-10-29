"""File-level data for the aesthetic projmap views: each src file with tier, primary feature, member count,
commits; plus file->file call links between different features (for edge bundling)."""
import json, re, collections, subprocess, sys

g = json.load(open(sys.argv[1], encoding='utf-8'))
d = json.load(open(sys.argv[2], encoding='utf-8'))
nodes = {n['id']: n for n in g['nodes']}
FEATURES = [('save', r'Persistence/'), ('export', r'Export|Poster|Print|MbTiles|Png|Vtt|Obsidian|PlayerPackage'),
            ('atlas', r'Atlas'), ('light', r'Light|Lighting|Fire|Night'), ('roads', r'Road|Bridge|Ford|Crossing'),
            ('rivers', r'River|Coast|Shore|Sea|Water'), ('hex', r'Hex'), ('style', r'Style|Beauty|Painter|Ink|Charcoal'),
            ('assets', r'Asset|Catalog|Stamp|Library'), ('save', r'Save|Autosave|Recovery|Checkpoint|Migration')]

def feature(p):
    for f, pat in FEATURES:
        if re.search(pat, p): return f
    return 'other'

def tier(p):
    m = p.split('/')[1]
    if m == 'Map.App': return 'ui'
    if m == 'Map.Presentation': return 'ui' if '/ViewModels/' in p else 'app'
    if m == 'Map.Core': return 'domain'
    if m in ('Map.Persistence', 'Map.Assets'): return 'data'
    if m == 'Map.Export': return 'app'
    return 'infra'

files = {}
for n in g['nodes']:
    if n['kind'] == 'file' and (n.get('path') or '').startswith('src/') and n['path'].endswith('.cs'):
        p = n['path']
        files[p] = {'p': p, 'n': p.split('/')[-1][:-3], 't': tier(p), 'f': feature(p), 'loc': n['metrics'].get('loc', 0), 'm': 0, 'c': 0}
for n in g['nodes']:
    if n['kind'] == 'member' and n.get('path') in files: files[n['path']]['m'] += 1
log = subprocess.run(['git', '-C', r'C:\Projects\Map', 'log', '--since=2026-08-01', '--name-only', '--format='],
                     capture_output=True).stdout.decode('utf-8', 'replace')
for line in log.splitlines():
    if line in files: files[line]['c'] += 1

pairs = collections.Counter()
for e in g['edges']:
    if e['kind'] != 'call': continue
    a = (nodes.get(e['source']) or {}).get('path'); b = (nodes.get(e['target']) or {}).get('path')
    if a in files and b in files and a != b and files[a]['f'] != files[b]['f']:
        pairs[tuple(sorted((a, b)))] += e.get('weight', 1)
order = sorted(files)
idx = {p: i for i, p in enumerate(order)}
d['files'] = [files[p] for p in order]
d['flinks'] = [[idx[a], idx[b], n] for (a, b), n in pairs.most_common(420)]
open(sys.argv[3], 'w', encoding='utf-8').write(json.dumps(d, ensure_ascii=False, separators=(',', ':')))
print(len(d['files']), 'files', len(d['flinks']), 'links', collections.Counter(f['f'] for f in d['files']))
