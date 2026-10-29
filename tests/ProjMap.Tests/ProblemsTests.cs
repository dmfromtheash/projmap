using System.Text.Json;
using ProjMap.Analysis;
using ProjMap.Cli;
using ProjMap.Core;

namespace ProjMap.Tests;

public sealed class ProblemsTests
{
    private const string Project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>";
    private static List<ProblemItem> Of(ProblemsInfo report, string kind) => report.Items.Where(i => i.Kind == kind).ToList();
    private static Node N(string id, string kind = NodeKinds.Member, string? path = null, string? parent = null,
        params string[] tags) => new() { Id = id, Kind = kind, Name = id, Path = path, ParentId = parent, Tags = [.. tags] };
    private static Edge E(string source, string target, string kind = EdgeKinds.Call) => new()
    {
        Id = $"{kind}:{source}->{target}",
        Source = source,
        Target = target,
        Kind = kind,
        Evidence = [new("", 7, "source call")]
    };
    private static ProjectGraph G(List<Node> nodes, List<Edge>? edges = null, List<TierAssignment>? tiers = null) => new()
    {
        Project = new() { Name = "Fixture", Root = "." },
        Nodes = nodes,
        Edges = edges ?? [],
        Views = tiers == null ? null : new() { RuleSet = "fixture", Assignments = tiers }
    };
    private static TierAssignment Tier(string file, string tier) => new()
    { NodeId = file, Tier = tier, Decision = TierDecisions.Override, Score = 1 };

