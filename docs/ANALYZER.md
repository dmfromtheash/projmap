# Analyzer and CLI

The analyzer is a .NET 10 library with no dependency on the desktop viewer,
MSBuild Workspace, a repository build, Node, or Python. It never runs repository
code, installs packages in a scanned repository, changes git configuration,
updates `.gitignore`, or writes scanner output itself. Git is optional.

## Build and use

```powershell
dotnet build ProjMap.slnx
dotnet test tests/ProjMap.Tests/ProjMap.Tests.csproj

dotnet run --project src/ProjMap.Cli -- scan C:\Projects\Example
dotnet run --project src/ProjMap.Cli -- scan C:\Projects\Example -o C:\output\graph.json --git-months 6 --no-members
dotnet run --project src/ProjMap.Cli -- stats C:\output\graph.json
```

The CLI assembly/executable is named `projmap-cli`. Default output is
`<root>/.projmap/graph.json`; its parent directory is created. An explicit `-o`
path is the only alternate scan output. The output file may be replaced on a
later scan. If `.gitignore` exists without `.projmap/`, the CLI prints a hint
and leaves the ignore file untouched. `.projmap` is always excluded from scans.
Exit codes: 0 success (including scans with warnings), 1 error, 2 usage, 130
cancellation. Ctrl+C cancels the CLI scan. `stats` prints every emitted node and
edge kind, roadmap statuses, database and warning counts.

For a portable Windows distribution (run these commands within projmap):

```powershell
dotnet publish src/ProjMap.Cli -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/cli
```

The default publish includes the runtime and SQLite native dependency. Native
single-file extraction is handled by .NET at launch; the repository being
scanned is never used for extraction. Runtime-owned caches are distinct from
analyzer scan output.

## In-process API

```csharp
using ProjMap.Analysis;
using ProjMap.Core;

var options = new ScanOptions(root)
{
    GitMonths = 12,              // 0 disables history
    MaxCommits = 3000,           // 0 disables history
    IncludeMembers = true,
    IncludeTests = true,
    Progress = new Progress<ScanProgress>(p => UpdateUi(p.Stage, p.Fraction, p.Detail))
};
ProjectGraph graph = await ProjectScanner.ScanAsync(options, cancellationToken);
```

The public types and signature are exactly:

```csharp
public sealed record ScanOptions(string Root) { /* properties above */ }
public sealed record ScanProgress(string Stage, double Fraction, string? Detail = null);
public static class ProjectScanner
{
    public static Task<ProjectGraph> ScanAsync(ScanOptions options, CancellationToken ct = default);
}
```

Stages are `structure`, `csharp`, `imports`, `git`, `roadmap`, `database`,
`complete`, with fractions 0, 1/6, 2/6, 3/6, 4/6, 5/6, 1. Work runs on worker
threads, and module work is parallel (up to eight workers). Use `Progress<T>`
to marshal notifications to the viewer's UI context. The library does no output
writing; the viewer decides where to save `GraphJson.Serialize(graph)`.

## Scanner organization

`ProjectScanner.cs` holds the API, common `IRepositoryScanner`, internal graph
builder, guarded process invocation, normalization and aggregation. Six separate
scanner classes implement that interface:

