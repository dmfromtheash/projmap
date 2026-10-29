namespace ProjMap.Core;

/// <summary>Compares two scans of the same project (two worktrees, two branches, before/after a move) by stable ids.
/// Scan metadata (root, time, timings) is never compared. With <see cref="IgnoreGit"/> git-derived data that depends on
/// the scan date or history window (commit counts, last change, authors, co-change edges) is skipped too, so the
/// result says only whether the code, docs, roadmap and database are the same.</summary>
public sealed record GraphDiffOptions
{
    public bool IgnoreGit { get; init; }
    /// Skips line numbers and edge evidence, so code that only moved inside its file is not reported as changed.
    public bool IgnorePositions { get; init; }
}

public sealed record GraphDiffSection
{
    public List<string> OnlyInA { get; init; } = [];
    public List<string> OnlyInB { get; init; } = [];
    public List<string> Changed { get; init; } = [];
    public bool Same => OnlyInA.Count == 0 && OnlyInB.Count == 0 && Changed.Count == 0;
}

public sealed record GraphDiffResult
{
    public required GraphDiffSection Nodes { get; init; }
    public required GraphDiffSection Edges { get; init; }
    public required GraphDiffSection Roadmap { get; init; }
    public required GraphDiffSection Database { get; init; }
    public bool Identical => Nodes.Same && Edges.Same && Roadmap.Same && Database.Same;
}

public static class GraphDiff
{
    public static GraphDiffResult Compare(ProjectGraph a, ProjectGraph b, GraphDiffOptions? options = null)
    {
        var ignoreGit = options?.IgnoreGit ?? false;
        var ignorePositions = options?.IgnorePositions ?? false;
        string NodeKey(Node n)
        {
            var m = n.Metrics;
            var git = ignoreGit ? "" : $"|{m.Commits}|{m.LastChange:O}|{m.Authors}";
            // The root node is named after the scanned folder, which is scan metadata like Project.Root.
            var name = n.Kind == NodeKinds.System ? "" : n.Name;
            var line = ignorePositions ? null : n.Line;
            return $"{n.Kind}|{name}|{n.ParentId}|{n.Path}|{line}|{n.Language}|{n.SubKind}|{n.Summary}|{string.Join(',', n.Tags)}" +
                   $"|{m.Loc}|{m.Files}|{m.Types}|{m.Members}|{m.Complexity}{git}";
        }
        string EdgeKey(Edge e) => ignorePositions ? $"{e.Weight}"
            : $"{e.Weight}|{e.Strength:R}|{string.Join(';', e.Evidence.Select(x => $"{x.Path}:{x.Line}:{x.Snippet}"))}";
        string RoadmapKey(RoadmapItem r) =>
            $"{r.Title}|{r.Status}|{r.ParentId}|{r.Phase}|{r.Source?.Path}:{(ignorePositions ? null : r.Source?.Line)}|{r.Detail}|{string.Join(',', r.Links)}";
        static string TableKey(DbTable t) =>
            string.Join(';', t.Columns.Select(c => $"{c.Name}:{c.Type}:{c.PrimaryKey}:{c.NotNull}")) + "|" +
            string.Join(';', t.ForeignKeys.Select(f => $"{f.Column}>{f.RefTable}.{f.RefColumn}"));
        bool KeepEdge(Edge e) => !(ignoreGit && e.Kind == EdgeKinds.CoChange);
        return new()
        {
            Nodes = Section(a.Nodes, b.Nodes, n => n.Id, NodeKey),
            Edges = Section(a.Edges.Where(KeepEdge), b.Edges.Where(KeepEdge), e => e.Id, EdgeKey),
            Roadmap = Section(a.Roadmap, b.Roadmap, r => r.Id, RoadmapKey),
            Database = Section(a.Database?.Tables ?? [], b.Database?.Tables ?? [], t => t.Name, TableKey),
        };
    }

    static GraphDiffSection Section<T>(IEnumerable<T> a, IEnumerable<T> b, Func<T, string> id, Func<T, string> content)
    {
        // Ids are unique within one scan; if a scanner ever emits a duplicate, the last one wins on both sides alike.
        var left = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var x in a) left[id(x)] = content(x);
        var right = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var x in b) right[id(x)] = content(x);
        var section = new GraphDiffSection();
        foreach (var (key, value) in left)
            if (!right.TryGetValue(key, out var other)) section.OnlyInA.Add(key);
            else if (!string.Equals(value, other, StringComparison.Ordinal)) section.Changed.Add(key);
        section.OnlyInB.AddRange(right.Keys.Where(k => !left.ContainsKey(k)));
        section.OnlyInA.Sort(StringComparer.Ordinal); section.OnlyInB.Sort(StringComparer.Ordinal); section.Changed.Sort(StringComparer.Ordinal);
        return section;
    }
}
