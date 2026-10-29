# Аудит связей Map через projmap — заметки 2026-10-29 (Claude)

Скан: `projmap-cli scan C:\Projects\Map` на projmap `ff2ec90`+ (19 738 узлов, 125 959 связей, 14 с).
Обход: `tools/analysis/reach.py <graph.json> <out.json>` — от `src/Map.App/Program.cs` по `call/reference`, плюс
«контракт → реализация» и методы с тегом `callback` у достигнутых типов. Корни «разработки» — всё в `tests/`, `tools/`.

## Итог обхода
- Функций и свойств программы (`src/`): 11 376 с обработчиками; достигнуто 10 899 (≈96 %).
- Не достигнуто 477; из них 230 используют только тесты/инструменты (крючки `…ForSmoke`, тестовые конструкторы).
- **247 не использует никто** — кандидаты; из них 27 — поля структур Windows (`interop`, ложные), 117 свойств
  (часто позиционные свойства record / сериализация — проверять), 73 метода, 8 обработчиков.

## Проверено по коду (настоящие находки)
- `ErrorWindow` / `IPrompts.ShowErrorAsync` — продукт **никогда** не показывает окно ошибки (зовут только заглушки тестов).
- `EditorView.AskGroupNameAsync` — окно «Создать группу» (ввод имени) не вызывается; группа создаётся без имени.
- `EditorView.ShortcutChipRow` — строка подсказок клавиш построена, нигде не показывается.
- `EditorViewModel.CycleSelectionLayer`, `CycleActiveLayerBlend`, `MoveSelectedVectorNode`, `ApplyLandShape(LandShapeKind)`,
  `LocalFontFamilies`; `MainWindow.ActiveShortcutContexts`, `IsTypingInText()`, `NotifyAssetCatalogChanged`; `Ui.PanelHeader`,
  `Ui.ToolButton`; `EditorView.BuildLibraryTile` — нигде не вызываются (вероятно, хвосты перестройки навигации DEC-070).
- `MapIcons.Eraser`, `HexGrid`, `HexBoard`, `HexFootprint`, `Help`, `Filter`, `Clock` — значки без использования.
- `IEditCommand.Name` — у каждой команды правки есть название, программа его нигде не читает.
- Попутно (не projmap): в main нет защиты от неожиданных ошибок сохранения/закрытия — см. Map `archive/stage10d-local-wip`.

## Слепые зоны projmap, закрытые в этой сессии (коммиты)
- 1755acf — `projmap-cli diff`; член → контракт (`implement`) и → переопределённый (`inherit`).
- 7aef85e — единственный кандидат Roslyn при непрочитанной библиотеке (`Closing += OnClosing`).
- 43fada3 — пакеты NuGet из `obj/project.assets.json` (Avalonia и др.); тег `callback` (вызывает библиотека).
- 31936e8 — тег `interop` у полей `[StructLayout]`.
- ff2ec90 (+ подписи) — лямбды-обработчики кнопок как узлы `subKind: handler` с подписью кнопки.

## Ещё открыто
- Обработчики, которые зовут только библиотеку (`cancel.Click → Close()`), выглядят «пустыми» — пометить, а не считать мёртвыми.
- Путь «через интерфейс» выбирает одну из реализаций — показывать как «возможные пути», со всеми реализациями.
- `.axaml` не читается (в Map почти не нужен: интерфейс в C#; для других проектов — нужен).
- TS/JS/Python: только импорты, нет графа вызовов.
- Независимый список кнопок Astra (xhigh): `docs/research/2026-10-29-map-ui-audit-astra.md` — появится, когда она закончит;
  сверить с узлами `handler` (что у неё есть, а у projmap нет — дыры).
