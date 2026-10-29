using System.Text.Json;
using System.Text.RegularExpressions;
using ProjMap.Core;

namespace ProjMap.Analysis;

/// <summary>Bounded templates over declarations, author text and existing views. Rebuilt after views,
/// never cached as facts. No translation service, body interpretation, or runtime claims.</summary>
internal sealed class ExplanationScanner : IRepositoryScanner
{
    public string Name => "explanations";

    public Task ScanAsync(ScanContext c, CancellationToken ct)
    {
        if (c.Graph.Views is not { } views) return Task.CompletedTask;
        var result = new ExplanationsInfo();
        var sources = new Dictionary<string, int>(StringComparer.Ordinal);
        var projectWords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var wordOrigins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var corrections = new List<(ExplanationOverride Value, string Origin)>();
        foreach (var (overrides, origin) in new[] { (c.ProjectViewOverrides, ViewsScanner.RulesPath), (c.Options.ViewOverrides, "owner") })
        {
            if (overrides == null) continue;
            foreach (var (word, text) in overrides.ExplanationWords)
                if (!string.IsNullOrWhiteSpace(text)) { projectWords[word] = text; wordOrigins[word] = origin; }
            corrections.AddRange(overrides.Explanations.Select(value => (value, origin)));
        }
        int Source(ExplanationSource value)
        {
            var key = JsonSerializer.Serialize(value, GraphJson.Options);
            if (sources.TryGetValue(key, out var existing)) return existing;
            var id = result.Sources.Count; sources[key] = id; result.Sources.Add(value); return id;
        }
        string? Hash(string? path) => path != null ? c.State.Hashes.GetValueOrDefault(path) : null;
        // "self" resolves against the profile's graph node (path/line/declarations), avoiding 20k copies.
        int Location(Node node) => Source(new() { Kind = "declaration", Reference = "self", RuleId = "structure" });
        int Comment(NodeDeclaration declaration) => Source(new()
        {
            Kind = "comment", Path = declaration.Path, Line = declaration.SummaryLine ?? declaration.Line,
            SourceHash = Hash(declaration.Path), Text = declaration.Summary
        });
        var nodes = c.Graph.Nodes.Values.Where(n => n.Kind is NodeKinds.Module or NodeKinds.Folder or NodeKinds.File or NodeKinds.Type or NodeKinds.Member)
            .OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
        var tiers = views.Assignments.ToDictionary(t => t.NodeId, StringComparer.Ordinal);
        var features = views.Features.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var memberships = views.Memberships.ToLookup(m => m.NodeId, StringComparer.Ordinal);
        var entries = (views.EntryPoints ?? []).SelectMany(e => e.HandlerIds.Select(id => (id, e)))
            .ToLookup(x => x.id, x => x.e, StringComparer.Ordinal);
        var declarationsByPath = nodes.Where(n => n.Kind == NodeKinds.Type).SelectMany(n => (n.Declarations ?? []).Select(d => (Node: n, Declaration: d)))
            .ToLookup(x => x.Declaration.Path, StringComparer.Ordinal);
        var gitByFile = c.Commits.SelectMany(commit => commit.Files.Select(path => (path, commit)))
            .ToLookup(x => x.path, x => x.commit, StringComparer.Ordinal);
        var stale = new List<string>(views.StaleOverrides);
        var applied = new HashSet<int>();
        var anchorNodes = nodes.Where(n => n.Anchor != null).ToLookup(n => n.Anchor!, StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            ct.ThrowIfCancellationRequested();
            var (translated, keys) = ExplanationDictionary.Name(node, projectWords);
            var nameSources = keys.Distinct(StringComparer.OrdinalIgnoreCase).Select(word => Source(new()
            {
                Kind = wordOrigins.ContainsKey(word) ? "ownerDictionary" : "dictionary", RuleId = "name:" + word.ToLowerInvariant(),
                Reference = wordOrigins.GetValueOrDefault(word), Text = projectWords.GetValueOrDefault(word) ?? ExplanationDictionary.Words[word].Name
            })).ToList();
            if (nameSources.Count == 0) nameSources.Add(Source(new() { Kind = "dictionary", RuleId = "unknown-name", Text = "Неизвестные слова сохранены без расшифровки" }));
            var display = new ExplanationClaim(string.IsNullOrWhiteSpace(translated) ? node.Name : translated, ExplanationConfidence.Guessed, nameSources);
            var description = new ExplanationClaim(BaseDescription(node), ExplanationConfidence.Derived, [Location(node)]);
            var claims = new List<ExplanationClaim>();
            var comments = (node.Declarations ?? []).Where(d => d.Summary != null).ToList();
            if (node.Kind == NodeKinds.File && node.Path != null)
            {
                // Match the declared owner's name, never the first commented child (HexKeyRow is not HexWorkshop).
                var stem = ExplanationDictionary.Stem(node.Name, true);
                foreach (var part in declarationsByPath[node.Path].Where(p => stem == p.Node.Name || stem.StartsWith(p.Node.Name + ".", StringComparison.Ordinal)))
                {
                    if (part.Declaration.Summary != null)
                        claims.Add(new($"Комментарий к {part.Node.Name} в этом файле: {part.Declaration.Summary}", ExplanationConfidence.Known, [Comment(part.Declaration)]));
                    if (part.Node.Declarations?.Count > 1)
                        description = new($"Файл {node.Language ?? "проекта"} содержит часть блока {part.Node.Name}; остальные объявления доступны отдельно.",
                            ExplanationConfidence.Derived, [Location(node)]);
                }
            }
            foreach (var declaration in comments)
                claims.Add(new("В комментарии автора: " + declaration.Summary, ExplanationConfidence.Known, [Comment(declaration)]));
            if (node.Kind == NodeKinds.Type && node.Declarations?.Count > 1)
                description = new("Блок кода объявлен в нескольких файлах; комментарии к его частям приведены отдельно.",
                    ExplanationConfidence.Derived, [Location(node)]);
            else if (comments.FirstOrDefault(d => HasRussian(d.Summary!)) is { } russian)
                description = new("По комментарию автора: «" + OneSentence(russian.Summary!) + "».", ExplanationConfidence.Known, [Comment(russian)]);
            else if (node.SummarySource is { } markdown && node.Summary != null)
            {
                var source = Source(new() { Kind = "markdown", Path = markdown.Path, Line = markdown.Line, Text = markdown.Snippet,
                    SourceHash = Hash(markdown.Path) });
                claims.Add(new("В документе: " + node.Summary, ExplanationConfidence.Known, [source]));
                if (HasRussian(node.Summary)) description = new("По документу: «" + OneSentence(node.Summary) + "».", ExplanationConfidence.Known, [source]);
            }

            if (description.Confidence != ExplanationConfidence.Known && keys.Count > 0 && comments.Count == 0 &&
                node.Kind is NodeKinds.Type or NodeKinds.Member && node.SubKind is not ("property" or "field" or "event" or "ctor" or "function") &&
                ExplanationDictionary.Purpose(node) is { } purpose)
                description = new("Предположительно по имени: «" + purpose + "»; точное назначение требует проверки.", ExplanationConfidence.Guessed, nameSources);

            foreach (var entry in entries[node.Id])
            {
                var source = Source(new() { Kind = "handlerLabel", Path = entry.Path, Line = entry.LabelLine ?? entry.Line, EndLine = entry.EndLine,
                    SourceHash = Hash(entry.Path), Reference = entry.Id, Text = entry.Label ?? entry.Event ?? entry.ControlName });
                if (entry.LabelKind == "literal" && !string.IsNullOrWhiteSpace(entry.Label))
                {
                    // A visible caption is known text; registration says nothing about a successful click.
                    if (entries[node.Id].Count() == 1) display = new("Реакция на «" + entry.Label + "»", ExplanationConfidence.Known, [source]);
                    claims.Add(new("В регистрации указана подпись «" + entry.Label + "».", ExplanationConfidence.Known, [source]));
                }
                else if (entry.Label != null)
                    claims.Add(new("Подпись вычисляется выражением: " + entry.Label, ExplanationConfidence.Derived, [source]));
                if (description.Confidence != ExplanationConfidence.Known)
                    description = new("Обработчик зарегистрирован в исходнике; условия его запуска и видимость элемента не проверены.", ExplanationConfidence.Derived, [source]);
            }

            // Context belongs to the file. Do not attribute a file's role/history to each method or helper type.
            if (node.Kind == NodeKinds.File && tiers.TryGetValue(node.Id, out var tier) && tier.Decision != TierDecisions.Unknown)
            {
                var source = Source(new() { Kind = "tier", Path = node.Path, Reference = tier.NodeId, RuleId = tier.Evidence.FirstOrDefault()?.Rule,
                    Text = tier.Reason });
                var guessed = tier.Decision == TierDecisions.Guess;
                var text = tier.Tier == Tiers.Mixed ? "У файла признаки нескольких ярусов; назначение неоднозначно." :
                    (guessed ? "Предположительно" : "По классификации") + " файл относится к ярусу «" + (views.Tiers.FirstOrDefault(t => t.Id == tier.Tier)?.Name ?? tier.Tier) + "».";
                claims.Add(new(text, guessed ? ExplanationConfidence.Guessed : ExplanationConfidence.Derived, [source]));
                if (description.Confidence != ExplanationConfidence.Known && tier.Decision == TierDecisions.Automatic &&
                    tier.Role is "endpoint" or "view" or "viewModel")
                    description = new(tier.Role switch
                    {
                        "endpoint" => "По признакам структуры это файл обработки серверных запросов; конкретные операции требуют отдельного анализа.",
                        "viewModel" => "По признакам структуры файл содержит данные и логику экрана.",
                        _ => "По признакам структуры это файл интерфейса; конкретные действия требуют отдельного анализа."
                    }, ExplanationConfidence.Derived, [source]);
            }
            foreach (var membership in memberships[node.Id])
            {
                var feature = features[membership.FeatureId];
                var source = Source(new() { Kind = "feature", Reference = feature.Id, Text = membership.Reason,
                    RuleId = "membership:" + membership.Role });
                // A manual display-name change does not confirm automatically selected membership.
                var manual = membership.Score == 1;
                claims.Add(new((manual ? "Вручную включён в группу" : "Кандидат в группу по назначению") + " «" + feature.Name + "».",
                    manual ? ExplanationConfidence.Derived : ExplanationConfidence.Guessed, [source]));
            }
            if (node.Kind == NodeKinds.File && node.Path != null && gitByFile[node.Path].Any())
            {
                var commits = gitByFile[node.Path].ToList();
                var ids = commits.Select(commit => Source(new() { Kind = "git", CommitId = commit.Hash, Text = commit.Subject,
                    Reference = "views.timeline" })).ToList();
                claims.Add(new($"В окне истории файл изменялся в {commits.Count} коммитах; это не описание сегодняшнего поведения кода.",
                    ExplanationConfidence.Derived, ids));
            }
            for (var i = 0; i < corrections.Count; i++)
            {
                var (correction, origin) = corrections[i];
                if (correction.Anchor != null)
                {
                    if (node.Anchor != correction.Anchor || anchorNodes[correction.Anchor].Count() != 1) continue;
                }
                else if (node.SubKind == "handler" || node.Id != correction.NodeId) continue;
                if (string.IsNullOrWhiteSpace(correction.DisplayName) && string.IsNullOrWhiteSpace(correction.Description)) continue;
                applied.Add(i);
                var source = Source(new() { Kind = "owner", Reference = origin, Text = correction.DisplayName, RuleId = correction.Anchor ?? correction.NodeId });
                if (!string.IsNullOrWhiteSpace(correction.DisplayName)) display = new(correction.DisplayName, ExplanationConfidence.Known, [source]);
                if (!string.IsNullOrWhiteSpace(correction.Description)) description = new(correction.Description, ExplanationConfidence.Known,
                    [Source(new() { Kind = "owner", Reference = origin, Text = correction.Description, RuleId = correction.Anchor ?? correction.NodeId })]);
            }
            var profile = new NodeExplanation { NodeId = node.Id, DisplayName = display, Description = description, Claims = claims.Count == 0 ? null : claims };
            var profileIndex = result.Nodes.Count; result.Nodes.Add(profile);
            foreach (var name in new[] { node.Name, node.Path, display.Text }.Concat(ExplanationDictionary.Split(node.Name, node.Kind == NodeKinds.File))
                         .Concat(display.Text.Split([' ', '·'], StringSplitOptions.RemoveEmptyEntries))
                         .Concat(entries[node.Id].Select(e => e.Label)).OfType<string>().Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var key = name.ToLowerInvariant();
                if (!result.NameIndex.TryGetValue(key, out var ids)) result.NameIndex[key] = ids = [];
                ids.Add(profileIndex);
            }
        }
        for (var i = 0; i < corrections.Count; i++)
            if (!applied.Contains(i)) stale.Add("explanation:" + (corrections[i].Value.Anchor ?? corrections[i].Value.NodeId ?? "missing-selector"));
        c.Graph.Views = views with { Explanations = result, StaleOverrides = stale.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() };
        return Task.CompletedTask;
    }

