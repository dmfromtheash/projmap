using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProjMap.Core;

/// <summary>
/// The whole project map. Written to <c>.projmap/graph.json</c> by the analyzer and read by the viewer.
/// Contract rules (see docs/GRAPH_CONTRACT.md):
///  - Nodes form one tree through <see cref="Node.ParentId"/> (system → module → folder → file → type → member).
///    Roadmap items, docs and database tables are separate roots (ParentId = their group node).
///  - Edges are stored at the finest level known (including members/handlers). The viewer rolls them up.
///  - Ids derive from paths / source signatures; handler ordinals are local to identical registration targets.
/// </summary>
public sealed record ProjectGraph
{
    public int SchemaVersion { get; init; } = 1;
    public required ProjectInfo Project { get; init; }
    public List<Node> Nodes { get; init; } = [];
    public List<Edge> Edges { get; init; } = [];
    public List<RoadmapItem> Roadmap { get; init; } = [];
    public DatabaseInfo? Database { get; init; }
    public List<ScanWarning> Warnings { get; init; } = [];
    /// Optional projection layer (tiers, features); absent in graphs written before it existed.
    public ViewsInfo? Views { get; init; }
    public ProblemsInfo? Problems { get; init; }
}

public sealed record ProjectInfo
{
    public required string Name { get; init; }
    public required string Root { get; init; }
    public DateTimeOffset ScannedAt { get; init; }
    public string? GitHead { get; init; }
    public string? GitBranch { get; init; }
    public List<string> Languages { get; init; } = [];
    public long ScanMilliseconds { get; init; }
    public ScanTimings? Timings { get; init; }
}

