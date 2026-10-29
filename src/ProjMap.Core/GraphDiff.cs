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
    /// Tier assignments ("tier:&lt;node&gt;"), features and memberships of the optional `views` section.
    public GraphDiffSection Views { get; init; } = new();
    public bool Identical => Nodes.Same && Edges.Same && Roadmap.Same && Database.Same && Views.Same;
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
            var declarations = string.Join(';', (n.Declarations ?? []).Select(d =>
                $"{d.Path}:{(ignorePositions ? null : d.Line)}:{(ignorePositions ? null : d.EndLine)}:{d.Summary}:{(ignorePositions ? null : d.SummaryLine)}"));
            var summarySource = n.SummarySource is { } source ? $"{source.Path}:{(ignorePositions ? null : source.Line)}:{source.Snippet}" : "";
            return $"{n.Kind}|{name}|{n.ParentId}|{n.Path}|{line}|{n.Language}|{n.SubKind}|{n.Anchor}|{n.Summary}|{string.Join(',', n.Tags)}" +
                   $"|{declarations}|{summarySource}|{m.Loc}|{m.Files}|{m.Types}|{m.Members}|{m.Complexity}{git}";
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
            Views = Section(ViewItems(a.Views, ignoreGit, ignorePositions), ViewItems(b.Views, ignoreGit, ignorePositions), x => x.Id, x => x.Content),
        };
    }

    static IEnumerable<(string Id, string Content)> ViewItems(ViewsInfo? v, bool ignoreGit, bool ignorePositions)
    {
        if (v == null) yield break;
        yield return ("ruleSet", v.RuleSet);
        foreach (var t in v.Assignments)
            yield return ("tier:" + t.NodeId, $"{t.Tier}|{t.Decision}|{t.Score:R}|{t.Role}|{t.Execution}|{string.Join(',', t.Alternatives ?? [])}");
        foreach (var f in v.Features) yield return (f.Id, $"{f.Name}|{f.State}|{string.Join(',', f.Terms)}");
        foreach (var m in v.Memberships) yield return ($"member:{m.FeatureId}|{m.NodeId}", $"{m.Role}|{m.Score:R}");
        if (v.Explanations is { } explanations)
        {
            string? ClaimKey(ExplanationClaim claim)
            {
                var sources = claim.SourceIds.Select(i => explanations.Sources[i]).Where(s => !ignoreGit || s.Kind != "git").ToList();
                if (sources.Count == 0) return null;
                // Content hashes cover the full file and can change because an unrelated line moved.
                // Compare meaningful source text/location instead, preserving IgnorePositions semantics.
                return claim.Text + "|" + claim.Confidence + "|" + string.Join(';', sources.Select(s =>
                    $"{s.Kind}:{s.Path}:{(ignorePositions ? null : s.Line)}:{(ignorePositions ? null : s.EndLine)}:{s.CommitId}:{s.RuleId}:{s.Reference}:{s.Text}"));
            }
            foreach (var profile in explanations.Nodes)
                yield return ("explanation:" + profile.NodeId, string.Join('\n', new[] { profile.DisplayName, profile.Description }
                    .Concat(profile.Claims ?? []).Select(ClaimKey).OfType<string>()));
        }
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
