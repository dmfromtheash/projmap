using System.Globalization;
using ProjMap.Core;

namespace ProjMap.Analysis;

/// <summary>Pure graph diagnostics. Never reads or writes the scanned repository.</summary>
public static class ProblemsAnalyzer
{
    private static bool IsCode(Edge e) => e.Kind is EdgeKinds.Call or EdgeKinds.Reference or
        EdgeKinds.Inherit or EdgeKinds.Implement or EdgeKinds.XamlEvent or EdgeKinds.Binding;

    public static ProblemsInfo Analyze(ProjectGraph graph, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result = new ProblemsInfo();
        var nodes = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var files = graph.Nodes.Where(n => n.Kind == NodeKinds.File && n.Path != null)
            .ToDictionary(n => n.Path!, StringComparer.OrdinalIgnoreCase);
        Node? FileOf(string id)
        {
            if (!nodes.TryGetValue(id, out var node)) return null;
            if (node.Kind == NodeKinds.File) return node;
            // A partial member's actual path wins over the canonical parent type's path.
            if (node.Path != null && files.TryGetValue(node.Path, out var file)) return file;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (node.ParentId != null && seen.Add(node.Id) && nodes.TryGetValue(node.ParentId, out node))
                if (node.Kind == NodeKinds.File) return node;
            return null;
        }
        var code = graph.Edges.Where(IsCode).OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
        var incoming = code.Select(e => e.Target).ToHashSet(StringComparer.Ordinal);
        var outgoing = code.ToLookup(e => e.Source, e => e.Target, StringComparer.Ordinal);
        var implementations = code.Where(e => e.Kind is EdgeKinds.Inherit or EdgeKinds.Implement &&
                nodes.TryGetValue(e.Source, out var source) && source.Kind == NodeKinds.Member)
            .ToLookup(e => e.Target, e => e.Source, StringComparer.Ordinal);
        var members = graph.Nodes.Where(n => n.Kind == NodeKinds.Member && n.ParentId != null)
            .ToLookup(n => n.ParentId!, n => n, StringComparer.Ordinal);

        bool Excluded(Node n)
        {
            if (n.SubKind == "handler" || n.Name is "Main" or "Program") return true;
            // Ancestor tags include test-project classification even for hand-built/older graphs.
            var current = n;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                if (current.Tags.Any(t => t is "callback" or "interop" or "handler" or "library-only" or
                    "not-shown" or "entrypoint" or "test" or "generated" or "tool" or "tools")) return true;
            } while (seen.Add(current.Id) && current.ParentId != null && nodes.TryGetValue(current.ParentId, out current));
            var file = FileOf(n.Id);
            if (file?.Tags.Any(t => t is "entrypoint" or "test" or "generated") == true) return true;
            return (n.Path ?? file?.Path ?? "").Split('/').Any(p =>
                p.Equals("tools", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("tests", StringComparison.OrdinalIgnoreCase));
        }

        // Port reach.py's parent-type promotion, reached-type callbacks and reverse contract dispatch.
        // Every excluded entry is a conservative root, including tests/tools and library-facing hooks.
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(graph.Nodes.Where(Excluded).Select(n => n.Id));
        while (pending.TryPop(out var id))
        {
            ct.ThrowIfCancellationRequested();
            if (!nodes.TryGetValue(id, out var node) || !reached.Add(id)) continue;
            foreach (var target in outgoing[id]) pending.Push(target);
            foreach (var implementation in implementations[id]) pending.Push(implementation);
            if (node.ParentId != null && nodes.TryGetValue(node.ParentId, out var parent) && parent.Kind == NodeKinds.Type)
                pending.Push(parent.Id);
            if (node.Kind == NodeKinds.Type)
                foreach (var member in members[id].Where(m => m.Tags.Contains("callback"))) pending.Push(member.Id);
        }
        // A used member also demonstrates use of its containing types, even without an app root.
        var usedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in incoming)
        {
            if (!nodes.TryGetValue(id, out var node)) continue;
            while (node.ParentId != null && nodes.TryGetValue(node.ParentId, out node) && node.Kind == NodeKinds.Type)
                if (!usedTypes.Add(node.Id)) break;
        }
        var dead = graph.Nodes.Where(n => n.Kind is NodeKinds.Member or NodeKinds.Type &&
                !incoming.Contains(n.Id) && !reached.Contains(n.Id) && !usedTypes.Contains(n.Id) && !Excluded(n))
            .OrderBy(n => n.Id, StringComparer.Ordinal).Select(n => new ProblemItem
            {
                Kind = ProblemKinds.DeadCode,
                NodeIds = [n.Id],
                Severity = "warn",
                Reason = $"Кандидат на мёртвый код: {(n.Kind == NodeKinds.Type ? "тип" : "член")} «{n.Name}» без входящих связей кода и без пути от известных точек входа."
            }).ToList();
        Add(ProblemKinds.DeadCode, dead);

