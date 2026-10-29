# Analyzer graph contract (schemaVersion 1)

The analyzer returns `ProjMap.Core.ProjectGraph` and the CLI serializes it with
`GraphJson.Options` (camelCase, omitted nulls, compact UTF-8 JSON). The existing
Core record shapes have not been changed. Consumers must accept unknown
kind strings and absent optional values.

## Identity and containment

Paths are relative to `Project.Root`, use `/`, retain filesystem spelling, and
never include `..`. The root's absolute location is **not** part of an id. Moving
the same repository preserves ids. Path lookup is case insensitive on Windows;
ids and output ordering use ordinal comparisons. Renaming a file, manifest,
namespace, symbol signature or roadmap title can change its identity.

| Kind | Id | Parent |
| --- | --- | --- |
| `system` | `system:root` | null; exactly one |
| `module` | `module:<manifest-relative-path>` | system |
| `folder` | `folder:<manifest-path>:<directory-path>` inside modules; `folder:<directory-path>` outside | nearest folder, module or system |
| `file` | `file:<relative-path>` | folder, module or system |
| C# `type` | `type:<module-id>:<fully-qualified-original-type-name>` | containing type, otherwise declaring file |
| C# `member` | `member:<module-id>:<Roslyn-documentation-comment-id>` | declaring type |
| TS/JS/Python `type` | `type:<file-path>:<exported-name>` | file |
| TS/JS/Python `member` | `member:<file-path>:<exported-name>` | file |
| `doc` | `doc:<markdown-path>` | `group:docs` |
| `table` | `table:<lowercase-schema-qualified-table-name>` | `group:database` |
| `group` | `group:docs`, `group:roadmap`, `group:database` | system |

Manifests are `.csproj`, `.fsproj`, `package.json`, `pyproject.toml`, `setup.py`,
or a top-level Python package's `__init__.py`. A Python directory with both
pyproject and setup.py creates one module. File assignment uses the deepest
module directory, then ordinal manifest order to resolve a tie. Two projects in
the same directory are distinct modules, but shared source is assigned to the
first one: this analyzer does not evaluate MSBuild `Compile` item expressions.
Loose C# source uses `system:root` as its identity scope.

C# fully qualified names omit `global::` and retain generic type parameters.
Documentation comment ids distinguish overloads, constructors, properties and
operators. A fallback fully qualified signature is used if Roslyn has no doc id.
Partial types have one canonical node: the first declaration by ordinal file
path and source span. Members in all partial files point to that node. This
means a type's `Path` can differ from a member's `Path`.

`SubKind` for C# types is `class`, `record`, `record struct`, `struct`,
`interface`, `enum`, or `delegate`; members are `method`, `property`, `ctor`,
`event` or non-private `field` (operators/destructors are methods). Private
fields are not emitted. Field and field-like event complexity is zero. TS
subkinds are `class`, `interface`, `type`, `enum`, and `function`; Python uses
`class` and `function`. Lightweight exported variable declarations are treated
as best-effort functions. `IncludeMembers=false` suppresses all member nodes.

Markdown has both a normal structural file node and a doc alias. The alias
has zero metrics so it does not double-count file/LOC totals. Table nodes use
their schema source's path/line when known. External packages have target ids
`pkg:<package-name>` and **do not** have nodes. These are the only intentionally
node-less edge targets.

## Roadmap items

Roadmap items live in `ProjectGraph.Roadmap`, not `Nodes`. Their ids are valid
edge sources. `group:roadmap` is a viewer navigation group; root roadmap items
have `ParentId=null`, and nested items use a roadmap parent id. Never resolve a
roadmap `ParentId` as a code-node parent.

Ids are `roadmap:<document-path>#<normalized-title>-<12-hex-hash>`.
Normalization lowercases Unicode letters/digits, replaces other runs with `-`,
and uses at most 72 slug characters. The SHA-256 prefix covers the parent id
and the complete normalized title. Repeated identical titles under one parent
get `-2`, `-3`, etc. Checkboxes are removed before title processing; toggling
one or inserting unrelated lines preserves ids. Reordering identical siblings
can change their duplicate suffixes.

