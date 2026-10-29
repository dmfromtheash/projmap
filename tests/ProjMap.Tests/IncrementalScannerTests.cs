using ProjMap.Analysis;
using ProjMap.Cli;
using ProjMap.Core;

namespace ProjMap.Tests;

public sealed class IncrementalScannerTests
{
    private const string Project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>";
    private static string Stable(ProjectGraph graph) => GraphJson.Serialize(graph with
    { Project = graph.Project with { ScannedAt = default, ScanMilliseconds = 0, Timings = null } });

    private static void Populate(FixtureRepository repo)
    {
        repo.Write("Core/Core.csproj", Project);
        repo.Write("Core/Service.cs", "namespace Demo; public partial class Service { public int Value; public int Run(int input) { return input + Value; } }");
        repo.Write("Core/Other.cs", "namespace Demo; public partial class Service { public int Other() => Run(1); } public class Entity { public int Id { get; set; } }");
        repo.Write("App/App.csproj", Project.Replace("</Project>", "<ItemGroup><ProjectReference Include=\"../Core/Core.csproj\"/></ItemGroup></Project>"));
        repo.Write("App/Main.cs", "using Demo; class Main { int Execute() => new Service().Run(3); }");
        repo.Write("docs/ROADMAP.md", "# Work\n- [ ] Service Core/Service.cs\n");
        repo.Write("docs/ARCHITECTURE.md", "Core  Shared services and entities\n");
        repo.Write("schema.sql", "CREATE TABLE users (id INTEGER PRIMARY KEY);");
        repo.Write("query.py", "query = 'SELECT * FROM users'\n");
    }

    [Fact]
    public async Task ForcedUnmodifiedFileActuallyReparsesAndMatchesFull()
    {
        using var repo = new FixtureRepository(); Populate(repo);
        await repo.Git("add", "."); await repo.Git("commit", "-q", "-m", "initial");
        var initial = await ProjectScanner.ScanAsync(new(repo.Root), null, null);
        var result = await ProjectScanner.ScanAsync(new(repo.Root), initial.State, [Path.Combine(repo.Root, "Core/Service.cs")]);
        Assert.Empty(result.Graph.Warnings);
        Assert.Equal(Stable(initial.Graph), Stable(result.Graph));
        Assert.Equal(1, result.Graph.Project.Timings!.Counts["csharp.parsedFiles"]);
        Assert.Equal(1, result.Graph.Project.Timings.Counts["csharp.analyzedFiles"]);
        Assert.False(result.Graph.Project.Timings.Counts.ContainsKey("git.logRuns"));
    }

    [Fact]
    public async Task RealBodyEditReusesOtherFilesAndMatchesFullIncludingWeightsAndEvidence()
    {
        using var repo = new FixtureRepository(); Populate(repo);
        var initial = await ProjectScanner.ScanAsync(new(repo.Root), null, null);
        repo.Write("Core/Service.cs", "namespace Demo; public partial class Service { public int Value; public int Run(int input) {\n if (input > 0) return Other() + Value + Value; return input; } }");
        var result = await ProjectScanner.ScanAsync(new(repo.Root), initial.State, ["Core/Service.cs"]);
        var full = await ProjectScanner.ScanAsync(new(repo.Root));
        Assert.Empty(result.Graph.Warnings);
        Assert.Equal(Stable(full), Stable(result.Graph));
        Assert.Equal(1, result.Graph.Project.Timings!.Counts["csharp.analyzedFiles"]);
        Assert.NotEqual(Stable(initial.Graph), Stable(result.Graph));
        // Reusing an older state after a completed rescan must also work; it was not mutated.
        var fromOld = await ProjectScanner.ScanAsync(new(repo.Root), initial.State, []);
        Assert.Equal(Stable(full), Stable(fromOld.Graph));
    }

    [Fact]
    public async Task SignatureChangesInvalidateDependentProjectsAndMissingWatcherEventsAreDetected()
    {
        using var repo = new FixtureRepository(); Populate(repo);
        var initial = await ProjectScanner.ScanAsync(new(repo.Root), null, null);
        repo.Write("Core/Service.cs", "namespace Demo; public partial class Service { public int Value; public int Renamed(int input) => input; }");
        var result = await ProjectScanner.ScanAsync(new(repo.Root), initial.State, []);
        Assert.Equal(Stable(await ProjectScanner.ScanAsync(new(repo.Root))), Stable(result.Graph));
        Assert.Equal(3, result.Graph.Project.Timings!.Counts["csharp.analyzedFiles"]);
        Assert.DoesNotContain(result.Graph.Edges, e => e.Target.EndsWith(":M:Demo.Service.Run(System.Int32)"));
    }

