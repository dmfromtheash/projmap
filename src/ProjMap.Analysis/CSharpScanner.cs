using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ProjMap.Core;

namespace ProjMap.Analysis;

internal sealed class CSharpScanner : IRepositoryScanner
{
    public string Name => "csharp";
    private static readonly Lazy<MetadataReference[]> Framework = new(() =>
        // In a single-file exe bundled assemblies have no path, so keep only real files and
        // fall back to the shared runtime directory (always on disk for framework-dependent apps).
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator)
        .Concat(Directory.EnumerateFiles(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"))
        .Where(p => p.Length > 0 && File.Exists(p))
        .DistinctBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
        .Select(p => { try { return (MetadataReference?)MetadataReference.CreateFromFile(p); } catch (BadImageFormatException) { return null; } })
        .OfType<MetadataReference>().ToArray());
    private sealed record Unit(RepoModule Module, CSharpCompilation Compilation, List<SyntaxTree> Trees);
    private sealed record TypeInfo(string Id, string File, string Module);
    public async Task ScanAsync(ScanContext c, CancellationToken ct)
    {
        EmitDependencies(c);
        var units = new ConcurrentDictionary<string, Unit>(StringComparer.Ordinal);
        var parallel = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 2, 1, 8) }; // leave cores for the UI
        var modules = c.Modules.Where(m => m.Language == "C#").ToList();
        // Loose C# scripts/source still get syntax analysis without inventing a module node.
        if (c.Files.Values.Any(f => f.Language == "C#" && c.ModuleFor(f.Path) == null))
            modules.Add(new("system:root", c.Graph.Project.Name, "", "", "C#"));
        await Parallel.ForEachAsync(modules, parallel, (module, token) =>
        {
            try
            {
                var trees = c.Files.Values.Where(f => f.Language == "C#" && (c.ModuleFor(f.Path)?.Id ?? "system:root") == module.Id)
                    .OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => (File: f, Text: f.Text(c))).Where(f => f.Text != null)
                    .Select(f => CSharpSyntaxTree.ParseText(f.Text!, new CSharpParseOptions(LanguageVersion.Preview,
                        preprocessorSymbols: module.Defines), f.File.Path, cancellationToken: token)).ToList();
                var allTrees = new List<SyntaxTree>(trees);
                if (module.ImplicitUsings) allTrees.Add(CSharpSyntaxTree.ParseText("global using System; global using System.Collections.Generic; global using System.Linq; global using System.Threading; global using System.Threading.Tasks; global using System.IO; global using System.Net.Http;", new CSharpParseOptions(LanguageVersion.Preview)));
                var assembly = "scan_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(module.Id)))[..16];
                var compilation = CSharpCompilation.Create(assembly, allTrees, Framework.Value,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
                units[module.Id] = new(module, compilation, trees);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { c.Warn(Name, ex.Message, module.Manifest); }
            return ValueTask.CompletedTask;
        });
        // Wire compilation references by topological layers; no builds, restores or target execution.
        var pending = units.Keys.ToHashSet(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var ready = pending.Where(id => Dependencies(units[id].Module, c).Where(units.ContainsKey).All(done.Contains)).Order(StringComparer.Ordinal).ToArray();
            if (ready.Length == 0)
            {
                c.Warn(Name, "Cyclic project references: semantic links across the cycle may be incomplete.");
                ready = pending.Order(StringComparer.Ordinal).ToArray();
            }
            foreach (var id in ready)
            {
                var unit = units[id];
                var refs = Reachable(unit.Module, c, new HashSet<string>(StringComparer.Ordinal)).Where(done.Contains)
                    .Distinct().Select(d => units[d].Compilation.ToMetadataReference()).ToArray();
                units[id] = unit with { Compilation = unit.Compilation.AddReferences(refs) };
                pending.Remove(id); done.Add(id);
            }
        }
        var types = new ConcurrentDictionary<string, TypeInfo>(StringComparer.Ordinal);
        // Declaration pass is completed everywhere before binding references to graph endpoints.
        await Parallel.ForEachAsync(units.Values.OrderBy(u => u.Module.Id, StringComparer.Ordinal), parallel, (u, token) =>
        {
            foreach (var tree in u.Trees)
            {
                token.ThrowIfCancellationRequested();
                try { Declare(c, u, tree, types, token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { c.Warn(Name, ex.Message, tree.FilePath); }
            }
            return ValueTask.CompletedTask;
        });
        await Parallel.ForEachAsync(units.Values, parallel, (u, token) =>
        {
            foreach (var tree in u.Trees)
            {
                token.ThrowIfCancellationRequested();
                try { Connect(c, u, tree, types, token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { c.Warn(Name, ex.Message, tree.FilePath); }
            }
            return ValueTask.CompletedTask;
        });
    }
    private static IEnumerable<string> Dependencies(RepoModule m, ScanContext c) => m.References
        .Select(r => c.Modules.FirstOrDefault(x => x.Manifest.Equals(r, StringComparison.OrdinalIgnoreCase))?.Id).OfType<string>();
    private static IEnumerable<string> Reachable(RepoModule m, ScanContext c, HashSet<string> seen)
    {
        foreach (var id in Dependencies(m, c))
        {
            if (!seen.Add(id)) continue;
            yield return id;
            foreach (var transitive in Reachable(c.Modules.First(x => x.Id == id), c, seen)) yield return transitive;
        }
    }
    private static void EmitDependencies(ScanContext c)
    {
        foreach (var m in c.Modules)
        {
            foreach (var r in m.References.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var target = c.Modules.FirstOrDefault(x => x.Manifest.Equals(r, StringComparison.OrdinalIgnoreCase));
                if (target != null) c.Edge(EdgeKinds.ProjectRef, m.Id, target.Id, new(m.Manifest, 1, r));
            }
            foreach (var p in m.Packages.Distinct(StringComparer.Ordinal))
            {
                var target = c.Modules.FirstOrDefault(x => x.Manifest.EndsWith("package.json") && x.Name == p);
                if (target != null) c.Edge(EdgeKinds.ProjectRef, m.Id, target.Id, new(m.Manifest, 1, p));
                else c.Edge(EdgeKinds.Package, m.Id, "pkg:" + p, new(m.Manifest, 1, p));
            }
        }
    }
    private static string Key(INamedTypeSymbol symbol) => symbol.ContainingAssembly.Identity.Name + ":" + symbol.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    private static string Fqn(INamedTypeSymbol symbol) => symbol.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "", StringComparison.Ordinal);
    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    private static string? Summary(ISymbol symbol, CancellationToken ct)
    {
        var xml = symbol.GetDocumentationCommentXml(cancellationToken: ct);
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var summary = XElement.Parse(xml).Element("summary");
            return summary == null ? null : Regex.Replace(summary.Value, @"\s+", " ").Trim();
        }
        catch (System.Xml.XmlException) { return null; }
    }
    private static void Declare(ScanContext c, Unit u, SyntaxTree tree, ConcurrentDictionary<string, TypeInfo> types, CancellationToken ct)
    {
        var model = u.Compilation.GetSemanticModel(tree, ignoreAccessibility: true);
        var root = tree.GetRoot(ct);
        var file = c.Graph.Nodes[ScanContext.FileId(tree.FilePath)];
        foreach (var declaration in root.DescendantNodes().Where(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax))
        {
            if (model.GetDeclaredSymbol(declaration, ct) is not INamedTypeSymbol symbol) continue;
            var id = "type:" + u.Module.Id + ":" + Fqn(symbol);
            var canonical = symbol.DeclaringSyntaxReferences.OrderBy(r => r.SyntaxTree.FilePath, StringComparer.Ordinal).ThenBy(r => r.Span.Start).First();
            types[Key(symbol)] = new(id, canonical.SyntaxTree.FilePath, u.Module.Id);
            if (canonical.SyntaxTree != tree || canonical.Span != declaration.Span) continue;
            var parent = symbol.ContainingType is { } containing ? "type:" + u.Module.Id + ":" + Fqn(containing) : file.Id;
            var kind = symbol.IsRecord ? (symbol.TypeKind == TypeKind.Struct ? "record struct" : "record") : symbol.TypeKind.ToString().ToLowerInvariant();
            c.Add(new Node
            {
                Id = id,
                Kind = NodeKinds.Type,
                SubKind = kind,
                Name = symbol.Name,
                Path = tree.FilePath,
                Line = Line(declaration),
                ParentId = parent,
                Language = "C#",
                Summary = Summary(symbol, ct),
                Tags = file.Tags,
                Metrics = new() { Types = 1 }
            });
        }
        if (root.DescendantNodes().OfType<GlobalStatementSyntax>().Any() && !file.Tags.Contains("entrypoint"))
            c.Graph.Nodes[file.Id] = file with { Tags = [.. file.Tags, "entrypoint"] };
        if (!c.Options.IncludeMembers) return;
        foreach (var declaration in root.DescendantNodes().Where(n => n is BaseMethodDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax))
        {
            var symbol = model.GetDeclaredSymbol(declaration, ct);
            if (symbol?.ContainingType == null) continue;
            var parent = "type:" + u.Module.Id + ":" + Fqn(symbol.ContainingType);
            if (!c.Graph.Nodes.ContainsKey(parent)) continue;
            var signature = symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var subkind = declaration is ConstructorDeclarationSyntax ? "ctor" : symbol is IPropertySymbol ? "property" : symbol is IEventSymbol ? "event" : "method";
            c.Add(new Node
            {
                Id = "member:" + u.Module.Id + ":" + signature,
                Kind = NodeKinds.Member,
                SubKind = subkind,
                Name = symbol.Name,
                ParentId = parent,
                Path = tree.FilePath,
                Line = Line(declaration),
                Language = "C#",
                Summary = Summary(symbol, ct),
                Tags = file.Tags,
                Metrics = new() { Members = 1, Complexity = Complexity(declaration) }
            });
        }
        foreach (var declaration in root.DescendantNodes().OfType<VariableDeclaratorSyntax>().Where(n => n.Parent?.Parent is FieldDeclarationSyntax or EventFieldDeclarationSyntax))
        {
            var symbol = model.GetDeclaredSymbol(declaration, ct);
            if (symbol?.ContainingType == null || symbol.DeclaredAccessibility == Accessibility.Private) continue;
            var parent = "type:" + u.Module.Id + ":" + Fqn(symbol.ContainingType);
            if (!c.Graph.Nodes.ContainsKey(parent)) continue;
            var signature = symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            c.Add(new Node
            {
                Id = "member:" + u.Module.Id + ":" + signature,
                Kind = NodeKinds.Member,
                SubKind = symbol is IEventSymbol ? "event" : "field",
                Name = symbol.Name,
                ParentId = parent,
                Path = tree.FilePath,
                Line = Line(declaration),
                Language = "C#",
                Summary = Summary(symbol, ct),
                Tags = file.Tags,
                Metrics = new() { Members = 1 }
            });
        }
    }
    private static int Complexity(SyntaxNode declaration) => 1 + declaration.DescendantNodes().Count(n =>
        n is IfStatementSyntax or ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax or CatchClauseSyntax
        or ConditionalExpressionSyntax or CaseSwitchLabelSyntax || n is SwitchExpressionArmSyntax { Pattern: not DiscardPatternSyntax }
        || n.IsKind(SyntaxKind.LogicalAndExpression) || n.IsKind(SyntaxKind.LogicalOrExpression) || n.IsKind(SyntaxKind.CoalesceExpression));
    private static void Connect(ScanContext c, Unit u, SyntaxTree tree, ConcurrentDictionary<string, TypeInfo> types, CancellationToken ct)
    {
        var model = u.Compilation.GetSemanticModel(tree, ignoreAccessibility: true);
        var root = tree.GetRoot(ct);
        var fileId = ScanContext.FileId(tree.FilePath);
        void Link(SyntaxNode site, ISymbol? symbol, string kind)
        {
            var targetType = symbol switch { INamedTypeSymbol t => t, IAliasSymbol { Target: INamedTypeSymbol t } => t, _ => symbol?.ContainingType };
            if (targetType is null || targetType.TypeKind == TypeKind.Error || !types.TryGetValue(Key(targetType), out var target)) return;
            var owner = site.AncestorsAndSelf().FirstOrDefault(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);
            var source = owner == null ? null : model.GetDeclaredSymbol(owner, ct) as INamedTypeSymbol;
            var snippet = Regex.Replace(site.ToString(), @"\s+", " ");
            var evidence = new SourceLocation(tree.FilePath, Line(site), snippet.Length > 90 ? snippet[..90] + "…" : snippet);
            // Finest level known: function/property → function/property/field when both are on the map,
            // otherwise the containing type (or the file for top-level statements). The viewer rolls
            // these up to types, files, folders and modules itself.
            var from = SourceMember(site) ?? (source != null && types.TryGetValue(Key(source), out var sourceType) ? sourceType.Id : fileId);
            var to = TargetMember(symbol, target.Module) ?? target.Id;
            if (from != to) c.Edge(kind, from, to, evidence);
        }
        // The function / property / field whose code contains the site, if it is a node on the map.
        string? SourceMember(SyntaxNode site)
        {
            var decl = site.AncestorsAndSelf().FirstOrDefault(n => n is BaseMethodDeclarationSyntax or PropertyDeclarationSyntax
                or IndexerDeclarationSyntax or EventDeclarationSyntax
                || n is VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax or EventFieldDeclarationSyntax });
            return decl == null ? null : MemberNode(model.GetDeclaredSymbol(decl, ct), u.Module.Id);
        }
        // The function / property / field the site points at ("x.Foo()", "x.Bar", "new T()").
        string? TargetMember(ISymbol? symbol, string module) => symbol switch
        {
            IMethodSymbol m => MemberNode(m.ReducedFrom ?? m, module),
            IPropertySymbol or IFieldSymbol or IEventSymbol => MemberNode(symbol, module),
            _ => null,
        };
        string? MemberNode(ISymbol? s, string module)
        {
            var doc = s?.OriginalDefinition.GetDocumentationCommentId();
            if (doc == null) return null;
            var id = "member:" + module + ":" + doc;
            return c.Graph.Nodes.ContainsKey(id) ? id : null;
        }
        // The name inside `Foo()`, `x.Foo()` or `new Foo()` is already counted as a call.
        static bool IsCallTarget(SimpleNameSyntax name) => name.Parent switch
        {
            InvocationExpressionSyntax inv => inv.Expression == name,
            MemberAccessExpressionSyntax ma => ma.Name == name && ma.Parent is InvocationExpressionSyntax inv && inv.Expression == ma,
            ObjectCreationExpressionSyntax oc => oc.Type == name,
            _ => false,
        };
        foreach (var node in root.DescendantNodes())
        {
            ct.ThrowIfCancellationRequested();
            switch (node)
            {
                case SimpleBaseTypeSyntax b:
                    var baseType = model.GetTypeInfo(b.Type, ct).Type as INamedTypeSymbol;
                    var declaringType = b.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                    Link(b, baseType, baseType?.TypeKind == TypeKind.Interface && declaringType is not InterfaceDeclarationSyntax ? EdgeKinds.Implement : EdgeKinds.Inherit);
                    break;
                case InvocationExpressionSyntax invocation:
                    Link(invocation, model.GetSymbolInfo(invocation, ct).Symbol, EdgeKinds.Call); break;
                case ObjectCreationExpressionSyntax creation:
                    Link(creation, model.GetSymbolInfo(creation, ct).Symbol, EdgeKinds.Call); break;
                case ImplicitObjectCreationExpressionSyntax creation:
                    Link(creation, model.GetSymbolInfo(creation, ct).Symbol, EdgeKinds.Call); break;
                case SimpleNameSyntax name when name.Parent is not UsingDirectiveSyntax && !IsCallTarget(name):
                    Link(name, model.GetSymbolInfo(name, ct).Symbol, EdgeKinds.Reference); break;
            }
        }
    }
}
