# Desktop E2E

Node 22, PowerShell, .NET 10 и два монитора. Harness использует только встроенные `fetch`/`WebSocket` и CDP, без npm-пакетов. Публикуйте отдельную копию приложения; `src/ProjMap.App/bin` запрещён, поскольку viewer может собираться параллельно.

```powershell
$scratch = 'C:\Users\Leshe\AppData\Local\Temp\claude\C--Projects-Map\2a840339-9a92-4e96-9cb6-e8811e371f66\scratchpad'
dotnet publish src/ProjMap.App/ProjMap.App.csproj -c Release -r win-x64 --self-contained false -o "$scratch\e2e-app" --artifacts-path "$scratch\e2e-build"
node tools/e2e/run.mjs --exe "$scratch\e2e-app\projmap.exe" --root 'C:\Users\Leshe\.codex\worktrees\stage10f-fonts-bindings\Map' --port 9333 --out tools/e2e/results
```

По умолчанию выполняются cold и warm прогоны (`--runs 1` — только cold). `--budget-seconds 600` ограничивает сценарии и исследование DOM на один прогон. Проверка экранов предшествует запуску; после запуска и каждую секунду проверяется прямоугольник окна Win32. Пересечение с primary немедленно завершает процесс. Снимки — только CDP viewport. Уже открытый projmap и занятый порт блокируют запуск.

Harness временно сохраняет и восстанавливает кеш выбранного проекта, `recent.json` и профиль `webview-test`; сканируемый репозиторий не изменяется. Он завершает только своё дерево процессов. Итог: `report.md`, `report.json`, PNG; Markdown также копируется в `$scratch\e2e-report.md`. В JSON находятся все состояния, 250-мс CDP heartbeat, секундные Win32-пробы, исключения, предшествующие действия и сведения о покрытии. Код завершения: 0 — без FAIL, 1 — измеренные FAIL, 2 — неполный прогон/ошибка harness. Отсутствующие элементы и исчерпание бюджета раскрываются в coverage; отчёт не означает перебор всех файлов большого дерева.

Проверка протокола без открытия окон: `node --test tools/e2e/self-test.mjs`. Проверяются перепутанный порядок ответов, таймаут, поздний ответ, разрыв соединения, точный текст исключения, доступ к лексическим глобальным переменным и отказ от клика по перекрытому элементу. При отсутствии второго активного Win32-монитора оба прогона получают BLOCKED до запуска приложения: `MainForm.cs:45` иначе допускает fallback на primary.
