using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ProjMap.Core;

namespace ProjMap.Analysis;

internal sealed partial class CSharpScanner : IRepositoryScanner
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
    // NuGet compile assemblies of a restored project (obj/project.assets.json, written by any build/restore).
    // Without them every package type (Avalonia, SkiaSharp …) is an error type: event handlers, overrides and
    // calls taking such arguments stay unbound. Not restored → no package references, as before.
    private static readonly ConcurrentDictionary<string, MetadataReference?> PackageCache = new(StringComparer.OrdinalIgnoreCase);
    internal static IReadOnlyList<string> PackageAssemblies(string projectDirectory)
    {
        var assets = Path.Combine(projectDirectory, "obj", "project.assets.json");
        if (!File.Exists(assets)) return [];
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(assets));
            var root = document.RootElement;
            var folders = root.GetProperty("packageFolders").EnumerateObject().Select(p => p.Name).ToArray();
            var libraries = root.GetProperty("libraries");
            // The runtime-independent target ("net10.0", not "net10.0/win-x64") holds the reference assemblies.
            var target = root.GetProperty("targets").EnumerateObject().FirstOrDefault(t => !t.Name.Contains('/'));
            if (target.Value.ValueKind != System.Text.Json.JsonValueKind.Object) return [];
            var result = new List<string>();
            foreach (var package in target.Value.EnumerateObject())
            {
                if (!package.Value.TryGetProperty("type", out var type) || type.GetString() != "package") continue;
                if (!package.Value.TryGetProperty("compile", out var compile)) continue;
                if (!libraries.TryGetProperty(package.Name, out var library) || !library.TryGetProperty("path", out var path)) continue;
                foreach (var file in compile.EnumerateObject().Select(f => f.Name).Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                    if (folders.Select(f => Path.Combine(f, path.GetString()!, file)).FirstOrDefault(File.Exists) is { } full) result.Add(Path.GetFullPath(full));
            }
            return result;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or IOException) { return []; }
    }
    private static IEnumerable<MetadataReference> PackageReferences(IEnumerable<string> paths) => paths
        .Select(p => PackageCache.GetOrAdd(p, path => { try { return MetadataReference.CreateFromFile(path); } catch (Exception ex) when (ex is IOException or BadImageFormatException) { return null; } }))
        .OfType<MetadataReference>();
    internal sealed record TypeInfo(string Id, string File, string Module);
    internal sealed class FileAnalysis(SyntaxTree tree, string hash)
    {
        public SyntaxTree Tree { get; } = tree;
        public string Hash { get; } = hash;
        public List<Node> Nodes { get; } = [];
        public List<(string Id, ExpressionSyntax Callback, SyntaxNode Declaration)> Handlers { get; } = [];
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
                    var packages = module.Manifest.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                        ? PackageAssemblies(Path.GetDirectoryName(c.Full(module.Manifest))!) : [];
                    var configuration = string.Join('\n', module.Id, module.ImplicitUsings, string.Join(';', module.Defines),
                        string.Join(';', module.References), string.Join(';', c.Graph.Nodes[module.Id].Tags), string.Join(';', packages));
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
                        compilation = CSharpCompilation.Create(assembly, allTrees, Framework.Value.Concat(PackageReferences(packages)),
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
        // Visibility depends on uses in other partial files, even when their bodies alone changed.
        foreach (var unit in units.Values) RefreshNotShown(c, unit, ct);
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
            // Overrides and interface implementations of library members (Avalonia's OnPointerPressed, IDisposable.Dispose …)
            // are called by the library, not by repository code: they have no incoming edge yet are live.
            List<string> tags = CalledByLibrary(symbol) ? [.. file.Tags, "callback"] : file.Tags;
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
                Tags = tags,
                Metrics = new() { Members = 1, Complexity = Complexity(declaration) }
            });
            foreach (var (lambda, id, label) in Handlers(declaration, "member:" + u.Module.Id + ":" + signature, model, ct))
            {
                result.Handlers.Add((id, lambda, declaration));
                Add(new Node
                {
                    Id = id,
                    Kind = NodeKinds.Member,
                    SubKind = "handler",
                    Name = label,
                    ParentId = parent,
                    Path = tree.FilePath,
                    Line = Line(lambda),
                    Language = "C#",
                    Anchor = "member:" + u.Module.Id + ":" + signature + "|" + HandlerAnchor(lambda, label, model, ct),
                    Tags = LibraryOnly(lambda, model, ct) ? [.. file.Tags, "handler", "library-only"] : [.. file.Tags, "handler"],
                    Metrics = new() { Members = 1, Complexity = Complexity(lambda) }
                });
            }
        }
        foreach (var declaration in root.DescendantNodes().OfType<VariableDeclaratorSyntax>().Where(n => n.Parent?.Parent is FieldDeclarationSyntax or EventFieldDeclarationSyntax))
        {
            var symbol = model.GetDeclaredSymbol(declaration, ct);
            if (symbol?.ContainingType == null || symbol.DeclaredAccessibility == Accessibility.Private) continue;
            var parent = "type:" + u.Module.Id + ":" + Fqn(symbol.ContainingType);
            if (!c.Graph.Nodes.ContainsKey(parent)) continue;
            var signature = symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            // Fields of a [StructLayout] struct mirror a native layout: Windows fills them, code may never read them all.
            var native = symbol.ContainingType.GetAttributes().Any(a => a.AttributeClass?.Name == "StructLayoutAttribute");
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
                Tags = native ? [.. file.Tags, "interop"] : file.Tags,
                Metrics = new() { Members = 1 }
            });
        }
    }
    // A UI handler is a lambda wired to a control: `Ui.TextButton("Up", "Наверх", () => …)` (an argument of a call that
    // also passes a string literal — the control's name or text) or `button.Click += (_, _) => …`. Each becomes its own
    // node, so "which button runs what" is visible instead of everything belonging to the method that builds the panel.
    // Ids: the containing member's id + "#h<n>" in source order, stable while that member's handlers keep their order.
    internal static List<(ExpressionSyntax Lambda, string Id, string Label)> Handlers(SyntaxNode declaration, string memberId, SemanticModel model, CancellationToken ct)
    {
        var result = new List<(ExpressionSyntax, string, string)>();
        foreach (var lambda in declaration.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>())
        {
            string? label = lambda.Parent switch
            {
                AssignmentExpressionSyntax a when a.IsKind(SyntaxKind.AddAssignmentExpression) && a.Right == lambda => a.Left.ToString(),
                // The arguments before the lambda name the control (name, visible text); later ones are style classes.
                ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax or ObjectCreationExpressionSyntax } list } arg =>
                    list.Arguments.TakeWhile(x => x != arg).Select(x => x.Expression).ToArray() is var before
                    && before.Any(IsText)
                        ? Callee(list.Parent!) + " «" + string.Join(" · ", before.Where(e => IsText(e) || e is IdentifierNameSyntax or MemberAccessExpressionSyntax)
                            .Take(2).Select(e => e is LiteralExpressionSyntax l ? l.Token.ValueText : e.ToString())) + "»"
                        : null,
                _ => null,
            };
            if (label == null) continue;
            label = Regex.Replace(label, @"\s+", " ");
            result.Add((lambda, memberId + "#h" + (result.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                label.Length > 80 ? label[..80] + "…" : label));
        }
        // Append newly supported forms, preserving every legacy #h<n> even if a new form occurs earlier.
        var known = result.Select(x => x.Item1).ToHashSet();
        foreach (var expression in declaration.DescendantNodes().OfType<ExpressionSyntax>())
        {
            if (known.Contains(expression)) continue;
            if (expression.Parent is not ArgumentSyntax { Parent: ArgumentListSyntax list } arg) continue;
            if (list.Parent is not (InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax)) continue;
            var before = list.Arguments.TakeWhile(a => a != arg).Select(a => a.Expression).ToArray();
            var method = Resolved(list.Parent!, model, ct) as IMethodSymbol;
            var timer = method?.ContainingType.Name.EndsWith("Timer", StringComparison.Ordinal) == true;
            var routedEvent = method?.Name == "AddHandler" && before.Any(e => e.ToString().EndsWith("Event", StringComparison.Ordinal));
            if (!timer && !routedEvent && !before.Any(e => IsText(e) || model.GetTypeInfo(e, ct).Type?.SpecialType == SpecialType.System_String)) continue;
            if (model.GetTypeInfo(expression, ct).ConvertedType?.TypeKind != TypeKind.Delegate) continue;
            // Delegate parameters/method groups are entry points too (e.g. Ui.IconButton(..., vm.Undo)).
            if (expression is not (AnonymousFunctionExpressionSyntax or IdentifierNameSyntax or MemberAccessExpressionSyntax)) continue;
            var captions = before.Where(e => IsText(e) || model.GetTypeInfo(e, ct).Type?.SpecialType == SpecialType.System_String).Take(2).ToArray();
            var label = Callee(list.Parent!) + (captions.Length == 0 ? "" : " «" + string.Join(" · ", captions
                .Select(e => e is LiteralExpressionSyntax l ? l.Token.ValueText : e.ToString())) + "»");
            label = Regex.Replace(label, @"\s+", " ");
            result.Add((expression, memberId + "#h" + (result.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                label.Length > 80 ? label[..80] + "…" : label));
        }
        return result;
        static bool IsText(ExpressionSyntax e) => e.IsKind(SyntaxKind.StringLiteralExpression) || e is InterpolatedStringExpressionSyntax
            || e is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } b && (IsText(b.Left) || IsText(b.Right));
        static string Callee(SyntaxNode call) => call switch
        {
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax m } => m.Name.Identifier.ValueText,
            InvocationExpressionSyntax { Expression: SimpleNameSyntax n } => n.Identifier.ValueText,
            ObjectCreationExpressionSyntax o => "new " + o.Type,
            ImplicitObjectCreationExpressionSyntax => "new",
            _ => "handler",
        };
    }
    // Scanned assemblies are named "scan_…"; anything else (framework, NuGet) is a library.
    private static bool FromLibrary(ISymbol member) => member.ContainingAssembly is { } a && !a.Name.StartsWith("scan_", StringComparison.Ordinal);
    private static bool CalledByLibrary(ISymbol symbol)
    {
        var overridden = symbol switch
        {
            IMethodSymbol m => m.OverriddenMethod as ISymbol,
            IPropertySymbol p => p.OverriddenProperty,
            IEventSymbol e => e.OverriddenEvent,
            _ => null,
        };
        while (overridden is not null && !FromLibrary(overridden))
            overridden = overridden switch { IMethodSymbol m => m.OverriddenMethod, IPropertySymbol p => p.OverriddenProperty, IEventSymbol e => e.OverriddenEvent, _ => null };
        if (overridden is not null) return true;
        var type = symbol.ContainingType;
        return type.AllInterfaces.Where(FromLibrary).SelectMany(i => i.GetMembers())
            .Any(m => SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(m), symbol));
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
        // The function / property / field whose code contains the site, if it is a node on the map — or the UI handler
        // lambda inside it (see Handlers), so a button's calls belong to that button.
        var handlerCache = new Dictionary<SyntaxNode, Dictionary<SyntaxNode, string>>();
        Dictionary<SyntaxNode, string> HandlersOf(SyntaxNode decl, string memberId)
        {
            if (!handlerCache.TryGetValue(decl, out var map))
            {
                handlerCache[decl] = map = [];
                foreach (var (lambda, id, _) in Handlers(decl, memberId, model, ct))
                    if (c.Graph.Nodes.ContainsKey(id)) map[lambda] = id;
            }
            return map;
        }
        string? SourceMember(SyntaxNode site)
        {
            var decl = site.AncestorsAndSelf().FirstOrDefault(n => n is BaseMethodDeclarationSyntax or PropertyDeclarationSyntax
                or IndexerDeclarationSyntax or EventDeclarationSyntax
                || n is VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax or EventFieldDeclarationSyntax });
            if (decl == null) return null;
            if (!sourceCache.TryGetValue(decl, out var id)) sourceCache[decl] = id = MemberNode(model.GetDeclaredSymbol(decl, ct), u.Module.Id);
            if (id == null) return null;
            var handlers = HandlersOf(decl, id);
            if (handlers.Count > 0)
                foreach (var ancestor in site.AncestorsAndSelf().TakeWhile(a => a != decl))
                    if (handlers.TryGetValue(ancestor, out var handler)) return handler;
            return id;
        }
        // Builder → each handler it wires (the innermost enclosing handler or the member itself).
        void WireHandlers(SyntaxNode decl)
        {
            if (SourceMember(decl) is not { } memberId) return;
            foreach (var (lambda, handler) in HandlersOf(decl, memberId))
            {
                var from = SourceMember(lambda.Parent!) ?? memberId;
                var key = (EdgeKinds.Reference, from, handler);
                if (!edges.TryGetValue(key, out var edge)) edges[key] = edge = new(EdgeKinds.Reference, from, handler);
                var snippet = Regex.Replace(lambda.Parent?.ToString() ?? lambda.ToString(), @"\s+", " ");
                edge.Add(new SourceLocation(tree.FilePath, Line(lambda), snippet.Length > 90 ? snippet[..90] + "…" : snippet));
            }
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
        // When a referenced library is missing (e.g. its types are unresolved), Roslyn may not bind `Event += Handler`
        // or a call with such an argument, yet it names the only possible target: take that single candidate.
        ISymbol? Resolve(SyntaxNode site)
        {
            var info = model.GetSymbolInfo(site, ct);
            return info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);
        }
        // Interface member → its implementation, per implementing type (implicit implementations included).
        var implementedCache = new Dictionary<INamedTypeSymbol, ILookup<ISymbol, ISymbol>>(SymbolEqualityComparer.Default);
        IEnumerable<ISymbol> Implemented(ISymbol member)
        {
            var type = member.ContainingType;
            if (!implementedCache.TryGetValue(type, out var lookup))
                implementedCache[type] = lookup = type.AllInterfaces.SelectMany(i => i.GetMembers())
                    .Select(m => (Interface: m, Impl: type.FindImplementationForInterfaceMember(m)))
                    .Where(p => p.Impl is not null && SymbolEqualityComparer.Default.Equals(p.Impl.ContainingType, type))
                    .ToLookup(p => p.Impl!, p => p.Interface, SymbolEqualityComparer.Default);
            return lookup[member];
        }
        foreach (var node in root.DescendantNodes())
        {
            ct.ThrowIfCancellationRequested();
            switch (node)
            {
                // A call through an interface or a base class reaches this member too: link the member to what it
                // implements or overrides, so callers of the contract lead to the code that actually runs.
                case BaseMethodDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax:
                    WireHandlers(node);
                    if (model.GetDeclaredSymbol(node, ct) is { ContainingType: { } } declared)
                    {
                        foreach (var contract in Implemented(declared)) Link(node, contract, EdgeKinds.Implement);
                        var overridden = declared switch
                        {
                            IMethodSymbol m => m.OverriddenMethod as ISymbol,
                            IPropertySymbol p => p.OverriddenProperty,
                            IEventSymbol e => e.OverriddenEvent,
                            _ => null,
                        };
                        if (overridden is not null) Link(node, overridden, EdgeKinds.Inherit);
                    }
                    break;
                case SimpleBaseTypeSyntax b:
                    var baseType = model.GetTypeInfo(b.Type, ct).Type as INamedTypeSymbol;
                    var declaringType = b.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                    Link(b, baseType, baseType?.TypeKind == TypeKind.Interface && declaringType is not InterfaceDeclarationSyntax ? EdgeKinds.Implement : EdgeKinds.Inherit);
                    break;
                case InvocationExpressionSyntax invocation:
                    Link(invocation, Resolve(invocation), EdgeKinds.Call); break;
                case ObjectCreationExpressionSyntax creation:
                    Link(creation, model.GetSymbolInfo(creation, ct).Symbol, EdgeKinds.Call); break;
                case ImplicitObjectCreationExpressionSyntax creation:
                    Link(creation, model.GetSymbolInfo(creation, ct).Symbol, EdgeKinds.Call); break;
                case SimpleNameSyntax name when name.Parent is not UsingDirectiveSyntax && !IsCallTarget(name):
                    Link(name, Resolve(name), EdgeKinds.Reference); break;
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
        public Edge ToEdge() => new()
        {
            Id = $"{kind}:{source}->{target}",
            Kind = kind,
            Source = source,
            Target = target,
            Weight = weight,
            Evidence = evidence
        };
    }
}
