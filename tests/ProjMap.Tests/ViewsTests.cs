using ProjMap.Analysis;
using ProjMap.Core;

namespace ProjMap.Tests;

public sealed class ViewsTests
{
    private const string Project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";
    private const string UiProject = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" +
        "<ItemGroup><PackageReference Include=\"Avalonia\" Version=\"11.0.0\"/><ProjectReference Include=\"../Demo.Core/Demo.Core.csproj\"/></ItemGroup></Project>";

    private static void Populate(FixtureRepository repo)
    {
        repo.Write("src/Demo.Core/Demo.Core.csproj", Project);
        repo.Write("src/Demo.Core/Documents/HexGrid.cs", "namespace Demo; public static class HexGrid { public static int Cells(int r) => 3 * r * (r + 1) + 1; }");
        repo.Write("src/Demo.Core/Documents/RiverPath.cs", "namespace Demo; public sealed class RiverPath { public int Length { get; set; } }");
        repo.Write("src/Demo.Core/Editing/HexEditing.cs", "namespace Demo; public static class HexEditing { public static void Paint() => HexGrid.Cells(2); }");
        repo.Write("src/Demo.Core/Editing/RiverEditing.cs", "namespace Demo; public static class RiverEditing { public static void Bend(RiverPath p) => p.Length++; }");
        repo.Write("src/Demo.Core/Misc/Thing.cs", "namespace Demo; public sealed class Thing { }");
        repo.Write("src/Demo.Persistence/Demo.Persistence.csproj", Project);
        repo.Write("src/Demo.Persistence/HexStore.cs", "namespace Demo; public static class HexStore { public static void Save() { using var z = new System.IO.Compression.ZipArchive(System.IO.Stream.Null, System.IO.Compression.ZipArchiveMode.Create); } }");
        repo.Write("src/Demo.Persistence/RiverStore.cs", "namespace Demo; public static class RiverStore { public static void Save() { System.IO.File.WriteAllText(\"r\", \"\"); } }");
        repo.Write("src/Demo.App/Demo.App.csproj", UiProject);
        repo.Write("src/Demo.App/Views/MainWindow.axaml", "<Window xmlns=\"https://github.com/avaloniaui\"/>");
        repo.Write("src/Demo.App/Views/HexPanel.cs",
            "namespace Demo; static class Ui { public static object Button(string text, System.Action a) => a; } " +
            "public sealed class HexPanel { public object Build() => Ui.Button(\"Рисовать гексы\", () => HexEditing.Paint()); }");
        repo.Write("src/Demo.App/Views/RiverPanel.cs",
            "namespace Demo; public sealed class RiverPanel { public object Build() => Ui.Button(\"Изогнуть реку\", () => RiverEditing.Bend(new RiverPath())); }");
        repo.Write("src/Demo.App/Program.cs", "namespace Demo; public static class Program { public static void Main() { } }");
        repo.Write("tests/Demo.Tests/Demo.Tests.csproj", Project);
        repo.Write("tests/Demo.Tests/HexGridTests.cs", "namespace Demo.Tests; public class HexGridTests { public void Cells() { } }");
        repo.Write("docs/README.md", "# Demo\n\nA small fixture.\n");
    }

    private static TierAssignment Tier(ProjectGraph g, string path) => Assert.Single(g.Views!.Assignments, a => a.NodeId == "file:" + path);