/// <summary>Elapsed milliseconds by stage; C# model time is summed across workers.</summary>
public sealed record ScanTimings
{
    public Dictionary<string, double> Milliseconds { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Counts { get; init; } = new(StringComparer.Ordinal);
}

public static class NodeKinds
{
    public const string System = "system";     // the repository itself (single root)
    public const string Module = "module";     // .csproj / package.json / python package / top-level source dir
    public const string Folder = "folder";
    public const string File = "file";
    public const string Type = "type";         // class / record / struct / interface / enum / TS class
    public const string Member = "member";     // method / property / exported function
    public const string Table = "table";       // database table
    public const string Doc = "doc";           // markdown documentation file
    public const string Group = "group";       // synthetic roots: "Roadmap", "Database", "Docs"
}

public sealed record Node
{
    /// Stable id, e.g. "file:src/Map.Core/Geo/Hex.cs", "type:Map.Core.Geo.Hex", "module:Map.Core", "table:projects".
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public string? ParentId { get; init; }
    /// Repo-relative path with forward slashes (files, folders, modules, docs).
    public string? Path { get; init; }
    public int? Line { get; init; }
    public string? Language { get; init; }
    /// For types: class|record|struct|interface|enum|delegate; for members: method|property|field|event|ctor|function.
    public string? SubKind { get; init; }
    /// Optional handler identity: owner member id + "|" + control name (or label).
    public string? Anchor { get; init; }
    public string? Summary { get; init; }      // XML doc summary / first doc comment / first markdown paragraph
    /// Exact declarations, including all parts of a C# type. Null in older graphs.
    public List<NodeDeclaration>? Declarations { get; init; }
    /// Source of a Markdown-derived summary; declaration comments live with their declaration.
    public SourceLocation? SummarySource { get; init; }
    public NodeMetrics Metrics { get; init; } = new();
    public List<string> Tags { get; init; } = []; // e.g. "test", "ui", "generated", "entrypoint", "hot"
}

public sealed record NodeDeclaration(string Path, int Line, int EndLine, string? Summary = null, int? SummaryLine = null);

public sealed record NodeMetrics
{
    public int Loc { get; init; }              // non-blank lines
    public int Files { get; init; }            // for containers
    public int Types { get; init; }
    public int Members { get; init; }
    public int Complexity { get; init; }       // summed cyclomatic estimate
    public int Commits { get; init; }          // git commits touching it (last N months window)
    public DateTimeOffset? LastChange { get; init; }
    public int Authors { get; init; }
}

public static class EdgeKinds
{
    public const string ProjectRef = "projectRef"; // module → module (csproj ProjectReference, workspace deps)
    public const string Package = "package";       // module → external package (target id "pkg:Name")
    public const string Reference = "reference";   // file/type uses a symbol of another file/type
    public const string Call = "call";
    public const string Dispatch = "dispatch";   // contract/base member -> possible implementation/override
    public const string XamlEvent = "xamlEvent";   // XAML file -> code-behind event method
    public const string Binding = "binding";       // XAML file -> statically resolved data-context member
    public const string Inherit = "inherit";
    public const string Implement = "implement";
    public const string Import = "import";         // TS/JS/Python import
    public const string CoChange = "cochange";     // files changed together in git history (undirected)
    public const string DbRead = "dbRead";
    public const string DbWrite = "dbWrite";
    public const string Mentions = "mentions";     // roadmap item / doc → code node it talks about
}

public sealed record Edge
{
    /// "{kind}:{source}->{target}"
    public required string Id { get; init; }
    public required string Source { get; init; }
    public required string Target { get; init; }
    public required string Kind { get; init; }
    /// Raw count (number of references, co-commits, mentions).
    public int Weight { get; init; } = 1;
    /// Normalized 0..1 within its kind; viewer uses it for thickness/opacity.
    public double Strength { get; init; }
    /// Up to ~5 example locations, so the viewer can show "why are these connected".
    public List<SourceLocation> Evidence { get; init; } = [];
}

public sealed record SourceLocation(string Path, int Line, string? Snippet = null);

public static class RoadmapStatus
{
    public const string Done = "done";
    public const string Active = "active";
    public const string Planned = "planned";
    public const string Deferred = "deferred";
    public const string Blocked = "blocked";
    public const string Unknown = "unknown";
}

public sealed record RoadmapItem
{
    public required string Id { get; init; }          // "roadmap:<file>#<slug>"
    public required string Title { get; init; }
    public string Status { get; init; } = RoadmapStatus.Unknown;
    public string? ParentId { get; init; }            // nesting (phase → stage → task)
    public string? Phase { get; init; }
    public SourceLocation? Source { get; init; }
    public string? Detail { get; init; }
    /// Code node ids this item touches (from mentioned paths/type names/commit messages).
    public List<string> Links { get; init; } = [];
}

public sealed record DatabaseInfo
{
    public required string Engine { get; init; }      // sqlite | postgres | mysql | mssql | unknown
    public List<string> Sources { get; init; } = [];  // files where the schema was found (db files, migrations, code)
    public List<DbTable> Tables { get; init; } = [];
}

public sealed record DbTable
{
    public required string Name { get; init; }
    public List<DbColumn> Columns { get; init; } = [];
    public List<DbForeignKey> ForeignKeys { get; init; } = [];
    public SourceLocation? DefinedAt { get; init; }
}

public sealed record DbColumn(string Name, string Type, bool PrimaryKey = false, bool NotNull = false);

public sealed record DbForeignKey(string Column, string RefTable, string RefColumn);

public sealed record ScanWarning(string Scanner, string Message, string? Path = null);

public static class GraphJson
{
    /// <summary>Streams the graph without allocating a second graph-sized string. Project metadata
    /// is written last so serialization timing covers the payload and its flush (excluding metadata).</summary>
    public static async Task WriteAsync(ProjectGraph graph, Stream destination, CancellationToken ct = default)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        using var writer = new Utf8JsonWriter(destination);
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", graph.SchemaVersion);
        void Property<T>(string name, T value)
        {
            ct.ThrowIfCancellationRequested();
            writer.WritePropertyName(name);
            JsonSerializer.Serialize(writer, value, Options);
        }
        Property("nodes", graph.Nodes);
        Property("edges", graph.Edges);
        Property("roadmap", graph.Roadmap);
        if (graph.Database != null) Property("database", graph.Database);
        Property("warnings", graph.Warnings);
        if (graph.Views != null) Property("views", graph.Views);
        if (graph.Problems != null) Property("problems", graph.Problems);
        await writer.FlushAsync(ct).ConfigureAwait(false);
        if (graph.Project.Timings is { } timings)
            timings.Milliseconds["serialization"] = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Property("project", graph.Project);
        writer.WriteEndObject();
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Serialize(ProjectGraph g) => JsonSerializer.Serialize(g, Options);