`Source.Line` and all other source lines are **1-based**. `Phase` is the first
heading below the document title when available. `Detail` contains section
prose or a complete table row. Decision headings containing `DEC-<digits>` are
matched case insensitively to git commit subjects. Related files are added to
`Links`; `Detail` also contains a `Commits:` section with abbreviated hashes and
the full matching subjects. Corresponding `mentions` evidence preserves those
subjects. `Links` is a sorted, unique list of graph node ids.

Statuses are `done`, `active`, `planned`, `deferred`, `blocked`, `unknown`.
Headings with no explicit status remain unknown; completion is not inferred
from their children. Checked tasks are done; unchecked tasks default to
planned unless an explicit status overrides that default. Status table cells
are authoritative. RU/EN words and common emoji are supported, including
`✅`, `🔄`, `🔀 PARTIAL`, `⬜`, `⏳`, `⏸`, `⛔`, `🚫`. When multiple status
markers occur, precedence is blocked, deferred, active, planned, done. This
keeps PARTIAL text containing a historical checkmark active.

## Edges and weights

Ids are always `<kind>:<source-id>-><target-id>`. Multiple observations of one
source/target/kind accumulate in one edge. Self edges are omitted. Evidence
contains up to five locations; semantic evidence is ordered by path/line,
git evidence lists up to five recent co-commits.

| Kind | Endpoints | Raw `Weight` |
| --- | --- | --- |
| `projectRef` | module → referenced module | distinct manifest dependency declarations (normally 1) |
| `package` | module → `pkg:Name` | distinct package declarations per module (normally 1) |
| `reference` | type → type **and** file → file | bound simple-name occurrences referencing repository types or their symbols |
| `call` | type → type **and** file → file | bound invocations and explicit/implicit constructor calls |
| `inherit` | type → base type **and** file → file | base declarations; interface → interface also inherits |
| `implement` | type → interface **and** file → file | class/record/struct base-list interface declarations |
| `import` | file → file | resolved JS/TS/Python import/re-export occurrences |
| `cochange` | file ↔ file | commits touching both files in the requested git window |
| `mentions` | roadmap item/doc → mentioned code/file/module | unique explicit linked targets per item/doc, plus decision commit-file matches |
| `dbRead` | file/type → table | SQL `FROM`/`JOIN` occurrences in a file containing `SELECT` |
| `dbWrite` | file/type → table | SQL `INSERT INTO`, `INSERT OR ... INTO`, `REPLACE INTO`, `UPDATE`, `DELETE FROM` occurrences |

Type and file semantic edges are deliberate parallel representations. The file
edge accumulates all contributing semantic occurrences, including sources in
partial-type files. Consumers rolling up file edges must **not also add type
edges** or they double count. References and calls are separate measures: a
method name may contribute both a reference and a call. No dependencies are
inferred from unresolved NuGet symbols or generated framework types. Modules
with missing packages still retain their declaration nodes.

Co-change is undirected and stored once, with ordinal-smaller path first.
Commits touching more than 40 paths are excluded from pair counts, but count
toward per-file activity. Only pairs satisfying `pairCommits >= 3` and
`pairCommits / (commitsA + commitsB - pairCommits) >= 0.3` are emitted. Deleted
and ignored files have no current nodes and do not become endpoints. The
40-file cutoff uses the full commit path list before filtering current files.

For **every** edge kind, after all scanners finish:

```text
Strength(edge) = Weight(edge) / max(Weight of every emitted edge of its kind)
```

Strength is therefore in `(0,1]`; it is relative to this scan and must not be
interpreted as semantic certainty or the Jaccard threshold itself. Raw weights
should be used for comparisons across scans.

## XAML markup edges

