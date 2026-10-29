using System.Security.Cryptography;
using System.Text;
using ProjMap.Core;

namespace ProjMap.Analysis;

/// <summary>Opaque in-memory cache for a repository and scan options. Does not write to the repository.
/// Treat a state as immutable; cancelled scans never publish a replacement state.</summary>
public sealed class ScanState
{
    internal ScanState(ScanOptions options) => Options = options;
    internal ScanOptions Options { get; }
    internal Dictionary<string, string> Hashes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    internal CSharpScanner.State? CSharp { get; set; }
    internal Dictionary<string, StageOutput> Stages { get; } = new(StringComparer.Ordinal);
    internal GitCache? Git { get; set; }
    internal bool Reusable { get; set; }

    internal bool Matches(ScanOptions options) => Reusable &&
        Options.Root.Equals(options.Root, StringComparison.OrdinalIgnoreCase) &&
        Options.GitMonths == options.GitMonths && Options.MaxCommits == options.MaxCommits &&
        Options.IncludeMembers == options.IncludeMembers && Options.IncludeTests == options.IncludeTests;

    internal static string Hash(RepoFile file, ScanContext context)
    {
        if (file.Language != null && file.Text(context) is { } text)
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        var info = new FileInfo(file.FullPath);
        // Only SQLite binaries have content-dependent graph data. Other binaries only have a file node.
        if (info.Exists &&
            Path.GetExtension(file.Path).ToLowerInvariant() is ".db" or ".sqlite" or ".sqlite3")
        {
            using var stream = File.OpenRead(file.FullPath);
            return Convert.ToHexString(SHA256.HashData(stream)) + ":wal=" + File.Exists(file.FullPath + "-wal");
        }
        return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "missing";
    }
}

public sealed record ScanResult(ProjectGraph Graph, ScanState State);

internal sealed record StageOutput(List<Node> Nodes, List<Edge> Edges, List<RoadmapItem> Roadmap, DatabaseInfo? Database)
{
    public static StageOutput Capture(ScanContext c, string stage) => new(
        c.Graph.Nodes.Values.Where(n => stage switch
        {
            "imports" => n.Language is "TypeScript" or "JavaScript" or "Python" && n.Kind is NodeKinds.Type or NodeKinds.Member,
            "roadmap" => n.Kind == NodeKinds.Doc || n.Id is "group:docs" or "group:roadmap",
            "database" => n.Kind == NodeKinds.Table || n.Id == "group:database",
            // Capture the full immutable node, including SummarySource. Explanations/entries are
            // assembled after cached stages and must never be replayed from a previous views result.
            "descriptions" => n.Kind is NodeKinds.Module or NodeKinds.Folder && n.Summary != null,
            _ => false
        }).ToList(),
        c.Graph.Edges.Values.Where(e => stage switch
        {
            "imports" => e.Kind == EdgeKinds.Import,
            "roadmap" => e.Kind == EdgeKinds.Mentions,
            "database" => e.Kind is EdgeKinds.DbRead or EdgeKinds.DbWrite,
            _ => false
        }).ToList(), stage == "roadmap" ? [.. c.Graph.Roadmap] : [], stage == "database" ? c.Graph.Database : null);

    public void Apply(ScanContext c, string stage)
    {
        foreach (var node in Nodes) c.Graph.Nodes[node.Id] = node;
        foreach (var edge in Edges) c.Graph.Edges[edge.Id] = edge;
        if (stage == "roadmap") c.Graph.Roadmap.AddRange(Roadmap);
        if (stage == "database") c.Graph.Database = Database;
    }
}

internal sealed record GitCache(string Fingerprint, string? Head, string? Branch, List<GitCommit> Commits,
    Dictionary<string, NodeMetrics> Metrics, List<Edge> Edges, DateTimeOffset ExpiresAt)
{
    public static GitCache Capture(ScanContext c, string fingerprint) => new(fingerprint, c.Graph.Project.GitHead,
        c.Graph.Project.GitBranch, [.. c.Commits], c.Graph.Nodes.Values.Where(n => n.Kind == NodeKinds.File)
            .ToDictionary(n => n.Id, n => n.Metrics, StringComparer.Ordinal),
        c.Graph.Edges.Values.Where(e => e.Kind == EdgeKinds.CoChange).ToList(),
        c.Commits.Select(x => x.CommittedAt.AddMonths(c.Options.GitMonths).AddDays(-3))
            .DefaultIfEmpty(DateTimeOffset.MaxValue).Min());

    public void Apply(ScanContext c)
    {
        c.Graph.Project = c.Graph.Project with { GitHead = Head, GitBranch = Branch };
        c.Commits.AddRange(Commits);
        foreach (var (id, old) in Metrics)
            if (c.Graph.Nodes.TryGetValue(id, out var node))
                c.Graph.Nodes[id] = node with { Metrics = node.Metrics with { Commits = old.Commits,
                    Authors = old.Authors, LastChange = old.LastChange } };
        foreach (var edge in Edges) c.Graph.Edges[edge.Id] = edge;
    }

    public static string FingerprintFor(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\0"));
        var directory = new DirectoryInfo(root);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, ".git")) && !File.Exists(Path.Combine(directory.FullName, ".git"))) directory = directory.Parent;
        if (directory == null) return "no-git";
        var git = Path.Combine(directory.FullName, ".git");
        if (File.Exists(git))
        {
            var pointer = File.ReadAllText(git).Trim(); Append(pointer);
            if (!pointer.StartsWith("gitdir: ", StringComparison.Ordinal)) return pointer;
            git = Path.GetFullPath(Path.Combine(directory.FullName, pointer[8..]));
        }
        var commonFile = Path.Combine(git, "commondir");
        var common = File.Exists(commonFile) ? Path.GetFullPath(Path.Combine(git, File.ReadAllText(commonFile).Trim())) : git;
        foreach (var dir in new[] { git, common }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var name in new[] { "HEAD", "packed-refs", "config", "config.worktree", "shallow", "info/grafts", "objects/info/alternates" })
            {
                var path = Path.Combine(dir, name);
                Append(path);
                if (File.Exists(path)) hash.AppendData(File.ReadAllBytes(path));
            }
            var refs = Path.Combine(dir, "refs");
            if (Directory.Exists(refs))
                foreach (var path in Directory.EnumerateFiles(refs, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                { Append(path); hash.AppendData(File.ReadAllBytes(path)); }
        }
        var mailmap = Path.Combine(directory.FullName, ".mailmap");
        if (File.Exists(mailmap)) hash.AppendData(File.ReadAllBytes(mailmap));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
