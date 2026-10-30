using System.Text;
using System.Text.Json;
using ProjMap.Analysis;
using ProjMap.Core;

namespace ProjMap.Tests;

public sealed class ExplanationTests
{
    const string Project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";
    static NodeExplanation Profile(ProjectGraph graph, string id) => Assert.Single(graph.Views!.Explanations!.Nodes, n => n.NodeId == id);
    static IEnumerable<ExplanationClaim> Claims(NodeExplanation profile) => new[] { profile.DisplayName, profile.Description }.Concat(profile.Claims ?? []);
    static string Stable(ProjectGraph graph) => GraphJson.Serialize(graph with
    { Project = graph.Project with { ScannedAt = default, ScanMilliseconds = 0, Timings = null } });
    static Task<ProjectGraph> Scan(FixtureRepository repo, ViewsOverrides? overrides = null) => ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0, ViewOverrides = overrides });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReferencedProjectControlFactoryKeepsSemanticModelsInTheirOwningCompilation(bool partialField)
    {
        using var repo = new FixtureRepository();
        repo.Write("Controls/Controls.csproj", Project);
        repo.Write("Controls/Factory.cs", """
            using System;
            namespace Controls;
            public class Button { public event Action Click; }
            public static class Factory {
                public static Button Make() { var button = new Button(); return button; }
                public static void Export() { }
            }
            """);
        repo.Write("App/App.csproj", Project.Replace("</Project>", "<ItemGroup><ProjectReference Include=\"../Controls/Controls.csproj\" /></ItemGroup></Project>"));
        var panel = """
            using Controls;
            public partial class Panel {
                public void Build() {
                    var button = Factory.Make();
                    button.Click += () => Factory.Export();
                }
            }
            """;
        if (partialField)
        {
            repo.Write("App/Panel.State.cs", "using Controls; public partial class Panel { private Button button = Factory.Make(); }");
            panel = panel.Replace("var button = Factory.Make();", "");
        }
        repo.Write("App/Panel.cs", panel);
        var options = new ScanOptions(repo.Root) { GitMonths = 0 };
        var first = await ProjectScanner.ScanAsync(options, null, null);
        Assert.Empty(first.Graph.Warnings);
        var graph = first.Graph;
        var handler = Assert.Single(graph.Nodes, n => n.SubKind == "handler");
        var export = Assert.Single(graph.Nodes, n => n.Name == "Export");
        var make = Assert.Single(graph.Nodes, n => n.Name == "Make");
        var build = Assert.Single(graph.Nodes, n => n.Name == "Build");
        Assert.Contains(graph.Edges, e => e.Kind == "call" && e.Source == handler.Id && e.Target == export.Id);
        var factoryCaller = partialField ? Assert.Single(graph.Nodes, n => n.Kind == "type" && n.Name == "Panel") : build;
        Assert.Contains(graph.Edges, e => e.Kind == "call" && e.Source == factoryCaller.Id && e.Target == make.Id);
        Assert.Contains("not-shown", handler.Tags);
        Assert.Single(graph.Views!.EntryPoints!);
        var repeated = await ProjectScanner.ScanAsync(options, first.State, []);
        Assert.Empty(repeated.Graph.Warnings);
        Assert.Equal(Stable(graph), Stable(repeated.Graph));
    }

    [Fact]
    public async Task CommentsDocumentsAndNamesKeepIndependentConfidenceAndOpenableSources()
    {
        using var repo = new FixtureRepository();
        repo.Write("Demo/Demo.csproj", Project);
        repo.Write("Demo/Orders/MapProjectStore.cs", """
            namespace Demo;
            /// <summary>
            /// Читает и записывает проект карты. Второе предложение.
            /// </summary>
            public class MapProjectStore {
                /// <summary>Saves the project.</summary>
                public void Save() { }
            }
            """);
        repo.Write("README.md", "# Structure\n\nDemo  Правила и хранение данных проекта\n- `Demo/Orders` — Работа с заказами проекта\n");
        var graph = await Scan(repo);
        Assert.Empty(graph.Warnings);
        var type = Assert.Single(graph.Nodes, n => n.Kind == "type");
        var profile = Profile(graph, type.Id);
        Assert.Equal("guessed", profile.DisplayName.Confidence);
        Assert.Equal("known", profile.Description.Confidence);
        Assert.Equal("По комментарию автора: «Читает и записывает проект карты».", profile.Description.Text);
        var source = graph.Views!.Explanations!.Sources[Assert.Single(profile.Description.SourceIds)];
        Assert.Equal(("comment", "Demo/Orders/MapProjectStore.cs", 2), (source.Kind, source.Path, source.Line));
        Assert.Equal(64, source.SourceHash!.Length);
        Assert.DoesNotContain("///", source.Text);
        var module = Assert.Single(graph.Nodes, n => n.Kind == "module");
        Assert.Equal("README.md", module.SummarySource!.Path);
        Assert.Equal(3, module.SummarySource.Line);
        Assert.Equal("known", Profile(graph, module.Id).Description.Confidence);
        var method = Assert.Single(graph.Nodes, n => n.Name == "Save");
        Assert.Equal("derived", Profile(graph, method.Id).Description.Confidence); // English text retained, not machine-translated.
        Assert.Contains(Profile(graph, method.Id).Claims!, claim => claim.Confidence == "known" && claim.Text.Contains("Saves the project"));
        Assert.All(graph.Views.Explanations.Nodes.SelectMany(Claims).SelectMany(x => x.SourceIds), id => Assert.InRange(id, 0, graph.Views.Explanations.Sources.Count - 1));
    }

    [Fact]
    public async Task PartialCommentsStayWithTheirOwnDeclarationsAndNeverDescribeTheFirstHelperAsTheFile()
    {
        using var repo = new FixtureRepository(); repo.Write("Demo.csproj", Project);
        repo.Write("EditorViewModel.HexWorkshop.cs", """
            namespace Demo;
            /// <summary>A row in a key table.</summary>
            public record HexKeyRow(int Key);
            /// <summary>Workshop tools: regions, fog and paths.</summary>
            public partial class EditorViewModel { public void Paint() {} }
            """);
        repo.Write("EditorViewModel.cs", "namespace Demo;\n/// <summary>Editor state.</summary>\npublic partial class EditorViewModel { public void Save() {} }");
        var graph = await Scan(repo);
        var type = Assert.Single(graph.Nodes, n => n.Kind == "type" && n.Name == "EditorViewModel");
        Assert.Equal(2, type.Declarations!.Count);
        Assert.Equal("Workshop tools: regions, fog and paths.", type.Declarations[0].Summary);
        Assert.Equal("Editor state.", type.Declarations[1].Summary);
        Assert.Equal("derived", Profile(graph, type.Id).Description.Confidence);
        Assert.Contains("нескольких файлах", Profile(graph, type.Id).Description.Text);
        var file = Profile(graph, "file:EditorViewModel.HexWorkshop.cs");
        Assert.Contains("часть блока EditorViewModel", file.Description.Text);
        Assert.Contains(file.Claims!, claim => claim.Text.Contains("Workshop tools"));
        Assert.DoesNotContain(file.Claims!, claim => claim.Text.Contains("row in a key table"));
        var helper = Assert.Single(graph.Nodes, n => n.Name == "HexKeyRow");
        Assert.DoesNotContain(Profile(graph, helper.Id).Claims!, claim => claim.Text.Contains("Workshop tools"));
        var paint = Assert.Single(graph.Nodes, n => n.Name == "Paint");
        Assert.Equal(type.Id, paint.ParentId); Assert.Equal("EditorViewModel.HexWorkshop.cs", paint.Path);
    }

    [Fact]
    public async Task RussianFeatureTitleInsideEnglishPartialCommentDoesNotBecomeTheTypesPurpose()
    {
        using var repo = new FixtureRepository(); repo.Write("Demo.csproj", Project);
        repo.Write("MapProjectStore.Part.cs", "/// <summary>Schema change for «Красота», written separately.</summary>\npublic partial class MapProjectStore {}");
        repo.Write("MapProjectStore.cs", "/// <summary>Reads and writes project files.</summary>\npublic partial class MapProjectStore {}");
        var graph = await Scan(repo); var type = Assert.Single(graph.Nodes, n => n.Kind == "type");
        var profile = Profile(graph, type.Id);
        Assert.Equal("derived", profile.Description.Confidence);
        Assert.DoesNotContain("Красота", profile.Description.Text);
        Assert.Equal(2, profile.Claims!.Count(c => c.Confidence == "known"));
    }

    [Fact]
    public async Task MultilineCaptionSourcesOpenTheCaptionAndProjectCompoundsWinBeforeWordSplitting()
    {
        using var repo = new FixtureRepository(); repo.Write("Demo.csproj", Project);
        repo.Write("EditorViewModel.HexWorkshop.cs", """
            using System;
            public class EditorViewModel {
              public void Build() {
                Button("Name",
                  "Экспорт слоёв",
                  () => {});
              }
              static void Button(string name, string text, Action click) {}
            }
            """);
        var graph = await Scan(repo, new() { ExplanationWords = new() { ["HexWorkshop"] = "Инструменты гексов" } });
        Assert.Contains("Инструменты гексов", Profile(graph, "file:EditorViewModel.HexWorkshop.cs").DisplayName.Text);
        var entry = Assert.Single(graph.Views!.EntryPoints!);
        Assert.Equal(4, entry.Line); Assert.Equal(5, entry.LabelLine);
        var name = Profile(graph, Assert.Single(entry.HandlerIds)).DisplayName;
        Assert.Equal(5, graph.Views.Explanations!.Sources[Assert.Single(name.SourceIds)].Line);
    }

    [Fact]
    public async Task DiffReportsExplanationCorrectionsWithoutChangingOriginalNodes()
    {
        using var repo = new FixtureRepository(); repo.Write("Demo.csproj", Project); repo.Write("Thing.cs", "public class Thing {}");
        var initial = await Scan(repo); var node = Assert.Single(initial.Nodes, n => n.Kind == "type");
        var amended = await Scan(repo, new() { Explanations = [new() { NodeId = node.Id, DisplayName = "Личное имя" }] });
        var diff = GraphDiff.Compare(initial, amended);
        Assert.True(diff.Nodes.Same);
        Assert.Contains("explanation:" + node.Id, diff.Views.Changed);
    }

    [Theory]
    [InlineData("SceneRenderer", "Отрисовка")]
    [InlineData("MapProjectStore", "Хранилище")]
    [InlineData("ProjectViewModel", "Логика экрана")]
    [InlineData("LayerExporter", "Экспорт")]
    [InlineData("MapImporter", "Импорт")]
    [InlineData("SaveCommand", "Команда")]
    [InlineData("FileService", "Служба")]
    [InlineData("LayerManager", "Управление")]
    [InlineData("SettingsPanel", "Панель")]
    [InlineData("MainWindow", "Окно")]
    [InlineData("ExportDialog", "Диалог")]
    [InlineData("MapSettings", "Настройки")]
    public async Task RoleDictionaryOnlyGuessesPurpose(string name, string russian)
    {
        using var repo = new FixtureRepository(); repo.Write("Demo.csproj", Project);
        repo.Write(name + ".cs", $"public class {name} {{}}");
        var graph = await Scan(repo); var node = Assert.Single(graph.Nodes, n => n.Kind == "type");
        var p = Profile(graph, node.Id);
        Assert.Contains(russian, p.DisplayName.Text);
        Assert.Equal("guessed", p.DisplayName.Confidence);
        Assert.Equal("guessed", p.Description.Confidence);
        Assert.StartsWith("Предположительно по имени", p.Description.Text);
    }

    [Theory]
    [InlineData("Save", "Сохранить")]
    [InlineData("Load", "Загрузить")]
    [InlineData("Export", "Экспортировать")]
    [InlineData("Open", "Открыть")]
    [InlineData("Create", "Создать")]
    [InlineData("Delete", "Удалить")]
    [InlineData("Apply", "Применить")]
    public async Task MethodVerbsAndAsyncSuffixHaveSearchableRussianNames(string verb, string russian)
    {
        using var repo = new FixtureRepository(); repo.Write("Demo.csproj", Project);
        repo.Write("Ops.cs", $"public class Ops {{ public void {verb}MapAsync() {{}} }}");
        var graph = await Scan(repo); var node = Assert.Single(graph.Nodes, n => n.Kind == "member");
        var p = Profile(graph, node.Id);
        Assert.StartsWith(russian, p.DisplayName.Text); Assert.DoesNotContain("Async", p.DisplayName.Text);
        var explanations = graph.Views!.Explanations!;
        Assert.Contains(explanations.Nodes.IndexOf(p), explanations.NameIndex[node.Name.ToLowerInvariant()]);
        Assert.Contains(explanations.Nodes.IndexOf(p), explanations.NameIndex[p.DisplayName.Text.ToLowerInvariant()]);
    }

    [Fact]
    public async Task IdentifierSplittingPreservesQualifiersAbbreviationsAndUnknownExtensions()
    {
        using var repo = new FixtureRepository();
        repo.Write("web/package.json", "{\"name\":\"web\"}");
        repo.Write("web/EditorViewModel.HexWorkshop.ts", "export const XYZ = 1;");
        repo.Write("web/RpHubForm.tsx", "export const RpHubForm = () => null;");
        repo.Write("data.unknownext", "raw");
        var graph = await Scan(repo);
        var p = Profile(graph, "file:web/EditorViewModel.HexWorkshop.ts");
        Assert.Contains("Логика экрана", p.DisplayName.Text); Assert.Contains("Мастерская", p.DisplayName.Text);
        var abbreviation = Assert.Single(graph.Nodes, n => n.Name == "XYZ");
        Assert.Equal("XYZ", Profile(graph, abbreviation.Id).DisplayName.Text);
        Assert.Equal("guessed", Profile(graph, abbreviation.Id).DisplayName.Confidence);
        Assert.Contains("не всегда отличает", Profile(graph, abbreviation.Id).Description.Text);
        Assert.Contains("Rp", Profile(graph, "file:web/RpHubForm.tsx").DisplayName.Text);
        Assert.Contains("unknownext", Profile(graph, "file:data.unknownext").DisplayName.Text);
    }

    static void Buttons(FixtureRepository repo, bool reverse = false, bool duplicate = false, string caption = "Слои отдельными PNG…")
    {
        repo.Write("Demo.csproj", Project);
        var first = $"Button(\"ExportLayersButton\", \"{caption}\", () => ExportLayersAsync());";
        var second = $"Button(\"{(duplicate ? "ExportLayersButton" : "OtherButton")}\", \"Другое\", () => {{ }});";
        repo.Write("Panel.cs", "using System; public class Panel { public void Build() { " +
            (reverse ? second + first : first + second) + " } public void ExportLayersAsync() {} static void Button(string name, string text, Action click) {} }");
    }

    [Fact]
    public async Task HandlerLabelsHaveLiteralSourcesAndAnchoredCorrectionsSurviveReordering()
    {
        using var repo = new FixtureRepository(); Buttons(repo);
        var first = await Scan(repo);
        var handler = Assert.Single(first.Nodes, n => n.SubKind == "handler" && n.Name.Contains("Слои"));
        var entry = Assert.Single(first.Views!.EntryPoints!, e => e.HandlerIds.Contains(handler.Id));
        Assert.Equal(("ExportLayersButton", "Слои отдельными PNG…", "literal"), (entry.ControlName, entry.Label, entry.LabelKind));
        var profile = Profile(first, handler.Id); Assert.Equal("known", profile.DisplayName.Confidence);
        var label = first.Views.Explanations!.Sources[Assert.Single(profile.DisplayName.SourceIds)];
        Assert.Equal(("handlerLabel", "Panel.cs", 1), (label.Kind, label.Path, label.Line));
        Assert.Contains(first.Edges, e => e.Source == handler.Id && e.Kind == "call" && e.Target.EndsWith("ExportLayersAsync", StringComparison.Ordinal));
        var overrides = new ViewsOverrides { Explanations = [new() { Anchor = handler.Anchor, DisplayName = "Экспорт картинок", Description = "Личное пояснение владельца." }] };
        Buttons(repo, reverse: true);
        var second = await Scan(repo, overrides);
        var reordered = Assert.Single(second.Nodes, n => n.SubKind == "handler" && n.Anchor == handler.Anchor);
        Assert.Equal(handler.Id, reordered.Id);
        Assert.Equal("Экспорт картинок", Profile(second, reordered.Id).DisplayName.Text);
        Assert.Equal("known", Profile(second, reordered.Id).DisplayName.Confidence);
        Assert.Empty(second.Views!.StaleOverrides);
        Buttons(repo, duplicate: true);
        var ambiguous = await Scan(repo, overrides);
        Assert.All(ambiguous.Views!.EntryPoints!, e => Assert.True(e.Ambiguous));
        Assert.Contains(ambiguous.Views.StaleOverrides, s => s.StartsWith("explanation:"));
        Assert.DoesNotContain(ambiguous.Views.Explanations!.Nodes, p => p.DisplayName.Text == "Экспорт картинок");
        var ordinalOnly = await Scan(repo, new() { Explanations = [new() { NodeId = handler.Id, DisplayName = "Unsafe" }] });
        Assert.Contains(ordinalOnly.Views!.StaleOverrides, s => s.StartsWith("explanation:"));
    }

    [Fact]
    public async Task ComputedCaptionsAndXamlBindingsKeepTheirActualRegistration()
    {
        using var repo = new FixtureRepository(); repo.Write("Demo.csproj", Project);
        repo.Write("Panel.cs", "using System; namespace Demo; public class Panel { public string Title => \"Computed\"; public void Build() { Button(\"Dynamic\", $\"{Title}!\", () => {}); } public void Open() {} static void Button(string name, string text, Action click) {} }");
        repo.Write("Panel.xaml", "<Window xmlns=\"using:Demo\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" x:Class=\"Demo.Panel\">\n<Button x:Name=\"OpenButton\" Content=\"Открыть карту\" Click=\"Open\"/>\n</Window>");
        var graph = await Scan(repo);
        var dynamic = Assert.Single(graph.Views!.EntryPoints!, e => e.Detection == "csharp-delegate-argument");
        Assert.Equal("expression", dynamic.LabelKind);
        Assert.Contains(Profile(graph, Assert.Single(dynamic.HandlerIds)).Claims!, c => c.Text.StartsWith("Подпись вычисляется") && c.Confidence == "derived");
        var markup = Assert.Single(graph.Views.EntryPoints!, e => e.Detection == "xaml-attribute");
        Assert.Equal(("Panel.xaml", 2, "Click", "Открыть карту"), (markup.Path, markup.Line, markup.Event, markup.Label));
        Assert.Equal("known", Profile(graph, Assert.Single(markup.HandlerIds)).DisplayName.Confidence);
    }

    [Fact]
    public async Task TechnicalControlNameAloneIsNeverPresentedAsAVisibleCaption()
    {
        using var repo = new FixtureRepository(); repo.Write("Demo.csproj", Project);
        repo.Write("Panel.cs", "using System; public class Panel { public void Build() { Button(\"ExportButton\", () => {}); } static void Button(string name, Action click) {} }");
        var graph = await Scan(repo);
        Assert.Empty(graph.Warnings);
        var entry = Assert.Single(graph.Views!.EntryPoints!);
        Assert.Equal("ExportButton", entry.ControlName); Assert.Null(entry.Label); Assert.Null(entry.LabelKind);
        var profile = Profile(graph, Assert.Single(entry.HandlerIds));
        Assert.Equal("guessed", profile.DisplayName.Confidence);
        Assert.DoesNotContain(profile.Claims ?? [], c => c.Confidence == "known" && c.Text.Contains("ExportButton"));
    }

    [Fact]
    public async Task ProjectWordsAreScopedOwnerCorrectionsWinAndDoNotOverwriteFacts()
    {
        using var repo = new FixtureRepository(); repo.Write("Demo.csproj", Project);
        repo.Write("XYZ.cs", "public class XYZ {}");
        var graph = await Scan(repo); var node = Assert.Single(graph.Nodes, n => n.Kind == "type");
        var project = new ViewsOverrides { ExplanationWords = new() { ["XYZ"] = "Проектное имя" },
            Explanations = [new() { NodeId = node.Id, DisplayName = "Имя проекта" }] };
        repo.Write(".projmap/views.rules.json", JsonSerializer.Serialize(new { overrides = project }, GraphJson.Options));
        Assert.Equal("Имя проекта", Profile(await Scan(repo), node.Id).DisplayName.Text);
        var owner = new ViewsOverrides { Explanations = [new() { NodeId = node.Id, DisplayName = "Имя владельца" }, new() { NodeId = "gone", DisplayName = "Старое" }] };
        Assert.False(owner.IsEmpty);
        var amended = await Scan(repo, owner);
        Assert.Equal("Имя владельца", Profile(amended, node.Id).DisplayName.Text);
        Assert.Equal("XYZ", amended.Nodes.Single(n => n.Id == node.Id).Name);
        Assert.Contains("explanation:gone", amended.Views!.StaleOverrides);
        using var other = new FixtureRepository(); other.Write("Demo.csproj", Project); other.Write("XYZ.cs", "public class XYZ {}");
        Assert.Equal("XYZ", Profile(await Scan(other), node.Id).DisplayName.Text);
    }

    [Fact]
    public async Task IncrementalEqualsFullAfterCommentsLabelsBodyPositionsAndOverrideChanges()
    {
        using var repo = new FixtureRepository(); Buttons(repo);
        repo.Write("Panel.Part.cs", "/// <summary>Часть панели.</summary>\npublic partial class Other { public void Run() {} }");
        repo.Write("Other.cs", "public partial class Other { public void Save() {} }");
        var options = new ScanOptions(repo.Root) { GitMonths = 0 };
        var initial = await ProjectScanner.ScanAsync(options, null, null);
        var unchanged = await ProjectScanner.ScanAsync(options, initial.State, []);
        Assert.Equal(Stable(initial.Graph), Stable(unchanged.Graph));
        Buttons(repo, caption: "Новая подпись");
        repo.Write("Panel.Part.cs", "/// <summary>Изменённая часть панели.</summary>\npublic partial class Other { public void Run() {\n\n} }");
        var incremental = await ProjectScanner.ScanAsync(options, initial.State, ["Panel.cs", "Panel.Part.cs"]);
        Assert.Empty(incremental.Graph.Warnings);
        Assert.Equal(Stable(await ProjectScanner.ScanAsync(options)), Stable(incremental.Graph));
        // Pure body edits must refresh end ranges of the noncanonical declaration too.
        repo.Write("Panel.Part.cs", "/// <summary>Изменённая часть панели.</summary>\npublic partial class Other { public void Run() {\n\n\n} }");
        var body = await ProjectScanner.ScanAsync(options, incremental.State, ["Panel.Part.cs"]);
        Assert.Equal(Stable(await ProjectScanner.ScanAsync(options)), Stable(body.Graph));
        repo.Write(".projmap/views.rules.json", "{\"overrides\":{\"explanationWords\":{\"Other\":\"Другой блок\"}}}");
        var corrected = await ProjectScanner.ScanAsync(options, body.State, []);
        Assert.Equal(Stable(await ProjectScanner.ScanAsync(options)), Stable(corrected.Graph));
    }

    [Fact]
    public async Task GitOnlyExplainsFileActivityAndTierAndFeatureSourcesReferToExistingEvidence()
    {
        using var repo = new FixtureRepository(); repo.Write("Demo.csproj", Project); repo.Write("SceneRenderer.cs", "public class SceneRenderer { public void Render() {} }");
        await repo.Git("add", "."); await repo.Git("commit", "-q", "-m", "Initial renderer");
        var overrides = new ViewsOverrides { AddedFeatures = [new() { Id = "feature:scene", Name = "Сцена", PathGlobs = ["*.cs"] }],
            Tiers = [new() { PathGlob = "*.cs", Tier = "infrastructure" }] };
        var graph = await ProjectScanner.ScanAsync(new(repo.Root) { ViewOverrides = overrides });
        var profile = Profile(graph, "file:SceneRenderer.cs");
        var sources = graph.Views!.Explanations!.Sources;
        Assert.Contains(profile.Claims!, c => c.SourceIds.Any(id => sources[id].Kind == "git" && sources[id].CommitId!.Length == 40));
        Assert.Contains(profile.Claims!, c => c.SourceIds.Any(id => sources[id].Kind == "tier" && sources[id].Reference == "file:SceneRenderer.cs"));
        Assert.Contains(profile.Claims!, c => c.SourceIds.Any(id => sources[id].Kind == "feature" && sources[id].Reference == "feature:scene"));
        Assert.All(graph.Nodes.Where(n => n.Kind is "type" or "member"), n => Assert.DoesNotContain(Claims(Profile(graph, n.Id)), c => c.SourceIds.Any(id => sources[id].Kind == "git")));
    }

    [Fact]
    public async Task OldFixtureAndAllThreeSerializationPathsPreserveEntitiesAndNewData()
    {
        const string old = """{"schemaVersion":1,"project":{"name":"Old","root":"old"},"nodes":[{"id":"file:route.ts","kind":"file","name":"route.ts","path":"route.ts"}],"edges":[]}""";
        var legacy = GraphJson.Deserialize(old)!;
        Assert.Null(legacy.Views); Assert.Null(legacy.Problems); Assert.Null(legacy.Nodes[0].Declarations);
        using var repo = new FixtureRepository(); Buttons(repo);
        repo.Write("web/route.ts", "export function GET() { return 1; }");
        var graph = await Scan(repo);
        using var stream = new MemoryStream(); await GraphJson.WriteAsync(graph, stream);
        foreach (var json in new[] { GraphJson.Serialize(graph), Encoding.UTF8.GetString(stream.ToArray()), Encoding.UTF8.GetString(GraphJson.SerializeCompact(graph)) })
        {
            var read = GraphJson.Deserialize(json)!;
            Assert.Equal(JsonSerializer.Serialize(graph.Nodes, GraphJson.Options), JsonSerializer.Serialize(read.Nodes, GraphJson.Options));
            Assert.Equal(JsonSerializer.Serialize(graph.Views, GraphJson.Options), JsonSerializer.Serialize(read.Views, GraphJson.Options));
            Assert.Equal(JsonSerializer.Serialize(graph.Edges, GraphJson.Options), JsonSerializer.Serialize(read.Edges, GraphJson.Options));
        }
        Assert.Contains(graph.Nodes, n => n.Name == "route.ts");
        Assert.Contains(graph.Nodes, n => n.Name == "ExportLayersAsync");
    }

    [Fact]
    public void LegacyCompactFixtureReadsWithoutAnyNoviceExtensions()
    {
        const string old = """
            {"schemaVersion":1,"compact":1,"project":{"name":"Old","root":"old"},
             "nodes":[{"id":"member:Demo:Run","kind":"member","name":"Run","path":"Demo.cs"},
                      {"id":"file:route.ts","kind":"file","name":"route.ts","path":"route.ts"}],
             "edgeKinds":["reference"],"edges":[[0,0,1,2,[["Demo.cs",4,"route"]]]],"roadmap":[],"warnings":[]}
            """;
        var graph = GraphJson.Deserialize(old)!;
        Assert.Null(graph.Views); Assert.Null(graph.Problems);
        Assert.All(graph.Nodes, n => { Assert.Null(n.Declarations); Assert.Null(n.SummarySource); });
        var edge = Assert.Single(graph.Edges);
        Assert.Equal(("member:Demo:Run", "file:route.ts", 2, 1d), (edge.Source, edge.Target, edge.Weight, edge.Strength));
        Assert.Equal(new SourceLocation("Demo.cs", 4, "route"), Assert.Single(edge.Evidence));
        Assert.Equal(GraphJson.Serialize(graph), GraphJson.Serialize(GraphJson.Deserialize(GraphJson.Serialize(graph))!));
    }
}