    [Fact]
    public async Task TiersCarryScoreDecisionAndExplanation()
    {
        using var repo = new FixtureRepository(); Populate(repo);
        var g = await ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0 });
        Assert.Empty(g.Warnings);
        Assert.Equal("builtin:tiers-v1", g.Views!.RuleSet);
        Assert.Contains(g.Views.Tiers, t => t.Id == Tiers.Unclassified);

        var markup = Tier(g, "src/Demo.App/Views/MainWindow.axaml");
        Assert.Equal((Tiers.Frontend, TierDecisions.Automatic, "desktop"), (markup.Tier, markup.Decision, markup.Execution));
        Assert.True(markup.Score >= 0.9);
        Assert.Contains(markup.Evidence, e => e.Family == "syntax" && e.Rule == "markup-view");

        // a lambda with a text label in a UI-framework module is a button → interface, from syntax
        var panel = Tier(g, "src/Demo.App/Views/HexPanel.cs");
        Assert.Equal((Tiers.Frontend, TierDecisions.Automatic), (panel.Tier, panel.Decision));
        Assert.Contains(panel.Evidence, e => e.Rule == "ui-handlers");

        // path and module name only: an honest guess, capped below the automatic threshold
        var domain = Tier(g, "src/Demo.Core/Documents/HexGrid.cs");
        Assert.Equal((Tiers.Domain, TierDecisions.Guess), (domain.Tier, domain.Decision));
        Assert.True(domain.Score <= 0.69);
        Assert.StartsWith("предположение", domain.Reason);
        Assert.Equal("shared", domain.Execution ?? "shared");

        Assert.Equal(Tiers.Data, Tier(g, "src/Demo.Persistence/HexStore.cs").Tier);
        Assert.Equal(Tiers.Tests, Tier(g, "tests/Demo.Tests/HexGridTests.cs").Tier);
        Assert.Equal(Tiers.Tests, Tier(g, "tests/Demo.Tests/Demo.Tests.csproj").Tier); // a test project's manifest is part of the tests
        Assert.Equal(Tiers.Docs, Tier(g, "docs/README.md").Tier);
        Assert.Equal(Tiers.Infrastructure, Tier(g, "src/Demo.App/Program.cs").Tier);

        var unknown = Tier(g, "src/Demo.Core/Misc/Thing.cs");
        Assert.Equal((Tiers.Domain, TierDecisions.Guess), (unknown.Tier, unknown.Decision)); // module «Core» is still a hint
        Assert.All(g.Views.Assignments, a => Assert.InRange(a.Score, 0, 1));
        Assert.Equal(g.Views.Assignments.Select(a => a.NodeId).Order(StringComparer.Ordinal), g.Views.Assignments.Select(a => a.NodeId));
    }

    [Fact]
    public async Task TwoStrongContradictingSignalsAreMixedNotAWinner()
    {
        using var repo = new FixtureRepository();
        repo.Write("Things/Things.csproj", Project);
        repo.Write("Things/Odd.cs", "namespace Demo; public class Window {} public class DbContext {} public class Odd : DbContext {} public class Shell : Window {}");
        repo.Write("Things/Plain.txt", "nothing to see");
        var g = await ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0 });
        var odd = Tier(g, "Things/Odd.cs");
        Assert.Equal((Tiers.Mixed, TierDecisions.Mixed), (odd.Tier, odd.Decision));
        Assert.Equal([Tiers.Frontend, Tiers.Data], odd.Alternatives!.Order(StringComparer.Ordinal).Reverse().ToList());
        var plain = Tier(g, "Things/Plain.txt");
        Assert.Equal((Tiers.Unclassified, TierDecisions.Unknown), (plain.Tier, plain.Decision));
    }

    [Fact]
    public async Task AnApiRouteThatQueriesTheDatabaseIsStillServer()
    {
        using var repo = new FixtureRepository();
        repo.Write("web/package.json", "{ \"name\": \"web\", \"dependencies\": { \"next\": \"15.0.0\", \"@prisma/client\": \"6.0.0\" } }");
        repo.Write("web/src/app/api/me/form/route.ts", "import { prisma } from '../../../db';\nexport async function GET() { return Response.json(await prisma.form.findMany()); }");
        repo.Write("web/src/db.ts", "import { PrismaClient } from '@prisma/client';\nexport const prisma = new PrismaClient();");
        var g = await ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0 });
        var route = Tier(g, "web/src/app/api/me/form/route.ts");
        Assert.Equal((Tiers.Backend, TierDecisions.Automatic), (route.Tier, route.Decision));
        Assert.Contains(route.Evidence, e => e.Rule == "db-access" && e.Family == "usage"); // still explained
        Assert.Equal(Tiers.Data, Tier(g, "web/src/db.ts").Tier);
    }

    [Fact]
    public async Task FeaturesCrossTiersAndStartFromButtons()
    {
        using var repo = new FixtureRepository(); Populate(repo);
        var g = await ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0 });
        var v = g.Views!;
        var hex = Assert.Single(v.Features, f => f.Id == "feature:hex");
        Assert.Equal("Гексы", hex.Name);
        Assert.Equal(FeatureStates.Candidate, hex.State);
        Assert.True(hex.TierIds.Count >= 3, string.Join(",", hex.TierIds)); // interface, model, storage
        Assert.Contains(v.Features, f => f.Id == "feature:river" && f.Name == "Реки");
        Assert.DoesNotContain(v.Features, f => f.Terms.Contains("demo")); // the project's own name is not a feature
        Assert.Contains(v.Memberships, m => m.FeatureId == "feature:hex" && m.NodeId == "file:src/Demo.Core/Editing/HexEditing.cs" && m.Role == FeatureRoles.Implementation);
        Assert.Contains(v.Memberships, m => m.FeatureId == "feature:hex" && m.NodeId == "file:tests/Demo.Tests/HexGridTests.cs" && m.Role == FeatureRoles.Test);
        // the «Рисовать гексы» button: a handler node that calls into the feature
        var button = Assert.Single(g.Nodes, n => n.SubKind == "handler" && n.Name.Contains("Рисовать гексы"));
        var entry = Assert.Single(v.Memberships, m => m.FeatureId == "feature:hex" && m.NodeId == button.Id);
        Assert.Equal(FeatureRoles.Entry, entry.Role);
        // one node may belong to several features; memberships are unique per pair
        Assert.Equal(v.Memberships.Count, v.Memberships.Select(m => (m.FeatureId, m.NodeId)).Distinct().Count());
    }

    [Fact]
    public async Task RulesFileThenOwnerCorrectionsWinAndStaleOnesAreReported()
    {
        using var repo = new FixtureRepository(); Populate(repo);
        repo.Write(".projmap/views.rules.json", """
            {
              // comments are allowed
              "schemaVersion": 1,
              "rules": [ { "id": "misc-is-app", "match": { "pathGlob": "src/**/Misc/*.cs" }, "assign": { "tier": "application", "role": "useCase" }, "score": 0.9 } ],
              "overrides": { "tiers": [ { "pathGlob": "src/Demo.Persistence/**", "tier": "infrastructure", "reason": "общий слой" } ] }
            }
            """);
        var owner = new ViewsOverrides
        {
            Tiers = [new TierOverride { NodeId = "file:src/Demo.Persistence/HexStore.cs", Tier = Tiers.Data, Reason = "это хранение" },
                     new TierOverride { NodeId = "file:src/Gone.cs", Tier = Tiers.Data }],
            FeatureNames = { ["feature:river"] = "Реки и берега" },
            FeatureMerges = [new("feature:hex", ["feature:river"])],
            AddedFeatures = [new FeatureDefinition { Id = "feature:owner-save", Name = "Сохранение", PathGlobs = ["src/Demo.Persistence/*.cs"] }],
            Members = [new("feature:hex", "file:src/Demo.Core/Editing/HexEditing.cs", false)],
            HiddenFeatures = ["feature:nope"],
        };
        var g = await ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0, ViewOverrides = owner });
        var v = g.Views!;
        Assert.Equal("builtin:tiers-v1+.projmap/views.rules.json+owner", v.RuleSet);
        var misc = Tier(g, "src/Demo.Core/Misc/Thing.cs");
        Assert.Equal((Tiers.Application, "useCase"), (misc.Tier, misc.Role));
        Assert.Contains(misc.Evidence, e => e.Family == "rules" && e.Rule == "misc-is-app");
        var river = Tier(g, "src/Demo.Persistence/RiverStore.cs");
        Assert.Equal((Tiers.Infrastructure, TierDecisions.Override, "общий слой"), (river.Tier, river.Decision, river.Reason));
        var hexStore = Tier(g, "src/Demo.Persistence/HexStore.cs"); // owner's exact node beats the file's glob
        Assert.Equal((Tiers.Data, TierDecisions.Override, 1.0), (hexStore.Tier, hexStore.Decision, hexStore.Score));
        Assert.Contains(hexStore.Evidence, e => e.Rule == "file-format"); // the automatic evidence stays for the explanation

        Assert.DoesNotContain(v.Features, f => f.Id == "feature:river"); // merged into hex
        var hex = Assert.Single(v.Features, f => f.Id == "feature:hex");
        Assert.Equal(FeatureStates.Manual, hex.State);
        Assert.Contains("river", hex.Terms);
        Assert.Contains(v.Memberships, m => m.FeatureId == "feature:hex" && m.NodeId == "file:src/Demo.Core/Editing/RiverEditing.cs");
        Assert.DoesNotContain(v.Memberships, m => m.FeatureId == "feature:hex" && m.NodeId == "file:src/Demo.Core/Editing/HexEditing.cs");
        var save = Assert.Single(v.Features, f => f.Id == "feature:owner-save");
        Assert.Equal(2, save.Files);
        Assert.Contains("tier:file:src/Gone.cs", v.StaleOverrides);
        Assert.Contains("name:feature:river", v.StaleOverrides); // renamed after it was merged away: reported, not re-targeted
        Assert.Contains("hide:feature:nope", v.StaleOverrides);
    }

    [Fact]
    public async Task InvalidRulesFileIsAWarningAndBuiltinsStillApply()
    {
        using var repo = new FixtureRepository(); Populate(repo);
        repo.Write(".projmap/views.rules.json", "{ \"rules\": [ ");
        var g = await ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0 });
        Assert.Contains(g.Warnings, w => w.Scanner == "views" && w.Path == ViewRules.RulesPath);
        Assert.Equal(Tiers.Frontend, Tier(g, "src/Demo.App/Views/MainWindow.axaml").Tier);
    }

    [Fact]
    public async Task AllSerializersKeepViewsAndOldGraphsStillRead()
    {
        using var repo = new FixtureRepository(); Populate(repo);
        var g = await ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0 });
        var plain = GraphJson.Deserialize(GraphJson.Serialize(g))!;
        Assert.True(GraphDiff.Compare(g, plain).Views.Same);
        Assert.NotEmpty(plain.Views!.Memberships);
        using var stream = new MemoryStream();
        await GraphJson.WriteAsync(g, stream);
        var streamed = GraphJson.Deserialize(System.Text.Encoding.UTF8.GetString(stream.ToArray()))!;
        Assert.True(GraphDiff.Compare(g, streamed).Views.Same);
        using var compact = System.Text.Json.JsonDocument.Parse(GraphJson.SerializeCompact(g));
        Assert.Equal(g.Views!.Assignments.Count, compact.RootElement.GetProperty("views").GetProperty("assignments").GetArrayLength());

        // a graph written before `views` existed
        var old = GraphJson.Deserialize(GraphJson.Serialize(g with { Views = null }))!;
        Assert.Null(old.Views);
        Assert.DoesNotContain("\"views\":{", GraphJson.Serialize(old)); // ("views" is also a timing stage name)
        var diff = GraphDiff.Compare(old, g);
        Assert.False(diff.Views.Same);
        Assert.True(diff.Nodes.Same && diff.Edges.Same);
    }

    [Fact]
    public async Task IncrementalAndFullScansGiveTheSameViews()
    {
        using var repo = new FixtureRepository(); Populate(repo);
        var first = await ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0 }, null, null);
        var file = repo.Write("src/Demo.Core/Editing/HexBrush.cs", "namespace Demo; public static class HexBrush { public static void Stroke() => HexEditing.Paint(); }");
        var incremental = await ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0 }, first.State, [file]);
        var full = await ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0 });
        var diff = GraphDiff.Compare(full, incremental.Graph);
        Assert.True(diff.Views.Same, string.Join("; ", diff.Views.OnlyInA.Concat(diff.Views.OnlyInB).Concat(diff.Views.Changed)));
        Assert.Contains(full.Views!.Memberships, m => m.FeatureId == "feature:hex" && m.NodeId == "file:src/Demo.Core/Editing/HexBrush.cs");
        var again = await ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0 });
        Assert.True(GraphDiff.Compare(full, again).Views.Same);
    }

    [Fact]
    public async Task TimelineLinksStagesByProgressTableFirstThenBySubject()
    {
        using var repo = new FixtureRepository();
        repo.Write("App/App.csproj", Project);
        repo.Write("ROADMAP.md", "# План\n\n- [x] R1 Печать постером\n- [ ] R2 Атлас\n");
        repo.Write("App/Poster.cs", "namespace Demo; public class Poster { }");
        await repo.Git("add", "-A"); await repo.Git("commit", "-q", "-m", "feat: poster printing");
        var sha = (await GitOut(repo.Root, "rev-parse", "HEAD")).Trim();
        repo.Write("App/Atlas.cs", "namespace Demo; public class Atlas { }");
        repo.Write("App/Poster.cs", "namespace Demo; public class Poster { public int Size; }");
        await repo.Git("add", "-A"); await repo.Git("commit", "-q", "-m", "feat(R2): atlas, R1 mentioned in passing");
        // the progress table names the R1 commit explicitly: that wins over the subject of the second commit
        repo.Write("PROGRESS.md", $"| Этап | Коммиты |\n|---|---|\n| R1 Печать | {sha[..7]} |\n");
        await repo.Git("add", "-A"); await repo.Git("commit", "-q", "-m", "docs: progress");
        var g = await ProjectScanner.ScanAsync(new(repo.Root));
        var t = g.Views!.Timeline!;
        Assert.Equal(3, t.Commits.Count);
        Assert.Contains("3 коммитов", t.Coverage);
        var r1 = Assert.Single(t.Stages, s => s.Key == "R1");
        Assert.Equal("explicit", r1.Link);
        Assert.Equal([sha], r1.CommitIds);
        Assert.Equal("Печать постером", r1.Title);
        var r2 = Assert.Single(t.Stages, s => s.Key == "R2");
        Assert.Equal("subject", r2.Link);
        var r2Files = t.Commits.Single(c => r2.CommitIds.Contains(c.Id)).Files.Select(i => t.Files[i]).Order().ToList();
        Assert.Equal(["file:App/Atlas.cs", "file:App/Poster.cs"], r2Files);
        // without git there is no timeline, and the rest of the views still work
        repo.DisableGit();
        var noGit = await ProjectScanner.ScanAsync(new(repo.Root));
        Assert.Null(noGit.Views!.Timeline);
        Assert.NotEmpty(noGit.Views.Assignments);
    }

    private static async Task<string> GitOut(string root, params string[] args)
    {
        using var p = new System.Diagnostics.Process { StartInfo = new("git") { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true } };
        foreach (var a in args) p.StartInfo.ArgumentList.Add(a);
        p.Start();
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return output;
    }

    [Theory]
    [InlineData("src/**/*.cs", "src/a/b/C.cs", true)]
    [InlineData("src/**/*.cs", "src/C.cs", true)]
    [InlineData("src/*.cs", "src/a/C.cs", false)]
    [InlineData("**/*.{xaml,axaml}", "x/Main.AXAML", true)]
    [InlineData("docs/?.md", "docs/a.md", true)]
    public void GlobsFollowTheDocumentedSyntax(string glob, string path, bool expected) =>
        Assert.Equal(expected, ViewRules.Glob(glob).IsMatch(path));
}
