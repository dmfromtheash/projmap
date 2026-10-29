# Desktop E2E

Node 22, PowerShell, .NET 10 и два монитора. Harness использует только встроенные `fetch`/`WebSocket` и CDP, без npm-пакетов. Публикуйте отдельную копию приложения; `src/ProjMap.App/bin` запрещён, поскольку viewer может собираться параллельно.

```powershell
$env:PROJMAP_SCRATCH = '<папка для временных файлов>'
dotnet publish src/ProjMap.App/ProjMap.App.csproj -c Release -r win-x64 --self-contained false -o "$env:PROJMAP_SCRATCH\e2e-app" --artifacts-path "$env:PROJMAP_SCRATCH\e2e-build"
node tools/e2e/run.mjs --exe "$env:PROJMAP_SCRATCH\e2e-app\projmap.exe" --root 'C:\Users\Leshe\.codex\worktrees\stage10f-fonts-bindings\Map' --port 9333 --out "$env:PROJMAP_SCRATCH\e2e-out"
```

По умолчанию выполняются cold и warm прогоны (`--runs 1` — только cold). `--budget-seconds 600` ограничивает сценарии и исследование DOM на один прогон. Проверка экранов предшествует запуску; после запуска и каждую секунду проверяется прямоугольник окна Win32. Пересечение с primary немедленно завершает процесс (`--allow-single-display` — когда подключён только экран ноутбука). Снимки — только CDP viewport. Занятый порт блокируют запуск.

Окно под тестом хранит всё своё состояние (кеш, `recent.json`, профиль WebView) в `%PROJMAP_SCRATCH%\e2e-home` через `PROJMAP_HOME`, поэтому открытое окно владельца не мешает и не меняется; внутри этой папки harness сохраняет и восстанавливает кеш между cold и warm. Сканируемый репозиторий не изменяется. Он завершает только своё дерево процессов. Итог: `report.md`, `report.json`, PNG; Markdown также копируется в `$scratch\e2e-report.md`. В JSON находятся все состояния, 250-мс CDP heartbeat, секундные Win32-пробы, исключения, предшествующие действия и сведения о покрытии. Код завершения: 0 — без FAIL, 1 — измеренные FAIL, 2 — неполный прогон/ошибка harness. Отсутствующие элементы и исчерпание бюджета раскрываются в coverage; отчёт не означает перебор всех файлов большого дерева.

Проверка протокола без открытия окон: `node --test tools/e2e/self-test.mjs`. Проверяются перепутанный порядок ответов, таймаут, поздний ответ, разрыв соединения, точный текст исключения, доступ к лексическим глобальным переменным и отказ от клика по перекрытому элементу. При отсутствии второго активного Win32-монитора оба прогона получают BLOCKED до запуска приложения: `MainForm.cs:45` иначе допускает fallback на primary.
