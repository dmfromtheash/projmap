using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using ProjMap.Core;

namespace ProjMap.Analysis;

internal sealed class DatabaseScanner : IRepositoryScanner
{
    public string Name => "database";
    private const string Identifier = "(?:\\[[^\\]]+\\]|`[^`]+`|\"[^\"]+\"|[A-Za-z_][\\w$]*)(?:\\.(?:\\[[^\\]]+\\]|`[^`]+`|\"[^\"]+\"|[A-Za-z_][\\w$]*))*";
    private static readonly Regex Create = new(@"\bCREATE\s+(?:TEMP(?:ORARY)?\s+)?TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>" + Identifier + @")\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public async Task ScanAsync(ScanContext c, CancellationToken ct)
    {
        var tables = new Dictionary<string, DbTable>(StringComparer.OrdinalIgnoreCase);
        var sources = new HashSet<string>(StringComparer.Ordinal);
        var engines = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in c.Modules) DetectEngine(string.Join(' ', module.Packages), engines);
        foreach (var f in c.Files.Values)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (Path.GetExtension(f.Path).ToLowerInvariant() is ".sqlite" or ".sqlite3" or ".db")
                {
                    await ReadSqlite(c, f, tables, sources, ct); engines.Add("sqlite"); continue;
                }
                if (f.Language is not ("C#" or "SQL" or "Prisma" or "Python" or "TypeScript" or "JavaScript" or "TOML" or "Text" or "JSON")) continue;
                if (f.Text(c) is not { } text) continue;
                if (f.Language is "TOML" or "Text" or "JSON" or "Prisma") DetectEngine(text, engines);
                foreach (Match match in Create.Matches(text))
                {
                    var end = BalancedEnd(text, match.Index + match.Length - 1, '(', ')');
                    if (end < 0) continue;
                    var table = ParseTable(Clean(match.Groups["name"].Value), text.Substring(match.Index + match.Length, end - match.Index - match.Length),
                        new(f.Path, Line(text, match.Index), match.Value.Trim()));
                    Merge(tables, table); sources.Add(f.Path);
                }
                if (f.Language == "Prisma") ParsePrisma(f, text, tables, sources);
                if (f.Language == "C#") ParseEf(c, f, text, tables, sources);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { c.Warn(Name, ex.Message, f.Path); }
        }
        if (tables.Count == 0 && engines.Count == 0) return;
        if (tables.Count > 0) c.Group("Database");
        foreach (var table in tables.Values)
            c.Add(new Node { Id = TableId(table.Name), Kind = NodeKinds.Table, Name = table.Name, ParentId = "group:database",
                Path = table.DefinedAt?.Path, Line = table.DefinedAt?.Line });
        foreach (var f in c.Files.Values.Where(f => f.Language is "C#" or "SQL" or "Python" or "TypeScript" or "JavaScript"))
        {
            ct.ThrowIfCancellationRequested();
            if (f.Text(c) is not { } text) continue;
            foreach (var (kind, pattern) in new[] {
                (EdgeKinds.DbRead, @"\b(?:FROM|JOIN)\s+(?<table>" + Identifier + ")"),
                (EdgeKinds.DbWrite, @"\b(?:INSERT\s+(?:OR\s+\w+\s+)?INTO|UPDATE|DELETE\s+FROM|REPLACE\s+INTO)\s+(?<table>" + Identifier + ")") })
            {
                if (kind == EdgeKinds.DbRead && !Regex.IsMatch(text, @"\bSELECT\b", RegexOptions.IgnoreCase)) continue;
                foreach (Match match in Regex.Matches(text, pattern, RegexOptions.IgnoreCase))
                {
                    if (kind == EdgeKinds.DbRead && Regex.IsMatch(text[Math.Max(0, match.Index - 32)..match.Index], @"\bDELETE\s+$", RegexOptions.IgnoreCase)) continue;
                    if (!tables.TryGetValue(Clean(match.Groups["table"].Value), out var table)) continue;
                    var line = Line(text, match.Index);
                    var evidence = new SourceLocation(f.Path, line, match.Value);
                    c.Edge(kind, ScanContext.FileId(f.Path), TableId(table.Name), evidence);
                    var owner = c.Graph.Nodes.Values.Where(n => n.Kind == NodeKinds.Type && n.Path == f.Path && n.Line <= line)
                        .OrderByDescending(n => n.Line).FirstOrDefault();
                    if (owner != null) c.Edge(kind, owner.Id, TableId(table.Name), evidence);
                }
            }
        }
        if (engines.Count > 1) c.Warn(Name, "Multiple database engines detected: " + string.Join(", ", engines.Order()));
        c.Graph.Database = new() { Engine = engines.Count == 1 ? engines.Single() : "unknown", Sources = sources.Order(StringComparer.Ordinal).ToList(),
            Tables = tables.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ToList() };
    }
    private static string TableId(string name) => "table:" + name.ToLowerInvariant();
    private static string Clean(string name) => string.Join('.', name.Split('.').Select(p => p.Trim(' ', '\r', '\n', '[', ']', '`', '"')));
    private static int Line(string text, int index) => 1 + text.AsSpan(0, index).Count('\n');
    private static void DetectEngine(string text, HashSet<string> engines)
    {
        if (Regex.IsMatch(text, @"\b(?:Microsoft\.Data\.Sqlite|EntityFrameworkCore\.Sqlite|sqlite3?|aiosqlite)\b", RegexOptions.IgnoreCase)) engines.Add("sqlite");
        if (Regex.IsMatch(text, @"\b(?:Npgsql|pg|postgres(?:ql)?|psycopg\w*)\b", RegexOptions.IgnoreCase)) engines.Add("postgres");
        if (Regex.IsMatch(text, @"\b(?:MySql\w*|Pomelo|pymysql)\b", RegexOptions.IgnoreCase)) engines.Add("mysql");
        if (Regex.IsMatch(text, @"\b(?:SqlServer|SqlClient|mssql)\b", RegexOptions.IgnoreCase)) engines.Add("mssql");
        if (Regex.IsMatch(text, @"\b(?:sqlalchemy|prisma|EntityFrameworkCore)\b", RegexOptions.IgnoreCase) && engines.Count == 0) engines.Add("unknown");
        if (engines.Count > 1) engines.Remove("unknown");
    }
    private static void Merge(Dictionary<string, DbTable> tables, DbTable table)
    {
        if (!tables.TryGetValue(table.Name, out var old)) { tables[table.Name] = table; return; }
        tables[table.Name] = old with { Columns = old.Columns.Concat(table.Columns).GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DbColumn(g.First().Name, g.FirstOrDefault(x => x.Type != "unknown")?.Type ?? "unknown", g.Any(x => x.PrimaryKey), g.Any(x => x.NotNull))).ToList(),
            ForeignKeys = old.ForeignKeys.Concat(table.ForeignKeys).Distinct().ToList() };
    }
    private static DbTable ParseTable(string name, string body, SourceLocation location)
    {
        var columns = new List<DbColumn>();
        var foreignKeys = new List<DbForeignKey>();
        var primary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in SplitTopLevel(body))
        {
            var value = part.Trim();
            var pk = Regex.Match(value, @"^(?:CONSTRAINT\s+" + Identifier + @"\s+)?PRIMARY\s+KEY\s*\((?<cols>[^)]+)\)", RegexOptions.IgnoreCase);
            if (pk.Success) { foreach (var col in pk.Groups["cols"].Value.Split(',')) primary.Add(Clean(col)); continue; }
            var fk = Regex.Match(value, @"FOREIGN\s+KEY\s*\((?<cols>[^)]+)\)\s*REFERENCES\s+(?<table>" + Identifier + @")\s*\((?<refs>[^)]+)\)", RegexOptions.IgnoreCase);
            if (fk.Success)
            {
                var local = fk.Groups["cols"].Value.Split(','); var remote = fk.Groups["refs"].Value.Split(',');
                for (var i = 0; i < Math.Min(local.Length, remote.Length); i++) foreignKeys.Add(new(Clean(local[i]), Clean(fk.Groups["table"].Value), Clean(remote[i])));
                continue;
            }
            if (Regex.IsMatch(value, @"^(?:CONSTRAINT|UNIQUE|CHECK|PRIMARY|FOREIGN)\b", RegexOptions.IgnoreCase)) continue;
            var colMatch = Regex.Match(value, @"^(?<name>" + Identifier + @")\s+(?<type>[\w]+(?:\s*\([^)]*\))?)", RegexOptions.IgnoreCase);
            if (!colMatch.Success) continue;
            var column = Clean(colMatch.Groups["name"].Value);
            columns.Add(new(column, colMatch.Groups["type"].Value, Regex.IsMatch(value, @"\bPRIMARY\s+KEY\b", RegexOptions.IgnoreCase), Regex.IsMatch(value, @"\bNOT\s+NULL\b", RegexOptions.IgnoreCase)));
            var reference = Regex.Match(value, @"\bREFERENCES\s+(?<table>" + Identifier + @")\s*\((?<col>[^)]+)\)", RegexOptions.IgnoreCase);
            if (reference.Success) foreignKeys.Add(new(column, Clean(reference.Groups["table"].Value), Clean(reference.Groups["col"].Value)));
        }
        return new() { Name = name, Columns = columns.Select(c => primary.Contains(c.Name) ? c with { PrimaryKey = true } : c).ToList(),
            ForeignKeys = foreignKeys, DefinedAt = location };
    }
    private static IEnumerable<string> SplitTopLevel(string text)
    {
        var start = 0; var depth = 0; char quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote != '\0') { if (ch == quote) { if (i + 1 < text.Length && text[i + 1] == quote) i++; else quote = '\0'; } continue; }
            if (ch is '\'' or '"' or '`') { quote = ch; continue; }
            if (ch == '(') depth++; if (ch == ')') depth--;
            if (ch == ',' && depth == 0) { yield return text[start..i]; start = i + 1; }
        }
        yield return text[start..];
    }
    private static int BalancedEnd(string text, int start, char open, char close)
    {
        var depth = 0; char quote = '\0';
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote != '\0') { if (ch == quote) { if (i + 1 < text.Length && text[i + 1] == quote) i++; else quote = '\0'; } continue; }
            if (ch is '\'' or '"' or '`') { quote = ch; continue; }
            if (ch == open) depth++; if (ch == close) depth--;
            if (ch == close && depth == 0) return i;
        }
        return -1;
    }
    private static void ParsePrisma(RepoFile file, string text, Dictionary<string, DbTable> tables, HashSet<string> sources)
    {
        foreach (Match model in Regex.Matches(text, @"\bmodel\s+(?<name>\w+)\s*\{(?<body>[^}]+)\}"))
        {
            var body = model.Groups["body"].Value;
            var mapped = Regex.Match(body, "@@map\\(\"(?<name>[^\"]+)\"\\)");
            var name = mapped.Success ? mapped.Groups["name"].Value : model.Groups["name"].Value;
            var columns = new List<DbColumn>(); var fks = new List<DbForeignKey>();
            foreach (Match field in Regex.Matches(body, @"(?m)^\s*(?<name>\w+)\s+(?<type>\w+)(?<optional>\?|\[\])?(?<rest>[^\r\n]*)"))
            {
                var type = field.Groups["type"].Value; var rest = field.Groups["rest"].Value;
                if (Regex.IsMatch(type, @"^(?:String|Int|BigInt|Boolean|DateTime|Float|Decimal|Bytes|Json)$"))
                {
                    var map = Regex.Match(rest, "@map\\(\"(?<name>[^\"]+)\"\\)");
                    columns.Add(new(map.Success ? map.Groups["name"].Value : field.Groups["name"].Value, type, rest.Contains("@id"), field.Groups["optional"].Value != "?"));
                }
                var relation = Regex.Match(rest, @"@relation\([^)]*fields:\s*\[(?<cols>[^]]+)\][^)]*references:\s*\[(?<refs>[^]]+)\]");
                if (relation.Success)
                {
                    var local = relation.Groups["cols"].Value.Split(','); var remote = relation.Groups["refs"].Value.Split(',');
                    for (var i = 0; i < Math.Min(local.Length, remote.Length); i++) fks.Add(new(local[i].Trim(), type, remote[i].Trim()));
                }
            }
            var primary = Regex.Match(body, @"@@id\(\[(?<cols>[^]]+)\]");
            var keys = primary.Groups["cols"].Value.Split(',').Select(x => x.Trim()).ToHashSet();
            Merge(tables, new() { Name = name, Columns = columns.Select(col => keys.Contains(col.Name) ? col with { PrimaryKey = true } : col).ToList(), ForeignKeys = fks,
                DefinedAt = new(file.Path, Line(text, model.Index)) }); sources.Add(file.Path);
        }
    }
    private static void ParseEf(ScanContext c, RepoFile file, string text, Dictionary<string, DbTable> tables, HashSet<string> sources)
    {
        foreach (Match set in Regex.Matches(text, @"\bDbSet\s*<(?<type>[\w.]+)>\s+(?<name>\w+)"))
        {
            var typeName = set.Groups["type"].Value.Split('.').Last();
            var node = c.Graph.Nodes.Values.FirstOrDefault(n => n.Kind == NodeKinds.Type && n.Name == typeName);
            var columns = new List<DbColumn>();
            if (node?.Path is { } path && c.Files.TryGetValue(path, out var entity) && entity.Text(c) is { } body)
                foreach (Match property in Regex.Matches(body, @"\bpublic\s+(?<type>[\w?]+)\s+(?<name>\w+)\s*\{\s*get\s*;"))
                    columns.Add(new(property.Groups["name"].Value, property.Groups["type"].Value, property.Groups["name"].Value is "Id" || property.Groups["name"].Value == typeName + "Id", !property.Groups["type"].Value.EndsWith('?')));
            Merge(tables, new() { Name = set.Groups["name"].Value, Columns = columns, DefinedAt = new(file.Path, Line(text, set.Index)) }); sources.Add(file.Path);
        }
        foreach (Match entity in Regex.Matches(text, "\\.Entity\\(\"[^\"]+\",\\s*\\w+\\s*=>\\s*\\{"))
        {
            var start = entity.Index + entity.Length - 1;
            var end = BalancedEnd(text, start, '{', '}');
            if (end < 0) continue;
            var body = text[start..end];
            var table = Regex.Match(body, "\\.ToTable\\(\"(?<name>[^\"]+)\"");
            if (!table.Success) continue;
            var key = Regex.Match(body, "\\.HasKey\\((?<keys>[^)]+)\\)");
            var primary = Regex.Matches(key.Groups["keys"].Value, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
            var columns = new List<DbColumn>();
            foreach (Match property in Regex.Matches(body, "\\.Property<(?<type>[^>]+)>\\(\"(?<name>[^\"]+)\"\\)(?<options>[^;]*);"))
            {
                var options = property.Groups["options"].Value;
                var sqlType = Regex.Match(options, "HasColumnType\\(\"(?<type>[^\"]+)\"");
                columns.Add(new(property.Groups["name"].Value, sqlType.Success ? sqlType.Groups["type"].Value : property.Groups["type"].Value,
                    primary.Contains(property.Groups["name"].Value), options.Contains("IsRequired()")));
            }
            Merge(tables, new() { Name = table.Groups["name"].Value, Columns = columns, DefinedAt = new(file.Path, Line(text, entity.Index)) }); sources.Add(file.Path);
        }
    }
    private static async Task ReadSqlite(ScanContext c, RepoFile file, Dictionary<string, DbTable> tables, HashSet<string> sources, CancellationToken ct)
    {
        // immutable prevents SQLite from creating WAL/SHM/journal sidecars in the scanned repository.
        // A WAL-backed live database is skipped instead of returning a stale immutable snapshot.
        if (File.Exists(file.FullPath + "-wal")) { c.Warn("database", "Live WAL database skipped to preserve strict read-only scanning.", file.Path); return; }
        var uri = new Uri(file.FullPath).AbsoluteUri + "?immutable=1";
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = uri,
            Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2 }.ToString());
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, sql FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0); var sql = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var match = Create.Match(sql);
            if (!match.Success) continue;
            var end = BalancedEnd(sql, match.Index + match.Length - 1, '(', ')');
            if (end < 0) continue;
            Merge(tables, ParseTable(name, sql.Substring(match.Index + match.Length, end - match.Index - match.Length), new(file.Path, 1)));
            sources.Add(file.Path);
        }
    }
}
