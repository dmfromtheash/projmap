using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProjMap.Core;

/// <summary>
/// The whole project map. Written to <c>.projmap/graph.json</c> by the analyzer and read by the viewer.
/// Contract rules (see docs/GRAPH_CONTRACT.md):
///  - Nodes form one tree through <see cref="Node.ParentId"/> (system → module → folder → file → type → member).
///    Roadmap items, docs and database tables are separate roots (ParentId = their group node).
///  - Edges are stored at the finest level known (file or type). The viewer rolls them up to folders/modules.
///  - Ids are stable between scans: derived from paths / fully qualified names, never from counters.
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
    public string? Summary { get; init; }      // XML doc summary / first doc comment / first markdown paragraph
    public NodeMetrics Metrics { get; init; } = new();
    public List<string> Tags { get; init; } = []; // e.g. "test", "ui", "generated", "entrypoint", "hot"
}

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
        };
        return JsonSerializer.SerializeToUtf8Bytes(doc, Options);
    }
    public static ProjectGraph? Deserialize(string json) => JsonSerializer.Deserialize<ProjectGraph>(json, Options);
}