    /// Viewer format: same document, but each edge is [kindIndex, source, target, weight, evidence?]
    /// where source/target are indexes into `nodes` (or the id itself for non-node ends such as
    /// roadmap items) and evidence is [[path, line, snippet], …]. With function-level edges this is
    /// ~4× smaller than the plain form, whose edge ids repeat long member signatures three times.
    public static byte[] SerializeCompact(ProjectGraph g)
    {
        var index = new Dictionary<string, int>(g.Nodes.Count, StringComparer.Ordinal);
        for (var i = 0; i < g.Nodes.Count; i++) index[g.Nodes[i].Id] = i;
        var kinds = g.Edges.Select(e => e.Kind).Distinct().ToList();
        var kindIndex = kinds.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);
        object Ref(string id) => index.TryGetValue(id, out var i) ? i : id;
        var edges = g.Edges.Select(e => e.Evidence.Count == 0
            ? new object[] { kindIndex[e.Kind], Ref(e.Source), Ref(e.Target), e.Weight }
            : new object[] { kindIndex[e.Kind], Ref(e.Source), Ref(e.Target), e.Weight,
                e.Evidence.Select(v => new object?[] { v.Path, v.Line, v.Snippet }).ToList() });
        var doc = new
        {
            schemaVersion = g.SchemaVersion,
            compact = 1,
            project = g.Project,
            nodes = g.Nodes,
            edgeKinds = kinds,
            edges,
            roadmap = g.Roadmap,
            database = g.Database,
            warnings = g.Warnings,
            views = g.Views == null ? null : new
            {
                g.Views.SchemaVersion, g.Views.RuleSet, g.Views.Tiers, g.Views.Assignments, g.Views.Features,
                g.Views.Memberships, g.Views.StaleOverrides, g.Views.Timeline, g.Views.EntryPoints,
                explanations = CompactExplanations(g.Views.Explanations, Ref)
            },
            problems = g.Problems,
        };
        return JsonSerializer.SerializeToUtf8Bytes(doc, Options);
    }
    public static ProjectGraph? Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Null) return null;
        if (!root.TryGetProperty("compact", out var compact) || compact.GetInt32() != 1)
            return JsonSerializer.Deserialize<ProjectGraph>(json, Options);
        // Expand edge tuples and the optional explanation text/source tables before DTO reading.
        var nodes = root.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("id").GetString()!).ToList();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name == "edges") continue;
                if (property.Name == "views" && property.Value.ValueKind == JsonValueKind.Object)
                {
                    writer.WritePropertyName("views"); writer.WriteStartObject();
                    foreach (var viewProperty in property.Value.EnumerateObject())
                    {
                        if (viewProperty.Name == "explanations" && viewProperty.Value.ValueKind == JsonValueKind.Object &&
                            viewProperty.Value.TryGetProperty("compact", out _))
                        {
                            writer.WritePropertyName("explanations");
                            JsonSerializer.Serialize(writer, ExpandExplanations(viewProperty.Value, nodes), Options);
                        }
                        else viewProperty.WriteTo(writer);
                    }
                    writer.WriteEndObject();
                }
                else property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        var graph = JsonSerializer.Deserialize<ProjectGraph>(stream.ToArray(), Options)!;
        var kinds = root.GetProperty("edgeKinds").EnumerateArray().Select(k => k.GetString()!).ToArray();
        string Ref(JsonElement value) => value.ValueKind == JsonValueKind.Number ? graph.Nodes[value.GetInt32()].Id : value.GetString()!;
        var edges = root.GetProperty("edges").EnumerateArray().Select(row =>
        {
            var kind = kinds[row[0].GetInt32()]; var source = Ref(row[1]); var target = Ref(row[2]);
            return new Edge
            {
                Id = $"{kind}:{source}->{target}", Kind = kind, Source = source, Target = target, Weight = row[3].GetInt32(),
                Evidence = row.GetArrayLength() < 5 ? [] : row[4].EnumerateArray().Select(e =>
                    new SourceLocation(e[0].GetString()!, e[1].GetInt32(), e.GetArrayLength() > 2 ? e[2].GetString() : null)).ToList()
            };
        }).ToList();
        var maxima = edges.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Max(e => e.Weight));
        return graph with { Edges = edges.Select(e => e with { Strength = (double)e.Weight / maxima[e.Kind] }).ToList() };
    }

    private static object? CompactExplanations(ExplanationsInfo? info, Func<string, object> nodeRef)
    {
        if (info == null) return null;
        var texts = new List<string>(); var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        int Text(string value)
        {
            if (indexes.TryGetValue(value, out var old)) return old;
            var index = texts.Count; indexes[value] = index; texts.Add(value); return index;
        }
        object Claim(ExplanationClaim claim) => new object[] { Text(claim.Text), claim.Confidence switch
            { ExplanationConfidence.Known => 0, ExplanationConfidence.Derived => 1, _ => 2 }, claim.SourceIds };
        var profiles = info.Nodes.Select(n => n.Claims is not { Count: > 0 }
            ? new object[] { nodeRef(n.NodeId), Claim(n.DisplayName), Claim(n.Description) }
            : new object[] { nodeRef(n.NodeId), Claim(n.DisplayName), Claim(n.Description), n.Claims.Select(Claim).ToList() }).ToList();
        object? OptionalText(string? value) => value == null ? null : Text(value);
        var sourceRows = info.Sources.Select(s =>
        {
            var row = new object?[] { Text(s.Kind), OptionalText(s.Path), s.Line, s.EndLine, OptionalText(s.SourceHash),
                OptionalText(s.CommitId), OptionalText(s.RuleId), OptionalText(s.Reference), OptionalText(s.Text) };
            return row.Take(Array.FindLastIndex(row, x => x != null) + 1).ToArray();
        }).ToList();
        return new { info.SchemaVersion, compact = 1, info.Language, info.RuleSet, texts, nodes = profiles, sources = sourceRows, info.NameIndex };
    }

    private static ExplanationsInfo ExpandExplanations(JsonElement value, List<string> nodeIds)
    {
        var texts = value.GetProperty("texts").EnumerateArray().Select(t => t.GetString()!).ToList();
        ExplanationClaim Claim(JsonElement row) => new(texts[row[0].GetInt32()], row[1].GetInt32() switch
        { 0 => ExplanationConfidence.Known, 1 => ExplanationConfidence.Derived, 2 => ExplanationConfidence.Guessed,
            _ => throw new JsonException("Unknown explanation confidence.") }, row[2].EnumerateArray().Select(i => i.GetInt32()).ToList());
        string? Text(JsonElement row, int index) => row.GetArrayLength() > index && row[index].ValueKind != JsonValueKind.Null
            ? texts[row[index].GetInt32()] : null;
        int? Number(JsonElement row, int index) => row.GetArrayLength() > index && row[index].ValueKind != JsonValueKind.Null
            ? row[index].GetInt32() : null;
        return new()
        {
            SchemaVersion = value.GetProperty("schemaVersion").GetInt32(), Language = value.GetProperty("language").GetString()!,
            RuleSet = value.GetProperty("ruleSet").GetString()!,
            Sources = value.GetProperty("sources").EnumerateArray().Select(row => new ExplanationSource
            {
                Kind = Text(row, 0)!, Path = Text(row, 1), Line = Number(row, 2), EndLine = Number(row, 3),
                SourceHash = Text(row, 4), CommitId = Text(row, 5), RuleId = Text(row, 6), Reference = Text(row, 7), Text = Text(row, 8)
            }).ToList(),
            NameIndex = value.GetProperty("nameIndex").Deserialize<Dictionary<string, List<int>>>(Options)!,
            Nodes = value.GetProperty("nodes").EnumerateArray().Select(row => new NodeExplanation
            {
                NodeId = row[0].ValueKind == JsonValueKind.Number ? nodeIds[row[0].GetInt32()] : row[0].GetString()!,
                DisplayName = Claim(row[1]), Description = Claim(row[2]),
                Claims = row.GetArrayLength() > 3 ? row[3].EnumerateArray().Select(Claim).ToList() : null
            }).ToList()
        };
    }
}
