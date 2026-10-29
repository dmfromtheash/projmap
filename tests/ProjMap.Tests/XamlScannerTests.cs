using ProjMap.Analysis;
using ProjMap.Core;

namespace ProjMap.Tests;

public sealed class XamlScannerTests
{
    private const string Project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";
    private const string Avalonia = """
        <Window xmlns="https://github.com/avaloniaui"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:vm="using:Demo.ViewModels" x:Class="Demo.MainWindow" x:DataType="vm:MainViewModel">
          <Button x:Name="SaveButton" Click="OnSave" Command="{Binding SaveCommand}" />
        </Window>
        """;

    private static void Populate(FixtureRepository repo)
    {
        repo.Write("App/App.csproj", Project);
        repo.Write("App/MainWindow.cs", "namespace Demo; public partial class MainWindow { private void OnSave(object sender, object e) {} private void OnCancel() {} public int NotAMethod { get; set; } }");
        repo.Write("App/OtherPartial.cs", "namespace Demo; public partial class MainWindow { private void OnTap() {} }");
        repo.Write("App/MainViewModel.cs", "namespace Demo.ViewModels; public class MainViewModel { public object SaveCommand { get; set; } public object CancelCommand; } public class OtherViewModel { public object SaveCommand { get; set; } }");
        repo.Write("App/MainWindow.axaml", Avalonia);
    }
    private static string Stable(ProjectGraph graph) => GraphJson.Serialize(graph with
    { Project = graph.Project with { ScannedAt = default, ScanMilliseconds = 0, Timings = null } });
    private static void ValidEndpoints(ProjectGraph graph) => Assert.All(graph.Edges,
        e => Assert.Contains(graph.Nodes, n => n.Id == e.Target || e.Target.StartsWith("pkg:", StringComparison.Ordinal)));

