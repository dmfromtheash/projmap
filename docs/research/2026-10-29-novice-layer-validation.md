# Novice data layer verification — 2026-10-29

Scope: steps 1–3 only, worktree `codex/novice-data`, starting HEAD `9084153cf5af9dc0082713a4b9397196a98b49c1`. No commits, stash, GUI, viewer edits, or writes to Map/ensi/projmap.

## Regression and baseline qualification

`RefreshNotShown → IsConstruction` resolved a source factory through a `CompilationReference`, then requested its syntax tree from the caller compilation. Roslyn throws `SyntaxTree does not belong to compilation`; the C# stage exited before merging its already collected semantic edges. A field initializer in another partial file also needs its own tree model.

`SourceModel` checks tree ownership in the current compilation and referenced compilations (with a visited set), then obtains that owner’s semantic model. Unknown owners leave the visibility heuristic undecided. The regression fixture uses two projects, a cross-project control factory, and a handler calling the other project. Both local-variable and separate-partial-field variants assert no warnings, cross-project calls, and incremental/full equivalence.

The prescribed clean baseline was created using `git worktree add --detach %TEMP%/pm-base HEAD`, built and scanned without source changes first. **That pristine HEAD itself reproduces the same exception**: 20,333 nodes, 6,948 edges, one warning. The incoming partial work produces the same broken totals on today’s Map snapshot. Therefore the healthy ~124k unmodified baseline mentioned in the request cannot be reproduced from this HEAD. The local `main` ref is only scaffold commit `80282f3`, not this analyzer; it was not substituted for the explicitly requested HEAD.

An additional, explicitly qualified baseline was built in that temporary worktree with **only** the `IsConstruction`/`SourceModel` repair (21 added lines, one removed line), without any novice fields/scanners. It produces 127,031 edges and no warnings. Every node ID and every complete edge object (kind, ID, endpoints, weight, strength, evidence) matches the completed implementation. The temporary source repair was then restored to HEAD. The worktree and temporary scan graphs were removed after verification.

## Checks

- `dotnet build ProjMap.slnx -c Release`: success, 0 errors; one pre-existing MSB3277 WindowsBase/WebView2 conflict warning, reproduced by the clean HEAD full solution build.
- `dotnet test tests/ProjMap.Tests -c Release`: **177 passed, 0 failed, 0 skipped**; 35 ExplanationTests cases, including two compilation-ownership regressions.
- `git diff --check`: clean; no changes under `src/ProjMap.App/web`.
- All six captured real graphs round-trip their nodes, edges and views through compact serialization without loss.
- Fixture coverage: independent confidence claims; declaration-local partial summaries and ranges; Markdown path/line/hash; literal/computed labels; technical name distinct from caption; XAML registrations; scoped dictionaries; unknown abbreviations; anchored owner corrections and ambiguity; changed/unchanged incremental scans; old plain/compact graphs; all three serializers.

## Measured graphs

Sizes are bytes on disk for streamed graph.json and bytes returned by SerializeCompact; decimal MB = bytes / 1,000,000. Default scan options include the same 12-month Git window. No owner overrides were injected for the examples.

| Scan | Nodes | Edges | Warnings | Plain bytes | Compact bytes |
|---|---:|---:|---:|---:|---:|
| Map pristine HEAD | 20,333 | 6,948 | 1 | 26,065,078 | 23,223,975 |
| Map incoming partial work | 20,333 | 6,948 | 1 | 54,022,565 | 35,575,939 |
| Map HEAD + compilation repair only | 20,333 | 127,031 | 0 | 109,868,345 | 26,677,692 |
| Map completed steps 1–3 | 20,333 | 127,031 | 0 | 137,906,423 | 38,981,813 |
| ensi pristine HEAD | 2,864 | 22,180 | 1 | 15,981,188 | 4,140,604 |
| ensi completed steps 1–3 | 2,864 | 22,180 | 1 | 19,439,421 | 5,146,952 |

