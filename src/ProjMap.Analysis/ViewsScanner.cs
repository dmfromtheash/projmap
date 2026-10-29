using System.Text.Json;
using System.Text.RegularExpressions;
using ProjMap.Core;

namespace ProjMap.Analysis;

/// <summary>
/// Builds the `views` section: a tier for every source file (rules `builtin:tiers-v1`, then `.projmap/views.rules.json`,
/// then the owner's corrections) and features — name groups that cut across tiers, with the handlers that start them.
/// Runs before explanations and only reads the graph and file texts, so full and incremental scans agree.
///
/// Scoring (explainable, not a probability): evidence is grouped by family; inside a family only the strongest item
/// counts; families combine as 1 − Π(1 − s). Only weak families (path, module, framework) cap the result at 0.69.
/// A tier is `automatic` at ≥ 0.75 with a 0.15 margin, `guess` below that, `mixed` when two tiers are both ≥ 0.75 and
/// close, `unknown` without signals.
/// </summary>
internal sealed class ViewsScanner : IRepositoryScanner
{
    public string Name => "views";
    public const string RulesPath = ".projmap/views.rules.json";
    const double AcceptScore = 0.75, MinimumMargin = 0.15, WeakCap = 0.69, GuessFloor = 0.3;
    static readonly HashSet<string> WeakFamilies = ["path", "module", "framework", "usage"];

