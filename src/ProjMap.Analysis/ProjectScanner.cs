using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ProjMap.Core;

namespace ProjMap.Analysis;

public sealed record ScanOptions(string Root)
{
    public int GitMonths { get; init; } = 12;
    public int MaxCommits { get; init; } = 3000;
    public bool IncludeMembers { get; init; } = true;
    public bool IncludeTests { get; init; } = true;
    public IProgress<ScanProgress>? Progress { get; init; }
}

public sealed record ScanProgress(string Stage, double Fraction, string? Detail = null);

public static class ProjectScanner
{
    public static async Task<ProjectGraph> ScanAsync(ScanOptions options, CancellationToken ct = default)
        => (await ScanAsync(options, null, null, ct).ConfigureAwait(false)).Graph;

    /// <summary>Rescans with immutable in-memory state. Explicit changed paths force analysis even
    /// when content is unchanged. Hashes also detect edits missing from the watcher event list.</summary>
    public static async Task<ScanResult> ScanAsync(ScanOptions options, ScanState? previousState,
        IEnumerable<string>? changedPaths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Root);
        if (options.GitMonths < 0 || options.MaxCommits < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Git limits must be non-negative.");
        var root = Path.GetFullPath(options.Root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var watch = Stopwatch.StartNew();
        options = options with { Root = root };
        if (previousState?.Matches(options) != true) previousState = null;
        var context = new ScanContext(options) { Previous = previousState };
        context.ForcedPaths.UnionWith((changedPaths ?? []).Select(p => context.Relative(Path.IsPathRooted(p) ? p : context.Full(p))));
        var state = new ScanState(options);
        context.State = state;
        var sameContent = false;
        var samePaths = false;
        var sameGit = false;
        IRepositoryScanner[] scanners = [new StructureScanner(), new CSharpScanner(), new ImportScanner(),
            new GitScanner(), new RoadmapScanner(), new DatabaseScanner(), new DescriptionScanner()];
        for (var i = 0; i < scanners.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var scanner = scanners[i];
            options.Progress?.Report(new(scanner.Name, (double)i / scanners.Length));
            using var timing = context.Measure(scanner.Name);
            try
            {
                if (scanner.Name is "imports" or "roadmap" or "database" or "descriptions" && sameContent &&
                    (scanner.Name != "roadmap" || sameGit) &&
                    previousState!.Stages.TryGetValue(scanner.Name, out var cached))
                    cached.Apply(context, scanner.Name);
                else if (scanner.Name == "git")
                {
                    var fingerprint = context.IsGit ? GitCache.FingerprintFor(root) : "no-git";
                    if (samePaths && previousState?.Git is { } git && git.Fingerprint == fingerprint && DateTimeOffset.UtcNow < git.ExpiresAt)
                    { git.Apply(context); sameGit = true; }
                    else await Task.Run(() => scanner.ScanAsync(context, ct), ct).ConfigureAwait(false);
                    state.Git = GitCache.Capture(context, fingerprint);
                }
                else await Task.Run(() => scanner.ScanAsync(context, ct), ct).ConfigureAwait(false);
                if (scanner.Name == "structure")
                {
                    foreach (var file in context.Files.Values)
                    {
                        ct.ThrowIfCancellationRequested();
                        state.Hashes[file.Path] = ScanState.Hash(file, context);
                    }
                    // Lookup follows Windows casing, but graph ids retain exact path spelling.
                    samePaths = previousState != null && state.Hashes.Count == previousState.Hashes.Count &&
                        state.Hashes.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(previousState.Hashes.Keys);
                    sameContent = samePaths && state.Hashes.All(p => previousState!.Hashes[p.Key] == p.Value);
                }
                if (scanner.Name is "imports" or "roadmap" or "database" or "descriptions")
                    state.Stages[scanner.Name] = StageOutput.Capture(context, scanner.Name);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { context.Warn(scanner.Name, ex.Message); }
        }
        ProjectGraph graph;
        using (context.Measure("finalize"))
        {
            context.Finish();
            graph = context.Graph.ToGraph();
        }
        graph = graph with { Project = graph.Project with { ScanMilliseconds = watch.ElapsedMilliseconds,
            Timings = new() { Milliseconds = context.Timings.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value), Counts = context.Counts } } };
        state.Reusable = graph.Warnings.Count == 0;
        options.Progress?.Report(new("complete", 1, $"{context.Graph.Nodes.Count} nodes"));
        return new(graph, state);
    }
}

internal interface IRepositoryScanner
{
    string Name { get; }
    Task ScanAsync(ScanContext context, CancellationToken ct);
}

// Mutable assembly state stays internal; public graph records remain unchanged.
internal sealed class GraphBuilder(ProjectInfo project)
{
    public ProjectInfo Project { get; set; } = project;
    public ConcurrentDictionary<string, Node> Nodes { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, Edge> Edges { get; } = new(StringComparer.Ordinal);
    public List<RoadmapItem> Roadmap { get; } = [];
    public ConcurrentQueue<ScanWarning> Warnings { get; } = new();
    public DatabaseInfo? Database { get; set; }
    public ProjectGraph ToGraph() => new()
    {
        Project = Project,
        Nodes = Nodes.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToList(),
        Edges = Edges.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToList(),
        Roadmap = Roadmap.OrderBy(x => x.Source?.Path, StringComparer.Ordinal).ThenBy(x => x.Source?.Line).ToList(),
        Warnings = Warnings.OrderBy(x => x.Scanner).ThenBy(x => x.Path).ThenBy(x => x.Message).ToList(),
        Database = Database
    };
}

internal sealed class ScanContext
{
    public ConcurrentDictionary<string, double> Timings { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);
    public ScanState? Previous { get; init; }
    public ScanState State { get; set; } = null!;
    public HashSet<string> ForcedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IDisposable Measure(string name) => new Measurement(this, name);
    private sealed class Measurement(ScanContext context, string name) : IDisposable
    {
        private readonly long start = Stopwatch.GetTimestamp();
        public void Dispose()
        {
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            context.Timings.AddOrUpdate(name, elapsed, (_, old) => old + elapsed);
        }
    }
    public ScanOptions Options { get; }
    public GraphBuilder Graph { get; }
    public Dictionary<string, RepoFile> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<RepoModule> Modules { get; } = [];
    public List<GitCommit> Commits { get; } = [];
    public bool IsGit { get; set; }
    public ScanContext(ScanOptions options)
    {
        Options = options;
        Graph = new(new ProjectInfo
        {
            Name = new DirectoryInfo(options.Root).Name,
            Root = options.Root,
            ScannedAt = DateTimeOffset.UtcNow
        });
    }
    public static string Rel(string path) => path.Replace('\\', '/');
    public string Full(string path) => Path.GetFullPath(Path.Combine(Options.Root, path));
    public string Relative(string path) => Rel(Path.GetRelativePath(Options.Root, path));
    public static string FileId(string path) => "file:" + path;
    public RepoModule? ModuleFor(string path) => Modules.Where(m => m.Directory.Length == 0 ||
        path.StartsWith(m.Directory + "/", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(m => m.Directory.Length).ThenBy(m => m.Manifest, StringComparer.Ordinal).FirstOrDefault();
    public void Warn(string scanner, string message, string? path = null) => Graph.Warnings.Enqueue(new(scanner, message, path));
    public void Add(Node node) => Graph.Nodes.TryAdd(node.Id, node);
    public void AddDeclared(Node node) => Graph.Nodes.AddOrUpdate(node.Id, node, (_, old) =>
        StringComparer.Ordinal.Compare(node.Path, old.Path) < 0 || node.Path == old.Path && node.Line < old.Line ? node : old);
    public void Merge(Edge edge) => Graph.Edges.AddOrUpdate(edge.Id, edge, (_, old) => old with
    {
        Weight = old.Weight + edge.Weight,
        Evidence = old.Evidence.Concat(edge.Evidence).Distinct().OrderBy(e => e.Path, StringComparer.Ordinal)
            .ThenBy(e => e.Line).Take(3).ToList()
    });
    public void Edge(string kind, string source, string target, SourceLocation? evidence = null, int weight = 1)
    {
        if (source == target) return;
        var id = $"{kind}:{source}->{target}";
        Graph.Edges.AddOrUpdate(id,
            _ => new Edge
            {
                Id = id,
                Kind = kind,
                Source = source,
                Target = target,
                Weight = weight,
                Evidence = evidence is null ? [] : [evidence]
            },
            (_, old) => old with
            {
                Weight = old.Weight + weight,
                Evidence = old.Evidence
                .Concat(evidence is null ? [] : new[] { evidence }).Distinct().OrderBy(e => e.Path, StringComparer.Ordinal)
                .ThenBy(e => e.Line).Take(3).ToList()
            });
    }
    public void Group(string name) => Add(new Node
    {
        Id = "group:" + name.ToLowerInvariant(),
        Kind = NodeKinds.Group,
        Name = name,
        ParentId = "system:root"
    });
    public void Finish()
    {
        foreach (var kind in Graph.Edges.Values.GroupBy(e => e.Kind))
        {
            var max = kind.Max(e => e.Weight);
            foreach (var edge in kind)
            {
                // the example lines are almost always in the source's own file: don't repeat the path
                // (function-level edges number in the hundred thousands; this halves graph.json)
                var srcPath = Graph.Nodes.TryGetValue(edge.Source, out var src) ? src.Path : null;
                var evidence = srcPath == null ? edge.Evidence
                    : edge.Evidence.Select(v => v.Path == srcPath ? v with { Path = "" } : v).ToList();
                Graph.Edges[edge.Id] = edge with { Strength = (double)edge.Weight / max, Evidence = evidence };
            }
        }
        // Propagate metrics through the tree once. Doc nodes are aliases of files and carry no LOC/files.
        var children = Graph.Nodes.Values.Where(n => n.ParentId != null).ToLookup(n => n.ParentId!);
        NodeMetrics Aggregate(Node n)
        {
            var parts = children[n.Id].Select(c => Aggregate(c)).ToList();
            var own = n.Metrics;
            var aggregateCode = n.Kind is NodeKinds.System or NodeKinds.Module or NodeKinds.Folder or NodeKinds.Group;
            var result = own with
            {
                Loc = aggregateCode ? own.Loc + parts.Sum(x => x.Loc) : own.Loc,
                Files = own.Files + parts.Sum(x => x.Files),
                Types = own.Types + parts.Sum(x => x.Types),
                Members = own.Members + parts.Sum(x => x.Members),
                Complexity = own.Complexity + parts.Sum(x => x.Complexity)
            };
            Graph.Nodes[n.Id] = n with { Metrics = result };
            return result;
        }
        if (Graph.Nodes.TryGetValue("system:root", out var system)) Aggregate(system);
        Graph.Project = Graph.Project with
        {
            Languages = Files.Values.Select(f => f.Language).OfType<string>()
            .Distinct().Order(StringComparer.Ordinal).ToList()
        };
    }
}

internal sealed record RepoModule(string Id, string Name, string Directory, string Manifest, string Language)
{
    public List<string> References { get; } = [];
    public List<string> Packages { get; } = [];
    public bool ImplicitUsings { get; set; }
    public List<string> Defines { get; } = [];
}

internal sealed class RepoFile(string path, string fullPath, string? language, List<string> tags)
{
    private string? text;
    private bool loaded;
    public string Path { get; } = path;
    public string FullPath { get; } = fullPath;
    public string? Language { get; } = language;
    public List<string> Tags { get; } = tags;
    public string? Text(ScanContext context)
    {
        if (loaded) return text;
        loaded = true;
        try
        {
            if (new FileInfo(FullPath).Length > 16 * 1024 * 1024)
            { context.Warn("structure", "Text file exceeds 16 MiB; content analysis skipped.", Path); return null; }
            var value = File.ReadAllText(FullPath, Encoding.UTF8);
            if (!value.Contains('\0')) text = value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { context.Warn("structure", ex.Message, Path); }
        return text;
    }
}

internal sealed record GitCommit(string Hash, string Subject, string Author, DateTimeOffset Date, List<string> Files, DateTimeOffset CommittedAt);

internal static class GitProcess
{
    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(string root, CancellationToken ct, params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.ArgumentList.Add("--no-optional-locks");
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("core.fsmonitor=false");
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        using var registration = ct.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }
}
