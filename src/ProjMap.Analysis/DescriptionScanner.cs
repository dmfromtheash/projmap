using System.Text.RegularExpressions;
using ProjMap.Core;

namespace ProjMap.Analysis;

/// Gives modules and folders a human description taken from the project's own Markdown docs:
/// a line that starts with the element's name followed by a description
/// ("Map.Core   document model, geometry…", "- `src/api` — HTTP handlers", "| Map.Export | export pipeline |").
internal sealed class DescriptionScanner : IRepositoryScanner
{
    public string Name => "descriptions";

    public Task ScanAsync(ScanContext c, CancellationToken ct)
    {
        var docs = c.Files.Values
            .Where(f => f.Language == "Markdown" && !Regex.IsMatch(f.Path, @"(?:^|/)_?archive", RegexOptions.IgnoreCase))
            // architecture / readme docs describe structure best
            .OrderByDescending(f => Regex.IsMatch(Path.GetFileName(f.Path), @"ARCH|README|STRUCTURE|OVERVIEW|АРХИТ", RegexOptions.IgnoreCase))
            .ThenBy(f => f.Path.Count(ch => ch == '/'))
            .ToArray();
        var targets = c.Graph.Nodes.Values
            .Where(n => (n.Kind is NodeKinds.Module or NodeKinds.Folder) && string.IsNullOrWhiteSpace(n.Summary))
            .Select(n => (Node: n, Keys: Keys(n).Select(key => (Key: key, Pattern: new Regex(
                @"^[`*_\[]*" + Regex.Escape(key) + @"/?[`*_\]]*(?:\([^)]*\))?\s*(?:[—–:|=-]+|\s{2,})\s*(?<d>.+)$", RegexOptions.IgnoreCase))).ToArray()))
            .Where(t => t.Keys.Length > 0)
            .ToList();
        if (targets.Count == 0) return Task.CompletedTask;

        var initialPatterns = targets.SelectMany(t => t.Keys.Select(k => k.Key[0])).Distinct().ToDictionary(
            ch => ch, ch => new Regex("^" + Regex.Escape(ch.ToString()) + "$", RegexOptions.IgnoreCase));
        var byInitial = new Dictionary<char, (Node Node, string Key, Regex Pattern)[]>();

        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var doc in docs)
        {
            ct.ThrowIfCancellationRequested();
            if (doc.Text(c) is not { } text) continue;
            foreach (var raw in text.Replace("\r", "").Split('\n'))
            {
                if (raw.Length > 400) continue;
                var line = raw.Trim().TrimStart('-', '*', '+', '|', ' ', '\t');
                var prefix = line.TrimStart('`', '*', '_', '[');
                if (prefix.Length == 0) continue;
                if (!byInitial.TryGetValue(prefix[0], out var candidates))
                {
                    var first = prefix[0].ToString();
                    candidates = targets.SelectMany(t => t.Keys.Where(k => k.Key[0] is '`' or '*' or '_' or '[' || initialPatterns[k.Key[0]].IsMatch(first))
                        .Select(k => (t.Node, k.Key, k.Pattern))).ToArray();
                    byInitial[prefix[0]] = candidates;
                }
                foreach (var (node, key, pattern) in candidates)
                {
                    if (found.ContainsKey(node.Id)) continue;
                    if (key[0] is not ('`' or '*' or '_' or '[') && !prefix.StartsWith(key, StringComparison.CurrentCultureIgnoreCase)) continue;
                    var m = pattern.Match(line);
                    if (!m.Success) continue;
                    var d = Clean(m.Groups["d"].Value);
                    if (d.Length < 12 || !d.Any(char.IsLetter)) continue;
                    found[node.Id] = d;
                }
            }
            if (found.Count == targets.Count) break;
        }
        foreach (var (id, d) in found)
            c.Graph.Nodes[id] = c.Graph.Nodes[id] with { Summary = d };
        return Task.CompletedTask;
    }

    static List<string> Keys(Node n)
    {
        var keys = new List<string>();
        if (n.Kind == NodeKinds.Module) keys.Add(n.Name);
        if (n.Path is { Length: > 0 } p)
        {
            var dir = n.Kind == NodeKinds.Module && Path.HasExtension(p) ? StructureScanner.DirectoryName(p) : p;
            var parts = dir.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // full path and the last two segments; a lone generic folder name ("Views") is too ambiguous
            if (parts.Length >= 2) { keys.Add(dir); keys.Add(string.Join('/', parts[^2..])); }
            else if (n.Kind == NodeKinds.Module && parts.Length == 1) keys.Add(parts[0]);
        }
        return keys.Where(k => k.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static string Clean(string s)
    {
        var cut = s.IndexOf(" | ", StringComparison.Ordinal);
        if (cut > 0) s = s[..cut];
        s = Regex.Replace(s, @"[`*_|]+", "").Trim().TrimEnd('.', ';', ',');
        return s.Length > 220 ? s[..220] + "…" : s;
    }
}
