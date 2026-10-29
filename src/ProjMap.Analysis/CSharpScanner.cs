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
    private static IEnumerable<string> FrameworkPaths()
    {
        var runtime = Path.TrimEndingDirectorySeparator(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
        var dotnet = Directory.GetParent(runtime)?.Parent?.Parent?.FullName;
        var reference = dotnet == null ? "" : Path.Combine(dotnet, "packs", "Microsoft.NETCore.App.Ref", Path.GetFileName(runtime), "ref", $"net{Environment.Version.Major}.0");
        var platform = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator);
        if (Directory.Exists(reference))
            return Directory.EnumerateFiles(reference, "*.dll").Concat(platform.Where(p => !string.Equals(Path.GetDirectoryName(p), runtime, StringComparison.OrdinalIgnoreCase)));
        return platform.Concat(Directory.EnumerateFiles(runtime, "*.dll"));
    }
    private static readonly Lazy<MetadataReference[]> Framework = new(() =>
        // Prefer matching SDK reference metadata; it avoids importing runtime implementation
        // details. Portable installs without the SDK retain the original runtime/TPA fallback.
        // Bundled assemblies in single-file hosts have no path, so keep only real files.
        FrameworkPaths()
        .Where(p => p.Length > 0 && File.Exists(p))
        .DistinctBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
        .Select(p => { try { return (MetadataReference?)MetadataReference.CreateFromFile(p); } catch (BadImageFormatException) { return null; } })
        .OfType<MetadataReference>().ToArray());
    internal sealed record TypeInfo(string Id, string File, string Module);
    internal sealed class FileAnalysis(SyntaxTree tree, string hash)
    {
        public SyntaxTree Tree { get; } = tree;
        public string Hash { get; } = hash;
        public List<Node> Nodes { get; } = [];
        public Dictionary<string, TypeInfo> Types { get; } = new(StringComparer.Ordinal);
        public List<Edge> Edges { get; set; } = [];
        public List<string>? FileTags { get; set; }
    }
    internal sealed class Unit(RepoModule module, CSharpCompilation compilation, List<SyntaxTree> trees,
        Dictionary<string, FileAnalysis> results, string configuration)
    {
        public RepoModule Module { get; } = module;
        public CSharpCompilation Compilation { get; } = compilation;
        public List<SyntaxTree> Trees { get; } = trees;
        public Dictionary<string, FileAnalysis> Results { get; } = results;
        public string Configuration { get; } = configuration;
        private readonly ConcurrentDictionary<SyntaxTree, SemanticModel> models = new();
        public SemanticModel Model(SyntaxTree tree, ScanContext c) => models.GetOrAdd(tree, t =>
        {
            using var timing = c.Measure("csharp.semanticModels");
            return Compilation.GetSemanticModel(t, ignoreAccessibility: true);
        });
        public void ReleaseModel(SyntaxTree tree) => models.TryRemove(tree, out _);
        public Unit WithCompilation(CSharpCompilation compilation) => new(Module, compilation, Trees, Results, Configuration);
    }
    internal sealed record State(Dictionary<string, Unit> Units);

    // Only bodies whose return/member types are explicit are removed. Initializers, constants,
    // attributes, usings and declaration signatures remain: they can affect other files' binding.
    private sealed class DeclarationSurface : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) => node.WithBody(null).WithExpressionBody(null);
        public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node) => node.WithBody(null).WithExpressionBody(null);
        public override SyntaxNode? VisitDestructorDeclaration(DestructorDeclarationSyntax node) => node.WithBody(null).WithExpressionBody(null);
        public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax node) => node.WithBody(null).WithExpressionBody(null);
        public override SyntaxNode? VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node) => node.WithBody(null).WithExpressionBody(null);
        public override SyntaxNode? VisitAccessorDeclaration(AccessorDeclarationSyntax node) => node.WithBody(null).WithExpressionBody(null);
        public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node) => base.VisitPropertyDeclaration(node.WithExpressionBody(null));
        public override SyntaxNode? VisitIndexerDeclaration(IndexerDeclarationSyntax node) => base.VisitIndexerDeclaration(node.WithExpressionBody(null));
    }

    public async Task ScanAsync(ScanContext c, CancellationToken ct)
    {
        c.Timings.TryAdd("csharp.semanticModels", 0);
        EmitDependencies(c);
        var previous = c.Previous?.CSharp;
        var units = new ConcurrentDictionary<string, Unit>(StringComparer.Ordinal);
        var invalidated = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var parallel = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 2, 1, 14) };
        var modules = c.Modules.Where(m => m.Language == "C#").ToList();
        var filesByModule = c.Files.Values.Where(f => f.Language == "C#")
            .ToLookup(f => c.ModuleFor(f.Path)?.Id ?? "system:root", StringComparer.Ordinal);
        if (filesByModule["system:root"].Any()) modules.Add(new("system:root", c.Graph.Project.Name, "", "", "C#"));
        int parsed = 0, reused = 0;
        using (c.Measure("csharp.parse"))
        await Parallel.ForEachAsync(modules, parallel, (module, token) =>
        {
            try
            {
                var configuration = string.Join('\n', module.Id, module.ImplicitUsings, string.Join(';', module.Defines),
                    string.Join(';', module.References));
                var old = previous?.Units.GetValueOrDefault(module.Id);
                if (old?.Configuration != configuration) old = null;
                var results = old == null ? new Dictionary<string, FileAnalysis>(StringComparer.Ordinal) : new(old.Results, StringComparer.Ordinal);
                var trees = new List<SyntaxTree>();
                var compilation = old?.Compilation;
                var surfaceChanged = old == null;
                var parseOptions = new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: module.Defines);
                foreach (var file in filesByModule[module.Id].OrderBy(f => f.Path, StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested();
                    if (file.Text(c) is not { } text) continue;
                    var hash = c.State.Hashes[file.Path];
                    var cached = results.GetValueOrDefault(file.Path);
                    SyntaxTree tree;
                    if (cached != null && cached.Hash == hash && !c.ForcedPaths.Contains(file.Path)) tree = cached.Tree;
                    else
                    {
                        tree = CSharpSyntaxTree.ParseText(text, parseOptions, file.Path, cancellationToken: token);
                        Interlocked.Increment(ref parsed);
                        if (cached != null)
                        {
                            compilation = compilation!.ReplaceSyntaxTree(cached.Tree, tree);
                            if (cached.Hash != hash && (!new DeclarationSurface().Visit(cached.Tree.GetRoot(token))!.IsEquivalentTo(
                                new DeclarationSurface().Visit(tree.GetRoot(token))) ||
                                !SharedTrivia(cached.Tree, token).SequenceEqual(SharedTrivia(tree, token)))) surfaceChanged = true;
                        }
                        else { compilation = compilation?.AddSyntaxTrees(tree); surfaceChanged = true; }
                        results.Remove(file.Path);
                    }
                    trees.Add(tree);
                }
                var currentPaths = trees.Select(t => t.FilePath).ToHashSet(StringComparer.Ordinal);
                foreach (var removed in results.Keys.Where(path => !currentPaths.Contains(path)).ToArray())
                {
                    compilation = compilation!.RemoveSyntaxTrees(results[removed].Tree);
                    results.Remove(removed);
                    surfaceChanged = true;
                }
                if (compilation == null)
                {
                    var allTrees = new List<SyntaxTree>(trees);
                    if (module.ImplicitUsings) allTrees.Add(CSharpSyntaxTree.ParseText("global using System; global using System.Collections.Generic; global using System.Linq; global using System.Threading; global using System.Threading.Tasks; global using System.IO; global using System.Net.Http;", parseOptions));
                    var assembly = "scan_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(module.Id)))[..16];
                    compilation = CSharpCompilation.Create(assembly, allTrees, Framework.Value,
                        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
                }
                else Interlocked.Increment(ref reused);
                if (surfaceChanged) invalidated[module.Id] = 0;
                units[module.Id] = new(module, compilation, trees, results, configuration);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { c.Warn(Name, ex.Message, module.Manifest); }
            return ValueTask.CompletedTask;
        });
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
                var dependencies = Reachable(unit.Module, c, new HashSet<string>(StringComparer.Ordinal)).Where(done.Contains).Distinct().ToArray();
                if (dependencies.Any(invalidated.ContainsKey)) invalidated[id] = 0;
                var oldRefs = unit.Compilation.References.OfType<CompilationReference>().ToArray();
                var refs = dependencies.Select(d => units[d].Compilation).ToArray();
                if (!oldRefs.Select(r => r.Compilation.AssemblyName).SequenceEqual(refs.Select(r => r.AssemblyName))) invalidated[id] = 0;
                if (oldRefs.Length != refs.Length || oldRefs.Where((r, i) => !ReferenceEquals(r.Compilation, refs[i])).Any())
                    unit = unit.WithCompilation(unit.Compilation.RemoveReferences(oldRefs).AddReferences(refs.Select(r => r.ToMetadataReference())));
                if (invalidated.ContainsKey(id)) unit.Results.Clear();
                units[id] = unit;
                pending.Remove(id); done.Add(id);
            }
        }
        var types = new ConcurrentDictionary<string, TypeInfo>(StringComparer.Ordinal);
        var work = units.Values.SelectMany(u => u.Trees.Where(t => !u.Results.ContainsKey(t.FilePath)).Select(t => (Unit: u, Tree: t))).ToArray();
        foreach (var unit in units.Values.OrderBy(u => u.Module.Id, StringComparer.Ordinal))
            foreach (var result in unit.Results.Values.OrderBy(r => r.Tree.FilePath, StringComparer.Ordinal))
            {
                foreach (var (key, info) in result.Types) types[key] = info;
                foreach (var node in result.Nodes) c.AddDeclared(node);
                ApplyTags(c, result);
            }
        foreach (var item in work) item.Unit.Results[item.Tree.FilePath] = new(item.Tree, c.State.Hashes[item.Tree.FilePath]);
        using (c.Measure("csharp.declare"))
        {
            // All type endpoints (including canonical partial declarations) precede all members.
            foreach (var members in new[] { false, true })
                await Parallel.ForEachAsync(work, parallel, (item, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    try { Declare(c, item.Unit, item.Tree, types, item.Unit.Results[item.Tree.FilePath], members, token); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { c.Warn(Name, ex.Message, item.Tree.FilePath); }
                    return ValueTask.CompletedTask;
                });
        }
        using (c.Measure("csharp.walk"))
        await Parallel.ForEachAsync(work, parallel, (item, token) =>
        {
            token.ThrowIfCancellationRequested();
            try { item.Unit.Results[item.Tree.FilePath].Edges = Connect(c, item.Unit, item.Tree, types, token); }
            catch (Exception ex) when (ex is not OperationCanceledException) { c.Warn(Name, ex.Message, item.Tree.FilePath); }
            finally { item.Unit.ReleaseModel(item.Tree); }
            return ValueTask.CompletedTask;
        });
        foreach (var unit in units.Values.OrderBy(u => u.Module.Id, StringComparer.Ordinal))
            foreach (var result in unit.Results.Values.OrderBy(r => r.Tree.FilePath, StringComparer.Ordinal))
                foreach (var edge in result.Edges) c.Merge(edge);
        c.State.CSharp = new(units.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
        c.Counts["csharp.parsedFiles"] = parsed;
        c.Counts["csharp.analyzedFiles"] = work.Length;
        c.Counts["csharp.reusedCompilations"] = reused;
    }
    private static IEnumerable<string> SharedTrivia(SyntaxTree tree, CancellationToken ct) => tree.GetRoot(ct).DescendantTrivia()
        .Where(t => t.IsDirective || t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
        .Select(t => t.ToFullString());
    private static void ApplyTags(ScanContext c, FileAnalysis result)
    {
        if (result.FileTags is not { } tags) return;
        var id = ScanContext.FileId(result.Tree.FilePath);
        c.Graph.Nodes[id] = c.Graph.Nodes[id] with { Tags = tags };
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
    private static void Declare(ScanContext c, Unit u, SyntaxTree tree, ConcurrentDictionary<string, TypeInfo> types, FileAnalysis result, bool members, CancellationToken ct)
    {
        void Add(Node node) { result.Nodes.Add(node); c.AddDeclared(node); }
        var model = u.Model(tree, c);
        var root = tree.GetRoot(ct);
        var file = c.Graph.Nodes[ScanContext.FileId(tree.FilePath)];
        if (!members)
        {
            foreach (var declaration in root.DescendantNodes().Where(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax))
            {
                if (model.GetDeclaredSymbol(declaration, ct) is not INamedTypeSymbol symbol) continue;
                var id = "type:" + u.Module.Id + ":" + Fqn(symbol);
                var canonical = symbol.DeclaringSyntaxReferences.OrderBy(r => r.SyntaxTree.FilePath, StringComparer.Ordinal).ThenBy(r => r.Span.Start).First();
                types[Key(symbol)] = result.Types[Key(symbol)] = new(id, canonical.SyntaxTree.FilePath, u.Module.Id);
                if (canonical.SyntaxTree != tree || canonical.Span != declaration.Span) continue;
                var parent = symbol.ContainingType is { } containing ? "type:" + u.Module.Id + ":" + Fqn(containing) : file.Id;
                var kind = symbol.IsRecord ? (symbol.TypeKind == TypeKind.Struct ? "record struct" : "record") : symbol.TypeKind.ToString().ToLowerInvariant();
                Add(new Node
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
                { result.FileTags = [.. file.Tags, "entrypoint"]; ApplyTags(c, result); }
            return;
        }
        if (!c.Options.IncludeMembers) return;
        foreach (var declaration in root.DescendantNodes().Where(n => n is BaseMethodDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax))
        {
            var symbol = model.GetDeclaredSymbol(declaration, ct);
            if (symbol?.ContainingType == null) continue;
            var parent = "type:" + u.Module.Id + ":" + Fqn(symbol.ContainingType);
            if (!c.Graph.Nodes.ContainsKey(parent)) continue;
            var signature = symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var subkind = declaration is ConstructorDeclarationSyntax ? "ctor" : symbol is IPropertySymbol ? "property" : symbol is IEventSymbol ? "event" : "method";
            Add(new Node
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
            Add(new Node
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
    private static List<Edge> Connect(ScanContext c, Unit u, SyntaxTree tree, ConcurrentDictionary<string, TypeInfo> types, CancellationToken ct)
    {
        var model = u.Model(tree, c);
        var root = tree.GetRoot(ct);
        var fileId = ScanContext.FileId(tree.FilePath);
        var typeCache = new Dictionary<INamedTypeSymbol, TypeInfo?>(SymbolEqualityComparer.Default);
        var memberCache = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        var sourceCache = new Dictionary<SyntaxNode, string?>();
        var ownerCache = new Dictionary<SyntaxNode, string>();
        var edges = new Dictionary<(string Kind, string From, string To), LocalEdge>();
        TypeInfo? TypeNode(INamedTypeSymbol type)
        {
            if (!typeCache.TryGetValue(type, out var result))
                typeCache[type] = result = type.TypeKind == TypeKind.Error ? null : types.GetValueOrDefault(Key(type));
            return result;
        }
        void Link(SyntaxNode site, ISymbol? symbol, string kind)
        {
            var targetType = symbol switch { INamedTypeSymbol t => t, IAliasSymbol { Target: INamedTypeSymbol t } => t, _ => symbol?.ContainingType };
            if (targetType is null || TypeNode(targetType) is not { } target) return;
            var owner = site.AncestorsAndSelf().FirstOrDefault(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);
            string? sourceId = null;
            if (owner != null && !ownerCache.TryGetValue(owner, out sourceId))
                ownerCache[owner] = sourceId = model.GetDeclaredSymbol(owner, ct) is INamedTypeSymbol source ? TypeNode(source)?.Id ?? fileId : fileId;
            // Finest level known: function/property → function/property/field when both are on the map,
            // otherwise the containing type (or the file for top-level statements). The viewer rolls
            // these up to types, files, folders and modules itself.
            var from = SourceMember(site) ?? sourceId ?? fileId;
            var to = TargetMember(symbol, target.Module) ?? target.Id;
            if (from == to) return;
            var snippet = Regex.Replace(site.ToString(), @"\s+", " ");
            var evidence = new SourceLocation(tree.FilePath, Line(site), snippet.Length > 90 ? snippet[..90] + "…" : snippet);
            var key = (kind, from, to);
            if (!edges.TryGetValue(key, out var edge)) edges[key] = edge = new(kind, from, to);
            edge.Add(evidence);
        }
        // The function / property / field whose code contains the site, if it is a node on the map.
        string? SourceMember(SyntaxNode site)
        {
            var decl = site.AncestorsAndSelf().FirstOrDefault(n => n is BaseMethodDeclarationSyntax or PropertyDeclarationSyntax
                or IndexerDeclarationSyntax or EventDeclarationSyntax
                || n is VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax or EventFieldDeclarationSyntax });
            if (decl == null) return null;
            if (!sourceCache.TryGetValue(decl, out var id)) sourceCache[decl] = id = MemberNode(model.GetDeclaredSymbol(decl, ct), u.Module.Id);
            return id;
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
            if (s == null) return null;
            if (memberCache.TryGetValue(s, out var cached)) return cached.Length == 0 ? null : cached;
            var doc = s.OriginalDefinition.GetDocumentationCommentId();
            var id = doc == null ? null : "member:" + module + ":" + doc;
            var canonical = id != null && c.Graph.Nodes.TryGetValue(id, out var node) ? node.Id : "";
            memberCache[s] = canonical;
            return canonical.Length == 0 ? null : canonical;
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
        return edges.Values.Select(e => e.ToEdge()).ToList();
    }

    private sealed class LocalEdge(string kind, string source, string target)
    {
        private int weight;
        private readonly List<SourceLocation> evidence = [];
        public void Add(SourceLocation location)
        {
            weight++;
            if (evidence.Contains(location)) return;
            // A file is walked in source order. Keep the original stable tie order on a line.
            var index = evidence.FindIndex(e => e.Line > location.Line);
            if (index < 0) index = evidence.Count;
            if (index >= 3) return;
            evidence.Insert(index, location);
            if (evidence.Count > 3) evidence.RemoveAt(3);
        }
        public Edge ToEdge() => new() { Id = $"{kind}:{source}->{target}", Kind = kind, Source = source,
            Target = target, Weight = weight, Evidence = evidence };
    }
}
