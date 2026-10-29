"""Compare Astra's JSON inventory with a plain projmap graph, without changing the scanned repo.

Usage: python tools/analysis/compare_ui_audit.py graph.json audit.md [-o comparison.json]
Counts are matched inventory rows, not unique callbacks. Shared factory callbacks,
virtual methods and shortcut routers are reported separately from named entry points.
This is an evidence cross-check of a partial inventory, not runtime coverage.
"""
import argparse
import collections
import json
import re
from pathlib import PurePosixPath, Path


def compare(graph, entries):
    nodes = graph['nodes']
    handlers = [n for n in nodes if n.get('subKind') == 'handler']
    incoming = collections.defaultdict(list)
    for edge in graph['edges']:
        if edge['kind'] in ('reference', 'call', 'xamlEvent'):
            incoming[edge['target']].append(edge)

    def path(value):
        # Audit paths are relative to src/Map.App (some explicitly reach ../Map.Presentation).
        parts = []
        for part in ('src/Map.App/' + value).split('/'):
            if part == '..':
                parts.pop()
            else:
                parts.append(part)
        return str(PurePosixPath(*parts))

    results = []
    for entry in entries:
        location = re.fullmatch(r'(.+\.cs):(\d+)', entry['where'])
        source = path(location[1]) if location else None
        line = int(location[2]) if location else None
        specific = []
        shared = []
        # Exact name in the same source file avoids confusing a Ctrl+K target with
        # the actual tab/control that has that target's name in another file.
        for node in handlers:
            if node.get('path') != source:
                continue
            anchor = node.get('anchor', '').split('|')[-1]
            if anchor == entry['id']:
                specific.append(node['id'])
            if entry['kind'] == 'action-search' and line <= node.get('line', 0) <= line + 5:
                title = entry['id'].removeprefix('Ctrl+K/')
                if '«' + title + ' · ' in node['name'] or node['name'].endswith('«' + title + '»'):
                    specific.append(node['id'])
            # Menu labels are not automation ids; exact call-site evidence identifies the item.
            if entry['kind'] == 'menu' and node.get('line') == line:
                specific.append(node['id'])

        # Resolve explicitly cited lambda lines to graph handlers in the cited owner.
        owner = re.search(r'(Map\.[\w.]+)\s*\[', entry['handler'])
        lambda_line = re.search(r'\[(?:λ|callback):(\d+)', entry['handler'])
        if owner and lambda_line:
            qualified = owner[1]
            for node in handlers:
                member = node['id'].split(':M:', 1)[-1].split('#h', 1)[0]
                # Constructors use Roslyn #ctor instead of the repeated type name.
                constructor = qualified.rsplit('.', 1)[0] + '.#ctor'
                cited_line = int(lambda_line[1])
                at_line = node.get('line') == cited_line
                # Some audit timer citations point to the constructor's first line,
                # with the delegate argument on the following line(s).
                if entry['kind'] == 'timer' and node['name'].startswith('new '):
                    at_line |= cited_line <= node.get('line', 0) <= cited_line + 2
                if (member.startswith(qualified + '(') or member == qualified
                        or member.startswith(constructor + '(') or member == constructor) and at_line:
                    shared.append(node['id'])

        qualified = re.match(r'(Map\.[\w.]+)\s+—', entry['handler'])
        if qualified:
            for node in nodes:
                if node.get('kind') != 'member' or node.get('subKind') == 'handler':
                    continue
                member = node['id'].split(':M:', 1)[-1]
                if member == qualified[1] or member.startswith(qualified[1] + '('):
                    if incoming[node['id']] or 'callback' in node.get('tags', []):
                        shared.append(node['id'])

        # Named controls can be represented by XAML event evidence as well.
        for edge in graph['edges']:
            if edge['kind'] == 'xamlEvent' and any('#' + entry['id'] + ' ' in e.get('snippet', '') for e in edge.get('evidence', [])):
                specific.append(edge['target'])

        status = 'identified' if specific else 'shared-wiring' if shared else 'gap'
        results.append(dict(entry, status=status, nodes=sorted(set(specific or shared))))
    totals = []
    for kind in sorted({entry['kind'] for entry in entries}):
        rows = [row for row in results if row['kind'] == kind]
        counts = collections.Counter(row['status'] for row in rows)
        totals.append({'kind': kind, 'astra': len(rows), **{s: counts[s] for s in ('identified', 'shared-wiring', 'gap')}})
    return {'categories': totals, 'entries': results}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('graph')
    parser.add_argument('audit')
    parser.add_argument('-o', '--output')
    args = parser.parse_args()
    graph = json.loads(Path(args.graph).read_text(encoding='utf-8'))
    document = Path(args.audit).read_text(encoding='utf-8')
    entries = json.loads(re.search(r'```json\s*(.*?)```', document, re.S)[1])
    result = compare(graph, entries)
    for row in result['categories']:
        print(row)
    if args.output:
        Path(args.output).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')


if __name__ == '__main__':
    main()
