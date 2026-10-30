# Analyzer graph contract (schemaVersion 1)

The analyzer returns `ProjMap.Core.ProjectGraph` and the CLI serializes it with
`GraphJson.Options` (camelCase, omitted nulls, compact UTF-8 JSON). The existing
Core record shapes remain backwards compatible. Consumers must accept unknown
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
| C# `member` | `member:<module-id>:<source-signature>` | declaring type |
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
Member source signatures use `M:`, `P:`, `E:` or `F:`, the original containing
type name, and the declared member name. Constructors use `#ctor`/`#cctor`,
generic methods append their arity as `` ``N ``, and operators use their metadata
name (conversions also append `~<written-return-type>`). Nonempty parameter lists
contain the written type syntax; by-reference parameters append `@`. Trivia and
`global::` are removed, built-in keywords normalize to their fixed CLR names
(`int` → `System.Int32`), and generic arguments, nullable markers, array ranks,
tuples and pointers retain their syntax. Written namespace qualifications and
using/extern alias names are retained, avoiding collisions between overloads such
as `Run(A.Context)` and `Run(B.Context)`. Changing an alias or type spelling can
change an id; restoring/building packages cannot. Bound references use the same
original source declaration, including cross-project and constructed generic
references. These ids replace the old resolution-dependent documentation ids;
old graph files remain readable without migration (schemaVersion stays 1).
Partial types have one canonical node: the first declaration by ordinal file
path and source span. Members in all partial files point to that node. This
means a type's `Path` can differ from a member's `Path`.

Optional `declarations` lists `{path, line, endLine, summary?, summaryLine?}`
for every C# type declaration, in ordinal path/line order, and for emitted
members/handlers where available. Ranges are inclusive, 1-based. The canonical
type's parent/path/line remain unchanged; open a member using its own path,
never by walking to the canonical type's file. Comments belong to individual
declarations. Legacy `summary` remains Roslyn's symbol summary (which may combine
partial documentation); explanations use declaration-local summaries instead.
`summaryLine` locates the XML summary, not the first line of the declaration.
Types' ranges are rebuilt from current per-file results on incremental scans,
including body edits in a noncanonical partial file. Missing declarations mean
unavailable metadata, not proof that the node has no source.

Optional `summarySource` is `{path,line,snippet?}` for module/folder summaries
found in Markdown. It identifies the exact matched document line. Archives are
not used by DescriptionScanner; a summary is an author's assertion, not verified
current behavior. Explanation sources also retain a content hash for these texts.

`SubKind` for C# types is `class`, `record`, `record struct`, `struct`,
`interface`, `enum`, or `delegate`; members are `method`, `property`, `ctor`,
`event`, non-private `field`, or synthetic `handler` (operators/destructors are methods). Private
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
| `reference` | finest available member/handler/type/file → member/type | bound simple-name occurrences; also builder/enclosing handler → registered handler |
| `call` | finest available member/handler/type/file → member/type | bound invocations and explicit/implicit constructor calls |
| `dispatch` | interface/base member → possible implementation/override member | resolved implementation/override declarations (normally 1) |
| `inherit` | type → base type; overriding member → overridden member | base declarations; interface → interface also inherits |
| `implement` | type → interface; implementing member → interface member | base-list interfaces and resolved member implementations |
| `import` | file → file | resolved JS/TS/Python import/re-export occurrences |
| `cochange` | file ↔ file | commits touching both files in the requested git window |
| `mentions` | roadmap item/doc → mentioned code/file/module | unique explicit linked targets per item/doc, plus decision commit-file matches |
| `dbRead` | file/type → table | SQL `FROM`/`JOIN` occurrences in a file containing `SELECT` |
| `dbWrite` | file/type → table | SQL `INSERT INTO`, `INSERT OR ... INTO`, `REPLACE INTO`, `UPDATE`, `DELETE FROM` occurrences |

C# emits each observation at the **finest available level**, without parallel
file/type copies. A method/property/field or nested synthetic handler is the
source when emitted; otherwise it falls back to the containing type, or the file
for top-level code. A mapped target member is preferred to its containing type.
With members disabled, type/file fallbacks remain. Handler registration uses
`reference`, and does not mean the callback was executed. Calls inside a handler
belong to that handler. Inherit/implement member edges point from the
implementation to the contract. An additional `dispatch` edge points from that
contract to each scanned implementation/override (including explicit interface
implementations, inherited implementations, properties, indexers and events). Dispatch denotes a possible
indirect runtime target, not an observed call; a virtual override chain can be
followed transitively. No external library endpoint is invented. The viewer lists
dispatch as “возможная реализация” and includes it in code-relation filters.

