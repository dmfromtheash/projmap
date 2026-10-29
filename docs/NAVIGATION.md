# Общая навигация web viewer (шаг 5)

`model.js` → `projection.js` → `navigation.js` загружаются обычными script-тегами.
Глобальные классы: `GraphModel`, `GraphProjection`, `NavigationState`. Те же классы
экспортируются через CommonJS для Node. Новые файлы уже входят в существующий
`EmbeddedResource Include="web\**\*"`. DOM и отрисовка для нового API не нужны.
Подключение действующих представлений к этому API — следующий визуальный шаг.

## NavigationState

```js
const model = new GraphModel(graph);
const projection = new GraphProjection(model);
const nav = new NavigationState(model); // Начальная область — systemId.
const unsubscribe = nav.on('change', ({ reason, state, cameraByView, status }) => {
  // Представление читает state; изменения идут через методы nav.
});
nav.go({ selectedId: memberId, level: 'code', filters: { excludeTags: ['test'] } });
nav.setCamera('links', { x: 100, y: 200, zoom: 1.5 });
nav.setView('tree');
nav.back();
```

`state` — копия общего состояния. `selectedId` — один точный ID из graph.nodes,
либо null. ID фигуры, feature/tier/role и entryPoint не являются выбором узла:
для входной кнопки выбирается один из её исходных `handlerIds`.

- `scope` — текущая область: канонический ID либо ID тематической группы.
- `route`: `location | purpose`; `level` — выбранный потребителем смысловой уровень.
- `featureId`, `role`, `tierId`, `timeFilter`, `filters` — общие для всех видов.
- `filePath` — фактический файл выбора, в том числе выбранная часть partial-типа.
- `scopeFilePath` — отдельный контекст файла области, когда выбор находится снаружи.
- `selectedLink`: `{source, target, kind}` исходного ребра либо null. Агрегированная
  линия сначала раскрывается в исходные отношения через `linksBetween`.
- `view` — имя текущего вида; `routePath` — готовые ступени до области;
  `location` сохраняет путь, строку и предков для восстановления после скана.

`go(id, reason = 'go')` выбирает узел и делает его областью. Объект-аргумент
задаёт частичное обновление состояния; `id` — сокращение для `selectedId`.
Если передан selectedId без scope, областью становится этот узел. Для выбора
без изменения области передайте оба поля. `go({selectedId: null})` снимает
выбор и сохраняет область. При переходе к другому узлу прежняя selectedLink
снимается, если новая связь не передана явно. Все значения состояния и камер
должны быть JSON-совместимы; фильтры передаются целым объектом.

`go` и `setView` сохраняют предыдущий экран в истории. `canBack` — getter;
`back()` восстанавливает выбор, связь, область, фильтры и камеры, возвращает
статус; при пустой истории — null. `up()` идёт по routePath и тоже записывается
в историю; на верхней ступени возвращает null. `setCamera(view, camera)` не
создаёт записи истории; `cameraFor(view)` и `cameraByView` возвращают копии.
`on` возвращает функцию отписки. `emit(event, payload)` уведомляет слушателей
копиями данных. Смена вида сама по себе не меняет выбор, путь или фильтры.

```js
nav.go({ scope: 'feature:export', selectedId: null }); // Включает purpose и featureId.
nav.go(moduleId); // Сохраняет возможность в маршруте и фильтрах.
// «Показать всю папку»: явно снять тематическое ограничение.
nav.go({ selectedId: folderId, route: 'location', featureId: null, role: null,
  tierId: null, filters: {} });
```

`restore(new GraphModel(newGraph))` возвращает `{status: 'ok'|'missing',
selectedId, path, missingIds, missingPart, missingScopePart, missingLink,
suggestedFolderId, suggestedFolderPath}` и посылает change с причиной restore.
Пропавший ID остаётся в состоянии: это потерянный выбор, а не другой одноимённый
объект. Исчезнувшая часть partial-типа или выбранная связь тоже сообщается.
Предложение — ближайшая существующая папка из старого пути; если её нет, null.
Принять предложение можно отдельным `go(status.suggestedFolderId)`. История
сохраняется: Back к удалённому узлу снова возвращает missing, без подмены.

## GraphProjection и дополнительные индексы GraphModel

`children(scope, options)` возвращает непосредственные ступени;
`routeTo(id, options)` — весь путь. В options можно передать nav.state.
Маршрут purpose: программа → feature/role/tier → **настоящие** модули → папки →
файлы → типы → члены. Маршрут location пропускает тематическую ступень.
У экспортированной функции TS без класса после файла сразу идёт member.
`purposeGroups()` даёт группы; feature использует исходный ID, роли и ярусы —
обозначения `role:<role>` и `tier:<tier>`, пригодные только для области/маршрута.
Тематические группы никогда не записываются в parentId.

