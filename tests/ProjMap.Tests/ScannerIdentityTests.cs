using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ProjMap.Analysis;
using ProjMap.Core;

namespace ProjMap.Tests;

public sealed class ScannerIdentityTests
{
    private const string Project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>";
    private static Task<ProjectGraph> Scan(FixtureRepository repo) => ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0 });

    [Fact]
    public async Task SourceIdsAndHandlersSurvivePackageResolutionAndIncrementalRestore()
    {
        using var repo = new FixtureRepository();
        repo.Write("App/App.csproj", Project);
        repo.Write("App/View.cs", """
            using Lib;
            using Alias = Lib.DrawingContext;
            public class View : Control {
                public override void Render(DrawingContext context) {}
                public void Shapes(Alias?[] context, global::Lib.DrawingContext[,] matrix) {}
                public void Build() {
                    new DispatcherTimer(System.TimeSpan.Zero, (_, _) => Render(null));
                    DispatcherTimer.RunOnce(() => Render(null));
                    AddHandler(PointerEvent, (_, _) => Render(null));
                    Ui.Button("CloseButton", "Close", Close);
                }
            }
            public static class Ui { public static void Button(string name, string text, System.Action action) {} }
            """);
        var options = new ScanOptions(repo.Root) { GitMonths = 0 };
        var clean = await ProjectScanner.ScanAsync(options, null, null);
        var library = CSharpCompilation.Create("Lib", [CSharpSyntaxTree.ParseText("""
            namespace Lib;
            public class DrawingContext {}
            public class Control {
                public static int PointerEvent;
                public virtual void Render(DrawingContext context) {}
                public void Close() {}
                public void AddHandler(int id, System.EventHandler callback) {}
            }
            public class DispatcherTimer {
                public DispatcherTimer(System.TimeSpan interval, System.EventHandler callback) {}
                public static void RunOnce(System.Action callback) {}
            }
            """)], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new(OutputKind.DynamicallyLinkedLibrary));
        var dll = repo.Write("pkgs/lib/1/lib/Lib.dll", "");
        using (var stream = File.Create(dll)) Assert.True(library.Emit(stream).Success);
        repo.Write("App/obj/project.assets.json", JsonSerializer.Serialize(new
        {
            targets = new Dictionary<string, object> { ["net10.0"] = new Dictionary<string, object>
                { ["Lib/1"] = new { type = "package", compile = new Dictionary<string, object> { ["lib/Lib.dll"] = new { } } } } },
            libraries = new Dictionary<string, object> { ["Lib/1"] = new { path = "lib/1" } },
            packageFolders = new Dictionary<string, object> { [Path.Combine(repo.Root, "pkgs") + Path.DirectorySeparatorChar] = new { } },
        }));
        var built = await Scan(repo);
        var incremental = await ProjectScanner.ScanAsync(options, clean.State, ["App/obj/project.assets.json"]);
        static string[] Members(ProjectGraph graph) => graph.Nodes.Where(n => n.Kind == NodeKinds.Member).Select(n => n.Id).Order().ToArray();
        Assert.Empty(clean.Graph.Warnings); Assert.Empty(built.Warnings); Assert.Empty(incremental.Graph.Warnings);
        Assert.Equal(Members(clean.Graph), Members(built));
        Assert.Equal(Members(built), Members(incremental.Graph));
        Assert.Equal(4, built.Nodes.Count(n => n.SubKind == "handler"));
        Assert.Contains(built.Nodes, n => n.Id.EndsWith(":M:View.Render(DrawingContext)"));
        Assert.Contains(built.Nodes, n => n.Id.EndsWith(":M:View.Shapes(Alias?[],Lib.DrawingContext[,])"));
    }

    [Fact]
    public async Task SourceSignaturesKeepOverloadsConversionsGenericsAndCrossProjectCallsDistinct()
    {
        using var repo = new FixtureRepository();
        repo.Write("Core/Core.csproj", Project);
        repo.Write("Core/Api.cs", """
            namespace A { public class Context {} }
            namespace B { public class Context {} }
            namespace Demo {
                public class Api<T> {
                    public Api() {}
                    public static void Run(A.Context context) {}
                    public static void Run(B.Context context) {}
                    public static void Generic<U>(T[] first, U? second, ref int value) {}
                    public int this[int index] => index;
                    public static implicit operator int(Api<T> value) => 0;
                    public static implicit operator long(Api<T> value) => 0;
                }
                public static class Extensions { public static void Use(this Api<int> api, int?[,] values) {} }
            }
            """);
        repo.Write("App/App.csproj", Project.Replace("</Project>", "<ItemGroup><ProjectReference Include=\"../Core/Core.csproj\" /></ItemGroup></Project>"));
        repo.Write("App/Entry.cs", """
            using Demo;
            public class Entry { public void Main() {
                var api = new Api<int>();
                Api<int>.Run(new A.Context()); Api<int>.Run(new B.Context());
                int value = 0; Api<int>.Generic<string>([], null, ref value);
                api.Use(new int?[1,1]); value = api[0];
                value = api; long other = api;
            } }
            """);
        var graph = await Scan(repo);
        Assert.Empty(graph.Warnings);
        foreach (var suffix in new[] { "Api<T>.#ctor", "Api<T>.Run(A.Context)", "Api<T>.Run(B.Context)",
                     "Api<T>.Generic``1(T[],U?,System.Int32@)", "Extensions.Use(Api<System.Int32>,System.Int32?[,])" })
        {
            var member = Assert.Single(graph.Nodes, n => n.Id.EndsWith(suffix));
            Assert.Contains(graph.Edges, e => e.Source.EndsWith(":M:Entry.Main") && e.Target == member.Id);
        }
        Assert.Contains(graph.Nodes, n => n.Id.EndsWith("Api<T>.Item(System.Int32)"));
        Assert.Equal(2, graph.Nodes.Count(n => n.Id.Contains(".op_Implicit(Api<T>)~System.")));
    }

    [Fact]
    public async Task HandlerIdsSurviveUnrelatedRegistrationsBodyEditsAndFileOrder()
    {
        using var repo = new FixtureRepository();
        repo.Write("App.csproj", Project);
        const string source = """
            public class Panel {
                public event System.Action Click;
                public void Build() {
                    Click += () => Work();
                    Ui.Button("Same", () => Work());
                    Ui.Button("Same", () => Work());
                    Ui.Button("Other", Work);
                }
                private void Work() {}
            }
            public static class Ui { public static void Button(string name, System.Action action) {} }
            """;
        repo.Write("Z.cs", source);
        var before = await Scan(repo);
        repo.Write("A.cs", "public class Earlier { public void Build() { Ui.Button(\"Earlier\", () => {}); } }");
        repo.Write("Z.cs", source.Replace("Click +=", "Ui.Button(\"Inserted\", () => {});\nClick +=")
            .Replace("() => Work()", "() => { Work(); Work(); }"));
        var after = await Scan(repo);
        var handlers = before.Nodes.Where(n => n.SubKind == "handler").ToArray();
        Assert.Equal(4, handlers.Length);
        Assert.Equal(4, handlers.Select(n => n.Id).Distinct().Count());
        Assert.All(handlers, h => Assert.Contains(after.Nodes, n => n.Id == h.Id));
        Assert.Contains(handlers, h => h.Id.EndsWith("#h:argument:Ui.Button(\"Same\"):2"));
        Assert.All(handlers, h => Assert.Contains(after.Edges, e => e.Source == h.Id && e.Target.EndsWith(":M:Panel.Work")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatchLinksInterfaceCallsToImplicitAndExplicitImplementations(bool explicitImplementation)
    {
        using var repo = new FixtureRepository();
        repo.Write("App.csproj", Project);
        repo.Write("Api.cs", $$"""
            public interface IImageExporter { void ExportAsync(); }
            public class ImageExportEngine : IImageExporter {
                {{(explicitImplementation ? "void IImageExporter.ExportAsync()" : "public void ExportAsync()")}} {}
            }
            public class OtherExporter : IImageExporter { public void ExportAsync() {} }
            public class Caller { public void Run(IImageExporter exporter) => exporter.ExportAsync(); }
            """);
        var graph = await Scan(repo);
        var contract = Assert.Single(graph.Nodes, n => n.Id.EndsWith(":M:IImageExporter.ExportAsync"));
        Assert.Contains(graph.Edges, e => e.Kind == EdgeKinds.Call && e.Target == contract.Id);
        var dispatch = graph.Edges.Where(e => e.Kind == EdgeKinds.Dispatch && e.Source == contract.Id).ToArray();
        Assert.Equal(2, dispatch.Length);
        Assert.All(dispatch, e =>
        {
            Assert.Single(graph.Nodes, n => n.Id == e.Target && n.Kind == NodeKinds.Member);
            Assert.Contains(graph.Edges, inverse => inverse.Kind == EdgeKinds.Implement && inverse.Source == e.Target && inverse.Target == e.Source);
            Assert.NotEmpty(e.Evidence);
        });
        foreach (var json in new[] { GraphJson.Serialize(graph), System.Text.Encoding.UTF8.GetString(GraphJson.SerializeCompact(graph)) })
            Assert.True(GraphDiff.Compare(graph, GraphJson.Deserialize(json)!).Identical);
        var typesOnly = await ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0, IncludeMembers = false });
        Assert.DoesNotContain(typesOnly.Edges, e => e.Kind == EdgeKinds.Dispatch);
    }

    [Fact]
    public async Task DispatchSupportsGenericContractsPropertiesIndexersEventsAndOverrideChains()
    {
        using var repo = new FixtureRepository();
        repo.Write("App.csproj", Project);
        repo.Write("Api.cs", """
            public interface IApi<T> {
                void Run(T value); int Value { get; } int this[int i] { get; }
                event System.Action Changed;
            }
            public abstract class Base { public abstract void Execute(); public virtual int Value => 0; }
            public class Impl : Base, IApi<int> {
                public void Run(int value) {}
                public override int Value => 1;
                public int this[int i] => i;
                public event System.Action Changed { add {} remove {} }
                public override void Execute() {}
            }
            public class Derived : Impl { public override void Execute() {} }
            """);
        var graph = await Scan(repo);
        var dispatch = graph.Edges.Where(e => e.Kind == EdgeKinds.Dispatch).ToArray();
        Assert.Equal(7, dispatch.Length);
        foreach (var name in new[] { "Run", "Value", "this[]", "Changed", "Execute" })
            Assert.Contains(dispatch, e => graph.Nodes.Any(n => n.Id == e.Target && n.Name == name));
        Assert.Contains(dispatch, e => e.Source.EndsWith(":M:Impl.Execute") && e.Target.EndsWith(":M:Derived.Execute"));
        Assert.Contains(dispatch, e => e.Source.EndsWith(":M:IApi<T>.Run(T)") && e.Target.EndsWith(":M:Impl.Run(System.Int32)"));
    }

    [Fact]
    public async Task DispatchFindsInheritedImplementationsAndIncrementalImplementationChanges()
    {
        using var repo = new FixtureRepository();
        repo.Write("App.csproj", Project);
        repo.Write("Api.cs", "public interface IApi { void Run(); } public class Base { public void Run() {} }");
        repo.Write("Derived.cs", "public class Derived : Base, IApi {}");
        var options = new ScanOptions(repo.Root) { GitMonths = 0 };
        var first = await ProjectScanner.ScanAsync(options, null, null);
        Assert.Contains(first.Graph.Edges, e => e.Kind == EdgeKinds.Dispatch && e.Source.EndsWith(":M:IApi.Run") && e.Target.EndsWith(":M:Base.Run"));
        repo.Write("Derived.cs", "public class Derived : Base, IApi { public new void Run() {} }");
        var changed = await ProjectScanner.ScanAsync(options, first.State, ["Derived.cs"]);
        Assert.Contains(changed.Graph.Edges, e => e.Kind == EdgeKinds.Dispatch && e.Source.EndsWith(":M:IApi.Run") && e.Target.EndsWith(":M:Derived.Run"));
        Assert.DoesNotContain(changed.Graph.Edges, e => e.Kind == EdgeKinds.Dispatch && e.Source.EndsWith(":M:IApi.Run") && e.Target.EndsWith(":M:Base.Run"));
        Assert.True(GraphDiff.Compare(await Scan(repo), changed.Graph).Identical);
    }
}
