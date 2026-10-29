using Microsoft.Data.Sqlite;
using ProjMap.Analysis;
using ProjMap.Cli;
using ProjMap.Core;

namespace ProjMap.Tests;

public sealed class ScannerTests
{
    private const string Project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>";
    [Fact]
    public async Task CSharpBindsAcrossProjectsAndPreservesFineEdges()
    {
        using var repo = new FixtureRepository();
        repo.Write("Core/Core.csproj", Project);
        repo.Write("Core/Base.cs", "namespace Sample; public interface IWorker { void Run(); } public record Base { public virtual void Run() {} }");
        repo.Write("Core/Service.cs", "namespace Sample; /// <summary>Useful service.</summary>\npublic class Service { public static void Work() {} }");
        repo.Write("App/App.csproj", Project.Replace("</Project>", "<ItemGroup><ProjectReference Include=\"../Core/Core.csproj\"/><PackageReference Include=\"Unresolved.Package\" Version=\"1.0\"/></ItemGroup></Project>"));
        repo.Write("App/Worker.cs", "namespace Sample; public record Worker : Base, IWorker { public Worker() {} public int Value { get; set; } public int Visible; private int Secret; public override void Run() { if (Value > 0 && Value < 10) Service.Work(); Service.Work(); } }");
        var before = repo.Snapshot();
        var graph = await ProjectScanner.ScanAsync(new(repo.Root));
        Assert.Empty(graph.Warnings);
        Assert.Contains(graph.Nodes, n => n.Name == "Service" && n.Summary == "Useful service.");
        Assert.Contains(graph.Nodes, n => n.Name == "Worker" && n.SubKind == "record");
        var run = Assert.Single(graph.Nodes, n => n.Kind == NodeKinds.Member && n.Name == "Run" && n.Path == "App/Worker.cs");
        Assert.Equal(3, run.Metrics.Complexity);
        Assert.Contains(graph.Nodes, n => n.SubKind == "ctor");
        Assert.Contains(graph.Nodes, n => n.SubKind == "property");
        Assert.Contains(graph.Nodes, n => n.SubKind == "field" && n.Name == "Visible");
        Assert.DoesNotContain(graph.Nodes, n => n.Kind == NodeKinds.Member && n.Name == "Secret");
        Assert.Contains(graph.Edges, e => e.Kind == EdgeKinds.ProjectRef && e.Source == "module:App/App.csproj" && e.Target == "module:Core/Core.csproj");
        Assert.Contains(graph.Edges, e => e.Kind == EdgeKinds.Package && e.Target == "pkg:Unresolved.Package");
        Assert.Contains(graph.Edges, e => e.Kind == EdgeKinds.Inherit && e.Source.StartsWith("type:") && e.Target.EndsWith(":Sample.Base"));
        Assert.Contains(graph.Edges, e => e.Kind == EdgeKinds.Implement && e.Target.EndsWith(":Sample.IWorker"));
        // finest level only: no file-level duplicate of a type-level edge (the viewer rolls up)
        Assert.DoesNotContain(graph.Edges, e => e.Source == "file:App/Worker.cs" && e.Target == "file:Core/Service.cs");
        // function level: Worker.Run() calls Service.Work() twice; the property read stays inside Worker
        Assert.Contains(graph.Edges, e => e.Kind == EdgeKinds.Call && e.Source.EndsWith(":M:Sample.Worker.Run") && e.Target.EndsWith(":M:Sample.Service.Work") && e.Weight == 2);
        // A call through IWorker.Run or Base.Run reaches Worker.Run: the member links to the contract it fulfils.
        Assert.Contains(graph.Edges, e => e.Kind == EdgeKinds.Implement && e.Source.EndsWith(":M:Sample.Worker.Run") && e.Target.EndsWith(":M:Sample.IWorker.Run"));
        Assert.Contains(graph.Edges, e => e.Kind == EdgeKinds.Inherit && e.Source.EndsWith(":M:Sample.Worker.Run") && e.Target.EndsWith(":M:Sample.Base.Run"));
        Assert.Contains(graph.Edges, e => e.Kind == EdgeKinds.Reference && e.Source.EndsWith(":M:Sample.Worker.Run") && e.Target.EndsWith(":P:Sample.Worker.Value"));
        Assert.All(graph.Edges, e => Assert.InRange(e.Strength, 0.0, 1.0));
        Assert.Equal(before.OrderBy(p => p.Key), repo.Snapshot().OrderBy(p => p.Key));
        AssertGraph(graph);
        var again = await ProjectScanner.ScanAsync(new(repo.Root));
        Assert.Equal(graph.Nodes.Select(n => n.Id), again.Nodes.Select(n => n.Id));
        Assert.Equal(graph.Edges.Select(e => (e.Id, e.Weight)), again.Edges.Select(e => (e.Id, e.Weight)));
    }
    [Fact]
    public async Task ImportsResolveAliasesReexportsWorkspacesAndPythonPackages()
    {
        using var repo = new FixtureRepository();
        repo.Write("package.json", "{\"name\":\"root\",\"workspaces\":[\"packages/*\"],\"dependencies\":{\"lib\":\"workspace:*\"}}");
        repo.Write("packages/lib/package.json", "{\"name\":\"lib\"}");
        repo.Write("packages/lib/index.ts", "export class Widget {}\nexport function create() {} ");
        repo.Write("tsconfig.json", "{ // config\n\"compilerOptions\":{\"baseUrl\":\".\",\"paths\":{\"@/*\":[\"src/*\"]}},}");
        repo.Write("src/main.ts", "import { value } from '@/value';\nexport { value } from './value.js';\nconst x = require('lib');\nimport './side';\nimport 'src/side';");
        repo.Write("src/value.ts", "export const value = () => 1;");
        repo.Write("src/side.js", "export function side() {}");
        repo.Write("pyproject.toml", "[project]\nname='sample'");
        repo.Write("pack/__init__.py", "");
        repo.Write("pack/a.py", "from . import b\nimport pack.b as helper\ndef run():\n    pass\nclass Runner:\n    pass");
        repo.Write("pack/b.py", "def helper(): pass");
        var graph = await ProjectScanner.ScanAsync(new(repo.Root));
        Assert.Empty(graph.Warnings);
        Assert.Contains(graph.Edges, e => e.Kind == EdgeKinds.Import && e.Source == "file:src/main.ts" && e.Target == "file:src/value.ts" && e.Weight == 2);
        Assert.Contains(graph.Edges, e => e.Kind == EdgeKinds.Import && e.Target == "file:packages/lib/index.ts");
        Assert.Contains(graph.Edges, e => e.Kind == EdgeKinds.Import && e.Target == "file:src/side.js" && e.Weight == 2);
        Assert.Contains(graph.Edges, e => e.Kind == EdgeKinds.Import && e.Source == "file:pack/a.py" && e.Target == "file:pack/b.py");
        Assert.Contains(graph.Edges, e => e.Kind == EdgeKinds.ProjectRef && e.Target == "module:packages/lib/package.json");
        Assert.Contains(graph.Nodes, n => n.Kind == NodeKinds.Type && n.Name == "Widget");
        Assert.Contains(graph.Nodes, n => n.Kind == NodeKinds.Member && n.Name == "run");
        AssertGraph(graph);
    }
    [Fact]
    public async Task RoadmapHandlesRussianEnglishTablesNestingAndDecisions()
    {
        using var repo = new FixtureRepository();
        repo.Write("Core/Core.csproj", Project);
        repo.Write("Core/Engine.cs", "namespace Demo; public class Engine {}");
        repo.Write("docs/ROADMAP.md", "# План\n## Этап 1\n- [x] Готовый Core/Engine.cs\n- [ ] Активный Engine — в работе\n  - [ ] Подзадача blocked\n- [ ] Отложено: deferred\n\n| Task | Status |\n|---|---|\n| Future Core | planned |\n| Ready | ✅ |\n| Partial | 🔀 PARTIAL |\n```md\n- [ ] fake\n```\n");
        repo.Write("docs/DECISIONS.md", "# Решения\n## DEC-070 — интерфейс\nСвязано с Engine и Core.\n");
        repo.Write("docs/archive/TODO.md", "- [ ] archived");
        repo.Write("docs/_archive_2026/TODO.md", "- [ ] archived");
        var graph = await ProjectScanner.ScanAsync(new(repo.Root));
        Assert.Empty(graph.Warnings);
        Assert.Contains(graph.Roadmap, r => r.Title.StartsWith("Готовый") && r.Status == "done" && r.Links.Contains("file:Core/Engine.cs"));
        var active = Assert.Single(graph.Roadmap, r => r.Title.StartsWith("Активный"));
        Assert.Equal("active", active.Status);
        Assert.Contains(graph.Roadmap, r => r.Title.StartsWith("Подзадача") && r.Status == "blocked" && r.ParentId == active.Id);
        Assert.Contains(graph.Roadmap, r => r.Status == "deferred");
        Assert.Contains(graph.Roadmap, r => r.Title == "Future Core" && r.Status == "planned");
        Assert.Contains(graph.Roadmap, r => r.Title == "Partial" && r.Status == "active");
        Assert.Contains(graph.Roadmap, r => r.Title.StartsWith("DEC-070") && r.Links.Any(id => id.EndsWith(":Demo.Engine")));
        Assert.DoesNotContain(graph.Roadmap, r => r.Title is "fake" or "archived");
        Assert.Equal(4, graph.Nodes.Count(n => n.Kind == NodeKinds.Doc));
        AssertGraph(graph);
    }
    [Fact]
    public async Task SqlAndActualSqliteSchemaAreReadWithoutChangingAnyFile()
    {
        using var repo = new FixtureRepository();
        repo.Write("schema.sql", "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL);\nCREATE TABLE posts (id INT, user_id INTEGER REFERENCES users(id), amount DECIMAL(10,2), PRIMARY KEY(id));\nCREATE TABLE pairs (a INT, b INT, FOREIGN KEY(a,b) REFERENCES posts(id,user_id));");
        repo.Write("query.py", "query = 'SELECT * FROM users JOIN posts ON posts.user_id = users.id'\nwrite = 'INSERT INTO posts VALUES (1,2,3)'\nupdate = 'UPDATE users SET name = ?'\ndelete = 'DELETE FROM posts WHERE id = 1'");
        var path = Path.Combine(repo.Root, "actual.db");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE actual (id INTEGER PRIMARY KEY, user_id INT REFERENCES users(id));";
            await command.ExecuteNonQueryAsync();
        }
        var before = repo.Snapshot();
        var graph = await ProjectScanner.ScanAsync(new(repo.Root));
        Assert.Empty(graph.Warnings);
        Assert.Equal("sqlite", graph.Database?.Engine);
        Assert.Equal(4, graph.Database!.Tables.Count);
        Assert.Contains(graph.Database.Tables, t => t.Name == "actual" && t.Columns.Any(col => col.Name == "id" && col.PrimaryKey));
        var posts = Assert.Single(graph.Database.Tables, t => t.Name == "posts");
        Assert.Contains(posts.Columns, col => col.Name == "amount" && col.Type == "DECIMAL(10,2)");
        Assert.Contains(posts.ForeignKeys, fk => fk.Column == "user_id" && fk.RefTable == "users" && fk.RefColumn == "id");
        Assert.Contains(posts.Columns, col => col.Name == "id" && col.PrimaryKey);
        Assert.Contains(graph.Edges, e => e.Kind == EdgeKinds.DbRead && e.Source == "file:query.py" && e.Target == "table:users");
        Assert.Contains(graph.Edges, e => e.Kind == EdgeKinds.DbRead && e.Source == "file:query.py" && e.Target == "table:posts" && e.Weight == 1);
        Assert.Contains(graph.Edges, e => e.Kind == EdgeKinds.DbWrite && e.Target == "table:posts" && e.Weight == 2);
        Assert.Equal(before.OrderBy(p => p.Key), repo.Snapshot().OrderBy(p => p.Key));
        AssertGraph(graph);
    }
    [Fact]
    public async Task EfSnapshotAndPrismaYieldColumnsAndRelations()
    {
        using var repo = new FixtureRepository();
        repo.Write("Store/Store.csproj", Project.Replace("</Project>", "<ItemGroup><PackageReference Include=\"Npgsql.EntityFrameworkCore.PostgreSQL\" Version=\"10\"/></ItemGroup></Project>"));
        repo.Write("Store/Context.cs", "class User { public int Id { get; set; } public string Name { get; set; } } class Context { public DbSet<User> Users { get; set; } }");
        repo.Write("Store/Snapshot.cs", "class Snapshot { void Build() { builder.Entity(\"Demo.Audit\", b => { b.Property<int>(\"Id\").HasColumnType(\"integer\"); b.HasKey(\"Id\"); b.ToTable(\"audit\"); }); } }");
        repo.Write("schema.prisma", "datasource db { provider = \"postgresql\" }\nmodel Post {\n id Int @id\n ownerId Int\n owner User @relation(fields: [ownerId], references: [Id])\n title String?\n}");
        var graph = await ProjectScanner.ScanAsync(new(repo.Root));
        Assert.Empty(graph.Warnings);
        Assert.Equal("postgres", graph.Database?.Engine);
        Assert.Contains(graph.Database!.Tables, t => t.Name == "Users" && t.Columns.Any(col => col.Name == "Id" && col.PrimaryKey));
        Assert.Contains(graph.Database.Tables, t => t.Name == "audit" && t.Columns.Any(col => col.PrimaryKey));
        Assert.Contains(graph.Database.Tables, t => t.Name == "Post" && t.ForeignKeys.Any(fk => fk.Column == "ownerId"));
    }
    [Fact]
    public async Task GitCochangeThresholdConfidenceAndDecisionCommitLinksWork()
    {
        using var repo = new FixtureRepository();
        await repo.Git("init", "-q");
        repo.Write(".gitignore", "ignored/\n");
        repo.Write("docs/DECISIONS.md", "# Decisions\n## DEC-070 Layout\n");
        repo.Write("ignored/private.ts", "never scanned");
        for (var i = 0; i < 3; i++)
        {
            repo.Write("a.py", $"x = {i}"); repo.Write("b.py", $"x = {i}");
            await repo.Git("add", "."); await repo.Git("commit", "-q", "-m", $"DEC-070 layout part {i}");
        }
        repo.Write("untracked.ts", "export class Pending {}");
        var before = repo.Snapshot();
        var graph = await ProjectScanner.ScanAsync(new(repo.Root));
        Assert.Empty(graph.Warnings);
        Assert.Equal(3, graph.Nodes.Single(n => n.Id == "file:a.py").Metrics.Commits);
        Assert.Equal(1, graph.Nodes.Single(n => n.Id == "file:a.py").Metrics.Authors);
        var cochange = Assert.Single(graph.Edges, e => e.Kind == EdgeKinds.CoChange);
        Assert.Equal(3, cochange.Weight); Assert.Equal(1, cochange.Strength);
        Assert.Equal("file:a.py", cochange.Source); Assert.Equal("file:b.py", cochange.Target);
        var decision = Assert.Single(graph.Roadmap, r => r.Title.StartsWith("DEC-070"));
        Assert.Contains("file:a.py", decision.Links);
        Assert.Contains("DEC-070 layout part 2", decision.Detail);
        Assert.Contains(graph.Edges, e => e.Source == decision.Id && e.Target == "file:b.py" && e.Evidence.Any(l => l.Snippet!.Contains("DEC-070")));
        Assert.DoesNotContain(graph.Nodes, n => n.Path == "ignored/private.ts");
        Assert.Contains(graph.Nodes, n => n.Path == "untracked.ts");
        Assert.Equal(before.OrderBy(p => p.Key), repo.Snapshot().OrderBy(p => p.Key));
        AssertGraph(graph);
    }
    [Fact]
    public async Task StructureExcludesBuildOutputAndTestsAndRespectsNoMembers()
    {
        using var repo = new FixtureRepository();
        repo.Write(".gitignore", "hidden/\n*.secret\n");
        repo.Write("hidden/a.cs", "class Secret {}"); repo.Write("x.secret", "secret");
        repo.Write("Core/Core.csproj", Project);
        repo.Write("Core/Views/Panel.g.cs", "class Panel { public void Open() {} }\n\n");
        repo.Write("checks/Spec.csproj", Project.Replace("</PropertyGroup>", "<IsTestProject>true</IsTestProject></PropertyGroup>"));
        repo.Write("checks/sub/Thing.cs", "class TestThing {}");
        foreach (var directory in new[] { "bin", "obj", "node_modules", ".git", ".vs", "dist", "build", "__pycache__", ".venv", ".projmap" }) repo.Write(directory + "/noise.cs", "class Noise {}");
        var graph = await ProjectScanner.ScanAsync(new(repo.Root) { IncludeMembers = false, IncludeTests = false });
        Assert.Empty(graph.Warnings);
        Assert.DoesNotContain(graph.Nodes, n => n.Kind == NodeKinds.Member || n.Path?.Contains("noise") == true || n.Path?.StartsWith("checks") == true || n.Path?.StartsWith("hidden/") == true || n.Path == "x.secret");
        var panel = graph.Nodes.Single(n => n.Id == "file:Core/Views/Panel.g.cs");
        Assert.Contains("generated", panel.Tags); Assert.Contains("ui", panel.Tags);
        Assert.Equal(1, panel.Metrics.Loc);
        AssertGraph(graph);
    }
    [Fact]
    public async Task InvalidInputsBecomeWarningsAndOtherScannersContinue()
    {
        using var repo = new FixtureRepository();
        repo.Write("broken.csproj", "<Project>"); repo.Write("package.json", "{");
        repo.Write("broken.db", "not a database"); repo.Write("TODO.md", "- [ ] still parsed");
        var graph = await ProjectScanner.ScanAsync(new(repo.Root));
        Assert.Contains(graph.Warnings, w => w.Scanner == "structure");
        Assert.Contains(graph.Warnings, w => w.Scanner == "database");
        Assert.Contains(graph.Roadmap, r => r.Title == "still parsed");
    }
    [Fact]
    public async Task CliWritesDefaultOutputHintsWithoutEditingGitignoreAndPrintsStats()
    {
        using var repo = new FixtureRepository();
        var ignore = repo.Write(".gitignore", "bin/\n"); repo.Write("main.py", "def start(): pass");
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(0, await CommandLine.RunAsync(["scan", repo.Root, "--no-members", "--git-months", "0"], output, error));
        Assert.Equal("bin/\n", File.ReadAllText(ignore)); Assert.Contains("Hint", error.ToString());
        var path = Path.Combine(repo.Root, ".projmap", "graph.json");
        Assert.True(File.Exists(path));
        var graph = GraphJson.Deserialize(File.ReadAllText(path))!;
        Assert.DoesNotContain(graph.Nodes, n => n.Kind == NodeKinds.Member);
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await CommandLine.RunAsync(["stats", path], output, error));
        Assert.Contains("file: 2", output.ToString()); Assert.Contains("Edges: 0", output.ToString());
        Assert.Equal(2, await CommandLine.RunAsync([], output, error));
        Assert.Equal(1, await CommandLine.RunAsync(["scan", repo.Root, "--git-months", "oops"], output, error));
    }
    [Fact]
    public async Task CancellationAndProgressAreExposed()
    {
        using var repo = new FixtureRepository();
        var reports = new List<ScanProgress>();
        await ProjectScanner.ScanAsync(new(repo.Root) { Progress = new CallbackProgress(reports.Add) });
        Assert.Equal(0, reports.First().Fraction); Assert.Equal(1, reports.Last().Fraction);
        Assert.Equal(8, reports.Count);
        using var source = new CancellationTokenSource(); source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectScanner.ScanAsync(new(repo.Root), source.Token));
    }
    [Fact]
    public async Task ModulesAndFoldersTakeDescriptionsFromDocs()
    {
        using var repo = new FixtureRepository();
        repo.Write("src/Shop.Core/Shop.Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        repo.Write("src/Shop.Core/Orders/Order.cs", "namespace Shop; public class Order {}");
        repo.Write("docs/ARCHITECTURE.md", "# Arch\n\nShop.Core      orders, prices and stock rules\n- `Shop.Core/Orders` — order lifecycle and totals\nShop.Core is great\n");
        var graph = await ProjectScanner.ScanAsync(new(repo.Root));
        Assert.Equal("orders, prices and stock rules", Assert.Single(graph.Nodes, n => n.Kind == NodeKinds.Module).Summary);
        Assert.Equal("order lifecycle and totals", Assert.Single(graph.Nodes, n => n.Kind == NodeKinds.Folder && n.Name == "Orders").Summary);
    }
    [Fact]
    public async Task FallbackWalkHonorsNestedIgnoresAndNegations()
    {
        using var repo = new FixtureRepository();
        repo.DisableGit();
        repo.Write(".gitignore", "git-backup/\nhidden/\n*.tmp\n!keep.tmp\n");
        repo.Write("hidden/secret.py", "never"); repo.Write("drop.tmp", "never"); repo.Write("keep.tmp", "keep");
        repo.Write("src/.gitignore", "private.py\n"); repo.Write("src/private.py", "never"); repo.Write("src/main.py", "x=1");
        var graph = await ProjectScanner.ScanAsync(new(repo.Root));
        Assert.Empty(graph.Warnings);
        Assert.Contains(graph.Nodes, n => n.Path == "keep.tmp"); Assert.Contains(graph.Nodes, n => n.Path == "src/main.py");
        Assert.DoesNotContain(graph.Nodes, n => n.Path is "drop.tmp" or "hidden/secret.py" or "src/private.py");
        Assert.Null(graph.Project.GitHead);
    }
    [Fact]
    public async Task PartialTypesOverloadsAndDuplicateNamesHaveStableDistinctIdentities()
    {
        using var repo = new FixtureRepository();
        repo.Write("one/Thing.csproj", Project); repo.Write("two/Thing.csproj", Project);
        repo.Write("one/A.cs", "namespace Shared; public partial class Thing { public void Run(int x) {} }");
        repo.Write("one/B.cs", "namespace Shared; public partial class Thing { public void Run(string x) {} }");
        repo.Write("two/A.cs", "namespace Shared; public class Thing { public void Run() {} }");
        var graph = await ProjectScanner.ScanAsync(new(repo.Root));
        Assert.Empty(graph.Warnings); AssertGraph(graph);
        Assert.Equal(2, graph.Nodes.Count(n => n.Kind == NodeKinds.Module && n.Name == "Thing"));
        Assert.Equal(2, graph.Nodes.Count(n => n.Kind == NodeKinds.Type && n.Name == "Thing"));
        Assert.Equal(3, graph.Nodes.Count(n => n.Kind == NodeKinds.Member && n.Name == "Run"));
        var file = repo.Write("one/A.cs", "\n\nnamespace Shared; public partial class Thing { public void Run(int x) {} }");
        var again = await ProjectScanner.ScanAsync(new(repo.Root));
        Assert.Equal(graph.Nodes.Select(n => n.Id), again.Nodes.Select(n => n.Id));
        Assert.True(File.Exists(file));
    }
    [Fact]
    public async Task GitSkipsBulkCommitsRejectsLowConfidenceAndHonorsMaxCommits()
    {
        using var repo = new FixtureRepository();
        for (var i = 0; i < 3; i++)
        {
            repo.Write("a.py", $"x={i}"); repo.Write("b.py", $"x={i}");
            for (var j = 0; j < 41; j++) repo.Write($"bulk/{j}.txt", $"{i}");
            await repo.Git("add", "."); await repo.Git("commit", "-q", "-m", "bulk");
        }
        var bulk = await ProjectScanner.ScanAsync(new(repo.Root));
        Assert.DoesNotContain(bulk.Edges, e => e.Kind == EdgeKinds.CoChange);
        for (var i = 3; i < 6; i++)
        {
            repo.Write("a.py", $"x={i}"); repo.Write("b.py", $"x={i}");
            await repo.Git("add", "."); await repo.Git("commit", "-q", "-m", "pair");
        }
        for (var i = 6; i < 11; i++)
        {
            repo.Write("a.py", $"x={i}"); await repo.Git("add", "."); await repo.Git("commit", "-q", "-m", "a only");
        }
        var graph = await ProjectScanner.ScanAsync(new(repo.Root));
        Assert.DoesNotContain(graph.Edges, e => e.Kind == EdgeKinds.CoChange);
        var limited = await ProjectScanner.ScanAsync(new(repo.Root) { MaxCommits = 2 });
        Assert.Equal(2, limited.Nodes.Single(n => n.Id == "file:a.py").Metrics.Commits);
        Assert.Equal(0, limited.Nodes.Single(n => n.Id == "file:b.py").Metrics.Commits);
    }
    [Fact]
    public async Task ThousandCSharpFilesAreScannedUnderAMinute()
    {
        using var repo = new FixtureRepository();
        for (var module = 0; module < 4; module++)
        {
            repo.Write($"M{module}/M{module}.csproj", Project);
            for (var file = 0; file < 250; file++) repo.Write($"M{module}/T{file}.cs", $"namespace M{module}; public class T{file} {{ public int Next(int value) => value > 0 ? value : 0; }}");
        }
        var graph = await ProjectScanner.ScanAsync(new(repo.Root));
        Assert.Empty(graph.Warnings);
        Assert.Equal(1000, graph.Nodes.Count(n => n.Kind == NodeKinds.Type));
        Assert.Equal(1000, graph.Nodes.Count(n => n.Kind == NodeKinds.Member));
        Assert.True(graph.Project.ScanMilliseconds < 60_000, $"Scan took {graph.Project.ScanMilliseconds} ms");
        AssertGraph(graph);
    }
    [Fact]
    public async Task WalDatabasesAreSkippedWithWarningAndNoSideEffects()
    {
        using var repo = new FixtureRepository();
        repo.Write("live.sqlite", "not opened"); repo.Write("live.sqlite-wal", "wal");
        var before = repo.Snapshot();
        var graph = await ProjectScanner.ScanAsync(new(repo.Root));
        Assert.Contains(graph.Warnings, w => w.Scanner == "database" && w.Message.Contains("WAL"));
        Assert.Equal(before.OrderBy(p => p.Key), repo.Snapshot().OrderBy(p => p.Key));
    }
    [Fact]
    public async Task PythonRepositoryInitFileDefinesRootModule()
    {
        using var repo = new FixtureRepository();
        repo.Write("__init__.py", ""); repo.Write("main.py", "def run(): pass");
        var graph = await ProjectScanner.ScanAsync(new(repo.Root));
        var module = Assert.Single(graph.Nodes, n => n.Kind == NodeKinds.Module);
        Assert.Equal("Python", module.Language);
        Assert.Equal(module.Id, graph.Nodes.Single(n => n.Id == "file:main.py").ParentId);
    }
    private sealed class CallbackProgress(Action<ScanProgress> action) : IProgress<ScanProgress> { public void Report(ScanProgress value) => action(value); }
    private static void AssertGraph(ProjectGraph graph)
    {
        var ids = graph.Nodes.Select(n => n.Id).Concat(graph.Roadmap.Select(r => r.Id)).ToHashSet();
        Assert.Equal(graph.Nodes.Count, graph.Nodes.Select(n => n.Id).Distinct().Count());
        Assert.All(graph.Nodes.Where(n => n.ParentId != null), n => Assert.Contains(n.ParentId!, ids));
        Assert.All(graph.Roadmap.Where(n => n.ParentId != null), n => Assert.Contains(n.ParentId!, ids));
        Assert.All(graph.Edges, e => { Assert.Contains(e.Source, ids); Assert.True(ids.Contains(e.Target) || e.Target.StartsWith("pkg:")); Assert.NotEqual(e.Source, e.Target); });
        var roundtrip = GraphJson.Deserialize(GraphJson.Serialize(graph));
        Assert.NotNull(roundtrip); Assert.Equal(graph.Nodes.Count, roundtrip.Nodes.Count);
    }
}
