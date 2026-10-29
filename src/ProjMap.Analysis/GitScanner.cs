using System.Globalization;
using ProjMap.Core;

namespace ProjMap.Analysis;

internal sealed class GitScanner : IRepositoryScanner
{
    public string Name => "git";
    public async Task ScanAsync(ScanContext c, CancellationToken ct)
    {
        if (!c.IsGit) return;
        var head = await GitProcess.RunAsync(c.Options.Root, ct, "rev-parse", "HEAD");
        var branch = await GitProcess.RunAsync(c.Options.Root, ct, "rev-parse", "--abbrev-ref", "HEAD");
        c.Graph.Project = c.Graph.Project with { GitHead = head.ExitCode == 0 ? head.Output.Trim() : null,
            GitBranch = branch.ExitCode == 0 ? branch.Output.Trim() : null };
        if (head.ExitCode != 0 || c.Options.GitMonths == 0 || c.Options.MaxCommits == 0) return;
        var log = await GitProcess.RunAsync(c.Options.Root, ct, "log", "--no-renames", "--no-ext-diff", "--name-only", "-z",
            "--format=%x1e%H%x00%aI%x00%aN <%aE>%x00%s%x00", "--since=" + DateTimeOffset.UtcNow.AddMonths(-c.Options.GitMonths).ToString("O", CultureInfo.InvariantCulture),
            "--max-count=" + c.Options.MaxCommits.ToString(CultureInfo.InvariantCulture), "--", ".");
        if (log.ExitCode != 0) { c.Warn(Name, log.Error.Trim()); return; }
        var touches = new Dictionary<string, List<GitCommit>>(StringComparer.Ordinal);
        var pairs = new Dictionary<(string A, string B), int>();
        // git log emits repository-root paths, even when scanning a subdirectory.
        var top = await GitProcess.RunAsync(c.Options.Root, ct, "rev-parse", "--show-toplevel");
        var gitRoot = top.ExitCode == 0 ? top.Output.Trim() : c.Options.Root;
        foreach (var section in log.Output.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
        {
            ct.ThrowIfCancellationRequested();
            var parts = section.Split('\0');
            if (parts.Length < 4 || !DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            var allFiles = parts.Skip(4).Select(p => p.TrimStart('\r', '\n')).Where(p => p.Length > 0).Distinct().ToList();
            var files = allFiles.Select(p => c.Relative(Path.Combine(gitRoot, p))).Where(c.Files.ContainsKey)
                .Select(p => c.Files[p].Path).Distinct().Order(StringComparer.Ordinal).ToList();
            var commit = new GitCommit(parts[0], parts[3], parts[2], date, files);
            c.Commits.Add(commit);
            foreach (var path in files)
            {
                if (!touches.TryGetValue(path, out var entries)) touches[path] = entries = [];
                entries.Add(commit);
            }
            if (allFiles.Count > 40) continue;
            for (var i = 0; i < files.Count; i++)
                for (var j = i + 1; j < files.Count; j++)
                    pairs[(files[i], files[j])] = pairs.GetValueOrDefault((files[i], files[j])) + 1;
        }
        foreach (var (path, commits) in touches)
        {
            var id = ScanContext.FileId(path);
            var node = c.Graph.Nodes[id];
            c.Graph.Nodes[id] = node with { Metrics = node.Metrics with { Commits = commits.Count,
                Authors = commits.Select(x => x.Author).Distinct(StringComparer.OrdinalIgnoreCase).Count(), LastChange = commits.Max(x => x.Date) } };
        }
        foreach (var ((a, b), count) in pairs)
        {
            if (count < 3 || (double)count / (touches[a].Count + touches[b].Count - count) < 0.3) continue;
            var evidence = touches[a].Where(x => x.Files.Contains(b)).OrderByDescending(x => x.Date).Take(5).ToList();
            c.Edge(EdgeKinds.CoChange, ScanContext.FileId(a), ScanContext.FileId(b), null, count);
            var id = $"{EdgeKinds.CoChange}:{ScanContext.FileId(a)}->{ScanContext.FileId(b)}";
            c.Graph.Edges[id] = c.Graph.Edges[id] with { Evidence = evidence.Select(x => new SourceLocation(a, 1, x.Hash[..8] + " " + x.Subject)).ToList() };
        }
    }
}
