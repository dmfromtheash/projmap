using System.Globalization;
using System.Text;
using ProjMap.Core;

namespace ProjMap.Cli;

internal static class ProblemsMarkdown
{
    public static string Render(ProjectGraph graph)
    {
        var report = graph.Problems!;
        var nodes = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var text = new StringBuilder();
        static string Escape(string value) => value.Replace("\r", " ").Replace("\n", " ")
            .Replace("\\", "\\\\").Replace("`", "\\`").Replace("*", "\\*")
            .Replace("_", "\\_").Replace("[", "\\[").Replace("]", "\\]")
            .Replace("<", "&lt;").Replace(">", "&gt;");
        text.Append("# Отчёт о проблемах: ").AppendLine(Escape(graph.Project.Name)).AppendLine();
        text.AppendLine("Кандидаты на проверку: статический граф не доказывает, что код можно удалить.").AppendLine();
        text.AppendLine("Горячие файлы: коммиты × сложность; показаны только положительные оценки.");
        text.AppendLine("Циклы — компоненты сильной связности, а не отдельные пути по кругу.").AppendLine();
        foreach (var (kind, count) in report.Counts)
        {
            var items = report.Items.Where(i => i.Kind == kind).ToList();
            text.Append("## ").Append(kind).Append(": ").Append(count.ToString(CultureInfo.InvariantCulture))
                .Append(" (показано ").Append(items.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(")").AppendLine();
            foreach (var item in items)
            {
                text.Append("- **").Append(item.Severity).Append("**: ").AppendLine(Escape(item.Reason));
                foreach (var id in item.NodeIds)
                {
                    text.Append("  - ").Append(Escape(id));
                    if (nodes.TryGetValue(id, out var node) && node.Path != null)
                        text.Append(" — ").Append(Escape(node.Path)).Append(':').Append(node.Line ?? 1);
                    text.AppendLine();
                }
                if (item.Edges == null) continue;
                foreach (var edge in item.Edges)
                {
                    text.Append("  - ").Append(edge.Kind).Append(": ").Append(Escape(edge.Source))
                        .Append(" → ").AppendLine(Escape(edge.Target));
                    foreach (var evidence in edge.Evidence)
                        text.Append("    - ").Append(Escape(evidence.Path)).Append(':').Append(evidence.Line)
                            .Append(" — ").AppendLine(Escape(evidence.Snippet ?? ""));
                }
            }
            text.AppendLine();
        }
        if (graph.Warnings.Count > 0)
        {
            text.AppendLine("## Предупреждения сканирования").AppendLine();
            foreach (var warning in graph.Warnings)
                text.Append("- ").Append(Escape(warning.Scanner)).Append(": ").Append(Escape(warning.Path ?? ""))
                    .Append(" — ").AppendLine(Escape(warning.Message));
        }
        return text.ToString().Replace("\r\n", "\n");
    }
}
