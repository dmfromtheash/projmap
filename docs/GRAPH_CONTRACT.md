# Analyzer graph contract (schemaVersion 1)

The analyzer returns `ProjMap.Core.ProjectGraph` and the CLI serializes it with
`GraphJson.Options` (camelCase, omitted nulls, compact UTF-8 JSON). The existing
Core records and constants have not been changed. Consumers must accept unknown
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
tags. Members additionally carry `callback` (an override or interface
implementation of a library member: the framework calls it, so no incoming edge
does not mean dead code), `interop` (a field of a `[StructLayout]` struct that
mirrors a native layout) and `handler`. A **handler** is its own member node
(`subKind: handler`) for a lambda wired to a control — an argument of a call
that also passes a string literal (`Ui.TextButton("Up", "Наверх", () => …)`) or
the right side of `x.Click += (…) => …`. Its `Name` is the call plus the
control's text («TextButton «Up · Наверх»»), its parent is the declaring type,
its id is the containing member's id + `#h<n>` in source order. Unknown languages omit `Language`. Languages are detected from extensions,
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

## Views (optional `views` section)

`views` is a projection over the same graph; it never changes nodes, edges or
the `ParentId` tree. It is absent in graphs written before it existed, and every
serializer (`WriteAsync`, `Serialize`, `SerializeCompact`) writes it when present.
`views.schemaVersion` is versioned separately from the graph's `schemaVersion`.
It is rebuilt on every full or incremental scan from the graph, file texts, the
project rules file and the owner's corrections, so both scans give equal views.

**Tiers.** `assignments` hold one entry per file node with a language:
`tier` ∈ `frontend | application | domain | data | backend | infrastructure |
tests | docs | mixed | unclassified`, optional `role` (`view`, `viewModel`,
`endpoint`, `botAdapter`, `schema`, `storage`, `platform`, `compositionRoot`,
`buildTool`, `cliEntry`, `test`, `doc`, …) and `execution` (`desktop | browser |
server | cli | shared`), a `score` 0..1, a Russian one-line `reason`, and the
`evidence` that produced it (`family`, `tier`, `score`, `rule`, `detail`).
Types and members take their file's tier; containers show the distribution.

Scoring is explainable, not a probability. Families: `syntax` (file content and
extension: markup, UI base classes, handlers in a UI-framework module, endpoints,
Discord events, schemas, DbContext, interop, build files, tests, Markdown),
`rules` (project rules file), and the weak `path` (folder words), `module`
(module name words), `framework` (≤ 0.15 from module packages) and `usage`
(storage access next to another role, file IO). Inside a family the strongest
item counts; families combine as 1 − Π(1 − s); only weak families cap the result
at 0.69. Decisions: `automatic` (≥ 0.75 and ≥ 0.15 ahead of the next tier),
`guess` (below that; the reason starts with «предположение»), `mixed` (two tiers
both ≥ 0.75 and close; `alternatives` names them, no winner is invented),
`unknown` (< 0.3), `override` (score 1; the automatic evidence is kept).
A test file or a Markdown document is `tests`/`docs` whatever else it contains.

**Features.** `features` are named groups that cut across tiers and modules:
`id` (`feature:<term>` for found ones, never derived from the display name),
`name` (Russian where known), `state` (`candidate | manual`), `terms`, `files`,
`tierIds`. They are found from file-name and folder words of product code
(tools, experiments, samples and docs folders do not seed them): a word in ≥ 3
files, in at most a fifth of them (floor 8), across ≥ 2 tiers; near-synonyms
(Jaccard ≥ 0.6) join; at most 24. `memberships` link a feature to nodes with a
`role`: `implementation` (code file), `test`, `doc`, and `entry` — a handler in
a feature file or one that directly calls into it. A node may be in several
features; totals must count unique nodes.

**Rules and corrections.** `.projmap/views.rules.json` (read by name; comments
and trailing commas allowed) has `rules` — `{id, match, assign:{tier, role?,
execution?}, score ≤ 0.95, reason?}` where `match` uses `pathGlob`, `language`,
`tag`, `text` (regex) and `all`/`any`/`not` — and `overrides` in the
`ViewsOverrides` shape. The owner's corrections live in the projmap app store
(`views/<root-hash>.json`), never in the scanned repository; the CLI accepts the
same file with `--views-overrides`. Order: builtin rules → project rules →
project overrides → owner overrides. Tier overrides select by exact `nodeId`
(wins), exact path, then glob; later entries win within a level. Feature
corrections: `addedFeatures` (terms and/or globs), `featureMerges`,
`hiddenFeatures`, `featureNames`, `members` include/exclude. A correction that
matches nothing is listed in `staleOverrides` and never re-targeted silently.
`ruleSet` records what contributed: `builtin:tiers-v1[+.projmap/views.rules.json][+owner]`.
`projmap-cli diff` compares views as its own section.
