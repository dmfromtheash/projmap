using System.Text.RegularExpressions;
using ProjMap.Core;

namespace ProjMap.Analysis;

/// <summary>Analyzer copy of the plain-Russian glossary, extended without changing the viewer fallback.
/// Translations are naming hints, not evidence of runtime behavior.</summary>
internal static class ExplanationDictionary
{
    internal static readonly Dictionary<string, (string Name, string Hint)> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["core"] = ("Ядро", "основные данные и правила программы; от него зависят остальные части"),
        ["domain"] = ("Предметная логика", "правила и понятия, ради которых существует программа"),
        ["app"] = ("Приложение", "запуск программы, главное окно, сборка всех частей вместе"),
        ["application"] = ("Приложение", "запуск программы и сборка всех частей вместе"),
        ["presentation"] = ("Логика экранов", "что показывает интерфейс и как он реагирует на действия — без самого рисования"),
        ["ui"] = ("Интерфейс", "то, что видит и нажимает пользователь"),
        ["rendering"] = ("Отрисовка", "превращает данные в картинку на экране"),
        ["render"] = ("Отрисовка", "превращает данные в картинку"),
        ["graphics"] = ("Графика", "рисование и работа с изображениями"),
        ["persistence"] = ("Сохранение", "запись проекта в файл и чтение обратно, защита от потери данных"),
        ["storage"] = ("Хранение", "где и как лежат данные"),
        ["export"] = ("Экспорт", "выгрузка результата в картинки и файлы для других программ"),
        ["import"] = ("Импорт", "загрузка данных из внешних файлов"),
        ["assets"] = ("Ресурсы", "картинки, шрифты, наборы объектов и их каталог"),
        ["platform"] = ("Связь с системой", "всё, что зависит от операционной системы"),
        ["windows"] = ("Windows", "особенности Windows: пути, настройки, окна"),
        ["tests"] = ("Проверки", "автоматические тесты — проверяют, что код работает правильно"),
        ["test"] = ("Проверки", "автоматические тесты"),
        ["docs"] = ("Документация", "описания, планы и решения проекта"),
        ["doc"] = ("Документация", "описания проекта"),
        ["tools"] = ("Инструменты", "вспомогательные программы и скрипты для разработки"),
        ["scripts"] = ("Скрипты", "небольшие программы для автоматизации"),
        ["experiments"] = ("Эксперименты", "черновые пробы идей, не часть продукта"),
        ["samples"] = ("Примеры", "образцы использования"),
        ["src"] = ("Исходный код", "код самой программы"),
        ["content"] = ("Содержимое", "данные и материалы, которые идут вместе с программой"),
        ["data"] = ("Данные", "файлы с данными"),
        ["config"] = ("Настройки", "параметры работы программы"),
        ["settings"] = ("Настройки", "параметры работы программы"),
        ["models"] = ("Модели данных", "описание того, из чего состоят данные"),
        ["model"] = ("Модель", "описание данных"),
        ["views"] = ("Экраны", "внешний вид окон и панелей"),
        ["viewmodels"] = ("Логика экранов", "данные и действия для каждого экрана"),
        ["controls"] = ("Элементы интерфейса", "кнопки, панели, поля и другие детали окна"),
        ["components"] = ("Компоненты", "переиспользуемые части интерфейса"),
        ["services"] = ("Службы", "отдельные задачи, которыми пользуются разные части программы"),
        ["utils"] = ("Вспомогательное", "мелкие общие функции"),
        ["helpers"] = ("Вспомогательное", "мелкие общие функции"),
        ["common"] = ("Общее", "то, чем пользуются все части"),
        ["shared"] = ("Общее", "то, чем пользуются все части"),
        ["geometry"] = ("Геометрия", "точки, линии, фигуры и расчёты с ними"),
        ["commands"] = ("Команды", "действия пользователя, которые можно отменить и повторить"),
        ["input"] = ("Ввод", "мышь, клавиатура, перо"),
        ["canvas"] = ("Холст", "область, на которой рисуется карта"),
        ["editor"] = ("Редактор", "режим редактирования"),
        ["map"] = ("Карта", "данные и работа с картой"),
        ["layers"] = ("Слои", "слои изображения"),
        ["terrain"] = ("Рельеф", "горы, высоты, земля"),
        ["water"] = ("Вода", "реки, моря, озёра"),
        ["labels"] = ("Подписи", "тексты на карте"),
        ["text"] = ("Текст", "работа с текстом"),
        ["fonts"] = ("Шрифты", "файлы и настройки шрифтов"),
        ["icons"] = ("Иконки", "значки интерфейса"),
        ["images"] = ("Изображения", "картинки"),
        ["styles"] = ("Стили", "оформление"),
        ["themes"] = ("Темы", "цветовое оформление"),
        ["catalog"] = ("Каталог", "список доступных объектов"),
        ["library"] = ("Библиотека", "набор готовых объектов"),
        ["generation"] = ("Генерация", "автоматическое создание содержимого"),
        ["generator"] = ("Генератор", "автоматическое создание содержимого"),
        ["migrations"] = ("Миграции", "обновление старых версий файлов и баз до нового формата"),
        ["database"] = ("База данных", "таблицы с данными"),
        ["db"] = ("База данных", "таблицы с данными"),
        ["api"] = ("API", "вход для запросов от других программ"),
        ["server"] = ("Сервер", "часть, которая работает на сервере"),
        ["client"] = ("Клиент", "часть, которая работает у пользователя"),
        ["web"] = ("Веб", "страницы и всё для браузера"),
        ["controllers"] = ("Обработчики запросов", "принимают запросы и отдают ответы"),
        ["routes"] = ("Маршруты", "какой адрес какую страницу открывает"),
        ["hooks"] = ("Хуки", "код, который срабатывает при событиях"),
        ["lib"] = ("Библиотека", "общий код"),
        ["auth"] = ("Вход и права", "кто может войти и что ему можно"),
        ["security"] = ("Безопасность", "защита данных"),
        ["network"] = ("Сеть", "обмен данными по сети"),
        ["logging"] = ("Журнал работы", "запись событий для поиска ошибок"),
        ["diagnostics"] = ("Диагностика", "сбор сведений для поиска ошибок"),
        ["performance"] = ("Замеры скорости", "проверки, насколько быстро всё работает"),
        ["livecheck"] = ("Живые проверки", "запуск настоящей программы и проверка её глазами робота"),
        ["journal"] = ("Журнал", "записи о ходе работы"),
        ["research"] = ("Исследования", "изучение подходов и примеров"),
        ["archive"] = ("Архив", "старые материалы"),
        ["github"] = ("GitHub", "настройки автоматической сборки и проверки на GitHub"),
        ["claude"] = ("Claude", "настройки и навыки для ИИ-помощника Claude"),
        ["codex"] = ("Codex", "настройки для ИИ-помощника Codex"),
        ["ai"] = ("ИИ-помощники", "инструкции и инструменты для ИИ"),
        ["live"] = ("Живые проверки", "скрипты для проверки запущенной программы"),
        ["killhost"] = ("Аварийный процесс", "программа, которую тесты специально «роняют», проверяя сохранность данных"),
        ["iconart"] = ("Рисование иконок", "создание значков"),
        ["reawin"] = ("Доступ к окнам", "чтение интерфейса запущенной программы"),
        ["native"] = ("Системные вызовы", "прямые обращения к Windows"),
        ["container"] = ("Формат файла", "как устроен файл проекта внутри"),
        ["regional"] = ("Регион", "региональная карта"),
        ["hex"] = ("Гексы", "шестиугольная сетка"),
        ["documents"] = ("Документ", "устройство открытого файла (документа) и работа с ним"),
        ["document"] = ("Документ", "устройство открытого файла (документа)"),
        ["editing"] = ("Редактирование", "изменения, которые делает пользователь, и их отмена"),
        ["properties"] = ("Свойства", "служебные настройки сборки"),
        ["resources"] = ("Встроенные ресурсы", "тексты и файлы, встроенные в программу"),
        ["objects"] = ("Объекты", "предметы на карте и работа с ними"),
        ["selection"] = ("Выделение", "что сейчас выбрано и как выбирать"),
        ["layout"] = ("Раскладка", "как элементы расставлены"),
        ["scene"] = ("Сцена", "всё, что нужно нарисовать, в одном месте"),
        ["tiles"] = ("Плитки", "изображение, разбитое на квадраты"),
        ["presets"] = ("Заготовки", "готовые наборы настроек"),
        ["history"] = ("История", "журнал изменений для отмены и повтора"),
        ["undo"] = ("Отмена", "отмена и повтор действий"),
        ["serialization"] = ("Сохранение в формат", "перевод данных в файл и обратно"),
        ["json"] = ("JSON", "текстовый формат данных"),
        ["interop"] = ("Связь с системой", "вызовы системных функций"),
        ["shaders"] = ("Шейдеры", "маленькие программы для видеокарты"),
        ["effects"] = ("Эффекты", "визуальные эффекты"),
        ["brushes"] = ("Кисти", "инструменты рисования"),
        ["palette"] = ("Палитра", "набор цветов"),
        ["colors"] = ("Цвета", "работа с цветом"),
        ["vector"] = ("Векторы", "линии и фигуры как математика"),
        ["raster"] = ("Растр", "изображения из пикселей"),
        ["pipeline"] = ("Конвейер", "последовательность шагов обработки"),
        ["workers"] = ("Фоновые задачи", "работа, которая идёт параллельно"),
        ["skills"] = ("Навыки", "готовые инструкции для ИИ-помощника"),
        ["workflows"] = ("Сценарии", "автоматические цепочки действий"),
        ["references"] = ("Образцы", "примеры и референсы"),
        ["source"] = ("Исходники", "исходные материалы"),
        ["prompts"] = ("Запросы к ИИ", "тексты заданий для ИИ"),
        ["fixtures"] = ("Тестовые данные", "заготовки, на которых проверяют код"),
        ["release"] = ("Выпуск", "сборка готовой версии"),
        ["ci"] = ("Автопроверка", "сборка и тесты на сервере после каждой правки"),
        ["textures"] = ("Текстуры", "изображения поверхностей"),
        ["lighting"] = ("Освещение", "свет и тени"),
        ["recovery"] = ("Восстановление", "возврат данных после сбоя"),
        ["support"] = ("Вспомогательное", "служебный код"),
        ["approved"] = ("Утверждённое", "то, что принято владельцем"),
        ["stages"] = ("Этапы", "материалы по этапам работы"),
        ["goldens"] = ("Эталоны", "образцовые результаты для сравнения в тестах"),
    };

    static ExplanationDictionary()
    {
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["store"] = "Хранилище", ["repository"] = "Хранилище", ["renderer"] = "Отрисовка", ["painter"] = "Отрисовка", ["drawer"] = "Отрисовка",
            ["viewmodel"] = "Логика экрана", ["view"] = "Экран", ["exporter"] = "Экспорт", ["importer"] = "Импорт",
            ["command"] = "Команда", ["service"] = "Служба", ["manager"] = "Управление", ["panel"] = "Панель",
            ["window"] = "Окно", ["dialog"] = "Диалог", ["page"] = "Страница", ["control"] = "Элемент интерфейса",
            ["options"] = "Настройки", ["configuration"] = "Настройки", ["config"] = "Настройки",
            ["writer"] = "Запись", ["reader"] = "Чтение", ["factory"] = "Создание объектов", ["builder"] = "Сборка",
            ["controller"] = "Обработка запросов", ["tool"] = "Инструмент", ["exception"] = "Ошибка", ["extensions"] = "Расширения",
            ["helper"] = "Вспомогательные действия", ["utils"] = "Вспомогательные действия", ["format"] = "Формат файла",
            ["cache"] = "Быстрая память", ["handler"] = "Обработчик", ["parser"] = "Разбор данных", ["validator"] = "Проверка данных",
            ["dto"] = "Данные для передачи", ["migration"] = "Обновление формата", ["router"] = "Маршрутизация",
            ["converter"] = "Преобразование", ["gate"] = "Ограничение доступа", ["project"] = "Проект", ["file"] = "Файл",
            ["layer"] = "Слой", ["button"] = "Кнопка", ["asyncbutton"] = "Кнопка", ["nextresponse"] = "Ответ сервера Next",
            ["workshop"] = "Мастерская", ["key"] = "Ключ", ["row"] = "Строка", ["form"] = "Анкета", ["hub"] = "Центр",
            ["route"] = "Маршрут запросов", ["state"] = "Состояние", ["list"] = "Список", ["item"] = "Элемент",
            ["on"] = "При событии", ["is"] = "Признак", ["has"] = "Наличие", ["can"] = "Возможность",
            ["try"] = "Попытка", ["get"] = "Получить", ["set"] = "Задать", ["save"] = "Сохранить", ["load"] = "Загрузить",
            ["open"] = "Открыть", ["close"] = "Закрыть", ["create"] = "Создать", ["delete"] = "Удалить", ["remove"] = "Удалить",
            ["add"] = "Добавить", ["apply"] = "Применить", ["update"] = "Обновить", ["validate"] = "Проверить",
            ["read"] = "Прочитать", ["write"] = "Записать", ["build"] = "Собрать", ["clear"] = "Очистить", ["reset"] = "Сбросить",
            ["find"] = "Найти", ["search"] = "Поиск", ["select"] = "Выбрать", ["execute"] = "Выполнить", ["run"] = "Запустить",
            ["refresh"] = "Обновить", ["draw"] = "Нарисовать", ["render"] = "Нарисовать", ["dispose"] = "Освободить ресурсы",
        }) Words[key] = (value, value);
    }

    private static readonly HashSet<string> Extensions = new("cs fs ts tsx js jsx mjs cjs py xaml axaml json md txt sql prisma csproj fsproj props targets html css scss vue svelte go rs java cpp h yml yaml xml".Split(' '), StringComparer.OrdinalIgnoreCase);
    internal static string Stem(string name, bool file)
    {
        var dot = name.LastIndexOf('.');
        return file && dot >= 0 && Extensions.Contains(name[(dot + 1)..]) ? name[..dot] : name;
    }

    internal static List<string> Split(string name, bool file = false) => Regex.Split(Stem(name, file), @"[.\-_/\s]+")
        .SelectMany(segment => Words.ContainsKey(segment) ? [segment] :
            Regex.Split(segment.Replace("ViewModel", "Viewmodel", StringComparison.Ordinal)
                .Replace("AsyncButton", "Asyncbutton", StringComparison.Ordinal).Replace("NextResponse", "Nextresponse", StringComparison.Ordinal),
                @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])"))
        .Where(w => w.Length > 0).ToList();

    internal static (string Text, List<string> Keys) Name(Node node, IReadOnlyDictionary<string, string> projectWords)
    {
        var stem = Stem(node.Name, node.Kind == NodeKinds.File);
        if (projectWords.TryGetValue(stem, out var whole)) return (whole, [stem]);
        var parts = Split(node.Name, node.Kind == NodeKinds.File);
        // Longest scoped compound wins before translating its individual words (RpHub, HexWorkshop).
        for (var i = 0; i < parts.Count; i++)
            for (var end = parts.Count; end > i + 1; end--)
            {
                var compound = string.Concat(parts.Skip(i).Take(end - i));
                if (!projectWords.ContainsKey(compound)) continue;
                parts.RemoveRange(i, end - i); parts.Insert(i, compound); break;
            }
        // Interface/field prefixes and the async suffix carry no invented domain meaning.
        if (node.SubKind == "interface" && parts.FirstOrDefault() == "I") parts.RemoveAt(0);
        if (node.Kind == NodeKinds.Member && parts.LastOrDefault() == "Async") parts.RemoveAt(parts.Count - 1);
        var keys = new List<string>();
        var translated = parts.Select(word =>
        {
            if (projectWords.TryGetValue(word, out var custom)) { keys.Add(word); return custom; }
            if (Words.TryGetValue(word, out var entry)) { keys.Add(word); return entry.Name; }
            return word; // RP, XYZ, QX etc. retain their spelling, never a fabricated expansion.
        }).ToList();
        if (node.Kind == NodeKinds.Member && translated.Count > 0)
        {
            if (parts[0].Equals("Export", StringComparison.OrdinalIgnoreCase)) translated[0] = "Экспортировать";
            if (parts[0].Equals("Import", StringComparison.OrdinalIgnoreCase)) translated[0] = "Импортировать";
        }
        else if (parts.Count > 1 && RoleWords.Contains(parts[^1]))
        {
            var role = translated[^1]; translated.RemoveAt(translated.Count - 1); translated.Insert(0, role);
        }
        return (string.Join(" · ", translated), keys);
    }
    private static readonly HashSet<string> RoleWords = new("Store Repository Renderer Painter Drawer ViewModel View Window Dialog Panel Control Page Service Exporter Importer Writer Reader Factory Builder Test Tests Controller Command Tool Options Settings Config Exception Extensions Helper Helpers Utils Format Cache Manager Handler Parser Validator Dto Migration Router Converter Generator Catalog Model Gate".Split(' '), StringComparer.OrdinalIgnoreCase);
    internal static string? Purpose(Node node)
    {
        var words = Split(node.Name, node.Kind == NodeKinds.File);
        var role = words.LastOrDefault(RoleWords.Contains);
        if (node.Kind == NodeKinds.Type && role != null) return Words[role].Name;
        if (node.Kind == NodeKinds.Member)
        {
            var verb = words.FirstOrDefault(w => VerbWords.Contains(w));
            if (verb != null) return verb.ToLowerInvariant() switch
            { "export" => "Экспортировать", "import" => "Импортировать", _ => Words[verb].Name };
        }
        return null;
    }
    private static readonly HashSet<string> VerbWords = new("Save Load Export Import Open Close Create Delete Remove Add Apply Update Validate Read Write Build Clear Reset Find Select Execute Run Refresh Draw Render Dispose Get Set".Split(' '), StringComparer.OrdinalIgnoreCase);
}