Roll up original edges once, resolving endpoint files from each node's actual
`path` (especially partial members), with containment only as a fallback.
Old graphs are not guaranteed to use the same edge granularity; inspect their
edges rather than assuming parallel representations or synthesizing duplicates.
The DB scanner separately emits file/type forms; these must not be double-counted.
Call target simple names are excluded from the simple-name reference pass;
references in arguments/receivers remain independent observations. No dependencies
are inferred for unresolved packages without repository endpoints. Missing
packages do not remove declarations. Semantic evidence is capped at three
locations; git co-change evidence is capped at five.

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
nodes are added; resolved registrations also appear in optional `views.entryPoints`.

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
tags. Members additionally carry `callback` (an override or interface
implementation of a library member: the framework calls it, so no incoming edge
does not mean dead code), `interop` (a field of a `[StructLayout]` struct that
mirrors a native layout) and `handler`. A **handler** is its own member node
(`subKind: handler`) for a lambda wired to a control — an argument of a call
that also passes a string literal (`Ui.TextButton("Up", "Наверх", () => …)`) or
the right side of `x.Click += (…) => …`. Typed delegate arguments (method groups,
delegate variables, lambdas) also cover factories with computed string arguments,
target-typed `new(...)`, timer constructors/helpers and `AddHandler(...Event, ...)`.
This is a bounded source heuristic, not proof of runtime UI reachability. Its `Name`
is the call plus the control's text («TextButton «Up · Наверх»»), and its parent
is the declaring type. Its id is the containing member's id plus
`#h:<event|argument>:<registration-target>:<ordinal>`. The target is the written
event expression or factory/constructor expression with preceding arguments,
normalized without trivia/global qualification; preceding lambdas use the fixed
placeholder `callback`. Only registrations with the same kind and full target
share an ordinal (starting at 1); unrelated handlers, file ordering,
line shifts and callback body edits do not renumber it. Identical registrations
remain distinguished by occurrence order. Targets are not truncated in ids.
Legacy `#h<n>` graphs remain readable; no reader parses either suffix format.

Handler metadata (all optional, graph schema remains 1):

- `Anchor` (JSON `anchor`) is the owner member id + `|` + the declared control
  name, otherwise the control variable/field name or label. It is independent of
  the handler ordinal and preserves full text even when `Name` is truncated.
  Factory name parameters, `Name` initializers and transparent fluent `.Named()`
  chains supply the name when known. Dynamic names retain their source expression.
  Several callbacks on the same control can share an anchor: consumers must treat
  collisions as ambiguous, not silently select one. Renaming the owner/control
  changes it. Other nodes have null anchors. `WriteAsync`, `Serialize` and
  `SerializeCompact` retain it; old graphs without it still read; GraphDiff
  compares it even with `IgnorePositions`.
- `library-only`: the nonempty callback uses at least one resolved external
  symbol and no repository symbol. Inherited library calls such as `Close()`
  qualify; repository fields/methods/types, supplied delegate behavior and
  unresolved symbols prevent classification. Empty/literal-only bodies do not
  qualify. Such handlers must not be reported as empty/dead merely because they
  have no outgoing repository edges (`tools/analysis/reach.py` excludes them from
  its dead-code candidates and reports their count separately).
- `not-shown`: a locally constructed control or private field has no use that
  could attach or expose it. Property setup and event wiring alone do not show
  a control. Passing it as an argument, assigning it as another object's
  property/content/child, adding it to a collection/initializer, returning it,
  aliasing it or any unknown use suppresses the tag. Public fields, properties,
  parameters, opaque factories and uncertain construction are skipped. Private
  field uses across partial files are checked on every scan, including incremental
  body edits. Absence of this tag does not prove visibility; it does not evaluate
  visibility conditions or whether an ancestor is ever shown.

Unknown languages omit `Language`. Languages are detected from extensions,
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

