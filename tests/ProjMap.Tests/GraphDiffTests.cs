using ProjMap.Analysis;
using ProjMap.Cli;
using ProjMap.Core;

namespace ProjMap.Tests;

public sealed class GraphDiffTests
{
    private static void Populate(FixtureRepository repo)
    {
        repo.Write("Demo.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        repo.Write("Core/Service.cs", "namespace Demo; public class Service { public int Run(int input) => input + 1; }");
        repo.Write("App/Main.cs", "using Demo; class Main { int Execute() => new Service().Run(3); }");
        repo.Write("README.md", "# Demo\n\n- [x] first stage\n");
    }

    [Fact]
    public async Task SameContentInTwoFoldersIsIdenticalAndAnAddedClassShowsOnlyInB()
    {
        using var a = new FixtureRepository(); Populate(a);
        using var b = new FixtureRepository(); Populate(b);
        var left = await ProjectScanner.ScanAsync(new(a.Root) { GitMonths = 0 });
        var right = await ProjectScanner.ScanAsync(new(b.Root) { GitMonths = 0 });
        Assert.NotEqual(left.Project.Root, right.Project.Root);
        Assert.True(GraphDiff.Compare(left, right).Identical);

        b.Write("Core/Extra.cs", "namespace Demo; public class Extra { public int Use() => new Service().Run(2); }");
        var changed = await ProjectScanner.ScanAsync(new(b.Root) { GitMonths = 0 });
        var diff = GraphDiff.Compare(left, changed);
        Assert.False(diff.Identical);
        Assert.Contains("file:Core/Extra.cs", diff.Nodes.OnlyInB);
        Assert.Contains(diff.Nodes.OnlyInB, id => id.StartsWith("type:", StringComparison.Ordinal) && id.EndsWith("Demo.Extra", StringComparison.Ordinal));
        Assert.Empty(diff.Nodes.OnlyInA);
        Assert.NotEmpty(diff.Edges.OnlyInB);
    }

    [Fact]
    public async Task CodeThatOnlyMovedDownIsUnchangedWhenPositionsAreIgnored()
    {
        using var a = new FixtureRepository(); Populate(a);
        using var b = new FixtureRepository(); Populate(b);
        b.Write("Core/Service.cs", "\n\n\nnamespace Demo; public class Service { public int Run(int input) => input + 1; }");
        var left = await ProjectScanner.ScanAsync(new(a.Root) { GitMonths = 0 });
        var right = await ProjectScanner.ScanAsync(new(b.Root) { GitMonths = 0 });
        Assert.False(GraphDiff.Compare(left, right).Identical);
        Assert.True(GraphDiff.Compare(left, right, new() { IgnorePositions = true }).Identical);
    }

    [Fact]
    public async Task CliReportsIdenticalWithExitZeroAndDifferencesWithExitThree()
    {
        using var a = new FixtureRepository(); Populate(a);
        using var b = new FixtureRepository(); Populate(b);
        b.Write("Core/Service.cs", "namespace Demo; public class Service { public int Run(int input) { if (input > 0) return input + 1; return 0; } }");
        var ga = Path.Combine(a.Root, "obj", "a.json"); var gb = Path.Combine(b.Root, "obj", "b.json"); var gc = Path.Combine(b.Root, "obj", "c.json");
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(0, await CommandLine.RunAsync(["scan", a.Root, "-o", ga, "--git-months", "0"], output, error));
        Assert.Equal(0, await CommandLine.RunAsync(["scan", a.Root, "-o", gc, "--git-months", "0"], output, error));
        Assert.Equal(0, await CommandLine.RunAsync(["scan", b.Root, "-o", gb, "--git-months", "0"], output, error));

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await CommandLine.RunAsync(["diff", ga, gc], output, error));
        Assert.Contains("IDENTICAL", output.ToString());

        output.GetStringBuilder().Clear();
        var report = Path.Combine(b.Root, "obj", "diff.json");
        Assert.Equal(3, await CommandLine.RunAsync(["diff", ga, gb, "--ignore-git", "--limit", "5", "-o", report], output, error));
        Assert.Contains("changed: file:Core/Service.cs", output.ToString());
        Assert.Contains("DIFFERENT", output.ToString());
        Assert.Contains("\"changed\"", File.ReadAllText(report));
        Assert.Equal(1, await CommandLine.RunAsync(["diff", ga], output, error));
        Assert.Equal(1, await CommandLine.RunAsync(["diff", ga, gb, "--limit", "x"], output, error));
    }
}