The one ensi warning is unchanged: `Multiple database engines detected: mysql, postgres, sqlite`.

| Edge kind | Map repair-only baseline | Map after | ensi baseline | ensi after |
|---|---:|---:|---:|---:|
| call | 36,242 | 36,242 | 0 | 0 |
| cochange | 231 | 231 | 116 | 116 |
| dbRead | 4 | 4 | 16 | 16 |
| dbWrite | 4 | 4 | 3 | 3 |
| implement | 1,110 | 1,110 | 0 | 0 |
| import | 0 | 0 | 326 | 326 |
| inherit | 31 | 31 | 0 | 0 |
| mentions | 6,633 | 6,633 | 21,676 | 21,676 |
| package | 35 | 35 | 38 | 38 |
| projectRef | 40 | 40 | 5 | 5 |
| reference | 82,701 | 82,701 | 0 | 0 |

final-map: plain growth **28,038,078 bytes (25.5%)**; compact growth **12,304,121 bytes (46.1%)**.

final-ensi: plain growth **3,458,233 bytes (21.6%)**; compact growth **1,006,348 bytes (24.3%)**.

Map has 20,225 explanation profiles and 8,557 shared sources. Compact explanations intern repeated text, paths, hashes and claims; search refers to profile indexes, claims to shared source indexes. Empty optional fields are omitted.

## Produced examples

These are actual output strings, with no hand-written translations substituted. `known` means an assertion/text was found at a cited source; it does not certify behavior. English comments remain separate author claims.

### `file:src/Map.Presentation/ViewModels/EditorViewModel.HexWorkshop.cs`

- Name [guessed]: Редактор · Логика экрана · Гексы · Мастерская
- Explanation [derived]: Файл C# содержит часть блока EditorViewModel; остальные объявления доступны отдельно.
- Author/label claim [known; src/Map.Presentation/ViewModels/EditorViewModel.HexWorkshop.cs:13]: Комментарий к EditorViewModel в этом файле: The workshop tools of the hex category beyond painting tiles: regions, the fog of war, paths, the hex key, the travel ruler, selection with copy / paste / turn, board rows and columns, and levels of the world. Every change is one undoable command on the hex layer.

### `member:module:src/Map.App/Map.App.csproj:M:Map.App.Views.EditorView.BuildPosterAndLayersExport#h1`

- Name [known]: Реакция на «Слои отдельными PNG…»
- Explanation [derived]: Обработчик зарегистрирован в исходнике; условия его запуска и видимость элемента не проверены.
- Author/label claim [known; src/Map.App/Views/EditorView.PlayerPackage.cs:20]: В регистрации указана подпись «Слои отдельными PNG…».

### `type:module:src/Map.Persistence/Map.Persistence.csproj:Map.Persistence.MapProjectStore`

- Name [guessed]: Хранилище · Карта · Проект
- Explanation [derived]: Блок кода объявлен в нескольких файлах; комментарии к его частям приведены отдельно.
- Author/label claim [known; src/Map.Persistence/MapProjectStore.AreaBeautyV30.cs:7]: В комментарии автора: v29 → v30 (R10 «Красота», written in its branch as v27 and renumbered on merge): an area's Beauty and DensityMask (written with the area record), depthSort of an object layer's effects, and tint / groundShadow of a placed asset. All are optional and absent on every older map, which therefore opens unchanged; a map that uses none of them keeps its older schema number.
- Author/label claim [known; src/Map.Persistence/MapProjectStore.LightingV28.cs:6]: В комментарии автора: Light and time of day in the manifest (schema v28, DEC-057).
- Author/label claim [known; src/Map.Persistence/MapProjectStore.StyleV29.cs:6]: В комментарии автора: The style of the map in the manifest (schema v29, DEC-059).
- Author/label claim [known; src/Map.Persistence/MapProjectStore.WaterCliffsV32.cs:6]: В комментарии автора: R12 «Вода и обрывы» in the scene (schema v32): cliff light and mask, river node features.
- Author/label claim [known; src/Map.Persistence/MapProjectStore.cs:13]: В комментарии автора: Reads and writes .mapproj. Save follows the docs/05 contract: serialize the immutable snapshot to a unique sibling temp, read the temp back and validate entries and checksums, flush, and only then replace the target atomically. A failure before the replace leaves the last correct version untouched and is reported as an exception, never as success.