    [Fact]
    public async Task AvaloniaClassEventsAndCommandsResolveWithControlEvidenceAndStableOutput()
    {
        using var repo = new FixtureRepository(); Populate(repo);
        var before = repo.Snapshot();
        var graph = await ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0 });
        Assert.Empty(graph.Warnings);
        var type = Assert.Single(graph.Nodes, n => n.Kind == NodeKinds.Type && n.Name == "MainWindow");
        Assert.Contains(graph.Edges, e => e.Kind == EdgeKinds.Reference && e.Source == "file:App/MainWindow.axaml" && e.Target == type.Id);
        var handler = Assert.Single(graph.Nodes, n => n.Name == "OnSave");
        Assert.Equal("method", handler.SubKind);
        Assert.Contains("xaml-handler", handler.Tags);
        Assert.Contains("callback", handler.Tags);
        var eventEdge = Assert.Single(graph.Edges, e => e.Kind == EdgeKinds.XamlEvent);
        Assert.Equal(handler.Id, eventEdge.Target);
        Assert.Equal(4, Assert.Single(eventEdge.Evidence).Line);
        Assert.Contains("Button #SaveButton Click=", eventEdge.Evidence[0].Snippet);
        Assert.Equal("", eventEdge.Evidence[0].Path); // source file path is elided by finalization
        var binding = Assert.Single(graph.Edges, e => e.Kind == EdgeKinds.Binding);
        Assert.EndsWith(":P:Demo.ViewModels.MainViewModel.SaveCommand", binding.Target);
        ValidEndpoints(graph);
        Assert.Equal(before.OrderBy(p => p.Key), repo.Snapshot().OrderBy(p => p.Key));
        Assert.Equal(Stable(graph), Stable(await ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0 })));
    }

    [Theory]
    [InlineData("{Binding SaveCommand}")]
    [InlineData("{Binding Path=SaveCommand, Mode=OneWay}")]
    [InlineData("{Binding Mode=OneWay, Path = SaveCommand}")]
    [InlineData("{CompiledBinding SaveCommand}")]
    [InlineData("{x:Bind SaveCommand}")]
    public async Task SupportedCommandFormsResolve(string binding)
    {
        using var repo = new FixtureRepository(); Populate(repo);
        repo.Write("App/MainWindow.axaml", Avalonia.Replace("{Binding SaveCommand}", binding));
        var graph = await ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0 });
        Assert.Single(graph.Edges, e => e.Kind == EdgeKinds.Binding);
        Assert.Empty(graph.Warnings);
    }

    [Theory]
    [InlineData("x:DataType=\"vm:MainViewModel\"")]
    [InlineData("d:DataContext=\"{x:Type vm:MainViewModel}\"")]
    [InlineData("d:DataContext=\"{d:DesignInstance Type=vm:MainViewModel, IsDesignTimeCreatable=True}\"")]
    [InlineData("d:DataContext=\"{d:DesignInstance vm:MainViewModel}\"")]
    [InlineData("d:DataContext=\"{d:DesignInstance IsDesignTimeCreatable=True, Type = vm:MainViewModel}\"")]
    public async Task WpfClrNamespaceAndDesignContextResolve(string context)
    {
        using var repo = new FixtureRepository(); Populate(repo);
        repo.Write("App/MainWindow.axaml", "<Window />");
        repo.Write("App/Wpf.xaml", $$"""
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                    xmlns:vm="clr-namespace:Demo.ViewModels;assembly=App" x:Class="Demo.MainWindow" {{context}}>
              <Button Name="Save" Click="OnSave" Command="{Binding Path=SaveCommand}" />
            </Window>
            """);
        var graph = await ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0 });
        Assert.Empty(graph.Warnings);
        Assert.Contains(graph.Edges, e => e.Source == "file:App/Wpf.xaml" && e.Kind == EdgeKinds.Reference);
        Assert.Contains(graph.Edges, e => e.Source == "file:App/Wpf.xaml" && e.Kind == EdgeKinds.XamlEvent);
        Assert.Single(graph.Edges, e => e.Kind == EdgeKinds.Binding && e.Source == "file:App/Wpf.xaml");
    }

    [Fact]
    public async Task NestedDataTypesOverrideInheritedContextAndRuntimeContextBlocksInference()
    {
        using var repo = new FixtureRepository(); Populate(repo);
        repo.Write("App/MainWindow.axaml", Avalonia.Replace("<Button x:Name=\"SaveButton\" Click=\"OnSave\" Command=\"{Binding SaveCommand}\" />", """
            <StackPanel>
              <Button Command="{Binding SaveCommand}" />
              <Button x:DataType="vm:OtherViewModel" Command="{Binding SaveCommand}" />
              <Button DataContext="{Binding Other}" Command="{Binding SaveCommand}" />
              <Button x:DataType="{x:Null}" Command="{Binding SaveCommand}" />
              <DataTemplate><Button Command="{Binding SaveCommand}" /></DataTemplate>
              <DataTemplate x:DataType="vm:OtherViewModel"><Button Command="{Binding SaveCommand}" /></DataTemplate>
            </StackPanel>
            """));
        var graph = await ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0 });
        var bindings = graph.Edges.Where(e => e.Kind == EdgeKinds.Binding).ToArray();
        Assert.Equal(2, bindings.Length);
        Assert.Equal(1, bindings.Single(e => e.Target.EndsWith(":P:Demo.ViewModels.MainViewModel.SaveCommand", StringComparison.Ordinal)).Weight);
        Assert.Equal(2, bindings.Single(e => e.Target.EndsWith(":P:Demo.ViewModels.OtherViewModel.SaveCommand", StringComparison.Ordinal)).Weight);
        Assert.Contains(bindings, e => e.Target.EndsWith(":P:Demo.ViewModels.OtherViewModel.SaveCommand", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{Binding MissingCommand}")]
    [InlineData("{Binding Other.SaveCommand}")]
    [InlineData("{Binding SaveCommand, ElementName = Other}")]
    [InlineData("{Binding SaveCommand, Source={StaticResource Other}}")]
    [InlineData("{Binding SaveCommand, RelativeSource = Self}")]
    [InlineData("{unknown:Binding SaveCommand}")]
    public async Task UnresolvedOrExplicitSourceBindingsAreSkippedWithoutDanglingEdges(string binding)
    {
        using var repo = new FixtureRepository(); Populate(repo);
        repo.Write("App/MainWindow.axaml", Avalonia.Replace("{Binding SaveCommand}", binding));
        var graph = await ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0 });
        Assert.Empty(graph.Warnings);
        Assert.DoesNotContain(graph.Edges, e => e.Kind == EdgeKinds.Binding);
        ValidEndpoints(graph);
    }

    [Fact]
    public async Task MissingContextUnknownEventsPropertiesAndAmbiguousMethodsAreSkipped()
    {
        using var repo = new FixtureRepository(); Populate(repo);
        repo.Write("App/Overload.cs", "namespace Demo; public partial class MainWindow { private void OnSave() {} }");
        repo.Write("App/MainWindow.axaml", Avalonia.Replace(" x:DataType=\"vm:MainViewModel\"", "")
            .Replace("Click=\"OnSave\"", "Click=\"OnSave\" Content=\"OnCancel\" ImaginaryEvent=\"OnCancel\" Tapped=\"OnTap\" PointerPressed=\"NotAMethod\" KeyDown=\"Missing\""));
        var graph = await ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0 });
        Assert.DoesNotContain(graph.Edges, e => e.Kind == EdgeKinds.Binding);
        Assert.EndsWith(":M:Demo.MainWindow.OnTap", Assert.Single(graph.Edges, e => e.Kind == EdgeKinds.XamlEvent).Target);
        Assert.DoesNotContain(graph.Nodes.Where(n => n.Name == "OnSave"), n => n.Tags.Contains("xaml-handler"));
    }

    [Fact]
    public async Task MalformedXmlProducesOneWarningAndOtherFilesStillScan()
    {
        using var repo = new FixtureRepository(); Populate(repo);
        repo.Write("App/Broken.xaml", "<Window><Button Click=\"OnSave\"></Window>");
        var graph = await ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0 });
        var warning = Assert.Single(graph.Warnings);
        Assert.Equal("xaml", warning.Scanner);
        Assert.Equal("App/Broken.xaml", warning.Path);
        Assert.DoesNotContain(graph.Edges, e => e.Source == "file:App/Broken.xaml");
        Assert.Single(graph.Edges, e => e.Kind == EdgeKinds.XamlEvent);
    }

    [Fact]
    public async Task SuppressedMembersStillPermitClassReference()
    {
        using var repo = new FixtureRepository(); Populate(repo);
        var graph = await ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0, IncludeMembers = false });
        Assert.Contains(graph.Edges, e => e.Kind == EdgeKinds.Reference && e.Source == "file:App/MainWindow.axaml");
        Assert.DoesNotContain(graph.Edges, e => e.Kind is EdgeKinds.XamlEvent or EdgeKinds.Binding);
        ValidEndpoints(graph);
    }

    [Fact]
    public async Task MauiXaml2009ClassAndDataTypeUseNamespaceUrisAndClickedEvent()
    {
        using var repo = new FixtureRepository(); Populate(repo);
        repo.Write("App/MainWindow.axaml", "<Window />");
        repo.Write("App/Page.xaml", """
            <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                         xmlns:markup="http://schemas.microsoft.com/winfx/2009/xaml"
                         xmlns:vm="clr-namespace:Demo.ViewModels"
                         markup:Class="Demo.MainWindow" markup:DataType="vm:MainViewModel">
              <Button markup:Name="SaveButton" Clicked="OnSave" Command="{Binding SaveCommand}" />
            </ContentPage>
            """);
        var graph = await ProjectScanner.ScanAsync(new(repo.Root) { GitMonths = 0 });
        Assert.Empty(graph.Warnings);
        Assert.Contains(graph.Edges, e => e.Kind == EdgeKinds.Reference && e.Source == "file:App/Page.xaml");
        var handler = Assert.Single(graph.Edges, e => e.Kind == EdgeKinds.XamlEvent);
        Assert.Contains("Button #SaveButton Clicked=", Assert.Single(handler.Evidence).Snippet);
        Assert.Single(graph.Edges, e => e.Kind == EdgeKinds.Binding);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MarkupEditsRefreshEdgesWeightsAndHandlerTagsWithReusedCSharp(bool notifyChange)
    {
        using var repo = new FixtureRepository(); Populate(repo);
        var options = new ScanOptions(repo.Root) { GitMonths = 0 };
        var initial = await ProjectScanner.ScanAsync(options, null, null);
        repo.Write("App/MainWindow.axaml", Avalonia.Replace("OnSave", "OnCancel")
            .Replace("{Binding SaveCommand}", "{CompiledBinding CancelCommand}"));
        var changed = await ProjectScanner.ScanAsync(options, initial.State, notifyChange ? ["App/MainWindow.axaml"] : []);
        Assert.Equal(0, changed.Graph.Project.Timings!.Counts["csharp.analyzedFiles"]);
        Assert.Equal(Stable(await ProjectScanner.ScanAsync(options)), Stable(changed.Graph));
        Assert.DoesNotContain("xaml-handler", Assert.Single(changed.Graph.Nodes, n => n.Name == "OnSave").Tags);
        Assert.Contains("xaml-handler", Assert.Single(changed.Graph.Nodes, n => n.Name == "OnCancel").Tags);
        Assert.EndsWith(":F:Demo.ViewModels.MainViewModel.CancelCommand", Assert.Single(changed.Graph.Edges, e => e.Kind == EdgeKinds.Binding).Target);
        // Removing the markup must also remove its tags and edges from reused declarations.
        File.Delete(Path.Combine(repo.Root, "App/MainWindow.axaml"));
        var removed = await ProjectScanner.ScanAsync(options, changed.State, ["App/MainWindow.axaml"]);
        Assert.Equal(Stable(await ProjectScanner.ScanAsync(options)), Stable(removed.Graph));
        Assert.DoesNotContain(removed.Graph.Nodes, n => n.Tags.Contains("xaml-handler"));
    }
}