- `StructureScanner`: git's null-delimited `ls-files --cached --others
  --exclude-standard`, including untracked files, or a filesystem walk with
  common build/cache exclusions and basic nested gitignore rules. Deleted
  tracked files and reparse-point files/directories are skipped. The scanner
  does not follow repository symlinks into external locations. Text content is
  cached, and files larger than 16 MiB are skipped for content analysis with a
  warning. Modules and the system/folder/file tree remain available regardless
  of language-specific analysis success.
- `CSharpScanner`: parallel syntax trees/declarations plus one Roslyn
  `CSharpCompilation` per C# module. Metadata comes from the running runtime's
  `TRUSTED_PLATFORM_ASSEMBLIES`; referenced projects are wired using in-memory
  compilation references in dependency order, including transitive references.
  Implicit global usings and simple project constants are recognized. Source
  XML summary comments, overload identity, partial/nested/generic types,
  properties, methods, constructors, events, basic inheritance and call binding
  are included. Missing NuGet symbols are ignored. Project/package/workspace
  dependency edges come from manifests even when semantic binding is incomplete.
- `ImportScanner`: JS/TS imports, side-effect imports, require, re-exports and
  dynamic import syntax; relative extension/index resolution, `.js` → `.ts`,
  nearest tsconfig/jsconfig baseUrl and path aliases, simple package workspaces;
  Python absolute/relative imports and package init files. Exports/top-level
  Python definitions become best-effort symbol nodes.
- `GitScanner`: bounded log parsing with null-delimited paths; commits, last
  author change, distinct authors, filtered co-change pairs. Process arguments
  use `ArgumentList` and no shell. Git runs with `--no-optional-locks` and
  `GIT_OPTIONAL_LOCKS=0`; processes are killed on cancellation.
- `RoadmapScanner`: doc aliases for Markdown; roadmap headings, nested checked
  tasks, status-bearing bullets and status tables in ROADMAP/TODO/STATE/PLAN/
  CHANGELOG/PROGRESS documents, plus DECISIONS. Archive directories are skipped
  for roadmap extraction. Fenced code is excluded. RU/EN status text and emoji,
  source links, node-name/path lookup and DEC-id commit subjects are preserved.
- `DatabaseScanner`: package/provider engine hints; balanced `CREATE TABLE`
  definitions, composite/inline PK and FK, Prisma model scalar fields and
  relations, basic EF `DbSet<T>`/snapshot extraction; SQL read/write edges to
  known tables. Actual SQLite files open with `Mode=ReadOnly`, pooling disabled,
  URI `immutable=1`, and only a SELECT of `sqlite_schema`. A file with an
  existing WAL sidecar is skipped with a warning, because reading an immutable
  main file would hide pending WAL changes. No pragmas or user SQL are executed.

Each stage and individual module/file operations turn failures into
`ScanWarning` and retain partial results. A broken project manifest/database
does not prevent documents/imports/other projects from being analyzed. The
input root/options and progress callback remain the caller's responsibility;
caller cancellation propagates as `OperationCanceledException`.

The semantic strategy follows [Roslyn's standalone compilation model](https://github.com/dotnet/roslyn/blob/main/docs/wiki/Getting-Started-C%23-Semantic-Analysis.md).
SQLite modes are documented in [Microsoft.Data.Sqlite connection strings](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings);
URI handling and immutable connections are described by [SQLite](https://www.sqlite.org/c3ref/open.html).

## Current limits

- MSBuild conditions, imports, linked files, `Compile Include/Remove`, target
  frameworks and source generators are not evaluated. F# has structural and
  manifest analysis, not semantic binding. Cyclic C# project references warn;
  semantic links across cycles can be incomplete.
- Syntax uses Roslyn 4.14 preview support. NuGet assemblies are not restored or
  loaded from the scanned repository. Symbols depending on unavailable package
  types can stay unresolved. Preprocessor expressions requiring MSBuild property
  evaluation are not inferred.
- Gitignore fallback covers common globs, nested rules and negation, not every
  escaped-character/character-class edge case. Git enumeration is authoritative
  whenever available. Build/cache folders are skipped even if accidentally
  tracked. Binary/unrecognized files have no content-derived LOC.
- Import/export parsing is lexical. tsconfig `extends`, package exports/main
  maps, dynamic Python sys.path, multilined Python imports and JS AST scope are
  not modeled. Exported variables are approximate function nodes; Python class
  methods are not expanded. Ambiguous bare names can link multiple code nodes.
- Roadmap heading status remains unknown without a marker. Natural-language
  status descriptions are heuristics; mixed historical/proposed status text
  can be ambiguous. Root items are in the roadmap list, not duplicated as nodes.
- SQL is inspected lexically across code files and may match comments/non-SQL
  text. Dynamic/interpolated table names, C# quote escaping, advanced SQL types,
  ALTER TABLE and custom EF conventions can be incomplete. DbSet entity property
  discovery and snapshots are approximations; separate databases with the same
  table name merge. Prisma mapped relation names and EF relationship metadata
  can need manual interpretation. Non-SQLite `.db` files become warnings.
- Per-file Git activity is bounded by months and commit count, uses author
  timestamps, and includes bulk changes; co-change excludes bulk commits. No
  history outside the requested window is inferred.

## Verification and real repository result

Tests create disposable fixture repositories under
`tests/ProjMap.Tests/obj/fixtures/<guid>` (temporary directories inside the
authorized workspace). Every fixture is cleaned in `Dispose`, including git
read-only object files. Tests cover semantic cross-project calls and inheritance,
stable partial/overloaded/duplicate symbols, imports/aliases/workspaces/Python,
RU+EN roadmap, SQL+actual SQLite+EF+Prisma, read-only file snapshots, Git pair
thresholds/confidence/bulk commits/limits/DEC subjects, fallback ignores,
warnings, cancellation, no-members/no-tests, CLI and JSON integrity. A 1,000-file
C# fixture is checked against the 60-second requirement.

The final verified full scan of
`C:\Users\Leshe\.codex\worktrees\stage10f-fonts-bindings\Map` at
`00bc91f7701cd2cb7ddb10ca4cea5278f5d2ed21` took **26,168 ms** in a Debug CLI
process, analyzing 1,403 files (930 C#). Output was explicitly written to the
user-specified scratchpad `map-graph.json`, outside Map. Map's git status stayed
clean before and after. Scan results:

| Node kind | Count | Edge kind | Count |
| --- | ---: | --- | ---: |
| system | 1 | reference | 23,481 |
| module | 19 | call | 14,727 |
| folder | 74 | inherit | 15 |
| file | 1,403 | implement | 353 |
| type | 1,844 | projectRef | 39 |
| member | 14,230 | package | 35 |
| doc | 102 | cochange | 254 |
| group | 3 | mentions | 4,441 |
| table | 2 | dbRead | 4 |
| | | dbWrite | 4 |
| **Total** | **17,678** | **Total** | **43,353** |

All eight requested production modules are present, plus six test/kill-host
projects and five tools/experiments. All **39** ProjectReference declarations
match the emitted edges exactly. There are 531 roadmap entries: 130 done, 70
active, 17 planned, 6 deferred, 2 blocked, 306 unknown (including headings with
no status). `DEC-070` in `docs/DECISIONS.md` links 38 nodes and seven commit
subjects. The progress table recognizes R00–R08 as done, R09 active, and PARTIAL
stages as active. There are no dangling internal edge endpoints and no warnings.

All **16 tests** pass with `-warnaserror`; analyzer/CLI/tests build with zero
warnings. The whole solution builds successfully; the concurrently developed
viewer project currently reports one MSB3277 WindowsBase/WebView2 version warning.

The requested assumption about SQLite in Map.Persistence differs from this
checkout: Map.Persistence uses ZIP/chunk persistence. The two actual tables,
`metadata` and `tiles`, come from **Map.Export/MbTilesWriter.cs** (MBTiles). They
are reported at that source; no Map.Persistence schema is fabricated.