### `type:module:src/Map.Presentation/Map.Presentation.csproj:Map.Presentation.ViewModels.HexKeyRow`

- Name [guessed]: Гексы · Ключ · Строка
- Explanation [derived]: Именованный блок кода объединяет связанные данные и действия.
- Author/label claim [known; src/Map.Presentation/ViewModels/EditorViewModel.HexWorkshop.cs:10]: В комментарии автора: One row of the hex key table: where, the printed hex number and the entry.

### `type:module:src/Map.Rendering/Map.Rendering.csproj:Map.Rendering.SceneRenderer`

- Name [guessed]: Отрисовка · Сцена
- Explanation [derived]: Блок кода объявлен в нескольких файлах; комментарии к его частям приведены отдельно.
- Author/label claim [known; src/Map.Rendering/SceneRenderer.Relief.cs:6]: В комментарии автора: The heights inside a land (R22, ), drawn the way the owner's sheets model ground: a shade on the slopes turned from the light (the same light as the cliffs), a little light on those turned to it, height contours in the slate ink with every fifth heavier, and, where asked, short hachures down the steep slopes. Everything lies inside the land.
- Author/label claim [known; src/Map.Rendering/SceneRenderer.Sea.cs:6]: В комментарии автора: The style of the open sea (R12, ): pen water lines, depth contours, engraved ripples or watercolour depth bands, read from the distance to the nearest shore of every land in view. It lies on the page under the land, so the land's own coast line and shadow stay on top of it; the land's ring surf is left out while a sea style is on, so no line is drawn twice.
- Author/label claim [known; src/Map.Rendering/SceneRenderer.cs:521]: В комментарии автора: The single scene evaluator: the editor canvas and the thumbnail exporter both go through it, so what the user sees and what gets stored cannot drift apart. It never mutates the document.

### `file:apps/web/src/app/api/rp-passport/me/form/route.ts`

- Name [guessed]: Маршрут запросов
- Explanation [derived]: По признакам структуры это файл обработки серверных запросов; конкретные операции требуют отдельного анализа.

### `file:apps/web/src/app/rp-passport/RpHubForm.tsx`

- Name [guessed]: Rp · Центр · Анкета
- Explanation [derived]: По признакам структуры это файл интерфейса; конкретные действия требуют отдельного анализа.

### `member:apps/web/src/components/hud.tsx:HUD_ACCENTS`

- Name [guessed]: HUD · ACCENTS
- Explanation [derived]: Экспортируемое объявление; текущий анализатор не всегда отличает функцию от данных и не разбирает локальные обработчики.

## Representative JSON

Profile from `views.explanations.nodes` (indexes resolve in the shared sources array):

```json
{
  "nodeId": "member:module:src/Map.App/Map.App.csproj:M:Map.App.Views.EditorView.BuildPosterAndLayersExport#h1",
  "displayName": {
    "text": "Реакция на «Слои отдельными PNG…»",
    "confidence": "known",
    "sourceIds": [
      3802
    ]
  },
  "description": {
    "text": "Обработчик зарегистрирован в исходнике; условия его запуска и видимость элемента не проверены.",
    "confidence": "derived",
    "sourceIds": [
      3802
    ]
  },
  "claims": [
    {
      "text": "В регистрации указана подпись «Слои отдельными PNG…».",
      "confidence": "known",
      "sourceIds": [
        3802
      ]
    },
    {
      "text": "Кандидат в группу по назначению «Экспорт».",
      "confidence": "guessed",
      "sourceIds": [
        3803
      ]
    },
    {
      "text": "Кандидат в группу по назначению «Package».",
      "confidence": "guessed",
      "sourceIds": [
        2910
      ]
    }
  ]
}
```