**Timeline.** Optional `views.timeline` has `coverage`, `files` (file node ids),
`commits` (`id`, `at`, `subject`, `files` indexes into that file array), and
`stages` (`key`, `title`, `status`, `commitIds`, `link`). Commits are newest
first; `at` uses the git committer timestamp. A stage link is `explicit` when a
document line names that one stage and a commit hash, otherwise `subject` when
its key occurs in a commit subject, otherwise `none`. Explicit links take
precedence. Only today's tracked/scanned paths are represented, within the scan's
history limits; this is file activity, not a historical snapshot or evidence of
every member's behavior. Timeline is absent when no commits are available.

## Explanations and provenance (optional views extensions)

Graph schema remains 1. `views.explanations` has its own `schemaVersion: 1`,
`language: "ru"`, `ruleSet: "builtin:explanations-v1"`, `nodes`, `sources`,
and `nameIndex`. ExplanationScanner runs **after views** on every scan; overrides
and current content hashes are read again even when source-analysis results are
reused. Modules, folders, files, types and members (including handlers) get
profiles. Synthetic groups, table/doc aliases and roadmap entries do not.
Original node names, ids, summaries and containment are never renamed.

A node profile contains `nodeId`, `displayName`, `description`, and optional
additional `claims` (omitted when empty). Every text is an independent claim:

```json
{
  "nodeId": "type:module:Demo.csproj:Demo.SceneRenderer",
  "displayName": {"text":"Отрисовка · Сцена","confidence":"guessed","sourceIds":[0,1]},
  "description": {"text":"Именованный блок кода объединяет связанные данные и действия.","confidence":"derived","sourceIds":[2]},
  "claims": [{"text":"В комментарии автора: Draws a scene.","confidence":"known","sourceIds":[3]}]
}
```

Confidence is `known` for quoted author comments, Markdown assertions, literal
labels or explicit owner corrections; `derived` for declarations, registration,
structural classification and file activity; `guessed` for name translations,
weak tier assignments and candidate feature membership. It is never one rating
for the whole node. `known` means the text was found, not that its asserted
behavior was executed or validated. Guessed purpose descriptions explicitly say
«Предположительно по имени». Unrecognized words/abbreviations remain unchanged.
No RP/XYZ expansion is invented. There is no automatic translation of arbitrary
English prose: retain it as a separate known author claim, with a Russian
structural explanation. Properties, fields, constructors, events, interfaces and
enums get distinct structural templates. The TS/JS `function` fallback explicitly
warns that exported data can have that subkind and local callbacks are unparsed.

`sourceIds` are zero-based indexes into the shared `sources` array, local to
this explanations section. A source has `kind` and optional `path`, `line`,
`endLine`, `sourceHash`, `commitId`, `ruleId`, `reference`, `text` (nulls omitted).
All source paths are repository-relative and lines 1-based. Kinds:

- `comment`: declaration-local summary text, exact summary location and SHA-256
  of the UTF-8 decoded full source text (same hashing as incremental ScanState).
- `markdown`: exact matched document line/snippet and its content hash.
- `handlerLabel`: registration location/hash and `reference` to the entry id;
  inspect that entry's `labelKind` before treating text as a literal.
- `dictionary`: versioned word rule (`name:<word>`) or `unknown-name` fallback.
- `ownerDictionary`: scoped translation, with `reference` = `owner` or the
  project rules path; its application to code purpose remains a guess.
- `declaration`: `reference: "self"` resolves to the profile's original graph
  node and its `path`, `line`, `declarations`; shared across profiles, not an
  independent global location.
- `tier`: `reference` = file node id in `views.assignments`; the complete
  original evidence remains there, with the rule/reason also given here.
- `feature`: `reference` = feature id; consult `views.memberships` for this
  profile's membership evidence. A manually renamed group does not by itself
  confirm an automatic membership.
- `git`: commit hash/subject, shared across files; `reference: "views.timeline"`.
  Used only for file activity within the existing history window, never to
  explain the current behavior of each method.
- `owner`: exact correction text, selector in `ruleId`, origin in `reference`.

All referenced sources are transmitted; indexes are deterministic for identical
scan input but are not persistent ids across scans. Comments from all partial
declarations are retained without selecting a universal purpose from whichever
declaration comes first. A file only borrows a declaration's comment as a
labelled claim when the filename matches that declaration's exact name or a
`Name.Part` form; it never takes the first helper type's summary as file purpose.

`nameIndex` maps case-folded original/Russian names, identifier words, paths and
labels to arrays of **profile indexes into explanations.nodes**. It is complete,
independent of drawing limits, and may return multiple nodes. It is not a new
identity map. Names can be used for substring search; use node ids for selection.