    [Fact]
    public async Task AddedDeletedPartialFilesManifestsDocsAndSchemaMatchFull()
    {
        using var repo = new FixtureRepository(); Populate(repo);
        var result = await ProjectScanner.ScanAsync(new(repo.Root), null, null);
        async Task Rescan(params string[] paths)
        {
            result = await ProjectScanner.ScanAsync(new(repo.Root), result.State, paths);
            Assert.Empty(result.Graph.Warnings);
            Assert.Equal(Stable(await ProjectScanner.ScanAsync(new(repo.Root))), Stable(result.Graph));
        }
        repo.Write("Core/Added.cs", "namespace Demo; public partial class Service { public void Added() {} }");
        await Rescan("Core/Added.cs");
        File.Delete(Path.Combine(repo.Root, "Core/Other.cs"));
        await Rescan("Core/Other.cs");
        File.Move(Path.Combine(repo.Root, "Core/Service.cs"), Path.Combine(repo.Root, "Core/Renamed.cs"));
        await Rescan("Core/Service.cs", "Core/Renamed.cs");
        repo.Write("docs/ROADMAP.md", "# Work\n- [x] Service Core/Renamed.cs\n");
        repo.Write("schema.sql", "CREATE TABLE users (id INT, name TEXT NOT NULL);");
        repo.Write("extra.py", "from query import query\n");
        await Rescan("docs/ROADMAP.md", "schema.sql", "extra.py");
        File.Delete(Path.Combine(repo.Root, "Core/Core.csproj"));
        await Rescan("Core/Core.csproj");
    }

    [Fact]
    public async Task ChangingProjectTestMetadataRefreshesCachedSourceTags()
    {
        using var repo = new FixtureRepository(); Populate(repo);
        var result = await ProjectScanner.ScanAsync(new(repo.Root), null, null);
        repo.Write("Core/Core.csproj", Project.Replace("</PropertyGroup>", "<IsTestProject>true</IsTestProject></PropertyGroup>"));
        result = await ProjectScanner.ScanAsync(new(repo.Root), result.State, ["Core/Core.csproj"]);
        Assert.Equal(Stable(await ProjectScanner.ScanAsync(new(repo.Root))), Stable(result.Graph));
        Assert.All(result.Graph.Nodes.Where(n => n.Kind is NodeKinds.Type or NodeKinds.Member && n.Path!.StartsWith("Core/")),
            n => Assert.Contains("test", n.Tags));
    }

    [Fact]
    public async Task CaseOnlyRenamesRefreshGraphIdsAndCachedMentions()
    {
        using var repo = new FixtureRepository(); Populate(repo);
        await repo.Git("add", ".");
        var result = await ProjectScanner.ScanAsync(new(repo.Root), null, null);
        await repo.Git("mv", "Core/Service.cs", "Core/Temporary.cs");
        await repo.Git("mv", "Core/Temporary.cs", "Core/SERVICE.cs");
        result = await ProjectScanner.ScanAsync(new(repo.Root), result.State, ["Core/Service.cs", "Core/SERVICE.cs"]);
        Assert.Equal(Stable(await ProjectScanner.ScanAsync(new(repo.Root))), Stable(result.Graph));
        Assert.DoesNotContain(result.Graph.Nodes, n => n.Id == "file:Core/Service.cs");
        Assert.Contains(result.Graph.Nodes, n => n.Id == "file:Core/SERVICE.cs");
    }

    [Fact]
    public async Task GitCommitInvalidatesHistoryCacheWithoutOverwritingNewLoc()
    {
        using var repo = new FixtureRepository(); Populate(repo);
        await repo.Git("add", "."); await repo.Git("commit", "-q", "-m", "initial");
        var result = await ProjectScanner.ScanAsync(new(repo.Root), null, null);
        repo.Write("query.py", "query = 'SELECT * FROM users'\n# added line\n");
        result = await ProjectScanner.ScanAsync(new(repo.Root), result.State, ["query.py"]);
        Assert.False(result.Graph.Project.Timings!.Counts.ContainsKey("git.logRuns"));
        Assert.Equal(2, result.Graph.Nodes.Single(n => n.Id == "file:query.py").Metrics.Loc);
        Assert.Equal(Stable(await ProjectScanner.ScanAsync(new(repo.Root))), Stable(result.Graph));
        await repo.Git("add", "."); await repo.Git("commit", "-q", "-m", "second");
        result = await ProjectScanner.ScanAsync(new(repo.Root), result.State, [".git/HEAD"]);
        Assert.Equal(1, result.Graph.Project.Timings!.Counts["git.logRuns"]);
        Assert.Equal(Stable(await ProjectScanner.ScanAsync(new(repo.Root))), Stable(result.Graph));
    }

