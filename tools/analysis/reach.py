"""Reachability over a projmap graph: from the app's entry points, what code in src/ is never reached.

Roots: members/files in entry-point files of the product (src/Map.App/Program.cs), plus members tagged
'callback' once their type is reached. Edges followed: call, reference (source -> target), and for a
reached contract member, its implementations/overrides (source of implement/inherit edges).
Output: JSON with unreached src members grouped by file, and which are reached only from tests/tools.
"""
import json, sys, collections

g = json.load(open(sys.argv[1], encoding='utf-8'))
nodes = {n['id']: n for n in g['nodes']}
out = collections.defaultdict(set)      # call/reference
impl_of = collections.defaultdict(set)  # contract -> implementations
for e in g['edges']:
    k = e['kind']
    if k in ('call', 'reference'):
        out[e['source']].add(e['target'])
    elif k in ('implement', 'inherit') and e['source'].startswith('member:'):
        impl_of[e['target']].add(e['source'])
members_of = collections.defaultdict(list)
for n in g['nodes']:
    if n['kind'] == 'member':
        members_of[n['parentId']].append(n['id'])

def path(i): return (nodes.get(i) or {}).get('path') or ''

def reach(roots):
    seen = set(); stack = list(roots); types = set()
    while stack:
        x = stack.pop()
        if x in seen or x not in nodes: continue
        seen.add(x)
        n = nodes[x]
        nxt = set(out[x]) | impl_of[x]
        # reaching a member or type reaches its type (and its outer types) and the type's framework callbacks
        t = n['parentId'] if n['kind'] == 'member' else (x if n['kind'] == 'type' else None)
        while t and t.startswith('type:') and t not in types:
            types.add(t); nxt.add(t)
            nxt.update(m for m in members_of[t] if 'callback' in nodes[m].get('tags', []))
            t = nodes[t].get('parentId') if t in nodes else None
        # a file-level root (top-level statements) reaches what the file references
        stack.extend(nxt)
    return seen

def roots_in(prefixes):
    r = set()
    for n in g['nodes']:
        p = n.get('path') or ''
        if any(p.startswith(x) for x in prefixes) and n['kind'] in ('member', 'file') and 'entrypoint' in n.get('tags', []):
            r.add(n['id'])
    # file nodes don't own call edges for top-level statements; add every member/type declared there too
    for n in g['nodes']:
        if n['kind'] in ('member', 'type') and n.get('path') in {nodes[i].get('path') for i in r}:
            r.add(n['id'])
    return r

app_roots = roots_in(['src/Map.App/Program.cs'])
app = reach(app_roots)
# Everything in tests/ and tools/ is a root: smokes, perf runners and tests call into product code directly.
dev = reach({n['id'] for n in g['nodes'] if n['kind'] in ('member', 'type')
             and ((n.get('path') or '').startswith(('tests/', 'tools/')) or 'test' in n.get('tags', []))})

src_members = [n for n in g['nodes'] if n['kind'] == 'member' and (n.get('path') or '').startswith('src/')]
unreached_all = [n for n in src_members if n['id'] not in app]
# No repository calls is expected for an action such as Window.Close(). These
# handlers are explicitly classified by the semantic scanner, not dead/empty code.
library_only = [n for n in unreached_all if n.get('subKind') == 'handler' and 'library-only' in n.get('tags', [])]
library_only_ids = {n['id'] for n in library_only}
unreached = [n for n in unreached_all if n['id'] not in library_only_ids]
by_file = collections.defaultdict(list)
for n in unreached:
    by_file[n['path']].append({'id': n['id'], 'name': n['name'], 'sub': n.get('subKind'), 'line': n.get('line'),
                               'devOnly': n['id'] in dev})
report = {
    'appRoots': len(app_roots), 'srcMembers': len(src_members), 'reached': len(src_members) - len(unreached_all),
    'libraryOnlyUnreached': len(library_only),
    'unreached': len(unreached), 'unreachedButUsedByTestsOrTools': sum(1 for n in unreached if n['id'] in dev),
    'files': dict(sorted(by_file.items(), key=lambda kv: -len(kv[1]))),
}
json.dump(report, open(sys.argv[2], 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
print({k: v for k, v in report.items() if k != 'files'})
for f, items in list(report['files'].items())[:25]:
    print(f'{len(items):4} {sum(1 for i in items if not i["devOnly"]):4}  {f}')