        var moduleIds = graph.Nodes.Where(n => n.Kind == NodeKinds.Module).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        AddCycles(ProblemKinds.ModuleCycle, moduleIds, graph.Edges.Where(e => e.Kind == EdgeKinds.ProjectRef)
            .Select(e => (e.Source, e.Target)), "модулей");
        var fileEdges = new List<(Edge Edge, Node Source, Node Target)>();
        foreach (var edge in graph.Edges.Where(e => IsCode(e) || e.Kind == EdgeKinds.Import).OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var source = FileOf(edge.Source); var target = FileOf(edge.Target);
            if (source != null && target != null && source.Id != target.Id) fileEdges.Add((edge, source, target));
        }
        AddCycles(ProblemKinds.FileCycle, files.Values.Select(f => f.Id).ToHashSet(StringComparer.Ordinal),
            fileEdges.Select(e => (e.Source.Id, e.Target.Id)), "файлов");

        var tiers = graph.Views?.Assignments.ToDictionary(a => a.NodeId, StringComparer.Ordinal) ?? [];
        var violations = new Dictionary<(string Rule, string Source, string Target), List<Edge>>();
        foreach (var (edge, source, target) in fileEdges)
        {
            ct.ThrowIfCancellationRequested();
            if (!tiers.TryGetValue(source.Id, out var from) || !tiers.TryGetValue(target.Id, out var to)) continue;
            List<string> rules = [];
            if (from.Tier == Tiers.Domain && to.Tier is Tiers.Frontend or Tiers.Application or Tiers.Backend)
                rules.Add("domain-dependency");
            if (from.Tier == Tiers.Data && to.Tier == Tiers.Frontend) rules.Add("data-frontend");
            if (from.Tier != Tiers.Tests && to.Tier == Tiers.Tests && !source.Tags.Contains("test"))
                rules.Add("production-tests");
            foreach (var rule in rules)
            {
                var key = (rule, source.Id, target.Id);
                if (!violations.TryGetValue(key, out var edges)) violations[key] = edges = [];
                edges.Add(edge with
                {
                    Evidence = edge.Evidence.Select(e => e.Path.Length == 0
                        ? e with { Path = nodes.GetValueOrDefault(edge.Source)?.Path ?? source.Path! } : e)
                        .OrderBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => e.Line)
                        .ThenBy(e => e.Snippet, StringComparer.Ordinal).ToList()
                });
            }
        }
        Add(ProblemKinds.TierViolation, violations.OrderBy(v => v.Key.Rule, StringComparer.Ordinal)
            .ThenBy(v => v.Key.Source, StringComparer.Ordinal).ThenBy(v => v.Key.Target, StringComparer.Ordinal)
            .Select(v =>
            {
                // a violation between guessed tiers is a question about the guess, not a finding: info, «возможно»
                static bool Sure(TierAssignment a) => a.Decision is TierDecisions.Automatic or TierDecisions.Override;
                var sure = Sure(tiers[v.Key.Source]) && Sure(tiers[v.Key.Target]);
                string Name(string tier) => ViewsScanner.TierList.FirstOrDefault(t => t.Id == tier)?.Name ?? tier;
                var reason = v.Key.Rule switch
                {
                    "domain-dependency" => $"«Правила и модель» зависят от яруса «{Name(tiers[v.Key.Target].Tier)}»",
                    "data-frontend" => "«Данные и хранение» зависят от интерфейса",
                    _ => "Код программы зависит от тестов"
                };
                return new ProblemItem
                {
                    Kind = ProblemKinds.TierViolation,
                    NodeIds = [v.Key.Source, v.Key.Target],
                    Severity = sure ? "warn" : "info",
                    Rule = v.Key.Rule,
                    Edges = v.Value,
                    Reason = sure ? reason : "Возможно (ярус угадан по имени): " + char.ToLowerInvariant(reason[0]) + reason[1..]
                };
            }).OrderBy(p => p.Severity == "warn" ? 0 : 1).ToList());
        var hot = graph.Nodes.Where(n => n.Kind == NodeKinds.File && n.Metrics.Commits > 0 && n.Metrics.Complexity > 0)
            .Select(n => (Node: n, Score: (long)n.Metrics.Commits * n.Metrics.Complexity))
            .OrderByDescending(x => x.Score).ThenBy(x => x.Node.Id, StringComparer.Ordinal).ToList();
        Add(ProblemKinds.HotFile, hot.Take(result.HotFileLimit).Select(x => new ProblemItem
        {
            Kind = ProblemKinds.HotFile,
            NodeIds = [x.Node.Id],
            Severity = "info",
            Score = x.Score,
            Reason = string.Create(CultureInfo.InvariantCulture,
                $"Часто меняется сложный файл: {x.Node.Metrics.Commits} коммитов × сложность {x.Node.Metrics.Complexity} = {x.Score}.")
        }).ToList(), hot.Count);
        return result;

        void Add(string kind, List<ProblemItem> items, int? total = null)
        {
            result.Counts[kind] = total ?? items.Count;
            result.Items.AddRange(items);
        }
        void AddCycles(string kind, HashSet<string> ids, IEnumerable<(string Source, string Target)> edges, string label)
        {
            var components = Components(ids, edges, ct).Where(c => c.Count > 1)
                .OrderByDescending(c => c.Count).ThenBy(c => c[0], StringComparer.Ordinal).ToList();
            Add(kind, components.Take(result.CycleLimit).Select(c => new ProblemItem
            {
                Kind = kind,
                NodeIds = c,
                Severity = "warn",
                Reason = $"Циклическая зависимость: группа из {c.Count.ToString(CultureInfo.InvariantCulture)} {label}, каждый достижим из каждого."
            }).ToList(), components.Count);
        }
    }

    // Iterative Kosaraju: large source-file components must not overflow the process stack.
    private static List<List<string>> Components(HashSet<string> ids,
        IEnumerable<(string Source, string Target)> edges, CancellationToken ct)
    {
        var adjacency = ids.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var reverse = ids.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var (source, target) in edges)
        {
            ct.ThrowIfCancellationRequested();
            if (source == target || !ids.Contains(source) || !ids.Contains(target)) continue;
            adjacency[source].Add(target); reverse[target].Add(source);
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();
        var stack = new Stack<(string Id, bool Exit)>();
        foreach (var root in ids.Order(StringComparer.Ordinal))
        {
            stack.Push((root, false));
            while (stack.TryPop(out var next))
            {
                ct.ThrowIfCancellationRequested();
                if (next.Exit) { order.Add(next.Id); continue; }
                if (!seen.Add(next.Id)) continue;
                stack.Push((next.Id, true));
                foreach (var target in adjacency[next.Id].OrderDescending(StringComparer.Ordinal)) stack.Push((target, false));
            }
        }
        seen.Clear();
        var components = new List<List<string>>();
        foreach (var root in order.AsEnumerable().Reverse())
        {
            if (!seen.Add(root)) continue;
            var component = new List<string>();
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out var id))
            {
                ct.ThrowIfCancellationRequested();
                component.Add(id);
                foreach (var target in reverse[id]) if (seen.Add(target)) pending.Push(target);
            }
            component.Sort(StringComparer.Ordinal);
            components.Add(component);
        }
        return components;
    }
}