Referenced shared source records, keyed here by their array indexes:

```json
{
  "3802": {
    "kind": "handlerLabel",
    "path": "src/Map.App/Views/EditorView.PlayerPackage.cs",
    "line": 20,
    "endLine": 24,
    "sourceHash": "FF9AC227D482123129B53215DCDAA8E0BA1B0F25C6AA200E86BB36B30C3EE209",
    "reference": "entry:member:module:src/Map.App/Map.App.csproj:M:Map.App.Views.EditorView.BuildPosterAndLayersExport#h1",
    "text": "Слои отдельными PNG…"
  },
  "3803": {
    "kind": "feature",
    "ruleId": "membership:entry",
    "reference": "feature:export",
    "text": "вызывает ExportLayersAsync"
  },
  "2910": {
    "kind": "feature",
    "ruleId": "membership:entry",
    "reference": "feature:package",
    "text": "кнопка в файле этой функции"
  }
}
```

The graph also carries declaration ranges/summaries in `nodes[].declarations`, Markdown locations in `summarySource`, registrations in `views.entryPoints`, and owner corrections in `ViewsOverrides.explanations` / `explanationWords`. Existing AppStore load/save and CLI override loading use that extended record; scanned repositories are never written.

## Files and decisions

- `docs/GRAPH_CONTRACT.md`: actual member/handler-level C# edges, canonical partial types and member paths, Anchor, timeline/problems and optional novice metadata; shared indexes and compact encoding.
- `src/ProjMap.Core/GraphModel.cs`, `ViewsModel.cs`, new `ExplanationsModel.cs`: provenance/registration/explanation DTOs, extended owner overrides, plain/stream/compact preservation and old compact reading.
- `src/ProjMap.Core/GraphDiff.cs`: compare explanation claims and meaningful provenance, honoring git/position exclusions.
- `src/ProjMap.Analysis/CSharpScanner.cs`, `CSharpHandlers.cs`, `XamlScanner.cs`, `DescriptionScanner.cs`: source-local declarations/comments, caption/registration locations and owning semantic models.
- `src/ProjMap.Analysis/ProjectScanner.cs`, `ScanState.cs`, `ViewsScanner.cs`: scan ordering after views, immutable provenance reuse and current registrations/overrides.
- New `src/ProjMap.Analysis/ExplanationDictionary.cs`, `ExplanationScanner.cs`: Russian identifier vocabulary, scoped words, guarded claims, shared sources and bilingual name index.
- New `tests/ProjMap.Tests/ExplanationTests.cs`; existing `HandlerTests.cs`, `ScannerTests.cs`: new rule/regression/compatibility coverage and revised progress/anchor assertions.
- This report retains the measured evidence after deleting the temporary graphs.

## Known gaps

- Exact equality with the **pristine** HEAD baseline is impossible because that baseline has the same C# crash; exact edge equality is verified against HEAD plus only its repair. Today’s Map has 127,031 healthy edges; the previously quoted ~124,000 / ~7,234 totals were not reproduced on this snapshot.
- TS/JS declarations remain the existing lightweight export analysis: no new local callbacks, JSDoc, fetch/HTTP or Prisma call-chain analysis. HUD stays unexpanded, and exported constants are not confidently described as actions.
- English prose is retained, not automatically translated. The examples therefore have conservative structural Russian explanations; project-specific polished wording requires owner overrides.
- Caption extraction uses resolved parameter names and a bounded fallback for legacy helpers; dynamic labels remain expressions. Visibility and successful execution are unverified.
- This is data work only. Viewer changes, GUI/UX verification and runtime behavior checks were not performed. Consumers of compact explanations must expand the documented text/profile/source tuples (Core GraphJson does so).
- Coverage DTOs, per-edge explanation profiles, later timeline/problem refinements and navigation belong to later steps.
- Compact Map growth is about 12.4 MB / 46%; shared source/text tables limit duplication but no stricter size budget was provided.
