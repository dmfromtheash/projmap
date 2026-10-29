using System.Globalization;
using ProjMap.Analysis;
using ProjMap.Core;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
return await ProjMap.Cli.CommandLine.RunAsync(args, Console.Out, Console.Error, cancellation.Token);

namespace ProjMap.Cli
{
    public static class CommandLine
    {
        public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct = default)
        {
            try
            {
                if (args.Length < 2 || args[0] is not ("scan" or "stats"))
                {
                    await error.WriteLineAsync("Usage: projmap-cli scan <root> [-o <file>] [--git-months N] [--no-members] [--timings]\n       projmap-cli stats <graph.json>");
                    return 2;
                }
                if (args[0] == "stats")
                {
                    if (args.Length != 2) throw new ArgumentException("stats expects exactly one graph file.");
                    var graph = GraphJson.Deserialize(await File.ReadAllTextAsync(args[1], ct)) ?? throw new InvalidDataException("Empty graph JSON.");
                    await output.WriteLineAsync($"Project: {graph.Project.Name}");
                    await output.WriteLineAsync($"Scan: {graph.Project.ScanMilliseconds} ms");
                    await output.WriteLineAsync($"Nodes: {graph.Nodes.Count}");
                    foreach (var kind in graph.Nodes.GroupBy(n => n.Kind).OrderBy(g => g.Key)) await output.WriteLineAsync($"  {kind.Key}: {kind.Count()}");
                    await output.WriteLineAsync($"Edges: {graph.Edges.Count}");
                    foreach (var kind in graph.Edges.GroupBy(e => e.Kind).OrderBy(g => g.Key)) await output.WriteLineAsync($"  {kind.Key}: {kind.Count()}");
                    await output.WriteLineAsync($"Roadmap: {graph.Roadmap.Count}");
                    foreach (var status in graph.Roadmap.GroupBy(r => r.Status).OrderBy(g => g.Key)) await output.WriteLineAsync($"  {status.Key}: {status.Count()}");
                    await output.WriteLineAsync($"Database: {graph.Database?.Engine ?? "none"}, {graph.Database?.Tables.Count ?? 0} tables");
                    await output.WriteLineAsync($"Warnings: {graph.Warnings.Count}");
                    return 0;
                }
                var root = Path.GetFullPath(args[1]);
                string? destination = null;
                var months = 12; var members = true; var timings = false;
                for (var i = 2; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "-o" when i + 1 < args.Length: destination = Path.GetFullPath(args[++i]); break;
                        case "--git-months" when i + 1 < args.Length:
                            if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out months) || months < 0 || months > 1200)
                                throw new ArgumentException("--git-months must be an integer from 0 to 1200.");
                            break;
                        case "--no-members": members = false; break;
                        case "--timings": timings = true; break;
                        default: throw new ArgumentException("Unknown or incomplete option: " + args[i]);
                    }
                }
                destination ??= Path.Combine(root, ".projmap", "graph.json");
                var graphResult = await ProjectScanner.ScanAsync(new(root) { GitMonths = months, IncludeMembers = members,
                    Progress = new InlineProgress(p => error.WriteLine($"[{p.Fraction:P0}] {p.Stage} {p.Detail}")) }, ct);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using (var stream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                    await GraphJson.WriteAsync(graphResult, stream, ct);
                await output.WriteLineAsync($"{destination}\n{graphResult.Nodes.Count} nodes, {graphResult.Edges.Count} edges; {graphResult.Project.ScanMilliseconds} ms; {graphResult.Warnings.Count} warnings");
                foreach (var warning in graphResult.Warnings.Take(20)) await error.WriteLineAsync($"Warning [{warning.Scanner}] {warning.Path}: {warning.Message}");
                if (timings)
                    foreach (var (stage, ms) in graphResult.Project.Timings!.Milliseconds)
                        await output.WriteLineAsync($"  {stage}: {ms.ToString("F3", CultureInfo.InvariantCulture)} ms");
                var ignore = Path.Combine(root, ".gitignore");
                if (File.Exists(ignore) && !(await File.ReadAllTextAsync(ignore, ct)).Split('\n').Any(l => l.Trim().TrimStart('/') is ".projmap/" or ".projmap"))
                    await error.WriteLineAsync("Hint: add .projmap/ to .gitignore to exclude generated scan output.");
                return 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { await error.WriteLineAsync("Scan cancelled."); return 130; }
            catch (Exception ex) { await error.WriteLineAsync("Error: " + ex.Message); return 1; }
        }
        private sealed class InlineProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
        { public void Report(ScanProgress value) => report(value); }
    }
}
