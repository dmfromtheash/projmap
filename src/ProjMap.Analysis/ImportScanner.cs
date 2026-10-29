using System.Text.Json;
using System.Text.RegularExpressions;
using ProjMap.Core;

namespace ProjMap.Analysis;

internal sealed class ImportScanner : IRepositoryScanner
{
    public string Name => "imports";
    private static readonly Regex JsImports = new("(?:\\b(?:import|export)\\s+(?:[^;\\r\\n]*?\\s+from\\s+)?|\\b(?:require|import)\\s*\\(\\s*)[\"'](?<path>[^\"']+)[\"']", RegexOptions.Compiled);
    private static readonly Regex PyImports = new(@"^\s*(?:from\s+(?<from>[.\w]+)\s+import\s+(?<names>[^#\r\n]+)|import\s+(?<imports>[^#\r\n]+))", RegexOptions.Multiline | RegexOptions.Compiled);
    public Task ScanAsync(ScanContext c, CancellationToken ct)
    {
        foreach (var f in c.Files.Values.Where(f => f.Language is "TypeScript" or "JavaScript" or "Python"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (f.Text(c) is not { } text) continue;
                // Comments do not introduce imports. Preserve line offsets for evidence.
                var source = f.Language == "Python" ? Regex.Replace(text, @"(?m)^\s*#.*$", m => new string(' ', m.Length)) :
                    Regex.Replace(text, @"/\*[\s\S]*?\*/|(?m)^\s*//.*$", m => Regex.Replace(m.Value, @"[^\r\n]", " "));
                var module = c.ModuleFor(f.Path);
                if (f.Language == "Python")
                {
                    foreach (Match match in PyImports.Matches(source))
                    {
                        if (match.Groups["from"].Success)
                        {
                            var imported = match.Groups["from"].Value;
                            AddImport(c, f, match, ResolvePython(c, f, imported));
                            foreach (var name in match.Groups["names"].Value.Trim('(', ')').Split(','))
                            {
                                var part = Regex.Split(name.Trim(), @"\s+as\s+")[0].Trim();
                                if (Regex.IsMatch(part, @"^\w+$")) AddImport(c, f, match, ResolvePython(c, f, imported + (imported.EndsWith('.') ? "" : ".") + part));
                            }
                        }
                        else foreach (var name in match.Groups["imports"].Value.Split(','))
                            AddImport(c, f, match, ResolvePython(c, f, Regex.Split(name.Trim(), @"\s+as\s+")[0]));
                    }
                }
                else
                {
                    var (baseDirectory, aliases) = Aliases(c, f);
                    foreach (Match match in JsImports.Matches(source))
                    {
                        var path = match.Groups["path"].Value;
                        string? target = null;
                        if (path.StartsWith('.')) target = ResolveJs(c, StructureScanner.DirectoryName(f.Path), path);
                        else
                        {
                            foreach (var (pattern, values) in aliases)
                            {
                                var capture = Regex.Match(path, "^" + Regex.Escape(pattern).Replace(@"\*", "(.*)") + "$");
                                if (!capture.Success) continue;
                                foreach (var value in values)
                                    target ??= ResolveJs(c, baseDirectory, value.Replace("*", capture.Groups.Count > 1 ? capture.Groups[1].Value : ""));
                            }
                            target ??= ResolveJs(c, baseDirectory, path);
                            var workspace = c.Modules.Where(m => m.Manifest.EndsWith("package.json") && (path == m.Name || path.StartsWith(m.Name + "/", StringComparison.Ordinal)))
                                .OrderByDescending(m => m.Name.Length).FirstOrDefault();
                            if (workspace != null) target ??= ResolveJs(c, workspace.Directory, path.Length > workspace.Name.Length ? path[(workspace.Name.Length + 1)..] : "index");
                        }
                        AddImport(c, f, match, target);
                    }
                }
                Exports(c, f, source);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { c.Warn(Name, ex.Message, f.Path); }
        }
        return Task.CompletedTask;
    }
    private static void AddImport(ScanContext c, RepoFile f, Match match, string? target)
    {
        if (target != null) c.Edge(EdgeKinds.Import, ScanContext.FileId(f.Path), ScanContext.FileId(target),
            new(f.Path, 1 + (f.Text(c) ?? "")[..match.Index].Count(ch => ch == '\n'), match.Value.Trim()));
    }
    private static string? ResolveJs(ScanContext c, string directory, string specifier)
    {
        var path = c.Relative(Path.Combine(c.Full(directory), specifier));
        var candidates = new List<string> { path };
        if (Path.GetExtension(path) is ".js" or ".jsx" or ".mjs") candidates.AddRange(new[] { ".ts", ".tsx", ".mts" }.Select(e => Path.ChangeExtension(path, e)));
        candidates.AddRange(new[] { ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".mts", ".cts", ".d.ts", "/index.ts", "/index.tsx", "/index.js", "/index.jsx" }.Select(e => path + e));
        return candidates.Select(p => c.Files.GetValueOrDefault(p)?.Path).FirstOrDefault(p => p != null);
    }
    private static string? ResolvePython(ScanContext c, RepoFile file, string name)
    {
        var dots = name.TakeWhile(ch => ch == '.').Count();
        var relative = name[dots..].Replace('.', '/');
        var bases = new List<string>();
        if (dots > 0)
        {
            var directory = StructureScanner.DirectoryName(file.Path);
            for (var i = 1; i < dots; i++) directory = StructureScanner.DirectoryName(directory);
            bases.Add(directory);
        }
        else
        {
            bases.Add(""); bases.Add("src");
            if (c.ModuleFor(file.Path) is { } m) { bases.Add(m.Directory); bases.Add(m.Directory + "/src"); }
        }
        foreach (var directory in bases.Distinct())
        {
            var path = c.Relative(Path.Combine(c.Full(directory), relative));
            foreach (var candidate in new[] { path + ".py", path.TrimEnd('/') + "/__init__.py" })
                if (c.Files.TryGetValue(candidate, out var target)) return target.Path;
        }
        return null;
    }
    private static (string Base, Dictionary<string, string[]> Aliases) Aliases(ScanContext c, RepoFile file)
    {
        var directory = StructureScanner.DirectoryName(file.Path);
        while (true)
        {
            foreach (var name in new[] { "tsconfig.json", "jsconfig.json" })
            {
                var path = directory.Length == 0 ? name : directory + "/" + name;
                if (!c.Files.TryGetValue(path, out var config)) continue;
                using var json = JsonDocument.Parse(config.Text(c) ?? "{}", new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                var aliases = new Dictionary<string, string[]>();
                if (!json.RootElement.TryGetProperty("compilerOptions", out var compiler)) return (directory, aliases);
                var baseDirectory = compiler.TryGetProperty("baseUrl", out var url) ? c.Relative(Path.Combine(c.Full(directory), url.GetString() ?? ".")) : directory;
                if (baseDirectory == ".") baseDirectory = "";
                if (compiler.TryGetProperty("paths", out var paths))
                    foreach (var p in paths.EnumerateObject()) aliases[p.Name] = p.Value.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
                return (baseDirectory, aliases);
            }
            if (directory.Length == 0) return ("", []);
            directory = StructureScanner.DirectoryName(directory);
        }
    }
    private static void Exports(ScanContext c, RepoFile file, string source)
    {
        var python = file.Language == "Python";
        var pattern = python ? @"(?m)^(?:async\s+)?(?<kind>def|class)\s+(?<name>[A-Za-z]\w*)" :
            @"(?m)\bexport\s+(?:default\s+)?(?:declare\s+)?(?:async\s+)?(?<kind>class|interface|type|enum|function|const|let)\s+(?<name>\w+)";
        foreach (Match match in Regex.Matches(source, pattern))
        {
            var name = match.Groups["name"].Value;
            var kind = match.Groups["kind"].Value;
            var isType = kind is "class" or "interface" or "type" or "enum";
            if (!isType && !c.Options.IncludeMembers) continue;
            var nodeKind = isType ? NodeKinds.Type : NodeKinds.Member;
            c.Add(new Node { Id = nodeKind + ":" + file.Path + ":" + name, Kind = nodeKind, Name = name,
                SubKind = isType ? kind : "function", ParentId = ScanContext.FileId(file.Path), Path = file.Path,
                Language = file.Language, Line = 1 + source[..match.Index].Count(ch => ch == '\n'), Tags = file.Tags,
                Metrics = new() { Types = isType ? 1 : 0, Members = isType ? 0 : 1 } });
        }
    }
}