    internal static readonly TierInfo[] TierList =
    [
        new(Tiers.Frontend, "Интерфейс", "окна, панели, кнопки, страницы — то, что видит и нажимает человек"),
        new(Tiers.Application, "Сценарии", "что программа делает по команде: экспорт, сохранение проекта, расчёты для экранов"),
        new(Tiers.Domain, "Правила и модель", "понятия и правила самой предметной области, без экрана и без диска"),
        new(Tiers.Data, "Данные и хранение", "чтение и запись файлов, базы данных, форматы"),
        new(Tiers.Backend, "Сервер и бот", "обработка запросов, команд бота, API"),
        new(Tiers.Infrastructure, "Инфраструктура", "отрисовка, связь с системой, сборка, вспомогательные инструменты"),
        new(Tiers.Tests, "Проверки", "автоматические тесты"),
        new(Tiers.Docs, "Документы", "описания, планы, журналы"),
        new(Tiers.Mixed, "Смешанное", "сильные признаки двух разных ярусов — нужно решение человека"),
        new(Tiers.Unclassified, "Не определено", "признаков не хватает"),
    ];
    static readonly HashSet<string> KnownTiers = TierList.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);

    // ---- syntax and path rules (builtin:tiers-v1) ----

    sealed record SyntaxRule(string Id, string Tier, string? Role, string? Execution, double Score, string Detail,
        Func<RepoFile, string?, bool> Match);

    static Func<RepoFile, string?, bool> Ext(params string[] extensions) =>
        (f, _) => extensions.Any(e => f.Path.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    static Func<RepoFile, string?, bool> Text(string language, string pattern)
    {
        var regex = new Regex(pattern, RegexOptions.Multiline | RegexOptions.CultureInvariant);
        var languages = language.Split('|');
        return (f, text) => text != null && languages.Contains(f.Language) && regex.IsMatch(text);
    }
    static Func<RepoFile, string?, bool> PathMatch(string pattern)
    {
        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return (f, _) => regex.IsMatch(f.Path);
    }

    static readonly SyntaxRule[] Syntax =
    [
        new("markup-view", Tiers.Frontend, "view", "desktop", 0.95, "файл разметки окна (.axaml/.xaml)", Ext(".axaml", ".xaml")),
        new("web-component", Tiers.Frontend, "view", "browser", 0.85, "компонент страницы (.tsx/.jsx/.vue/.svelte)", Ext(".tsx", ".jsx", ".vue", ".svelte")),
        new("web-markup", Tiers.Frontend, "page", "browser", 0.8, "разметка или стиль страницы (.html/.css)", Ext(".html", ".htm", ".css", ".scss")),
        new("ui-base-class", Tiers.Frontend, "view", "desktop", 0.9, "класс окна или элемента интерфейса",
            Text("C#", @"\b(?:class|record)\s+\w+(?:<[^>]*>)?\s*:\s*(?:Window|UserControl|TemplatedControl|Control|Panel|Decorator|ContentControl|Form|Page|ContentPage|ContentView|ComponentBase)\b")),
        new("mvvm-viewmodel", Tiers.Frontend, "viewModel", null, 0.8, "состояние экрана с уведомлением об изменениях",
            Text("C#", @"\b(?:ObservableObject|ViewModelBase|ReactiveObject|INotifyPropertyChanged)\b")),
        new("aspnet-endpoint", Tiers.Backend, "endpoint", "server", 0.9, "обработчик HTTP-запросов (ASP.NET)",
            Text("C#", @"\[ApiController\]|:\s*(?:Controller|ControllerBase)\b|\.Map(?:Get|Post|Put|Delete|Patch)\s*\(|:\s*Hub\b")),
        new("next-route", Tiers.Backend, "endpoint", "server", 0.9, "маршрут API (Next: app/api/…/route, pages/api)",
            PathMatch(@"(?:^|/)app/(?:.*/)?api/(?:.*/)?route\.[cm]?[jt]sx?$|(?:^|/)pages/api/")),
        new("express-router", Tiers.Backend, "endpoint", "server", 0.8, "маршруты сервера (Express/Nest/Fastify)",
            Text("TypeScript|JavaScript", @"\bexpress\s*\(\s*\)|\bRouter\s*\(\s*\)|\b(?:app|router|server)\.(?:get|post|put|delete|patch)\s*\(\s*['""`/]|@Controller\s*\(")),
        new("discord-adapter", Tiers.Backend, "botAdapter", "server", 0.85, "обработка событий и команд Discord",
            Text("TypeScript|JavaScript|Python", @"SlashCommandBuilder|Events\.(?:InteractionCreate|MessageCreate)|['""](?:interactionCreate|messageCreate)['""]|@(?:bot|client)\.(?:command|event)\b")),
        new("python-http", Tiers.Backend, "endpoint", "server", 0.85, "маршруты сервера (FastAPI/Flask)",
            Text("Python", @"@(?:app|router|bp|blueprint)\.(?:get|post|put|delete|patch|route)\s*\(|\bAPIRouter\s*\(")),
        new("sql-schema", Tiers.Data, "schema", null, 0.9, "схема базы данных", Ext(".sql", ".prisma")),
        new("ef-context", Tiers.Data, "schema", null, 0.9, "контекст базы данных (DbContext/DbSet)", Text("C#", @":\s*DbContext\b|\bDbSet<")),
        new("db-access", Tiers.Data, "storage", null, 0.8, "прямой доступ к базе данных",
            Text("C#|TypeScript|JavaScript|Python", @"\b(?:SqliteConnection|NpgsqlConnection|SqlConnection|MySqlConnection|PrismaClient|sqlite3\.connect|create_engine)\b|\bprisma\.\w+\.(?:find|create|update|delete|upsert)")),
        // a file read inside a screen or a scenario does not make it storage: weak "usage" family
        new("file-format", Tiers.Data, "storage", null, 0.5, "читает или пишет файлы",
            Text("C#", @"\b(?:ZipArchive|ZipFile|FileStream|File\.(?:WriteAll|ReadAll|Open|Replace|Move)\w*)\b")),
        new("interop", Tiers.Infrastructure, "platform", null, 0.85, "прямые вызовы функций Windows (interop)",
            Text("C#", @"\[(?:DllImport|LibraryImport)\b")),
        new("composition-root", Tiers.Infrastructure, "compositionRoot", null, 0.75, "точка запуска программы",
            (f, text) => f.Tags.Contains("entrypoint") && f.Language == "C#"),
        new("python-cli", Tiers.Application, "cliEntry", "cli", 0.7, "команда командной строки",
            Text("Python", @"\bargparse\.ArgumentParser\b|^\s*@click\.command|\btyper\.Typer\s*\(")),
        new("build-config", Tiers.Infrastructure, "buildTool", null, 0.85, "сборка, CI или служебный скрипт",
            (f, _) => Regex.IsMatch(f.Path, @"(?:^|/)\.github/|(?:^|/)Dockerfile$|\.(?:csproj|fsproj|props|targets|slnx?|ps1|psm1|sh|cmd|bat)$|(?:^|/)(?:package\.json|pyproject\.toml|setup\.py|tsconfig[^/]*\.json|global\.json|nuget\.config)$", RegexOptions.IgnoreCase)),
        new("test-file", Tiers.Tests, "test", null, 0.95, "файл тестов", (f, _) => f.Tags.Contains("test")),
        new("markdown-doc", Tiers.Docs, "doc", null, 0.95, "документ (Markdown)", (f, _) => f.Language == "Markdown"),
    ];

    // Folder and module-name words. Weak by design: naming conventions are not architecture facts.
    static readonly Dictionary<string, string> PathWords = BuildWords(new()
    {
        [Tiers.Frontend] = "views view controls control ui pages page components component screens widgets viewmodels layouts dialogs windows app client frontend web",
        [Tiers.Application] = "services usecases application commands workflows features presentation export exporters import importers",
        [Tiers.Domain] = "domain core model models entities documents document geometry editing rules logic engine",
        [Tiers.Data] = "persistence storage repositories repository data db database migrations store stores serialization io schema prisma assets resources",
        [Tiers.Backend] = "api server routes controllers endpoints bot middleware hubs backend gateway",
        [Tiers.Infrastructure] = "infrastructure infra deploy docker ops rendering render renderer platform interop native tools scripts build ci perf benchmarks lib",
        [Tiers.Tests] = "tests test specs spec __tests__ e2e",
        [Tiers.Docs] = "docs doc documentation",
    });
    static Dictionary<string, string> BuildWords(Dictionary<string, string> source) =>
        source.SelectMany(p => p.Value.Split(' ').Select(w => (w, p.Key))).ToDictionary(x => x.w, x => x.Key, StringComparer.OrdinalIgnoreCase);

    // Package → tier hint for every file of the module (framework family, ≤ 0.15) and the execution environment.
    static readonly (Regex Package, string Tier, string Execution)[] Frameworks =
    [
        (new(@"^(?:Avalonia|Microsoft\.WindowsDesktop|Microsoft\.Maui|Microsoft\.WindowsAppSDK|Uno\.)", RegexOptions.IgnoreCase), Tiers.Frontend, "desktop"),
        (new(@"^(?:react|react-dom|vue|svelte|@angular/core|solid-js)$", RegexOptions.IgnoreCase), Tiers.Frontend, "browser"),
        (new(@"^(?:next)$", RegexOptions.IgnoreCase), Tiers.Frontend, "browser"),
        (new(@"^(?:express|fastify|koa|@nestjs/core|hono|fastapi|flask|django|Microsoft\.AspNetCore\.)", RegexOptions.IgnoreCase), Tiers.Backend, "server"),
        (new(@"^(?:discord\.js|discord\.py|telegraf|grammy)$", RegexOptions.IgnoreCase), Tiers.Backend, "server"),
        (new(@"^(?:@prisma/client|prisma|Microsoft\.EntityFrameworkCore|Dapper|Microsoft\.Data\.Sqlite|sqlalchemy|pg|mysql2|better-sqlite3)", RegexOptions.IgnoreCase), Tiers.Data, "shared"),
    ];

    public Task ScanAsync(ScanContext c, CancellationToken ct)
    {
        var (rules, fileOverrides) = ReadRulesFile(c);
        c.ProjectViewOverrides = fileOverrides;
        var stale = new List<string>();
        var assignments = new Dictionary<string, TierAssignment>(StringComparer.Ordinal);
        var moduleSignals = ModuleSignals(c);
        var handlerCounts = c.Graph.Nodes.Values.Where(n => n.SubKind == "handler" && n.Path != null)
            .GroupBy(n => n.Path!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var file in c.Files.Values.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (file.Language == null || !c.Graph.Nodes.ContainsKey(ScanContext.FileId(file.Path))) continue;
            assignments[ScanContext.FileId(file.Path)] = Classify(c, file, rules, moduleSignals, handlerCounts.GetValueOrDefault(file.Path));
        }
        var overrides = new List<ViewsOverrides>();
        if (fileOverrides is { IsEmpty: false }) overrides.Add(fileOverrides);
        if (c.Options.ViewOverrides is { IsEmpty: false } owner) overrides.Add(owner);
        foreach (var o in overrides) ApplyTierOverrides(c, o, assignments, stale);

        var (features, memberships) = Features(c, assignments, ct);
        foreach (var o in overrides) ApplyFeatureOverrides(c, o, assignments, features, memberships, stale);
        var finalFeatures = features.Values.Select(f =>
        {
            var files = memberships.Where(m => m.FeatureId == f.Id && m.NodeId.StartsWith("file:", StringComparison.Ordinal)).Select(m => m.NodeId).ToList();
            var tiers = files.Select(id => assignments.TryGetValue(id, out var a) ? a.Tier : null).OfType<string>()
                .Where(t => t is not (Tiers.Mixed or Tiers.Unclassified)).Distinct()
                .OrderBy(t => Array.FindIndex(TierList, x => x.Id == t)).ToList();
            return f with { Files = files.Count, TierIds = tiers };
        }).OrderByDescending(f => f.Files).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();

        var ruleSet = "builtin:tiers-v1" + (rules.Count > 0 || fileOverrides != null ? "+" + RulesPath : "") +
            (c.Options.ViewOverrides is { IsEmpty: false } ? "+owner" : "");
        c.Graph.Views = new ViewsInfo
        {
            RuleSet = ruleSet,
            Tiers = [.. TierList],
            Assignments = assignments.Values.OrderBy(a => a.NodeId, StringComparer.Ordinal).ToList(),
            Features = finalFeatures,
            Memberships = memberships.OrderBy(m => m.FeatureId, StringComparer.Ordinal).ThenBy(m => m.NodeId, StringComparer.Ordinal).ToList(),
            StaleOverrides = stale.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            Timeline = Timeline(c, ct),
            EntryPoints = c.EntryPoints.Count == 0 ? null : c.EntryPoints.OrderBy(e => e.Id, StringComparer.Ordinal)
                .Select(e => e with { Ambiguous = e.Anchor != null && c.EntryPoints.Count(other => other.Anchor == e.Anchor) > 1 }).ToList(),
        };
        return Task.CompletedTask;
    }

    // ---- timeline ----

    static readonly Regex Sha = new(@"(?<![0-9a-f])[0-9a-f]{7,40}(?![0-9a-z])", RegexOptions.CultureInvariant);

    static TimelineInfo? Timeline(ScanContext c, CancellationToken ct)
    {
        if (c.Commits.Count == 0) return null;
        var files = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        int Index(string path)
        {
            var id = ScanContext.FileId(path);
            if (!index.TryGetValue(id, out var i)) { index[id] = i = files.Count; files.Add(id); }
            return i;
        }
        var commits = c.Commits.Select(x => new TimelineCommit(x.Hash, x.CommittedAt, x.Subject, x.Files.Select(Index).ToList())).ToList();

        // stages: roadmap items with a key (R13, DEC-071 …); the first one with a known status per key
        var items = c.Graph.Roadmap.Select(r => (Item: r, Key: RoadmapScanner.ItemKey(r.Title))).Where(x => x.Key != null)
            .GroupBy(x => x.Key!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(x => x.Item.Status == RoadmapStatus.Unknown ? 1 : 0).First()).ToList();
        var stages = new List<TimelineStage>();
        if (items.Count > 0)
        {
            var keys = new Regex(@"(?<![\p{L}\p{N}])(?<k>" + string.Join("|", items.Select(x => Regex.Escape(x.Key!)).OrderByDescending(k => k.Length)) + @")(?![\p{L}\p{N}])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var explicitLinks = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            // a line naming exactly one stage and some commit hashes (a progress table row) links them explicitly
            foreach (var doc in c.Files.Values.Where(f => f.Language == "Markdown").OrderBy(f => f.Path, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (doc.Text(c) is not { } text) continue;
                foreach (var line in text.Split('\n'))
                {
                    if (line.Length > 2000 || !Sha.IsMatch(line)) continue;
                    var found = keys.Matches(line).Select(m => m.Groups["k"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (found.Count != 1) continue;
                    foreach (Match h in Sha.Matches(line))
                        foreach (var commit in c.Commits.Where(x => x.Hash.StartsWith(h.Value, StringComparison.Ordinal)))
                            (explicitLinks.TryGetValue(found[0], out var set) ? set : explicitLinks[found[0]] = new(StringComparer.Ordinal)).Add(commit.Hash);
                }
            }
            var subjectLinks = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var commit in c.Commits)
                foreach (var key in keys.Matches(commit.Subject).Select(m => m.Groups["k"].Value).Distinct(StringComparer.OrdinalIgnoreCase))
                    (subjectLinks.TryGetValue(key, out var set) ? set : subjectLinks[key] = new(StringComparer.Ordinal)).Add(commit.Hash);
            foreach (var (item, key) in items.OrderBy(x => x.Item.Source?.Path, StringComparer.Ordinal).ThenBy(x => x.Item.Source?.Line))
            {
                var title = Regex.Replace(item.Title, @"^[\s*_`#\[]*" + Regex.Escape(key!) + @"[\s:.\-—–·)\]]*", "").Trim();
                var (ids, link) = explicitLinks.TryGetValue(key!, out var e) ? (e, "explicit") : subjectLinks.TryGetValue(key!, out var s) ? (s, "subject") : (null, "none");
                stages.Add(new TimelineStage(key!, title.Length > 0 ? title : item.Title, item.Status, ids?.ToList() ?? [], link));
            }
        }
        var oldest = c.Commits.Min(x => x.CommittedAt);
        return new TimelineInfo
        {
            Coverage = $"{c.Commits.Count} коммитов с {oldest:yyyy-MM-dd} (окно {c.Options.GitMonths} мес., не больше {c.Options.MaxCommits}); удалённые с тех пор файлы не показаны",
            Files = files,
            Commits = commits,
            Stages = stages,
        };
    }

    // ---- tiers ----

    sealed record ModuleSignal(List<TierEvidence> Evidence, string? Execution);

    static Dictionary<string, ModuleSignal> ModuleSignals(ScanContext c)
    {
        var result = new Dictionary<string, ModuleSignal>(StringComparer.Ordinal);
        foreach (var module in c.Modules)
        {
            var packages = module.Packages.Concat(c.Graph.Edges.Values
                .Where(e => e.Kind == EdgeKinds.Package && e.Source == module.Id).Select(e => e.Target["pkg:".Length..]))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
            var evidence = new List<TierEvidence>();
            string? execution = null;
            foreach (var (pattern, tier, exec) in Frameworks)
                if (packages.FirstOrDefault(p => pattern.IsMatch(p)) is { } hit)
                {
                    evidence.Add(new("framework", tier, 0.15, "framework-package", $"модуль подключает {hit}"));
                    execution ??= exec;
                }
            if (Path.GetExtension(module.Manifest) is ".csproj" && c.Files.TryGetValue(module.Manifest, out var manifest) &&
                manifest.Text(c) is { } text && Regex.IsMatch(text, @"<Use(?:WPF|WindowsForms)>\s*true", RegexOptions.IgnoreCase))
            { evidence.Add(new("framework", Tiers.Frontend, 0.15, "framework-package", "модуль — оконное приложение Windows")); execution ??= "desktop"; }
            result[module.Id] = new(evidence, execution);
        }
        return result;
    }

    static TierAssignment Classify(ScanContext c, RepoFile file, List<CustomRule> rules, Dictionary<string, ModuleSignal> modules, int handlers)
    {
        var evidence = new List<TierEvidence>();
        var syntax = Syntax.Where(r => r.Match(file, NeedsText(file) ? file.Text(c) : null)).ToList();
        // A test or a document is that whatever else it contains (a test project's .csproj, a test building a window).
        if (syntax.FirstOrDefault(r => r.Id is "test-file" or "markdown-doc") is { } exclusive) syntax = [exclusive];
        string? role = null, execution = null;
        // An endpoint or a screen that also queries the database is still an endpoint/screen: storage access found next to
        // another role is a weak "usage" hint, not a second strong tier (otherwise every API route would be «mixed»).
        var otherRole = syntax.Any(r => r.Tier != Tiers.Data);
        foreach (var rule in syntax)
        {
            var usage = rule.Id == "file-format" || otherRole && rule.Id == "db-access";
            evidence.Add(new(usage ? "usage" : "syntax", rule.Tier, usage ? Math.Min(rule.Score, 0.5) : rule.Score, rule.Id, rule.Detail));
        }
        if (otherRole) syntax.RemoveAll(r => r.Id == "db-access");
        var module = c.ModuleFor(file.Path);
        var signal = module != null && modules.TryGetValue(module.Id, out var s) ? s : null;
        // lambdas passed with a text label are buttons only where a UI framework is present
        if (handlers > 0 && syntax.All(r => r.Tier == Tiers.Frontend || r.Id == "file-format") && (signal?.Execution is "desktop" or "browser" || file.Tags.Contains("ui")))
            evidence.Add(new("syntax", Tiers.Frontend, 0.85, "ui-handlers", $"кнопки и обработчики событий: {handlers}"));
        foreach (var rule in rules.Where(r => r.Matches(file, c)))
            evidence.Add(new("rules", rule.Assign.Tier, rule.Score, rule.Id, rule.Reason ?? "правило проекта " + RulesPath));

        foreach (var segment in file.Path.Split('/')[..^1].Distinct(StringComparer.OrdinalIgnoreCase))
            if (PathWords.TryGetValue(segment, out var tier))
                evidence.Add(new("path", tier, 0.5, "path-word", $"папка «{segment}»"));
        if (module != null)
        {
            foreach (var word in module.Name.Split('.', '-', '_', ' ').Skip(module.Name.Contains('.') ? 1 : 0))
                if (PathWords.TryGetValue(word, out var tier))
                    evidence.Add(new("module", tier, 0.45, "module-name", $"модуль «{module.Name}»"));
            if (signal != null)
            {
                evidence.AddRange(signal.Evidence);
                execution = signal.Execution;
            }
        }

        var scores = evidence.GroupBy(e => e.Tier).Select(g =>
        {
            var families = g.GroupBy(e => e.Family).Select(f => f.Max(e => e.Score)).ToList();
            var score = 1 - families.Aggregate(1.0, (p, s) => p * (1 - s));
            if (g.All(e => WeakFamilies.Contains(e.Family))) score = Math.Min(score, WeakCap);
            return (Tier: g.Key, Score: Math.Round(score, 2));
        }).OrderByDescending(x => x.Score).ThenBy(x => Array.FindIndex(TierList, t => t.Id == x.Tier)).ToList();

        var ordered = evidence.OrderByDescending(e => e.Score).ThenBy(e => e.Rule, StringComparer.Ordinal).ToList();
        if (scores.Count == 0 || scores[0].Score < GuessFloor)
            return new TierAssignment
            {
                NodeId = ScanContext.FileId(file.Path),
                Tier = Tiers.Unclassified,
                Decision = TierDecisions.Unknown,
                Score = scores.Count == 0 ? 0 : scores[0].Score,
                Reason = "признаков не хватает",
                Execution = execution,
                Evidence = ordered
            };
        var best = scores[0];
        var second = scores.Count > 1 ? scores[1] : (Tier: "", Score: 0.0);
        var bestRule = syntax.FirstOrDefault(r => r.Tier == best.Tier);
        role = rules.FirstOrDefault(r => r.Assign.Tier == best.Tier && r.Matches(file, c))?.Assign.Role ?? bestRule?.Role;
        execution = rules.FirstOrDefault(r => r.Assign.Tier == best.Tier && r.Matches(file, c))?.Assign.Execution ?? bestRule?.Execution ?? execution;
        if (best.Tier is Tiers.Domain or Tiers.Data && execution is "desktop" or "browser") execution = "shared";
        if (second.Score >= AcceptScore && best.Score - second.Score < MinimumMargin)
            return new TierAssignment
            {
                NodeId = ScanContext.FileId(file.Path),
                Tier = Tiers.Mixed,
                Decision = TierDecisions.Mixed,
                Score = best.Score,
                Alternatives = [best.Tier, second.Tier],
                Execution = execution,
                Evidence = ordered,
                Reason = $"сильные признаки двух ярусов: {TierName(best.Tier)} и {TierName(second.Tier)}"
            };
        var automatic = best.Score >= AcceptScore && best.Score - second.Score >= MinimumMargin;
        var top = ordered.First(e => e.Tier == best.Tier);
        return new TierAssignment
        {
            NodeId = ScanContext.FileId(file.Path),
            Tier = best.Tier,
            Role = role,
            Execution = execution,
            Decision = automatic ? TierDecisions.Automatic : TierDecisions.Guess,
            Score = best.Score,
            Alternatives = second.Score >= GuessFloor && best.Score - second.Score < MinimumMargin ? [second.Tier] : null,
            Reason = (automatic ? "" : "предположение: ") + top.Detail,
            Evidence = ordered
        };
    }

    static bool NeedsText(RepoFile f) => f.Language is "C#" or "TypeScript" or "JavaScript" or "Python" or "XML";
    static string TierName(string id) => TierList.FirstOrDefault(t => t.Id == id)?.Name ?? id;

    static void ApplyTierOverrides(ScanContext c, ViewsOverrides o, Dictionary<string, TierAssignment> assignments, List<string> stale)
    {
        // exact node id > exact path > glob; inside a level the later entry wins
        foreach (var entry in o.Tiers.Select((t, i) => (t, i))
                     .OrderBy(x => x.t.NodeId != null ? 3 : x.t.PathGlob is { } g && !g.AsSpan().ContainsAny("*?{") ? 2 : 1).ThenBy(x => x.i))
        {
            var t = entry.t;
            if (!KnownTiers.Contains(t.Tier)) { stale.Add($"tier:{t.NodeId ?? t.PathGlob}: неизвестный ярус «{t.Tier}»"); continue; }
            List<string> targets;
            if (t.NodeId != null) targets = assignments.ContainsKey(t.NodeId) ? [t.NodeId] : [];
            else if (t.PathGlob != null)
            {
                var regex = Glob(t.PathGlob);
                targets = assignments.Keys.Where(id => regex.IsMatch(id["file:".Length..])).ToList();
            }
            else targets = [];
            if (targets.Count == 0) { stale.Add($"tier:{t.NodeId ?? t.PathGlob}"); continue; }
            foreach (var id in targets)
            {
                var old = assignments[id];
                assignments[id] = old with
                {
                    Tier = t.Tier,
                    Role = t.Role ?? (old.Tier == t.Tier ? old.Role : null),
                    Execution = t.Execution ?? old.Execution,
                    Decision = TierDecisions.Override,
                    Score = 1,
                    Reason = t.Reason ?? "назначено вручную",
                    Alternatives = old.Tier != t.Tier && old.Tier is not (Tiers.Unclassified) ? [old.Tier] : null,
                    Evidence = [new("override", t.Tier, 1, "override", t.Reason ?? "назначено вручную"), .. old.Evidence.Where(e => e.Family != "override")]
                };
            }
        }
    }

    // ---- features ----

    static readonly HashSet<string> StopWords = new(
        ("editor view model viewmodel service manager helper util utils extension extensions base test tests spec data common shared " +
         "core app application program main index page component control window dialog panel options factory provider store state " +
         "context info item list type types result event args handler handlers command commands document interface abstract default impl " +
         "internal public async cache config constant enum record entity dto request response api route server client web ui lib src " +
         "get set new old the and for with from into part parts tool tools script scripts perf benchmark sample samples program " +
         "render renderer rendering presentation persistence platform windows window views controls models services kind kinds " +
         "builder builders writer reader json xml text string value values helper source sources output input file files " +
         "edit editing project object unit native smoke generator set sets structure model resource resources budget node nodes name names").Split(' '),
        StringComparer.Ordinal);
    static readonly HashSet<string> NonProduct = new(["tools", "scripts", "experiments", "samples", "examples", "benchmarks", "perf", "docs", ".github"], StringComparer.OrdinalIgnoreCase);
    const int MaxFeatures = 24;

    internal static readonly Dictionary<string, string> FeatureNames = new(StringComparer.Ordinal)
    {
        ["export"] = "Экспорт",
        ["poster"] = "Постер",
        ["print"] = "Печать",
        ["hex"] = "Гексы",
        ["light"] = "Свет",
        ["river"] = "Реки",
        ["road"] = "Дороги",
        ["bridge"] = "Мосты",
        ["atlas"] = "Атлас",
        ["asset"] = "Ресурсы",
        ["catalog"] = "Каталог",
        ["stamp"] = "Штампы",
        ["style"] = "Стили",
        ["layer"] = "Слои",
        ["label"] = "Подписи",
        ["area"] = "Области",
        ["brush"] = "Кисти",
        ["terrain"] = "Местность",
        ["coast"] = "Берега",
        ["sea"] = "Море",
        ["mountain"] = "Горы",
        ["forest"] = "Леса",
        ["save"] = "Сохранение",
        ["autosave"] = "Автосохранение",
        ["recovery"] = "Восстановление",
        ["shortcut"] = "Горячие клавиши",
        ["setting"] = "Настройки",
        ["selection"] = "Выделение",
        ["undo"] = "Отмена действий",
        ["history"] = "История",
        ["tile"] = "Тайлы",
        ["gallery"] = "Галерея",
        ["beauty"] = "Красота",
        ["texture"] = "Текстуры",
        ["grid"] = "Сетка",
        ["room"] = "Комнаты",
        ["dungeon"] = "Подземелья",
        ["world"] = "Мир",
        ["fog"] = "Туман",
        ["weather"] = "Погода",
        ["symbol"] = "Значки",
        ["icon"] = "Значки",
        ["font"] = "Шрифты",
        ["path"] = "Пути",
        ["vector"] = "Векторы",
        ["snap"] = "Привязка",
        ["zoom"] = "Масштаб",
        ["camera"] = "Камера",
        ["passport"] = "Паспорт",
        ["form"] = "Анкета",
        ["like"] = "Симпатии",
        ["scene"] = "Сцены",
        ["chronicle"] = "Хроники",
        ["proxy"] = "Прокси-посты",
        ["auth"] = "Авторизация",
        ["graph"] = "Граф",
        ["guild"] = "Серверы",
        ["character"] = "Персонажи",
        ["message"] = "Сообщения",
        ["user"] = "Пользователи",
        ["checkpoint"] = "Точки сохранения",
        ["migration"] = "Миграции",
        ["png"] = "PNG",
        ["obsidian"] = "Obsidian",
        ["vtt"] = "VTT",
        ["placement"] = "Размещение",
        ["land"] = "Суша",
        ["generation"] = "Генерация",
        ["cliff"] = "Обрывы",
        ["composition"] = "Композиция",
        ["decoration"] = "Украшения",
        ["material"] = "Материалы",
        ["isometric"] = "Изометрия",
        ["night"] = "Ночь", ["audit"] = "Аудит", ["hub"] = "Хаб", ["authz"] = "Права доступа", ["activity"] = "Активность",
        ["visibility"] = "Видимость", ["access"] = "Доступ", ["dashboard"] = "Панель", ["post"] = "Посты", ["reconcile"] = "Сверка",
        ["role"] = "Роли", ["discord"] = "Discord", ["member"] = "Участники", ["channel"] = "Каналы", ["ticket"] = "Тикеты",
        ["fire"] = "Огонь",
        ["water"] = "Вода",
        ["shore"] = "Берега",
        ["ink"] = "Тушь",
        ["painter"] = "Живопись",
        ["library"] = "Библиотека",
        ["search"] = "Поиск",
        ["action"] = "Действия",
        ["workspace"] = "Рабочие области",
        ["preview"] = "Предпросмотр",
        ["clipboard"] = "Буфер обмена",
        ["token"] = "Фишки",
        ["player"] = "Игроки",
        ["image"] = "Изображения",
        ["sound"] = "Звук",
        ["video"] = "Видео",
        ["montage"] = "Монтаж",
        ["studio"] = "Студия",
        ["upload"] = "Загрузка",
        ["thumbnail"] = "Обложки",
    };

    static readonly Regex Camel = new(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+", RegexOptions.CultureInvariant);

    internal static IEnumerable<string> Terms(string path, string projectName)
    {
        var project = Camel.Matches(projectName).Select(m => m.Value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var parts = path.Split('/');
        var stem = parts[^1];
        var dot = stem.IndexOf('.');
        if (dot > 0 && !stem.StartsWith('.')) stem = stem[..stem.LastIndexOf('.')];
        foreach (var piece in parts[..^1].Append(stem))
            foreach (Match m in Camel.Matches(piece))
            {
                var t = Stem(m.Value.ToLowerInvariant());
                if (t.Length >= 3 && !StopWords.Contains(t) && !project.Contains(t)) yield return t;
            }
    }

    static string Stem(string t)
    {
        if (t.EndsWith("ies", StringComparison.Ordinal) && t.Length > 5) return t[..^3] + "y";
        if (t.EndsWith("ing", StringComparison.Ordinal) && t.Length > 6) t = t[..^3];
        else if (t.EndsWith('s') && !t.EndsWith("ss", StringComparison.Ordinal) && t.Length > 4) t = t[..^1];
        return t;
    }

    static bool IsCode(RepoFile f) => f.Language is "C#" or "F#" or "TypeScript" or "JavaScript" or "Python" or "XAML" or "Go" or "Rust" or "Java" or "C++"
        && !f.Tags.Contains("generated");

    (Dictionary<string, FeatureInfo>, List<FeatureMembership>) Features(ScanContext c, Dictionary<string, TierAssignment> assignments, CancellationToken ct)
    {
        var name = c.Graph.Project.Name;
        // features describe the product: tools, experiments and samples do not seed them
        var code = c.Files.Values.Where(f => IsCode(f) && !f.Tags.Contains("test") && assignments.ContainsKey(ScanContext.FileId(f.Path)) &&
                !f.Path.Split('/').Any(s => NonProduct.Contains(s)))
            .OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        var byTerm = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var f in code)
            foreach (var t in Terms(StripModule(c, f.Path), name).Distinct())
                (byTerm.TryGetValue(t, out var set) ? set : byTerm[t] = new(StringComparer.Ordinal)).Add(f.Path);
        // "-er" forms join their stem when it is a term too (exporter → export, painter stays).
        foreach (var t in byTerm.Keys.Where(t => t.EndsWith("er", StringComparison.Ordinal) && byTerm.ContainsKey(t[..^2])).ToList())
        { byTerm[t[..^2]].UnionWith(byTerm[t]); byTerm.Remove(t); }

        // a word in a fifth of all product files is vocabulary, not a function (small projects keep a floor)
        var limit = Math.Max(8, (int)(code.Count * 0.2));
        string Tier(string path) => assignments[ScanContext.FileId(path)].Tier;
        var candidates = byTerm.Where(p => p.Value.Count >= 3 && p.Value.Count <= limit)
            .Select(p => (Term: p.Key, Files: p.Value, Tiers: p.Value.Select(Tier).Where(t => t is not (Tiers.Mixed or Tiers.Unclassified)).Distinct().Count()))
            .Where(x => x.Tiers >= 2)
            .OrderByDescending(x => x.Files.Count * x.Tiers).ThenBy(x => x.Term, StringComparer.Ordinal).ToList();
        var accepted = new List<(string Term, List<string> Terms, SortedSet<string> Files)>();
        foreach (var cand in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var merged = false;
            // near-synonyms (Light/Lighting, Poster/Print in the same files) join; a small topic inside a big one stays separate
            foreach (var a in accepted)
            {
                var overlap = cand.Files.Count(a.Files.Contains);
                if (overlap >= 0.6 * (cand.Files.Count + a.Files.Count - overlap)) { a.Terms.Add(cand.Term); a.Files.UnionWith(cand.Files); merged = true; break; }
            }
            if (!merged && accepted.Count < MaxFeatures) accepted.Add((cand.Term, [cand.Term], new(cand.Files, StringComparer.Ordinal)));
        }

        var features = new Dictionary<string, FeatureInfo>(StringComparer.Ordinal);
        var memberships = new List<FeatureMembership>();
        foreach (var a in accepted)
        {
            var id = "feature:" + a.Term;
            features[id] = new FeatureInfo { Id = id, Name = FeatureNames.GetValueOrDefault(a.Term) ?? Capitalize(a.Term), Terms = [.. a.Terms.Order(StringComparer.Ordinal)] };
            foreach (var path in a.Files)
                memberships.Add(new()
                {
                    FeatureId = id,
                    NodeId = ScanContext.FileId(path),
                    Role = FeatureRoles.Implementation,
                    Score = 0.6,
                    Reason = "в имени: " + string.Join(", ", a.Terms.Where(t => Terms(StripModule(c, path), name).Contains(t)))
                });
        }
        AddRelated(c, features, memberships, assignments, name);
        return (features, memberships);
    }

    static string StripModule(ScanContext c, string path) =>
        c.ModuleFor(path) is { Directory.Length: > 0 } m ? path[(m.Directory.Length + 1)..] : path;
    static string Capitalize(string t) => char.ToUpperInvariant(t[0]) + t[1..];

    /// Tests and documents named after a feature, and handlers that start it (own file in the feature, or a direct call into it).
    static void AddRelated(ScanContext c, Dictionary<string, FeatureInfo> features, List<FeatureMembership> memberships,
        Dictionary<string, TierAssignment> assignments, string projectName)
    {
        var termToFeature = features.Values.SelectMany(f => f.Terms.Select(t => (t, f.Id))).GroupBy(x => x.t)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList(), StringComparer.Ordinal);
        foreach (var f in c.Files.Values.Where(f => assignments.ContainsKey(ScanContext.FileId(f.Path)) && (f.Tags.Contains("test") || f.Language == "Markdown"))
                     .OrderBy(f => f.Path, StringComparer.Ordinal))
            foreach (var featureId in Terms(StripModule(c, f.Path), projectName).SelectMany(t => termToFeature.GetValueOrDefault(t) ?? []).Distinct())
                memberships.Add(new()
                {
                    FeatureId = featureId,
                    NodeId = ScanContext.FileId(f.Path),
                    Role = f.Tags.Contains("test") ? FeatureRoles.Test : FeatureRoles.Doc,
                    Score = 0.6,
                    Reason = "название совпадает с функцией"
                });

        var fileFeatures = memberships.Where(m => m.Role == FeatureRoles.Implementation)
            .GroupBy(m => m.NodeId["file:".Length..], StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(m => m.FeatureId).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var handlers = c.Graph.Nodes.Values.Where(n => n.SubKind == "handler" && n.Path != null).OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
        if (handlers.Count == 0) return;
        var handlerIds = handlers.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
        var calls = c.Graph.Edges.Values.Where(e => e.Kind is EdgeKinds.Call or EdgeKinds.Reference && handlerIds.Contains(e.Source))
            .ToLookup(e => e.Source, StringComparer.Ordinal);
        foreach (var h in handlers)
        {
            var found = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (fileFeatures.TryGetValue(h.Path!, out var own))
                foreach (var f in own) found.TryAdd(f, "кнопка в файле этой функции");
            foreach (var e in calls[h.Id])
                if (c.Graph.Nodes.TryGetValue(e.Target, out var target) && target.Path != null && fileFeatures.TryGetValue(target.Path, out var called))
                    foreach (var f in called) found.TryAdd(f, "вызывает " + target.Name);
            foreach (var (featureId, reason) in found)
                memberships.Add(new() { FeatureId = featureId, NodeId = h.Id, Role = FeatureRoles.Entry, Score = reason.StartsWith("вызывает", StringComparison.Ordinal) ? 0.8 : 0.6, Reason = reason });
        }
    }

    static void ApplyFeatureOverrides(ScanContext c, ViewsOverrides o, Dictionary<string, TierAssignment> assignments,
        Dictionary<string, FeatureInfo> features, List<FeatureMembership> memberships, List<string> stale)
    {
        foreach (var added in o.AddedFeatures)
        {
            var globs = added.PathGlobs.Select(Glob).ToList();
            var terms = added.Terms.Select(t => Stem(t.ToLowerInvariant())).ToHashSet(StringComparer.Ordinal);
            features[added.Id] = new FeatureInfo { Id = added.Id, Name = added.Name, State = FeatureStates.Manual, Terms = [.. terms.Order(StringComparer.Ordinal)] };
            memberships.RemoveAll(m => m.FeatureId == added.Id);
            foreach (var id in assignments.Keys)
            {
                var path = id["file:".Length..];
                if (globs.Any(g => g.IsMatch(path)) || terms.Count > 0 && Terms(StripModule(c, path), c.Graph.Project.Name).Any(terms.Contains))
                    memberships.Add(new()
                    {
                        FeatureId = added.Id,
                        NodeId = id,
                        Role = c.Files.TryGetValue(path, out var f) && f.Tags.Contains("test") ? FeatureRoles.Test
                        : c.Files.TryGetValue(path, out var d) && d.Language == "Markdown" ? FeatureRoles.Doc : FeatureRoles.Implementation,
                        Score = 1,
                        Reason = "функция задана вручную"
                    });
            }
        }
        foreach (var merge in o.FeatureMerges)
        {
            if (!features.TryGetValue(merge.Into, out var into)) { stale.Add("merge:" + merge.Into); continue; }
            var terms = new SortedSet<string>(into.Terms, StringComparer.Ordinal);
            foreach (var from in merge.From.Where(f => f != merge.Into))
            {
                if (!features.Remove(from, out var source)) { stale.Add("merge:" + from); continue; }
                terms.UnionWith(source.Terms);
                for (var i = 0; i < memberships.Count; i++)
                    if (memberships[i].FeatureId == from) memberships[i] = memberships[i] with { FeatureId = merge.Into };
            }
            features[merge.Into] = into with { State = FeatureStates.Manual, Terms = [.. terms] };
        }
        Dedupe(memberships);
        foreach (var hidden in o.HiddenFeatures)
            if (features.Remove(hidden)) memberships.RemoveAll(m => m.FeatureId == hidden);
            else stale.Add("hide:" + hidden);
        foreach (var (id, name) in o.FeatureNames.OrderBy(p => p.Key, StringComparer.Ordinal))
            if (features.TryGetValue(id, out var f)) features[id] = f with { Name = name, State = FeatureStates.Manual };
            else stale.Add("name:" + id);
        foreach (var m in o.Members)
        {
            if (!features.ContainsKey(m.FeatureId) || !c.Graph.Nodes.TryGetValue(m.NodeId, out var node)) { stale.Add($"member:{m.FeatureId}:{m.NodeId}"); continue; }
            memberships.RemoveAll(x => x.FeatureId == m.FeatureId && x.NodeId == m.NodeId);
            if (m.Include)
                memberships.Add(new()
                {
                    FeatureId = m.FeatureId,
                    NodeId = m.NodeId,
                    Score = 1,
                    Reason = "добавлено вручную",
                    Role = node.SubKind == "handler" ? FeatureRoles.Entry : node.Tags.Contains("test") ? FeatureRoles.Test : FeatureRoles.Implementation
                });
        }
    }

    static void Dedupe(List<FeatureMembership> memberships)
    {
        var best = memberships.GroupBy(m => (m.FeatureId, m.NodeId)).Select(g => g.OrderByDescending(m => m.Score).First()).ToList();
        memberships.Clear();
        memberships.AddRange(best);
    }

    // ---- .projmap/views.rules.json ----

    internal sealed record RulesFile
    {
        public int SchemaVersion { get; init; } = 1;
        public List<CustomRule> Rules { get; init; } = [];
        public ViewsOverrides? Overrides { get; init; }
    }

    internal sealed record RuleAssign(string Tier, string? Role = null, string? Execution = null);

    internal sealed record RuleMatch
    {
        public string? PathGlob { get; init; }
        public string? Language { get; init; }
        public string? Tag { get; init; }
        /// Regular expression over the file text.
        public string? Text { get; init; }
        public List<RuleMatch>? All { get; init; }
        public List<RuleMatch>? Any { get; init; }
        public RuleMatch? Not { get; init; }
        Regex? glob, text;
        public bool Matches(RepoFile f, ScanContext c)
        {
            if (PathGlob != null && !(glob ??= Glob(PathGlob)).IsMatch(f.Path)) return false;
            if (Language != null && !string.Equals(Language, f.Language, StringComparison.OrdinalIgnoreCase)) return false;
            if (Tag != null && !f.Tags.Contains(Tag)) return false;
            if (Text != null && (f.Text(c) is not { } body || !(text ??= new Regex(Text, RegexOptions.Multiline, TimeSpan.FromSeconds(1))).IsMatch(body))) return false;
            if (All != null && !All.All(m => m.Matches(f, c))) return false;
            if (Any != null && !Any.Any(m => m.Matches(f, c))) return false;
            return Not == null || !Not.Matches(f, c);
        }
    }

    internal sealed record CustomRule
    {
        public required string Id { get; init; }
        public required RuleMatch Match { get; init; }
        public required RuleAssign Assign { get; init; }
        public double Score { get; init; } = 0.9;
        public string? Reason { get; init; }
        public bool Matches(RepoFile f, ScanContext c) => Match.Matches(f, c);
    }

    static (List<CustomRule>, ViewsOverrides?) ReadRulesFile(ScanContext c)
    {
        var path = Path.Combine(c.Options.Root, ".projmap", "views.rules.json");
        if (!File.Exists(path)) return ([], null);
        try
        {
            var file = JsonSerializer.Deserialize<RulesFile>(File.ReadAllText(path), new JsonSerializerOptions(GraphJson.Options)
            { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) ?? new();
            var rules = new List<CustomRule>();
            foreach (var rule in file.Rules)
                if (!KnownTiers.Contains(rule.Assign.Tier)) c.Warn("views", $"Rule {rule.Id}: unknown tier '{rule.Assign.Tier}'.", RulesPath);
                else rules.Add(rule with { Score = Math.Clamp(rule.Score, 0, 0.95) });
            return (rules, file.Overrides);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            c.Warn("views", "Invalid rules file: " + ex.Message, RulesPath);
            return ([], null);
        }
    }

    /// `**` any depth, `*` inside one segment, `?` one character, `{a,b}` alternatives; case-insensitive, `/` separators.
    internal static Regex Glob(string glob)
    {
        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var ch = glob[i];
            if (ch == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '/') { i++; sb.Append("(?:.*/)?"); } else sb.Append(".*");
            }
            else if (ch == '*') sb.Append("[^/]*");
            else if (ch == '?') sb.Append("[^/]");
            else if (ch == '{') sb.Append("(?:");
            else if (ch == '}') sb.Append(')');
            else if (ch == ',' && sb.ToString().LastIndexOf("(?:", StringComparison.Ordinal) > sb.ToString().LastIndexOf(')')) sb.Append('|');
            else sb.Append(Regex.Escape(ch.ToString()));
        }
        return new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

/// <summary>Public face of the views rules for tools and tests.</summary>
public static class ViewRules
{
    /// Project rules file, relative to the repository root. Read by name; the `.projmap` folder is never scanned.
    public const string RulesPath = ViewsScanner.RulesPath;
    /// `**` any depth, `*` inside one segment, `?` one character, `{a,b}` alternatives; case-insensitive, `/` separators.
    public static System.Text.RegularExpressions.Regex Glob(string glob) => ViewsScanner.Glob(glob);
}