After C# declarations are available, `.axaml` and `.xaml` files are parsed with
`System.Xml.Linq`. XAML 2006 and 2009 namespace URIs are recognized (including
MAUI's `x:Class`/`x:DataType`). Each resolved observation contributes weight 1, using the
normal edge id, evidence, aggregation and strength rules:

| Kind | Endpoints | Meaning |
| --- | --- | --- |
| `reference` | XAML file → C# type | Root `x:Class` names an existing code-behind type. |
| `xamlEvent` | XAML file → C# method | A known event attribute names one unambiguous method declared on the `x:Class` type, including its partial declarations. |
| `binding` | XAML file → C# property/non-private field | A `Command` binding names an existing member on a statically identified data-context type. |

`xamlEvent` distinguishes markup subscriptions from C# references/calls and
synthetic code-created handler nodes. Its target keeps subkind `method` and gains
tags `callback` and `xaml-handler`. Evidence includes the element local type,
`x:Name`/`Name` when present, the attribute text and its 1-based line. No control
nodes or general-purpose metadata fields are added.

Events require a bounded, ordinal, case-sensitive known-name list (for example
`Click`, `Tapped`, `PointerPressed`, `Loaded`, `KeyDown`, `SelectionChanged`) and
a plain C# identifier value matching exactly one method. Arbitrary properties,
unknown events, missing methods and overloaded methods are skipped. This is a
heuristic without framework event/signature validation; custom/attached events
and inherited methods are not resolved.

Command forms are `{Binding X}`, `{Binding Path=X}`, `{CompiledBinding X}` and
`{x:Bind X}`, including simple comma-separated options. Only a single direct
member identifier is resolved. Dotted/indexed paths, nested extensions and
explicit `Source`, `RelativeSource` or `ElementName` are skipped. For this
lightweight analysis all four forms use the explicitly typed context; WinUI's
implicit code-behind source for `x:Bind` is not inferred.

The nearest ancestor-or-self `x:DataType` identifies the context. Alternatively,
`d:DataContext="{x:Type vm:Foo}"` or
`d:DataContext="{d:DesignInstance Type=vm:Foo, ...}"` (also positional `vm:Foo`)
provides a design-time hint. Prefixes are resolved by XML namespace URI, not by
their spelling: `using:Ns` and `clr-namespace:Ns;assembly=Name` identify C#
namespaces. Assembly suffixes are not evaluated. Type lookup prefers an exact
fully qualified name in the file's module, otherwise requires a unique graph
type with that name. Ambiguous types are skipped. An explicit unknown/null
`x:DataType` or a runtime `DataContext` attribute/property element blocks
inference from more distant ancestors. Runtime context assignments, property
element design contexts, templates without explicit types, inheritance and
generated command properties absent from the graph are not inferred.

Unresolved bindings are **skipped**, without placeholder nodes, dangling targets
or warnings: missing runtime context is ordinary markup, and Core has no
dedicated unresolved-binding metadata field. `IncludeMembers=false` still
permits `x:Class` references but suppresses event/binding edges. Malformed XML
produces one `xaml` warning for that file and no markup edges from it; other
files continue. DTDs and external entities are prohibited.

XAML runs after C# on every full or incremental scan, including when C# nodes
are reused. This refreshes edges and handler tags after markup edits/deletions
or C# declaration changes. Files, candidate types and added tags use ordinal
ordering; output is deterministic apart from existing volatile scan timestamp
and timing diagnostics.

## Metrics, metadata and failure behavior

`Loc` counts nonblank text lines, including comments. Binary/unrecognized files
have zero LOC. Containers sum descendant file LOC once. `Files`, `Types`,
`Members` and `Complexity` roll up through containment. Member complexity is
`1 + if/for/foreach/while/do/catch/conditional/case/non-default-switch-arm/
&&/||/?? occurrences`; it is an estimate, not a control-flow analysis. These
metrics include generated/test source unless its directory is excluded or
`IncludeTests=false`. Complexity is zero when members are suppressed.

`Commits`, `Authors`, `LastChange` are per-file values only (not summed into
containers). Authors are distinct git name/email identities; `LastChange` uses
the author timestamp. Other node kinds keep the default git metric values.
Tags are `test`, `generated`, `entrypoint`, `ui`. Type/member nodes inherit file
tags. Unknown languages omit `Language`. Languages are detected from extensions,
not proof that a language-specific semantic scanner exists.

`Project.ScannedAt` is UTC start time. `ScanMilliseconds` covers scanning,
normalization and metric aggregation, excluding JSON serialization and writing.
Nodes/edges are sorted by ordinal id; roadmap entries by source path/line.
Warnings have scanner, message and optional relative path. Independent scanners
and per-file/module operations catch failures and continue. Invalid API options,
missing roots and requested cancellation are caller errors and propagate.
Cancellation is never converted into a warning.

`Database.Engine` is `sqlite`, `postgres`, `mysql`, `mssql` or `unknown`.
Multiple engines yield `unknown` and a warning. `Sources` are schema-bearing
files. Table definitions merge by case-insensitive schema-qualified name; column
and FK evidence from several sources can enrich one table. Database identity
is not currently separated per connection or physical database.
