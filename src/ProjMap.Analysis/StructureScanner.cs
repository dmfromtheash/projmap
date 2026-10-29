using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ProjMap.Core;

namespace ProjMap.Analysis;

internal sealed class StructureScanner : IRepositoryScanner
{
    public string Name => "structure";
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "node_modules", ".git", ".vs", "dist", "build", "__pycache__", ".venv", ".projmap" };
    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase) {
        [".cs"] = "C#", [".fs"] = "F#", [".ts"] = "TypeScript", [".tsx"] = "TypeScript", [".js"] = "JavaScript",
        [".jsx"] = "JavaScript", [".mjs"] = "JavaScript", [".cjs"] = "JavaScript", [".mts"] = "TypeScript",
        [".cts"] = "TypeScript", [".py"] = "Python", [".md"] = "Markdown", [".mdx"] = "Markdown",
        [".sql"] = "SQL", [".prisma"] = "Prisma", [".json"] = "JSON", [".jsonc"] = "JSON",
        [".xml"] = "XML", [".csproj"] = "XML", [".fsproj"] = "XML", [".props"] = "XML", [".targets"] = "XML",
        [".xaml"] = "XAML", [".axaml"] = "XAML", [".html"] = "HTML", [".css"] = "CSS", [".scss"] = "SCSS",
        [".yml"] = "YAML", [".yaml"] = "YAML", [".toml"] = "TOML", [".txt"] = "Text", [".ps1"] = "PowerShell",
        [".sh"] = "Shell", [".go"] = "Go", [".rs"] = "Rust", [".java"] = "Java", [".cpp"] = "C++", [".h"] = "C++"
    };
    public async Task ScanAsync(ScanContext c, CancellationToken ct)
    {
        c.Add(new Node { Id = "system:root", Kind = NodeKinds.System, Name = c.Graph.Project.Name, Path = "" });
        string[]? paths = null;
        try
        {
            var probe = await GitProcess.RunAsync(c.Options.Root, ct, "rev-parse", "--show-toplevel");
            c.IsGit = probe.ExitCode == 0;
            if (c.IsGit)
            {
                var listing = await GitProcess.RunAsync(c.Options.Root, ct, "ls-files", "--cached", "--others", "--exclude-standard", "-z", "--", ".");
                if (listing.ExitCode == 0) paths = listing.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                else c.Warn(Name, "git ls-files failed; using directory walk: " + listing.Error.Trim());
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { c.Warn(Name, "Git unavailable; using directory walk: " + ex.Message); }
        paths ??= Walk(c, "", [], ct).ToArray();
        foreach (var raw in paths.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var path = ScanContext.Rel(raw);
            if (path.Split('/').Any(Skipped.Contains) || !SafeFile(c, path)) continue;
            var language = Languages.GetValueOrDefault(Path.GetExtension(path));
            c.Files[path] = new(path, c.Full(path), language, Tags(path));
        }
        foreach (var file in c.Files.Values.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            var ext = Path.GetExtension(file.Path).ToLowerInvariant();
            if (ext is ".csproj" or ".fsproj") ReadProject(c, file, ext == ".csproj" ? "C#" : "F#");
            else if (Path.GetFileName(file.Path) == "package.json") ReadPackage(c, file);
            else if (Path.GetFileName(file.Path) is "pyproject.toml" or "setup.py")
                AddModule(c, file, "Python", Path.GetFileName(DirectoryName(file.Path)) is { Length: > 0 } n ? n : c.Graph.Project.Name);
            else if (Path.GetFileName(file.Path) == "__init__.py" && DirectoryName(file.Path).Split('/').Length == 1)
                AddModule(c, file, "Python", DirectoryName(file.Path).Length > 0 ? DirectoryName(file.Path) : c.Graph.Project.Name);
        }
        var excludedModules = new List<string>();
        foreach (var module in c.Modules.ToArray())
        {
            var tags = Tags(module.Directory + "/" + module.Name);
            if (module.Packages.Any(p => p.Contains("Test.Sdk", StringComparison.OrdinalIgnoreCase))) tags.Add("test");
            if (c.Files[module.Manifest].Text(c) is { } manifest && Regex.IsMatch(manifest, @"<IsTestProject>\s*true", RegexOptions.IgnoreCase)) tags.Add("test");
            if (!c.Options.IncludeTests && tags.Contains("test")) { excludedModules.Add(module.Directory); c.Modules.Remove(module); continue; }
            c.Add(new Node { Id = module.Id, Kind = NodeKinds.Module, Name = module.Name, Path = module.Directory,
                ParentId = "system:root", Language = module.Language, Tags = tags.Distinct().ToList() });
        }
        foreach (var file in c.Files.Values.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            var module = c.ModuleFor(file.Path);
            if (module != null && c.Graph.Nodes[module.Id].Tags.Contains("test") && !file.Tags.Contains("test")) file.Tags.Add("test");
            // Removed test modules must not leak their untagged source files back into the system tree.
            if (!c.Options.IncludeTests && (file.Tags.Contains("test") || excludedModules.Any(d => d.Length == 0 || file.Path.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase))))
            { c.Files.Remove(file.Path); continue; }
            var text = file.Language != null ? file.Text(c) : null;
            if (text != null && file.Language is "C#" && Regex.IsMatch(text, @"\b(?:static\s+(?:async\s+)?(?:void|int|Task(?:<int>)?)\s+Main\s*\()")) file.Tags.Add("entrypoint");
            var parent = Folder(c, DirectoryName(file.Path), module);
            c.Add(new Node { Id = ScanContext.FileId(file.Path), Kind = NodeKinds.File, Name = Path.GetFileName(file.Path),
                Path = file.Path, ParentId = parent, Language = file.Language, Tags = file.Tags.Distinct().ToList(),
                Metrics = new() { Files = 1, Loc = text?.Split('\n').Count(l => !string.IsNullOrWhiteSpace(l)) ?? 0 } });
        }
    }
    internal static string DirectoryName(string path) => ScanContext.Rel(Path.GetDirectoryName(path) ?? "");
    private static void AddModule(ScanContext c, RepoFile file, string language, string name)
    {
        var directory = DirectoryName(file.Path);
        if (language == "Python" && c.Modules.Any(m => m.Directory == directory && m.Language == language)) return;
        c.Modules.Add(new("module:" + file.Path, name, directory, file.Path, language));
    }
    private void ReadProject(ScanContext c, RepoFile file, string language)
    {
        try
        {
            var xml = XDocument.Parse(file.Text(c) ?? "");
            AddModule(c, file, language, Path.GetFileNameWithoutExtension(file.Path));
            var m = c.Modules[^1];
            foreach (var e in xml.Descendants())
            {
                if (e.Name.LocalName == "ProjectReference" && e.Attribute("Include") is { } include)
                    m.References.Add(c.Relative(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file.FullPath)!, include.Value.Replace('\\', Path.DirectorySeparatorChar)))));
                if (e.Name.LocalName == "PackageReference" && (e.Attribute("Include") ?? e.Attribute("Update")) is { } package) m.Packages.Add(package.Value);
                if (e.Name.LocalName == "ImplicitUsings") m.ImplicitUsings = e.Value.Trim() is "enable" or "true";
                if (e.Name.LocalName == "DefineConstants") m.Defines.AddRange(e.Value.Split(';', StringSplitOptions.RemoveEmptyEntries).Where(s => Regex.IsMatch(s, @"^\w+$")));
            }
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or ArgumentException) { c.Warn(Name, "Invalid project: " + ex.Message, file.Path); }
    }
    private void ReadPackage(ScanContext c, RepoFile file)
    {
        try
        {
            using var json = JsonDocument.Parse(file.Text(c) ?? "");
            var name = json.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
            AddModule(c, file, "TypeScript", name ?? Path.GetFileName(DirectoryName(file.Path)) ?? c.Graph.Project.Name);
            var m = c.Modules[^1];
            foreach (var section in new[] { "dependencies", "devDependencies", "peerDependencies" })
                if (json.RootElement.TryGetProperty(section, out var deps) && deps.ValueKind == JsonValueKind.Object)
                    m.Packages.AddRange(deps.EnumerateObject().Select(x => x.Name));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { c.Warn(Name, "Invalid package.json: " + ex.Message, file.Path); }
    }
    private static string Folder(ScanContext c, string directory, RepoModule? module)
    {
        var root = module?.Directory ?? "";
        if (directory == root || directory.Length == 0) return module?.Id ?? "system:root";
        // A manifest scopes folder ids, even when two projects share a directory.
        var id = "folder:" + (module == null ? "" : module.Manifest + ":") + directory;
        c.Add(new Node { Id = id, Kind = NodeKinds.Folder, Name = Path.GetFileName(directory), Path = directory,
            ParentId = Folder(c, DirectoryName(directory), module) });
        return id;
    }
    internal static List<string> Tags(string path)
    {
        var tags = new List<string>();
        if (Regex.IsMatch(path, @"(?:^|[/_.-])tests?(?:[/_.-]|$)|\.test\.|\.spec\.", RegexOptions.IgnoreCase)) tags.Add("test");
        if (Regex.IsMatch(path, @"\.g\.cs$|\.Designer\.cs$|(?:^|/)obj/", RegexOptions.IgnoreCase)) tags.Add("generated");
        if (Regex.IsMatch(path, @"(?:^|/)(?:Program\.cs|(?:index|main)\.[^/]+)$", RegexOptions.IgnoreCase)) tags.Add("entrypoint");
        if (Regex.IsMatch(path, @"\.(?:a?xaml)$|(?:^|/)(?:Views|Controls)(?:/|$)", RegexOptions.IgnoreCase)) tags.Add("ui");
        return tags;
    }
    private static bool SafeFile(ScanContext c, string path)
    {
        if (Path.IsPathRooted(path) || path.Split('/').Any(p => p == "..")) return false;
        try
        {
            var full = c.Full(path);
            if (!File.Exists(full)) return false;
            for (var current = full; current.Length > c.Options.Root.Length; current = Path.GetDirectoryName(current)!)
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { c.Warn("structure", ex.Message, path); return false; }
    }
    private sealed record IgnoreRule(Regex Pattern, bool Negated);
    private static IEnumerable<string> Walk(ScanContext c, string directory, List<IgnoreRule> inherited, CancellationToken ct)
    {
        var rules = new List<IgnoreRule>(inherited);
        var ignore = c.Full(directory + (directory.Length == 0 ? "" : "/") + ".gitignore");
        try
        {
            if (File.Exists(ignore))
                foreach (var raw in File.ReadLines(ignore))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#')) continue;
                    var negated = line.StartsWith('!');
                    if (negated) line = line[1..];
                    var anchored = line.StartsWith('/') || line.TrimEnd('/').Contains('/');
                    line = line.Trim('/');
                    var glob = Regex.Escape(line).Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]");
                    var prefix = directory.Length == 0 ? "" : Regex.Escape(directory) + "/";
                    rules.Add(new(new Regex("^" + prefix + (anchored ? "" : "(?:.*/)?") + glob + "(?:/.*)?$", RegexOptions.IgnoreCase), negated));
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { c.Warn("structure", ex.Message, directory); }
        string[] entries;
        try { entries = Directory.GetFileSystemEntries(c.Full(directory)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { c.Warn("structure", ex.Message, directory); yield break; }
        foreach (var full in entries.Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var path = c.Relative(full);
            if (Skipped.Contains(Path.GetFileName(full))) continue;
            FileAttributes attributes;
            try { attributes = File.GetAttributes(full); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { c.Warn("structure", ex.Message, path); continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            var ignored = false;
            foreach (var rule in rules) if (rule.Pattern.IsMatch(path)) ignored = !rule.Negated;
            if (ignored) continue;
            if ((attributes & FileAttributes.Directory) != 0)
                foreach (var child in Walk(c, path, rules, ct)) yield return child;
            else yield return path;
        }
    }
}