**Entry registrations.** Optional `views.entryPoints` entries have `id`, `path`,
`line`, optional `endLine`, `anchor`, `controlName`, `label`, `labelLine`, `labelKind`
(`literal | expression`), `event`, `detection`, `handlerIds`, and `ambiguous`
(omitted when false). C# entries use `entry:<handler-id>` and detection
`csharp-delegate-argument` or `csharp-event`; XAML entries use a path/line/event/
target id and `xaml-attribute`. Factory parameter names distinguish technical
control names from captions; unresolved factories use the last preceding string
as a bounded convention. Dynamic labels remain source expressions. XAML takes
Content/Text/Header as its caption and stores the event/Command registration.
`labelLine`, when available, opens the caption itself when it is on another line
than the registration; the handlerLabel explanation source uses that line.
Only already resolved XAML registrations are included. C# entries sharing an
anchor are ambiguous; none is silently selected. Entry ids can change with
line/handler ordinals; stable corrections use an unambiguous C# anchor.
This metadata is source observation, not proof of UI visibility/reachability.

**Owner explanations.** Existing `ViewsOverrides` now also accepts
`explanations: [{nodeId?, anchor?, displayName?, description?}]` and
`explanationWords: {"XYZ":"project-specific wording"}`. The existing AppStore
load/save and CLI `--views-overrides` persist the same extended record, under
`views/<root-hash>.json`; no scanned repository is written. Project corrections
precede owner corrections; later entries win. Ordinary nodes use exact ids.
Synthetic C# handlers require an anchor resolving to exactly one node; a
handler's ordinal id alone is rejected as stale, as are missing/ambiguous
selectors. When an anchor is present it takes precedence over nodeId, allowing
the ordinal to change. Stale corrections appear in `views.staleOverrides` with
`explanation:` prefix. Corrections rename only explanation texts and keep
automatic evidence; they do not upgrade other claims' confidence.
`projmap-cli diff` includes explanation texts, markers and meaningful provenance;
`--ignore-git` omits git claims, and `--ignore-positions` omits declaration/source
positions. Whole-file hashes are not diff keys, since an unrelated line edit can
change them without changing a claim.

**Serialization.** `Serialize`, streamed `WriteAsync` and `SerializeCompact`
retain all metadata. Plain and streamed explanations use the object shape above.
Compact graphs additionally use `views.explanations.compact: 1`, a shared
`texts` string array, and tuple profiles:

```text
nodes: [ [graphNodeIndexOrId, displayClaim, descriptionClaim, extraClaims?], … ]
claim: [textIndex, confidenceIndex, sourceIds]
confidenceIndex: 0 = known, 1 = derived, 2 = guessed
sources: [ [kindTextIndex, pathTextIndex?, line?, endLine?, sourceHashTextIndex?,
            commitIdTextIndex?, ruleIdTextIndex?, referenceTextIndex?, textIndex?], … ]
```

Source tuples omit trailing null slots (internal null slots remain); all text
indexes refer to the same `texts` array, sharing repeated paths/hashes/words.
`nameIndex` retains its object shape. Source and profile indexes retain their
ordinary domains. Empty extra claims are omitted. Repeated descriptions occur once. This is
separate from compact graph edge tuples; neither source nor search indexes are
graph node indexes. `GraphJson.Deserialize` reads old/plain/compact graphs and
expands both tuple formats, recalculating compact edge strengths from weights.
Absent extensions stay null; consumers can provide neutral old-graph fallbacks.
Coverage DTOs, edge explanation profiles, new TS/React/Prisma analysis, navigation,
and later timeline/problem refinements are outside novice steps 1–3.

## Problems (optional `problems` section and `report` command)

`problems` is derived after the final views assignments (including project and
owner overrides) and metric aggregation, on every full and incremental scan.
Older graphs may omit it. All three graph serializers include it when present;
it does not change graph nodes, edges, or the graph schema version. Its own
`schemaVersion` is currently 1.

The section contains `items`, `counts` (all five kinds, including zeros),
`cycleLimit` (100 per cycle kind), and `hotFileLimit` (20). Counts describe all
candidates **before** caps, so they can exceed the number of listed items.
Each item has `kind`, `nodeIds`, a short Russian `reason`, and `severity`
(`warn` for candidates/cycles/violations, `info` for hot files). Tier violations
also have `rule` and `edges`; hot files have a 64-bit integer `score`.

