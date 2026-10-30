using System.Text.Json;
using ProjMap.Analysis;
using ProjMap.Core;

namespace ProjMap.Tests;

public sealed class HandlerTests
{
    private const string Project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>";
    private const string Controls = """
        using System;
        using System.Collections.Generic;
        public class Button { public string Name { get; set; } public object Content { get; set; } public event EventHandler Click; }
        public class Host { public object Child { get; set; } public List<Button> Children { get; } = new(); public List<Button> Items { get; } = new(); }
        public static class Ui { public static Button TextButton(string name, string label, Action action) => new(); }
        """;

    private static FixtureRepository Repository(string source)
    {
        var repo = new FixtureRepository();
        repo.Write("App.csproj", Project);
        repo.Write("Controls.cs", Controls);
        repo.Write("Panel.cs", source);
        return repo;
    }
    private static Task<ProjectGraph> Scan(FixtureRepository repo) => ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0 });
    private static Node Handler(ProjectGraph graph, string label) => Assert.Single(graph.Nodes, n => n.SubKind == "handler" && n.Name == label);

    [Theory]
    [InlineData("System.Console.Clear()", true)]
    [InlineData("Close()", true)]
    [InlineData("System.Console.Title = \"done\"", true)]
    [InlineData("text.Trim()", true)]
    [InlineData("{ }", false)]
    [InlineData("Own()", false)]
    [InlineData("{ System.Console.Clear(); Own(); }", false)]
    [InlineData("_value++", false)]
    [InlineData("Missing.Close()", false)]
    [InlineData("action()", false)]
    public async Task LibraryOnlyRequiresExternalUseAndNoRepositoryOrUnknownBehavior(string body, bool expected)
    {
        using var repo = Repository($$"""
            public class Panel : System.IO.MemoryStream {
                private int _value;
                private void Own() {}
                public void Build(System.Action action) {
                    var text = "hello";
                    var cancel = new Button();
                    cancel.Click += (_, _) => {{body}}{{(body.StartsWith('{') ? "" : ";")}}
                }
            }
            """);
        var graph = await Scan(repo);
        Assert.Empty(graph.Warnings);
        Assert.Equal(expected, Handler(graph, "cancel.Click").Tags.Contains("library-only"));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("button.Content = \"Configured\";", true)]
    [InlineData("host.Children.Add(button);", false)]
    [InlineData("host.Items.Add(button);", false)]
    [InlineData("host.Child = button;", false)]
    [InlineData("var other = new Button { Content = button };", false)]
    [InlineData("Consume(button);", false)]
    [InlineData("return button;", false)]
    [InlineData("var list = new System.Collections.Generic.List<Button> { button };", false)]
    [InlineData("Button[] list = [button];", false)]
    [InlineData("var alias = button;", false)]
    [InlineData("button.ToString();", false)]
    public async Task NotShownOnlyWhenAConstructedControlDoesNotEscape(string use, bool expected)
    {
        using var repo = Repository($$"""
            public class Panel {
                public object Build(Host host) {
                    var button = Ui.TextButton("UnusedButton", "Unused", () => System.Console.Clear());
                    button.Click += (_, _) => System.Console.Clear();
                    {{use}}
                    return null;
                }
                private void Consume(object value) {}
            }
            """);
        var graph = await Scan(repo);
        Assert.Empty(graph.Warnings);
        var handlers = graph.Nodes.Where(n => n.SubKind == "handler").ToList();
        Assert.Equal(2, handlers.Count);
        Assert.All(handlers, h => Assert.Equal(expected, h.Tags.Contains("not-shown")));
        Assert.All(handlers, h => Assert.EndsWith("|UnusedButton", h.Anchor));
    }

    [Fact]
    public async Task ParametersPropertiesAndNonControlEventsAreNotGuessedToBeNotShown()
    {
        using var repo = Repository("""
            public class Panel {
                public Button Field = new();
                private Button Property { get; } = new();
                public void Build(Button supplied, System.Timers.Timer timer) {
                    supplied.Click += (_, _) => {};
                    Property.Click += (_, _) => {};
                    Field.Click += (_, _) => {};
                    timer.Elapsed += (_, _) => {};
                }
            }
            """);
        var graph = await Scan(repo);
        Assert.Equal(4, graph.Nodes.Count(n => n.SubKind == "handler"));
        Assert.DoesNotContain(graph.Nodes, n => n.Tags.Contains("not-shown"));
    }

    [Theory]
    [InlineData("return _existing;")]
    [InlineData("var button = new Button(); host.Children.Add(button); return button;")]
    [InlineData("var button = new Button(); button = _existing; return button;")]
    public async Task OpaqueFactoriesAreNotAssumedToCreateUnshownControls(string body)
    {
        using var repo = Repository($$"""
            public class Panel {
                private Button _existing;
                private Button Existing(Host host) { {{body}} }
                public void Build(Host host) {
                    var button = Existing(host);
                    button.Click += (_, _) => {};
                }
            }
            """);
        Assert.DoesNotContain("not-shown", Handler(await Scan(repo), "button.Click").Tags);
    }

    [Fact]
    public async Task ComputedCaptionsTimerCallbacksAndRoutedEventsBecomeHandlers()
    {
        using var repo = Repository("""
            public class DispatcherTimer {
                public DispatcherTimer(System.TimeSpan interval, System.EventHandler tick) {}
                public static void RunOnce(System.Action action, System.TimeSpan interval) {}
            }
            public class Panel {
                private int KeyDownEvent;
                private void AddHandler(int eventId, System.EventHandler action) {}
                private void Run() {}
                public void Build(string name, string label) {
                    var button = Ui.TextButton(name, label, () => Run());
                    var timer = new DispatcherTimer(System.TimeSpan.Zero, (_, _) => Run());
                    DispatcherTimer.RunOnce(Run, System.TimeSpan.Zero);
                    AddHandler(KeyDownEvent, (_, _) => Run());
                }
            }
            """);
        var graph = await Scan(repo);
        var handlers = graph.Nodes.Where(n => n.SubKind == "handler").ToList();
        Assert.Equal(4, handlers.Count);
        Assert.All(handlers, h => Assert.Contains(graph.Edges, e => e.Source == h.Id && e.Target.EndsWith(":M:Panel.Run")));
        Assert.EndsWith("|name", Handler(graph, "TextButton «name · label»").Anchor);
        Assert.DoesNotContain(handlers.Where(h => h.Name.Contains("Timer") || h.Name == "RunOnce"), h => h.Tags.Contains("not-shown"));
    }

    [Fact]
    public async Task EventAnchorsKeepNamesFromInitializersAndFluentFactoriesWithoutTruncating()
    {
        var name = new string('x', 100);
        using var repo = Repository($$"""
            public static class Extensions {
                public static Button Named(this Button button, string name) { button.Name = name; return button; }
                public static Button Classed(this Button button, string style) => button;
            }
            public class Panel {
                public void Build() {
                    var first = new Button { Name = "{{name}}" };
                    first.Click += (_, _) => {};
                    var second = Ui.TextButton("FactoryName", "Caption", () => {}).Named("ActualName").Classed("style");
                    second.Click += (_, _) => {};
                }
            }
            """);
        var graph = await Scan(repo);
        Assert.EndsWith("|" + name, Handler(graph, "first.Click").Anchor);
        Assert.EndsWith("|ActualName", Handler(graph, "second.Click").Anchor);
    }

    [Fact]
    public async Task EventAnchorsUseNamesAssignedByFieldBuilders()
    {
        using var repo = Repository("""
            public class Panel {
                private Button _button = null!;
                public void Build() {
                    _button = new Button { Name = "QualityButton" };
                    _button.Click += (_, _) => {};
                }
            }
            """);
        var graph = await Scan(repo);
        Assert.EndsWith("|QualityButton", Handler(graph, "_button.Click").Anchor);
        Assert.Contains("not-shown", Handler(graph, "_button.Click").Tags);
    }

    [Fact]
    public async Task PrivateFieldUsesInPartialBodyRefreshEvenWhenHandlerFileIsReused()
    {
        using var repo = Repository("""
            public partial class Panel {
                private Button _button = null!;
                public void Build(System.Action action) {
                    _button = Ui.TextButton("ShortcutSettingsButton", "Shortcuts", action);
                }
            }
            """);
        repo.Write("Other.cs", "public partial class Panel { public void Attach(Host host) { } }");
        var options = new ScanOptions(repo.Root) { GitMonths = 0 };
        var initial = await ProjectScanner.ScanAsync(options, null, null);
        var button = Assert.Single(initial.Graph.Nodes, n => n.SubKind == "handler");
        Assert.Contains("not-shown", button.Tags);
        repo.Write("Other.cs", "public partial class Panel { public void Attach(Host host) { host.Children.Add(_button); } }");
        var changed = await ProjectScanner.ScanAsync(options, initial.State, ["Other.cs"]);
        Assert.DoesNotContain("not-shown", Assert.Single(changed.Graph.Nodes, n => n.SubKind == "handler").Tags);
        var full = await Scan(repo);
        Assert.True(GraphDiff.Compare(full, changed.Graph).Identical);
        // The saved state is immutable; rescanning it after a second body edit restores the tag.
        repo.Write("Other.cs", "public partial class Panel { public void Attach(Host host) { } }");
        var removed = await ProjectScanner.ScanAsync(options, changed.State, ["Other.cs"]);
        Assert.Contains("not-shown", Assert.Single(removed.Graph.Nodes, n => n.SubKind == "handler").Tags);
    }

    [Fact]
    public async Task MethodGroupsDelegateArgumentsAndTargetTypedActionsHaveTargetBasedIds()
    {
        using var repo = Repository("""
            public record SearchAction(string Title, System.Action Open);
            public class Panel {
                public void Build(System.Action request) {
                    var undo = Ui.TextButton("UndoButton", "Undo", Undo);
                    var settings = Ui.TextButton("ShortcutSettingsButton", "Shortcuts", request);
                    SearchAction search = new("Search", () => Undo());
                    var old = Ui.TextButton("OldButton", "Old", () => Undo());
                    old.Click += (_, _) => Undo();
                }
                private void Undo() {}
            }
            """);
        var graph = await Scan(repo);
        var old = Handler(graph, "TextButton «OldButton · Old»");
        Assert.EndsWith("#h:argument:Ui.TextButton(\"OldButton\",\"Old\"):1", old.Id);
        Assert.EndsWith("#h:event:old.Click:1", Handler(graph, "old.Click").Id);
        Assert.Equal(5, graph.Nodes.Count(n => n.SubKind == "handler"));
        var undo = Handler(graph, "TextButton «UndoButton · Undo»");
        Assert.Contains(graph.Edges, e => e.Source == undo.Id && e.Target.EndsWith(":M:Panel.Undo"));
        Assert.Contains("not-shown", Handler(graph, "TextButton «ShortcutSettingsButton · Shortcuts»").Tags);
        Assert.DoesNotContain("not-shown", Handler(graph, "new «Search»").Tags);
        Assert.Contains(graph.Edges, e => e.Source == Handler(graph, "new «Search»").Id && e.Target.EndsWith(":M:Panel.Undo"));
    }

    [Fact]
    public async Task AnchorsSurviveReorderingAndAllSerializersAndDiffCompareThem()
    {
        using var repo = Repository("""
            public class Panel {
                public void Build() {
                    var first = Ui.TextButton("FirstButton", "First", () => {});
                    var second = Ui.TextButton("SecondButton", "Second", () => {});
                }
            }
            """);
        var graph = await Scan(repo);
        var first = Handler(graph, "TextButton «FirstButton · First»");
        var source = File.ReadAllText(Path.Combine(repo.Root, "Panel.cs"));
        // Swap the two statements explicitly.
        repo.Write("Panel.cs", source.Replace("FirstButton", "TempButton").Replace("SecondButton", "FirstButton")
            .Replace("TempButton", "SecondButton").Replace("\"First\"", "\"Temp\"").Replace("\"Second\"", "\"First\"").Replace("\"Temp\"", "\"Second\""));
        var reordered = Handler(await Scan(repo), "TextButton «FirstButton · First»");
        Assert.Equal(first.Anchor, reordered.Anchor);
        Assert.Equal(first.Id, reordered.Id);
        Assert.All(graph.Nodes.Where(n => n.SubKind != "handler"), n => Assert.Null(n.Anchor));
        Assert.Equal(first.Anchor, Handler(GraphJson.Deserialize(GraphJson.Serialize(graph))!, first.Name).Anchor);
        using var stream = new MemoryStream();
        await GraphJson.WriteAsync(graph, stream);
        Assert.Equal(first.Anchor, Handler(GraphJson.Deserialize(System.Text.Encoding.UTF8.GetString(stream.ToArray()))!, first.Name).Anchor);
        using var compact = JsonDocument.Parse(GraphJson.SerializeCompact(graph));
        Assert.Equal(first.Anchor, compact.RootElement.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == first.Id).GetProperty("anchor").GetString());
        var legacy = graph with { Nodes = graph.Nodes.Select(n => n with { Anchor = null }).ToList() };
        var json = GraphJson.Serialize(legacy);
        using var legacyDocument = JsonDocument.Parse(json);
        Assert.All(legacyDocument.RootElement.GetProperty("nodes").EnumerateArray(), n => Assert.False(n.TryGetProperty("anchor", out _)));
        Assert.All(GraphJson.Deserialize(json)!.Nodes, n => Assert.Null(n.Anchor));
        Assert.Contains(first.Id, GraphDiff.Compare(graph, legacy, new() { IgnorePositions = true }).Nodes.Changed);
    }
}
