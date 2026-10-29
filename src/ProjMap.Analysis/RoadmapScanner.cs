using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ProjMap.Core;

namespace ProjMap.Analysis;

internal sealed class RoadmapScanner : IRepositoryScanner
{
    public string Name => "roadmap";
    private static readonly Regex Heading = new(@"^(?<level>#{1,6})\s+(?<title>.+?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex Checklist = new(@"^(?<indent>\s*)[-*+]\s+\[(?<check>[ xX])\]\s+(?<title>.+)$", RegexOptions.Compiled);
    private static readonly Regex Decision = new(@"\bDEC-\d+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public Task ScanAsync(ScanContext c, CancellationToken ct)
    {
        var docs = c.Files.Values.Where(f => f.Language == "Markdown").OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        if (docs.Length == 0) return Task.CompletedTask;
        c.Group("Docs");
        foreach (var file in docs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (file.Text(c) is not { } text) continue;
                c.Add(new Node
                {
                    Id = "doc:" + file.Path,
                    Kind = NodeKinds.Doc,
                    Name = Path.GetFileName(file.Path),
                    Path = file.Path,
                    ParentId = "group:docs",
                    Language = "Markdown",
                    Summary = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith('|'))
                });
                var name = Path.GetFileNameWithoutExtension(file.Path);
                if (Regex.IsMatch(file.Path, @"(?:^|/)(?:_?archive(?:s|d)?|архив)(?:[-_][^/]*)?/", RegexOptions.IgnoreCase)) continue;
                if (!Regex.IsMatch(name, @"^(?:ROADMAP|TODO|STATE|PLAN|CHANGELOG|PROGRESS|DECISIONS)", RegexOptions.IgnoreCase)) continue;
                Parse(c, file, text);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { c.Warn(Name, ex.Message, file.Path); }
        }
        Normalize(c.Graph.Roadmap);
        if (c.Graph.Roadmap.Count > 0) c.Group("Roadmap");
        var index = BuildIndex(c);
        var commitIndex = c.Commits.SelectMany(commit => KeyToken.Matches(commit.Subject).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(k => (Key: k, Commit: commit)))
            .ToLookup(x => x.Key, x => x.Commit, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < c.Graph.Roadmap.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = c.Graph.Roadmap[i];
            var links = Mentioned(index, item.Title + "\n" + item.Detail, item.Source!.Path).ToHashSet(StringComparer.Ordinal);
            var keys = Decision.Matches(item.Title).Select(m => m.Value).ToList();
            if (ItemKey(item.Title) is { } key) keys.Add(key);
            var commits = keys.Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(k => commitIndex[k]).DistinctBy(x => x.Hash)
                .OrderByDescending(x => x.Date).ToArray();
            foreach (var commit in commits)
                foreach (var file in commit.Files)
                {
                    var id = ScanContext.FileId(file);
                    links.Add(id);
                    c.Edge(EdgeKinds.Mentions, item.Id, id, new(item.Source.Path, item.Source.Line, commit.Hash[..8] + " " + commit.Subject));
                }
            foreach (var link in links)
                if (!commits.Any(commit => commit.Files.Any(f => ScanContext.FileId(f) == link))) c.Edge(EdgeKinds.Mentions, item.Id, link, item.Source);
            var detail = item.Detail;
            if (commits.Length > 0) detail = (detail == null ? "" : detail + "\n\n") + "Commits:\n" + string.Join('\n', commits.Select(commit => commit.Hash[..8] + " " + commit.Subject));
            c.Graph.Roadmap[i] = item with { Links = links.Order(StringComparer.Ordinal).ToList(), Detail = detail };
        }
        foreach (var file in docs)
        {
            ct.ThrowIfCancellationRequested();
            if (file.Text(c) is not { } text) continue;
            foreach (var link in Mentioned(index, text, file.Path))
                if (link != ScanContext.FileId(file.Path) && link != "doc:" + file.Path)
                    c.Edge(EdgeKinds.Mentions, "doc:" + file.Path, link, new(file.Path, 1, "Mention in document"));
        }
        return Task.CompletedTask;
    }
    private static void Parse(ScanContext c, RepoFile file, string text)
    {
        var headings = new Stack<(int Level, string Id, string Title)>();
        var tasks = new Stack<(int Indent, string Id)>();
        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        var lines = text.Replace("\r", "").Split('\n');
        int statusColumn = -1, titleColumn = 0;
        var fenced = false;
        RoadmapItem Add(string title, string status, int line, string? parent, string? detail = null)
        {
            title = title.Trim();
            // Content-derived identities survive unrelated inserted lines and status checkbox changes.
            var identity = Regex.Replace(title.Normalize(), @"[^\p{L}\p{N}]+", "-").Trim('-').ToLowerInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((parent ?? "") + "\n" + identity)))[..12].ToLowerInvariant();
            var stem = "roadmap:" + file.Path + "#" + (identity.Length > 72 ? identity[..72] : identity) + "-" + hash;
            var count = used.GetValueOrDefault(stem) + 1; used[stem] = count;
            var item = new RoadmapItem
            {
                Id = stem + (count == 1 ? "" : "-" + count),
                Title = title,
                Status = status,
                ParentId = parent,
                Phase = headings.Reverse().Skip(1).FirstOrDefault().Title,
                Source = new(file.Path, line, title),
                Detail = detail
            };
            c.Graph.Roadmap.Add(item);
            return item;
        }
        for (var n = 0; n < lines.Length; n++)
        {
            var line = lines[n];
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~")) { fenced = !fenced; continue; }
            if (fenced) continue;
            var heading = Heading.Match(line);
            if (heading.Success)
            {
                var level = heading.Groups["level"].Length;
                while (headings.Count > 0 && headings.Peek().Level >= level) headings.Pop();
                var title = heading.Groups["title"].Value;
                var item = Add(title, Status(title), n + 1, headings.Count > 0 ? headings.Peek().Id : null);
                headings.Push((level, item.Id, title)); tasks.Clear(); statusColumn = -1;
                continue;
            }
            var check = Checklist.Match(line);
            if (check.Success)
            {
                var indent = check.Groups["indent"].Value.Replace("\t", "    ").Length;
                while (tasks.Count > 0 && tasks.Peek().Indent >= indent) tasks.Pop();
                var title = check.Groups["title"].Value;
                var status = check.Groups["check"].Value != " " ? RoadmapStatus.Done : Status(title);
                if (status == RoadmapStatus.Unknown) status = RoadmapStatus.Planned;
                var item = Add(title, status, n + 1, tasks.Count > 0 ? tasks.Peek().Id : headings.Count > 0 ? headings.Peek().Id : null);
                tasks.Push((indent, item.Id));
                continue;
            }
            if (trimmed.StartsWith('|'))
            {
                var cells = Regex.Split(trimmed.Trim('|'), @"(?<!\\)\|").Select(s => s.Trim()).ToArray();
                if (cells.All(s => Regex.IsMatch(s, @"^:?-+:?$"))) continue;
                var header = Array.FindIndex(cells, s => Regex.IsMatch(s, @"^(?:status|статус|состояние|state)$", RegexOptions.IgnoreCase));
                if (header >= 0)
                {
                    statusColumn = header;
                    titleColumn = Array.FindIndex(cells, s => Regex.IsMatch(s, @"^(?:этап|задача|пункт|название|stage|task|title|item|feature)$", RegexOptions.IgnoreCase));
                    if (titleColumn < 0) titleColumn = 0;
                    continue;
                }
                if (statusColumn >= 0 && statusColumn < cells.Length)
                    Add(cells[Math.Min(titleColumn, cells.Length - 1)], Status(cells[statusColumn]), n + 1,
                        headings.Count > 0 ? headings.Peek().Id : null, string.Join(" | ", cells));
                continue;
            }
            statusColumn = -1;
            var statusLine = Regex.Match(trimmed, @"^[-*+]\s+(?<title>.+)$");
            if (statusLine.Success && Status(statusLine.Groups["title"].Value) is { } statusValue && statusValue != RoadmapStatus.Unknown)
            {
                Add(statusLine.Groups["title"].Value, statusValue, n + 1, headings.Count > 0 ? headings.Peek().Id : null);
                continue;
            }
            if (headings.Count > 0 && trimmed.Length > 0)
            {
                var id = headings.Peek().Id;
                var index = c.Graph.Roadmap.FindLastIndex(x => x.Id == id);
                var item = c.Graph.Roadmap[index];
                c.Graph.Roadmap[index] = item with { Detail = item.Detail == null ? line : item.Detail + "\n" + line };
            }
        }
    }
    // An item named by an id ("R09 · Styles", "DEC-070 — …", "PROJ-12: …"): the id links it to
    // the same item in other files (status tables) and to commits that mention it.
    private static readonly Regex LeadingKey = new(@"^[\s*_`#\[]*(?<key>[A-Za-zА-Яа-я]{1,5}-?\d{1,4}[a-z]?)(?![\p{L}\p{N}])", RegexOptions.Compiled);
    private static readonly Regex KeyToken = new(@"(?<![\p{L}\p{N}])[A-Za-z]{1,5}-?\d{1,4}[a-z]?(?![\p{L}\p{N}])", RegexOptions.Compiled);
    internal static string? ItemKey(string title)
    {
        var m = LeadingKey.Match(title);
        // "2026-10-28 …", "v28" and other plain numbers are not ids: require a capital letter
        return m.Success && char.IsUpper(m.Groups["key"].Value[0]) ? m.Groups["key"].Value.ToUpperInvariant() : null;
    }

    /// Makes statuses meaningful across files: history docs are done, keyed items borrow a known status
    /// from their twin in another file, and plain document sections without any task inside are dropped.
    internal static void Normalize(List<RoadmapItem> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var file = Path.GetFileName(items[i].Source?.Path ?? "");
            if (items[i].Status == RoadmapStatus.Unknown && Regex.IsMatch(file, @"^(?:CHANGELOG|DECISIONS)", RegexOptions.IgnoreCase))
                items[i] = items[i] with { Status = RoadmapStatus.Done };
        }

        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // table rows are the usual "status board", so they win over headings
        foreach (var item in items.OrderByDescending(x => x.Detail?.Contains(" | ") == true))
            if (item.Status != RoadmapStatus.Unknown && ItemKey(item.Title) is { } key)
                known.TryAdd(key, item.Status);
        for (var i = 0; i < items.Count; i++)
            if (items[i].Status == RoadmapStatus.Unknown && ItemKey(items[i].Title) is { } key && known.TryGetValue(key, out var status))
                items[i] = items[i] with { Status = status };

        var children = items.Where(x => x.ParentId != null).ToLookup(x => x.ParentId!);
        var keep = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool Keep(RoadmapItem item)
        {
            if (keep.TryGetValue(item.Id, out var k)) return k;
            k = children[item.Id].Aggregate(false, (any, child) => Keep(child) | any)
                || item.Status != RoadmapStatus.Unknown || ItemKey(item.Title) != null;
            keep[item.Id] = k;
            return k;
        }
        items.RemoveAll(x => !Keep(x));
    }

    internal static string Status(string text)
    {
        var value = Regex.Replace(text, @"[*`_]", "").Trim();
        if (Regex.IsMatch(value, @"\b(?:blocked|блокирован\w*|заблокирован\w*|waiting|ждёт|ждет|ожидает)\b|⛔|🚫|⏳", RegexOptions.IgnoreCase)) return RoadmapStatus.Blocked;
        if (Regex.IsMatch(value, @"\b(?:deferred|отложен\w*|отложить|paused)\b|⏸", RegexOptions.IgnoreCase)) return RoadmapStatus.Deferred;
        if (Regex.IsMatch(value, @"\b(?:active|partial|in\s+progress|в\s+работе|частично|текущая\s+работа)\b|🔄|🔀", RegexOptions.IgnoreCase)) return RoadmapStatus.Active;
        if (Regex.IsMatch(value, @"\b(?:planned|план|планируется|запланирован\w*|todo|pending|не\s+начат)\b|⬜", RegexOptions.IgnoreCase)) return RoadmapStatus.Planned;
        if (Regex.IsMatch(value, @"\b(?:done|completed|готово|готов|сделано|завершен[оа]?|выполнен[оа]?)\b|✅|☑", RegexOptions.IgnoreCase)) return RoadmapStatus.Done;
        return RoadmapStatus.Unknown;
    }
    private static Dictionary<string, HashSet<string>> BuildIndex(ScanContext c)
    {
        var index = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Alias(string? alias, string id)
        {
            if (string.IsNullOrWhiteSpace(alias) || alias.Length < 3) return;
            if (!index.TryGetValue(alias, out var ids)) index[alias] = ids = new(StringComparer.Ordinal);
            ids.Add(id);
        }
        foreach (var node in c.Graph.Nodes.Values.Where(n => n.Kind is NodeKinds.File or NodeKinds.Type or NodeKinds.Module))
        {
            Alias(node.Name, node.Id);
            if (node.Path != null) Alias(node.Path, node.Id);
            if (node.Kind == NodeKinds.Type && node.Id.StartsWith("type:module:", StringComparison.Ordinal)) Alias(node.Id[(node.Id.LastIndexOf(':') + 1)..], node.Id);
        }
        return index;
    }
    private static IEnumerable<string> Mentioned(Dictionary<string, HashSet<string>> index, string text, string doc)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match token in Regex.Matches(text.Replace('\\', '/'), @"[\p{L}\p{N}_@./-]+"))
        {
            var value = token.Value.TrimEnd('.', '/', '-');
            if (index.TryGetValue(value, out var ids)) result.UnionWith(ids);
            if (value.Contains('/'))
            {
                var relative = ScanContext.Rel(Path.GetRelativePath("/", Path.GetFullPath(Path.Combine("/", StructureScanner.DirectoryName(doc), value))));
                if (index.TryGetValue(relative, out ids)) result.UnionWith(ids);
            }
        }
        return result;
    }
}