Ступень имеет `{id, kind, subKind, name, path, line, shownFiles, totalFiles, partial}`.
У папок счётчики включают вложенные папки: показано shownFiles из totalFiles
**уникальных файлов**. `filesOf(id)` даёт физические файлы объекта/контейнера;
`fileSet(options)` — общую выборку. Пересечение тематических групп не удваивает
файлы. `filters.featureIds` объединяет возможности; отдельный featureId имеет
приоритет. Поддержаны `filters.fileIds`, `filters.excludeTags`, role, tierId и
`timeFilter: {from?, to?, timezone?}`. Границы времени включительные, передавайте
ISO-время со смещением/UTC; timezone сохраняется как контекст потребителя.
Временной фильтр выбирает текущие файлы по views.timeline, а не исторический код;
без timeline выборка активности пустая. Прочие фильтры сохраняются навигацией,
но не интерпретируются проекцией. `linksOf` всегда даёт полный список отношений.

- `actualFileOf(id)` в модели и проекции возвращает ID фактического файла либо
  null. Реализация одна — в GraphModel: собственный path/объявление, затем
  дерево только при отсутствии собственного пути. Member partial-класса
  открывается в своём файле. Старый `fileOf()` оставлен для совместимости;
  следующим визуальным шагом все views следует перевести на actualFileOf.
- `locationOf(id, {filePath?})` → `{module, folders, file, path, line}`; module/file
  — исходные узлы либо null, folders — узлы от внешней к внутренней папке.
  Чужой filePath даёт null. Путь и строка относятся к объявлению, не к фигуре.
- `thisPartInFile(typeId, filePath)` → `{typeId, file, declarations, members, types}`.
  `wholeBlockAllFiles(typeId)` → `{typeId, files, declarations, members, types}`.
  members/types — непосредственные дети. Канонический ID типа сохраняется во
  всех частях. Отсутствие declarations в старом графе ограничивает сведения о
  частях; пустой список не доказывает отсутствие кода или локальных действий TS.
- `linksOf(id, {direction: 'in'|'out'|'both'})` → `[{kind, relations}]`.
  Каждый relation содержит исходный id/kind/weight/strength (если переданы),
  `source` и `target` с полными id/name/displayName/path/line, все доступные
  evidence `{path, line, snippet}`. Пустой path основания раскрывается в путь
  источника. Никакого лимита партнёров/оснований поверх данных сканера нет.
- `linksBetween(sourceContainer, targetContainer, {direction = 'out'})` раскрывает
  агрегат в тот же формат. Контейнером может быть модуль, папка, файл, тип или
  тематическая группа. Каждое исходное ребро попадает в результат один раз.
  call/import/cochange и прочие виды остаются отдельными группами, общей «силы»
  не создаётся. cochange ненаправлен и доступен с обеих сторон при любом direction.

Аддитивные индексы модели: canonicalIds, realParent/realChildren (без старой
виртуальной папки корневых файлов), declarationsByPath/declarationsByNode,
nodesByFile, membershipsByNode/membershipsByFeature, featuresById/tiersById,
assignmentsByNode, sources/explanationsById, nameIndex (строка → Set канонических
ID), allEdges/edgesByKind/relationsOf. `sourcesFor(claim, nodeId)` раскрывает
источники, включая декларацию `reference: self`. Принимаются обычные и compact
объяснения с таблицей texts. Старые byId/children/edges/edgesOf/fileOf сохранены.
Новую проекцию создавайте для новой модели; кэш файлов принадлежит её снимку.

## Проверка

Без внешних данных: `node --test tests/web`. Реальные тесты явно пропускаются,
если переменные с путями к сканам не заданы; JSON исходников не хранится в git.

```powershell
dotnet build -c Release
$env:PROJMAP_HOME = Join-Path $PWD '.tmp-home'
dotnet src/ProjMap.Cli/bin/Release/net10.0/projmap-cli.dll scan C:\Projects\Map -o .tmp-home/map.json
$env:PROJMAP_MAP_GRAPH = Join-Path $PWD '.tmp-home/map.json'
node --test tests/web
dotnet test
```

Если существует `C:\Projects\ensi-bot`, так же сканировать его с явным `-o`
внутри .tmp-home и задать PROJMAP_ENSI_GRAPH. После проверки удалить .tmp-home
и убрать переменные окружения. Сканируемые репозитории и обычный app-store не
используются для записи.

Проверено 2026-10-29: Map **20 333 узла, 127 032 связи, 0 предупреждений**.
Отличие от отчёта шагов 1–3 — только mentions: 6 634 вместо 6 633; остальные
категории совпадают. C#-сканер не менялся. Старого JSON для установления точной
добавленной связи нет. EditorViewModel: 70 файлов, 2 057 непосредственных членов;
PosterExport: 2 члена. 2 146 вхождений файлов в 24 возможности дают 504 уникальных
файла. Полный список EditorView содержит 2 684 отношения. C:\Projects\ensi-bot
отсутствует; различимость одинаковых route.ts дополнительно покрыта fixture.
Итог: 23 автономных web-проверки и 6 проверок Map прошли; проверка ensi пропущена.
Все 177 .NET-тестов прошли. Release-сборка успешна с прежним MSB3277 WindowsBase,
который также был на начальной сборке до изменений.
