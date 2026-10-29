"""Real Map data for the projmap views prototype: tiers x features x months (file counts, commits)."""
import json, re, subprocess, collections, sys

ROOT = r'C:\Projects\Map'
def git(*a): return subprocess.run(['git', '-C', ROOT, *a], capture_output=True).stdout.decode('utf-8', 'replace')

files = [f for f in git('ls-files').splitlines() if f.startswith('src/') and f.endswith(('.cs', '.axaml'))]

def tier(f):
    m = f.split('/')[1]
    if m == 'Map.App': return 'ui'
    if m == 'Map.Presentation':
        return 'ui' if '/ViewModels/' in f else 'app'
    if m == 'Map.Core': return 'domain'
    if m in ('Map.Persistence', 'Map.Assets'): return 'data'
    if m == 'Map.Export': return 'app'
    if m in ('Map.Rendering', 'Map.Platform.Windows'): return 'infra'
    return 'infra'

FEATURES = [
    ('export', 'Экспорт и печать', r'Export|Poster|Print|MbTiles|Png|Vtt|Obsidian|PlayerPackage'),
    ('hex', 'Гексы', r'Hex'),
    ('light', 'Свет', r'Light|Lighting|Fire|Night'),
    ('save', 'Сохранение', r'Persistence/|Save|Autosave|Recovery|Checkpoint|Migration'),
    ('atlas', 'Атлас', r'Atlas'),
    ('roads', 'Дороги и мосты', r'Road|Bridge|Ford|Crossing'),
    ('rivers', 'Реки и берега', r'River|Coast|Shore|Sea|Water'),
    ('assets', 'Каталог ресурсов', r'Asset|Catalog|Stamp|Library'),
    ('style', 'Стили и красота', r'Style|Beauty|Painter|Ink|Charcoal'),
]
feat_of = {}
for f in files:
    for fid, _, pat in FEATURES:
        if re.search(pat, f): feat_of.setdefault(f, []).append(fid)

TIERS = ['ui', 'app', 'domain', 'data', 'infra']
matrix = {t: {fid: 0 for fid, _, _ in FEATURES} | {'_other': 0} for t in TIERS}
for f in files:
    t = tier(f)
    fs = feat_of.get(f) or ['_other']
    for fid in fs: matrix[t][fid] += 1

# commits per month per feature (files touched), first-parent of HEAD
log = git('log', '--first-parent', '--since=2026-08-01', '--name-only', '--format=@@%H %cs %s')
months = collections.defaultdict(lambda: collections.Counter())
month_commits = collections.Counter()
cur = None
for line in log.splitlines():
    if line.startswith('@@'):
        sha, date, *_ = line[2:].split(' ', 2); cur = date[:7]; month_commits[cur] += 1; touched = set()
    elif line.strip() and cur:
        f = line.strip()
        for fid, _, pat in FEATURES:
            if f.startswith('src/') and re.search(pat, f) and (cur, fid, sha) not in touched:
                months[cur][fid] += 1; touched.add((cur, fid, sha))

# roadmap stages with dates from PROGRESS table
prog = open(ROOT + r'\docs\ROADMAP_2026-10_PROGRESS.md', encoding='utf-8').read()
stages = []
for m in re.finditer(r'^\|\s*\d+/44\s*\|\s*(R\d+b?)\s+([^|]+?)\s*\|[^|]*\|[^|]*\|\s*([^|]+?)\s*\|[^|]*\|\s*(\d{4}-\d{2}-\d{2})\s*\|', prog, re.M):
    stages.append({'key': m.group(1), 'name': m.group(2).strip(), 'status': m.group(3).strip(), 'date': m.group(4)})

json.dump({'tiers': TIERS, 'features': [{'id': a, 'name': b} for a, b, _ in FEATURES], 'matrix': matrix,
           'months': {k: dict(v) for k, v in sorted(months.items())}, 'monthCommits': dict(sorted(month_commits.items())),
           'stages': stages, 'fileCount': len(files)}, open(sys.argv[1], 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
print(len(files), 'files'); print(json.dumps(matrix, ensure_ascii=False)); print(dict(sorted(month_commits.items()))); print(len(stages), 'stages', stages[:3])