    [Fact]
    public async Task DifferentOptionsAndCancellationDoNotReuseIncompatibleOrPartialState()
    {
        using var repo = new FixtureRepository(); Populate(repo);
        var result = await ProjectScanner.ScanAsync(new(repo.Root), null, null);
        var options = new ScanOptions(repo.Root) { IncludeMembers = false, GitMonths = 0 };
        var changed = await ProjectScanner.ScanAsync(options, result.State, []);
        Assert.Equal(Stable(await ProjectScanner.ScanAsync(options)), Stable(changed.Graph));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectScanner.ScanAsync(new(repo.Root), result.State, [], cancellation.Token));
        Assert.Equal(Stable(result.Graph), Stable((await ProjectScanner.ScanAsync(new(repo.Root), result.State, [])).Graph));
    }

    [Fact]
    public async Task DocumentationInAnotherPartialFileInvalidatesTheCanonicalSummary()
    {
        using var repo = new FixtureRepository(); Populate(repo);
        var result = await ProjectScanner.ScanAsync(new(repo.Root), null, null);
        repo.Write("Core/Other.cs", "namespace Demo;\n/// <summary>Shared partial documentation.</summary>\npublic partial class Service { public int Other() => Run(1); }");
        result = await ProjectScanner.ScanAsync(new(repo.Root), result.State, ["Core/Other.cs"]);
        Assert.Equal(Stable(await ProjectScanner.ScanAsync(new(repo.Root))), Stable(result.Graph));
        Assert.Equal("Shared partial documentation.", result.Graph.Nodes.Single(n => n.Kind == NodeKinds.Type && n.Name == "Service").Summary);
    }

    [Fact]
    public async Task ParallelPartialMethodsHaveDeterministicNodesAndEvidence()
    {
        using var repo = new FixtureRepository();
        repo.Write("Core/Core.csproj", Project);
        repo.Write("Core/A.cs", "namespace Demo; public partial class Service { partial void Work(); public void Run() { Work(); Work(); } }");
        repo.Write("Core/B.cs", "namespace Demo; public partial class Service { partial void Work() { if (true) Run(); } }");
        var result = await ProjectScanner.ScanAsync(new(repo.Root), null, null);
        for (var i = 0; i < 3; i++)
            Assert.Equal(Stable(result.Graph), Stable(await ProjectScanner.ScanAsync(new(repo.Root))));
        repo.Write("Core/B.cs", "namespace Demo; public partial class Service { partial void Work() { Run(); Run(); } }");
        result = await ProjectScanner.ScanAsync(new(repo.Root), result.State, ["Core/B.cs"]);
        Assert.Equal(Stable(await ProjectScanner.ScanAsync(new(repo.Root))), Stable(result.Graph));
    }

    [Fact]
    public async Task HeadChangeWithoutContentChangeRefreshesRoadmapCommitDetails()
    {
        using var repo = new FixtureRepository(); Populate(repo);
        repo.Write("docs/DECISIONS.md", "# Decisions\n## DEC-070 Service\n");
        await repo.Git("add", "."); await repo.Git("commit", "-q", "-m", "initial");
        var result = await ProjectScanner.ScanAsync(new(repo.Root), null, null);
        await repo.Git("commit", "-q", "--amend", "-m", "DEC-070 new decision context");
        result = await ProjectScanner.ScanAsync(new(repo.Root), result.State, [".git/HEAD"]);
        Assert.Equal(Stable(await ProjectScanner.ScanAsync(new(repo.Root))), Stable(result.Graph));
        Assert.Contains("DEC-070 new decision context", result.Graph.Roadmap.Single(r => r.Title == "DEC-070 Service").Detail);
    }

    [Fact]
    public async Task TimingsAreDiagnosticsAndCliStreamsBackwardCompatibleGraph()
    {
        using var empty = new FixtureRepository();
        var emptyGraph = await ProjectScanner.ScanAsync(new(empty.Root));
        Assert.Equal(0, emptyGraph.Project.Timings!.Milliseconds["csharp.semanticModels"]);
        using var repo = new FixtureRepository(); Populate(repo);
        var file = Path.Combine(repo.Root, "obj", "graph.json");
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(0, await CommandLine.RunAsync(["scan", repo.Root, "-o", file, "--timings"], output, error));
        var graph = GraphJson.Deserialize(await File.ReadAllTextAsync(file))!;
        Assert.Empty(graph.Warnings);
        foreach (var stage in new[] { "structure", "csharp", "csharp.parse", "csharp.semanticModels", "csharp.declare", "csharp.walk",
            "xaml", "imports", "git", "roadmap", "database", "descriptions", "views", "finalize", "serialization" })
        {
            Assert.True(graph.Project.Timings!.Milliseconds[stage] >= 0, stage);
            Assert.Contains(stage + ":", output.ToString());
        }
        Assert.True(graph.Project.Timings!.Milliseconds["serialization"] > 0);
        Assert.Equal(Stable(await ProjectScanner.ScanAsync(new(repo.Root))), Stable(graph));
    }
}