    [Fact]
    public async Task ScanFindsDeadMembersAndTypesButExcludesEntriesTestsToolsGeneratedAndLibraryHooks()
    {
        using var repo = new FixtureRepository();
        repo.Write("src/Core.csproj", Project);
        repo.Write("src/Service.cs", """
            namespace Demo;
            public class Service : System.IDisposable
            {
                public static void Live() { }
                private void Unused() { }
                public void Dispose() { }
            }
            public class UnusedType { }
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            public struct Native { public int Flags; }
            """);
        repo.Write("src/Program.cs", "namespace Demo; public static class Program { public static void Main() => Service.Live(); public static void BootHook() {} }");
        repo.Write("src/Auto.g.cs", "class Auto { public void GeneratedMethod() {} }");
        repo.Write("tools/Check.cs", "class Check { public void ToolMethod() {} }");
        repo.Write("checks/Checks.csproj", Project.Replace("</PropertyGroup>", "<IsTestProject>true</IsTestProject></PropertyGroup>"));
        repo.Write("checks/Api.cs", "public class TestApi { public void PublicTestApi() {} private void TestHelper() {} }");
        var before = repo.Snapshot();
        var graph = await ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0 });
        Assert.Empty(graph.Warnings);
        var dead = Of(graph.Problems!, ProblemKinds.DeadCode);
        Assert.Contains(dead, p => p.NodeIds[0].EndsWith(":M:Demo.Service.Unused", StringComparison.Ordinal));
        Assert.Contains(dead, p => p.NodeIds[0].EndsWith(":Demo.UnusedType", StringComparison.Ordinal));
        foreach (var name in new[] { "Live", "Dispose", "Flags", "BootHook", "GeneratedMethod", "ToolMethod", "PublicTestApi", "TestHelper" })
        {
            var node = Assert.Single(graph.Nodes, n => n.Kind == NodeKinds.Member && n.Name == name);
            Assert.DoesNotContain(dead, p => p.NodeIds.Contains(node.Id));
        }
        Assert.DoesNotContain(dead, p => p.NodeIds.Contains(graph.Nodes.Single(n => n.Kind == NodeKinds.Type && n.Name == "Service").Id));
        Assert.All(dead, p => { Assert.Equal("warn", p.Severity); Assert.Contains("без входящих связей", p.Reason); });
        Assert.Equal(before.OrderBy(p => p.Key), repo.Snapshot().OrderBy(p => p.Key));
    }

    [Theory]
    [InlineData("callback")]
    [InlineData("interop")]
    [InlineData("handler")]
    [InlineData("library-only")]
    [InlineData("not-shown")]
    [InlineData("entrypoint")]
    [InlineData("generated")]
    [InlineData("test")]
    public void ExclusionTagsAreHonoredEvenBeforeParallelScannerChangesArrive(string tag)
    {
        var graph = G([N("excluded", tags: tag), N("unused"), N("handler") with { SubKind = "handler" }]);
        Assert.Equal("unused", Assert.Single(Of(ProblemsAnalyzer.Analyze(graph), ProblemKinds.DeadCode)).NodeIds[0]);
    }

    [Theory]
    [InlineData(EdgeKinds.Call)]
    [InlineData(EdgeKinds.Reference)]
    [InlineData(EdgeKinds.Inherit)]
    [InlineData(EdgeKinds.Implement)]
    [InlineData(EdgeKinds.XamlEvent)]
    [InlineData(EdgeKinds.Binding)]
    public void IncomingCodeEdgesPreventDeadCandidates(string kind)
    {
        var graph = G([N("source"), N("target")], [E("source", "target", kind)]);
        Assert.DoesNotContain(Of(ProblemsAnalyzer.Analyze(graph), ProblemKinds.DeadCode), p => p.NodeIds.Contains("target"));
    }

    [Fact]
    public void MentionsAndCochangeDoNotCountAsIncomingCodeAndDisconnectedCyclesAreNotDeadCandidates()
    {
        var graph = G([N("source"), N("unused"), N("a"), N("b")],
            [E("source", "unused", EdgeKinds.Mentions), E("source", "unused", EdgeKinds.CoChange), E("a", "b"), E("b", "a")]);
        var dead = Of(ProblemsAnalyzer.Analyze(graph), ProblemKinds.DeadCode);
        Assert.Contains(dead, p => p.NodeIds.Contains("unused"));
        Assert.DoesNotContain(dead, p => p.NodeIds.Contains("a") || p.NodeIds.Contains("b"));
    }

    [Fact]
    public async Task ReachabilityPortsReverseContractDispatchAndTopLevelEntryRoots()
    {
        using var repo = new FixtureRepository();
        repo.Write("Core.csproj", Project);
        repo.Write("entry.cs", "Demo.Start.Run(null!);");
        repo.Write("Api.cs", """
            namespace Demo;
            public interface ITask { void Run(); }
            public class Task : ITask { public void Run() {} }
            public static class Start { public static void Run(ITask t) => t.Run(); }
            """);
        var graph = await ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0 });
        var implementation = Assert.Single(graph.Nodes, n => n.Id.EndsWith(":M:Demo.Task.Run", StringComparison.Ordinal));
        Assert.DoesNotContain(graph.Edges, e => e.Target == implementation.Id); // only the contract is called
        Assert.DoesNotContain(Of(graph.Problems!, ProblemKinds.DeadCode), p => p.NodeIds.Contains(implementation.Id));
        Assert.DoesNotContain(Of(graph.Problems!, ProblemKinds.DeadCode), p => p.NodeIds.Contains("type:module:Core.csproj:Demo.Task"));
    }

    [Fact]
    public async Task CyclesUseProjectReferencesAndResolveSemanticMembersToFiles()
    {
        using var repo = new FixtureRepository();
        repo.Write("A/A.csproj", Project.Replace("</Project>", "<ItemGroup><ProjectReference Include=\"../B/B.csproj\"/></ItemGroup></Project>"));
        repo.Write("B/B.csproj", Project.Replace("</Project>", "<ItemGroup><ProjectReference Include=\"../A/A.csproj\"/></ItemGroup></Project>"));
        repo.Write("A/First.cs", "namespace Demo; public class First { public static void Run() => Second.Run(); }");
        repo.Write("A/Second.cs", "namespace Demo; public class Second { public static void Run() => First.Run(); }");
        var graph = await ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0 });
        Assert.Equal(["module:A/A.csproj", "module:B/B.csproj"], Assert.Single(Of(graph.Problems!, ProblemKinds.ModuleCycle)).NodeIds);
        Assert.Equal(["file:A/First.cs", "file:A/Second.cs"], Assert.Single(Of(graph.Problems!, ProblemKinds.FileCycle)).NodeIds);
        Assert.DoesNotContain(graph.Edges, e => e.Source == "file:A/First.cs" && e.Target == "file:A/Second.cs");
    }

    [Fact]
    public void CyclesAreLargestFirstCappedAndIgnorePackagesCochangeAndSameFileEdges()
    {
        List<Node> nodes = []; List<Edge> edges = [];
        for (var i = 0; i < 105; i++)
        {
            var a = $"file:{i:D3}a"; var b = $"file:{i:D3}b";
            nodes.Add(N(a, NodeKinds.File, a)); nodes.Add(N(b, NodeKinds.File, b));
            edges.Add(E(a, b, EdgeKinds.Import)); edges.Add(E(b, a, EdgeKinds.Reference));
        }
        nodes.Add(N("file:big", NodeKinds.File, "big"));
        edges.Add(E("file:big", "file:000a")); edges.Add(E("file:000b", "file:big"));
        nodes.Add(N("file:solo", NodeKinds.File, "solo")); edges.Add(E("file:solo", "file:solo"));
        edges.Add(E("file:solo", "file:001a", EdgeKinds.CoChange)); edges.Add(E("file:001a", "file:solo", EdgeKinds.CoChange));
        edges.Add(E("file:solo", "pkg:unknown", EdgeKinds.Package));
        var report = ProblemsAnalyzer.Analyze(G(nodes, edges));
        Assert.Equal(105, report.Counts[ProblemKinds.FileCycle]);
        var cycles = Of(report, ProblemKinds.FileCycle);
        Assert.Equal(100, cycles.Count);
        Assert.Equal(3, cycles[0].NodeIds.Count);
        Assert.Equal("file:001a", cycles[1].NodeIds[0]);
        Assert.DoesNotContain(cycles, p => p.NodeIds.Contains("file:solo"));
        Assert.Equal(0, report.Counts[ProblemKinds.ModuleCycle]);
    }

    [Fact]
    public void AViolationBetweenGuessedTiersIsOnlyAPossibleOne()
    {
        var graph = G([N("file:a", NodeKinds.File, "a.cs"), N("file:b", NodeKinds.File, "b.cs"), N("source", path: "a.cs"), N("target", path: "b.cs")],
            [E("source", "target")],
            [Tier("file:a", Tiers.Data) with { Decision = TierDecisions.Guess, Score = 0.6 }, Tier("file:b", Tiers.Frontend)]);
        var violation = Assert.Single(Of(ProblemsAnalyzer.Analyze(graph), ProblemKinds.TierViolation));
        Assert.Equal("info", violation.Severity);
        Assert.StartsWith("Возможно", violation.Reason);
    }

    [Theory]
    [InlineData(Tiers.Domain, Tiers.Frontend, "domain-dependency")]
    [InlineData(Tiers.Domain, Tiers.Application, "domain-dependency")]
    [InlineData(Tiers.Domain, Tiers.Backend, "domain-dependency")]
    [InlineData(Tiers.Data, Tiers.Frontend, "data-frontend")]
    [InlineData(Tiers.Application, Tiers.Tests, "production-tests")]
    [InlineData(Tiers.Unclassified, Tiers.Tests, "production-tests")]
    public void EveryTierRuleRetainsAllOriginalEdgesAndExpandsEvidence(string sourceTier, string targetTier, string rule)
    {
        var graph = G([N("file:a", NodeKinds.File, "a.cs"), N("file:b", NodeKinds.File, "b.cs"),
            N("source", path: "a.cs"), N("target", path: "b.cs")],
            [E("source", "target"), E("source", "target", EdgeKinds.Reference)],
            [Tier("file:a", sourceTier), Tier("file:b", targetTier)]);
        var violation = Assert.Single(Of(ProblemsAnalyzer.Analyze(graph), ProblemKinds.TierViolation));
        Assert.Equal(rule, violation.Rule); Assert.Equal("warn", violation.Severity);
        Assert.Equal(["file:a", "file:b"], violation.NodeIds);
        Assert.Equal(2, violation.Edges!.Count);
        Assert.All(violation.Edges, e => Assert.Equal(new SourceLocation("a.cs", 7, "source call"), Assert.Single(e.Evidence)));
        Assert.All(graph.Edges, e => Assert.Equal("", e.Evidence[0].Path));
    }

    [Theory]
    [InlineData(Tiers.Domain, Tiers.Infrastructure)]
    [InlineData(Tiers.Domain, Tiers.Data)]
    [InlineData(Tiers.Data, Tiers.Application)]
    [InlineData(Tiers.Tests, Tiers.Tests)]
    [InlineData(Tiers.Tests, Tiers.Frontend)]
    [InlineData(Tiers.Frontend, Tiers.Domain)]
    public void AllowedTierDirectionsDoNotInventRendererOrOtherRules(string from, string to)
    {
        var graph = G([N("a", NodeKinds.File, "a"), N("b", NodeKinds.File, "b")], [E("a", "b")], [Tier("a", from), Tier("b", to)]);
        Assert.Empty(Of(ProblemsAnalyzer.Analyze(graph), ProblemKinds.TierViolation));
    }

    [Fact]
    public async Task FinalProjectAndOwnerOverridesApplyAndPartialMembersUseTheirActualFile()
    {
        using var repo = new FixtureRepository(); repo.Write("Core.csproj", Project);
        repo.Write("A.cs", "namespace Demo; public partial class Source { }");
        repo.Write("B.cs", "namespace Demo; public partial class Source { public void Run() => Target.Run(); }");
        repo.Write("C.cs", "namespace Demo; public class Target { public static void Run() {} }");
        repo.Write(".projmap/views.rules.json", """
            { "overrides": { "tiers": [
              { "pathGlob": "A.cs", "tier": "data" },
              { "pathGlob": "B.cs", "tier": "domain" },
              { "pathGlob": "C.cs", "tier": "frontend" }
            ] } }
            """);
        var options = new ScanOptions(repo.Root) { GitMonths = 0 };
        var first = await ProjectScanner.ScanAsync(options, null, null);
        var violation = Assert.Single(Of(first.Graph.Problems!, ProblemKinds.TierViolation));
        Assert.Equal(["file:B.cs", "file:C.cs"], violation.NodeIds);
        Assert.All(violation.Edges!, e => Assert.All(e.Evidence, v => Assert.Equal("B.cs", v.Path)));
        options = options with { ViewOverrides = new() { Tiers = [new() { PathGlob = "C.cs", Tier = Tiers.Domain }] } };
        var incremental = await ProjectScanner.ScanAsync(options, first.State, []);
        Assert.Empty(Of(incremental.Graph.Problems!, ProblemKinds.TierViolation));
        var full = await ProjectScanner.ScanAsync(options);
        Assert.Equal(JsonSerializer.Serialize(full.Problems, GraphJson.Options), JsonSerializer.Serialize(incremental.Graph.Problems, GraphJson.Options));
    }

    [Fact]
    public void HotFilesUseLongProductsPositiveScoresTopTwentyAndOrdinalTies()
    {
        var nodes = Enumerable.Range(0, 25).Select(i => N($"file:{i:D2}", NodeKinds.File) with
        { Metrics = new() { Commits = 2, Complexity = 10 } }).ToList();
        nodes.Add(N("file:largest", NodeKinds.File) with { Metrics = new() { Commits = int.MaxValue, Complexity = int.MaxValue } });
        nodes.Add(N("file:no-history", NodeKinds.File) with { Metrics = new() { Complexity = 10 } });
        nodes.Add(N("file:no-complexity", NodeKinds.File) with { Metrics = new() { Commits = 10 } });
        nodes.Add(N("type:not-file", NodeKinds.Type) with { Metrics = new() { Commits = int.MaxValue, Complexity = int.MaxValue } });
        var report = ProblemsAnalyzer.Analyze(G(nodes)); var hot = Of(report, ProblemKinds.HotFile);
        Assert.Equal(26, report.Counts[ProblemKinds.HotFile]); Assert.Equal(20, hot.Count);
        Assert.Equal("file:largest", hot[0].NodeIds[0]); Assert.Equal((long)int.MaxValue * int.MaxValue, hot[0].Score);
        Assert.Equal("file:00", hot[1].NodeIds[0]); Assert.Equal("file:18", hot[^1].NodeIds[0]);
        Assert.All(hot, i => Assert.Equal("info", i.Severity));
    }

    [Fact]
    public async Task HotFilesReadAggregatedComplexityAndExistingGitMetrics()
    {
        using var repo = new FixtureRepository(); repo.Write("Core.csproj", Project);
        repo.Write("Service.cs", "public class Service { public int Run(int x) { if (x > 0) return 1; return 0; } }");
        await repo.Git("add", "."); await repo.Git("commit", "-q", "-m", "fixture");
        var graph = await ProjectScanner.ScanAsync(new(repo.Root));
        var item = Assert.Single(Of(graph.Problems!, ProblemKinds.HotFile));
        Assert.Equal(["file:Service.cs"], item.NodeIds); Assert.Equal(2, item.Score);
    }

    [Fact]
    public async Task SerializationRoundTripsOptionalProblemsInEveryGraphFormatAndOrderingIsDeterministic()
    {
        var graph = G([N("b"), N("a")], [E("b", "a")]);
        var report = ProblemsAnalyzer.Analyze(graph);
        Assert.Equal(JsonSerializer.Serialize(report, GraphJson.Options), JsonSerializer.Serialize(
            ProblemsAnalyzer.Analyze(graph with { Nodes = graph.Nodes.AsEnumerable().Reverse().ToList(), Edges = graph.Edges.AsEnumerable().Reverse().ToList() }), GraphJson.Options));
        Assert.Null(GraphJson.Deserialize(GraphJson.Serialize(graph))!.Problems);
        Assert.DoesNotContain("\"problems\"", GraphJson.Serialize(graph));
        graph = graph with { Problems = report };
        await using var stream = new MemoryStream(); await GraphJson.WriteAsync(graph, stream);
        foreach (var json in new[] { GraphJson.Serialize(graph), System.Text.Encoding.UTF8.GetString(GraphJson.SerializeCompact(graph)), System.Text.Encoding.UTF8.GetString(stream.ToArray()) })
        {
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(JsonSerializer.Serialize(report, GraphJson.Options), doc.RootElement.GetProperty("problems").GetRawText());
        }
        Assert.Equal(report.Items[0].NodeIds, GraphJson.Deserialize(GraphJson.Serialize(graph))!.Problems!.Items[0].NodeIds);
    }

    [Fact]
    public async Task CliReportWritesStandaloneJsonAndMarkdownOrJsonToStdoutWithoutWritingScannedRoot()
    {
        using var repo = new FixtureRepository(); using var outputs = new FixtureRepository();
        repo.Write("Core.csproj", Project); repo.Write("Unused.cs", "class Unused { void Dead() {} }");
        var before = repo.Snapshot(); var json = Path.Combine(outputs.Root, "out", "report.json"); var md = Path.Combine(outputs.Root, "out", "report.md");
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(0, await CommandLine.RunAsync(["report", repo.Root, "-o", json, "--md", md, "--git-months", "0"], output, error));
        var report = JsonSerializer.Deserialize<ProblemsInfo>(await File.ReadAllTextAsync(json), GraphJson.Options)!;
        Assert.Equal(2, report.Counts[ProblemKinds.DeadCode]);
        Assert.Contains("без входящих связей", await File.ReadAllTextAsync(md)); Assert.Contains("Unused.cs", await File.ReadAllTextAsync(md));
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await CommandLine.RunAsync(["report", repo.Root, "--git-months", "0"], output, error));
        Assert.Equal(report.Counts[ProblemKinds.DeadCode], JsonSerializer.Deserialize<ProblemsInfo>(output.ToString(), GraphJson.Options)!.Counts[ProblemKinds.DeadCode]);
        Assert.Equal(before.OrderBy(p => p.Key), repo.Snapshot().OrderBy(p => p.Key));
        Assert.Equal(1, await CommandLine.RunAsync(["report", repo.Root, "--md"], output, error));
        Assert.Equal(1, await CommandLine.RunAsync(["report", repo.Root, "-o", json, "--md", json], output, error));
        Assert.Equal(1, await CommandLine.RunAsync(["report", Path.Combine(repo.Root, "missing")], output, error));
    }

    [Fact]
    public void AnalysisPropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ProblemsAnalyzer.Analyze(G([]), cancellation.Token));
    }
}