| Kind | Detection and ordering |
| --- | --- |
| `deadCode` | Type/member candidates with no incoming `call`, `reference`, `inherit`, `implement`, `xamlEvent`, or `binding`; ordinal node id order. |
| `moduleCycle` | Strongly connected components of module-to-module `projectRef` dependencies. |
| `fileCycle` | Strongly connected components after mapping code edge endpoints to their files; includes `import` as well as the six code kinds above. |
| `tierViolation` | Forbidden dependencies grouped by rule and ordered source/target file pair; ordinal rule, source id, target id order. |
| `hotFile` | Files with positive commits and complexity, ranked by `commits × complexity` descending, then ordinal file id; first 20. |

Dead-code findings are conservative **candidates**, not proof that deletion is
safe. Exclude `callback`, `interop`, `handler`, `library-only`, `not-shown`,
`entrypoint`, `test`, `generated`, `tool`/`tools` tags (also on ancestors),
`subKind: handler`, `Main`/`Program`, entry-point files, and code in `tests/` or
`tools/` directory segments. All code in test projects is excluded, including
their public API; visibility is not stored by the graph, so private helpers are
excluded too. File tags also cover partial declarations in other files.

The reachability guard ports `tools/analysis/reach.py`: excluded nodes are
conservative roots (including test/tool/generated entries and library hooks),
follow the six code kinds, promote reached members to their containing types,
activate callbacks on reached types, and follow member `inherit`/`implement`
in reverse from contracts to implementations. Declarations in an entry-point
file are roots, including top-level entry files. Reached nodes are never dead
candidates. A type with a referenced member is also considered used.
Disconnected components whose nodes have incoming code edges are not listed as
dead code by this zero-incoming criterion. `mentions`, `cochange`, package and
project dependencies are not incoming code usage. File-only imports do not
prove use of individual TS/JS/Python declarations.

Cycles exclude self/file-internal edges and components with one node. Each
component lists ordinal-sorted node ids, not every distinct circular path.
Components are ordered by size descending, then first node id, capped separately
at 100 module and 100 file components. Endpoints use their actual `Path` (so a
partial member uses its own file), with containment as a fallback. Parallel
type/member/file representations create only one adjacency, not extra cycles.

Builtin tier rules are deliberately limited to:

- `domain-dependency`: `domain` must not depend on `frontend`, `application`, or `backend`.
- `data-frontend`: `data` must not depend on `frontend`.
- `production-tests`: a non-`tests` source file must not depend on `tests`; a source tagged `test` is excluded from this rule.

Use the **final** assignment even when it is a guess; mixed/unknown tiers do
not imply a concrete tier for domain/data rules. A non-test source with an
unknown tier still qualifies for `production-tests`. No renderer-specific
infrastructure restriction is inferred. Edges without both assigned endpoint
files are skipped. Direction matters; allowed reverse directions are not
violations. Each violation includes every contributing original code/import
edge, ordered by edge id, with original weights and evidence; empty evidence
paths are expanded from the source node for standalone report use. Evidence
retains the scanner's existing per-edge cap; no additional cap is applied here.

Hot files use the existing scan's git history window (default 12 months, at most
3000 commits). A high score is a review priority, not a defect. No git history or
zero complexity yields no hot-file item. Graph diffs continue to compare their
existing factual/views sections; derived problems are not a separate diff section.

```text
projmap-cli report <root> [-o <file.json>] [--md <file.md>] [--views-overrides <file>]
```

`report` scans the repository and writes a standalone `ProblemsInfo` JSON
document (the same shape as `graph.problems`). Without `-o`, JSON goes to stdout;
with `-o`, stdout contains the output path. `--md` additionally writes a Russian
Markdown report with totals, shown counts, node ids, locations and violation
evidence. JSON and Markdown destinations must differ. Progress and scanner
warnings go to stderr. Markdown also lists scanner warnings. Common scan
options `--git-months N` and `--no-members` apply; omitting members limits
dead-code detection to the declarations still present. The command creates no
default `.projmap/graph.json` and writes only explicitly requested files. Use
output paths outside the scanned repository for a read-only audit. Findings
return exit code 0; invalid arguments/scan or output errors return 1,
missing command/root arguments return 2, cancellation returns 130.