    private static bool HasRussian(string text)
    {
        // A Russian feature title inside English prose is not a Russian explanation.
        var russian = Regex.Matches(text, "[А-Яа-яЁё]").Count;
        return russian > 0 && russian >= Regex.Matches(text, "[A-Za-z]").Count;
    }
    private static string OneSentence(string text)
    {
        var end = Regex.Match(text, @"[.!?](?:\s|$)");
        var sentence = end.Success ? text[..end.Index] : text;
        return sentence.Length > 220 ? sentence[..220].TrimEnd() + "…" : sentence;
    }
    private static string BaseDescription(Node node) => node.Kind switch
    {
        NodeKinds.Module => "Часть проекта, объединённая манифестом; назначение нужно уточнить по исходникам.",
        NodeKinds.Folder => "Папка объединяет файлы проекта; назначение нужно уточнить по содержимому.",
        NodeKinds.File => $"Файл {node.Language ?? "проекта"}; назначение не подтверждено комментарием или документом.",
        NodeKinds.Type => node.SubKind switch
        {
            "interface" => "Интерфейс задаёт набор членов, которые могут реализовать другие блоки кода.",
            "enum" => "Перечисление задаёт именованные варианты значения.",
            "delegate" => "Тип описывает форму передаваемого действия.",
            "struct" or "record struct" => "Блок кода описывает значение и связанные с ним члены.",
            _ => "Именованный блок кода объединяет связанные данные и действия."
        },
        _ => node.SubKind switch
        {
            "property" => "Свойство предоставляет значение; это не обязательно отдельное действие пользователя.",
            "field" => "Поле хранит значение внутри блока кода.",
            "event" => "Событие позволяет регистрировать реакции на уведомления.",
            "ctor" => "Конструктор подготавливает новый объект.",
            "handler" => "Реакция зарегистрирована в исходнике; выполнение и видимость элемента не проверены.",
            "function" when node.Language is "TypeScript" or "JavaScript" => "Экспортируемое объявление; текущий анализатор не всегда отличает функцию от данных и не разбирает локальные обработчики.",
            _ => "Действие объявлено в исходнике; его назначение требует проверки."
        }
    };
}
