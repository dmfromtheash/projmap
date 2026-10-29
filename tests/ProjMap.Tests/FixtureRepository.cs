using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ProjMap.Tests;

internal sealed class FixtureRepository : IDisposable
{
    // Temp fixtures stay inside the authorized workspace, never in the user's scanned repos.
    public string Root { get; } = Path.Combine(FindWorkspace(), "tests", "ProjMap.Tests", "obj", "fixtures", Guid.NewGuid().ToString("N"));
    public FixtureRepository()
    {
        Directory.CreateDirectory(Root);
        // Isolate from the enclosing projmap repository (obj is ignored by its gitignore).
        using var process = Process.Start(new ProcessStartInfo("git") { WorkingDirectory = Root,
            ArgumentList = { "init", "-q" }, UseShellExecute = false, CreateNoWindow = true })!;
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Could not initialize fixture git repository.");
    }
    private static string FindWorkspace()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "ProjMap.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("ProjMap workspace not found.");
    }
    public string Write(string path, string content)
    {
        var full = Path.Combine(Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
        return full;
    }
    public Dictionary<string, string> Snapshot() => Directory.GetFiles(Root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(Root, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    public void DisableGit()
    {
        // Rename only inside this unique fixture; an invalid pointer also prevents parent repo discovery.
        Directory.Move(Path.Combine(Root, ".git"), Path.Combine(Root, "git-backup"));
        Write(".git", "gitdir: nonexistent-fixture-git-dir");
        Write(".gitignore", "git-backup/\n");
    }
    public async Task Git(params string[] args)
    {
        using var process = new Process { StartInfo = new("git") { WorkingDirectory = Root, RedirectStandardError = true,
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        // Commit identity is process-local. No user/global git configuration is modified.
        process.StartInfo.Environment["GIT_AUTHOR_NAME"] = "Fixture";
        process.StartInfo.Environment["GIT_AUTHOR_EMAIL"] = "fixture@example.invalid";
        process.StartInfo.Environment["GIT_COMMITTER_NAME"] = "Fixture";
        process.StartInfo.Environment["GIT_COMMITTER_EMAIL"] = "fixture@example.invalid";
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await output + await error);
    }
    public void Dispose()
    {
        var full = Path.GetFullPath(Root);
        var allowed = Path.GetFullPath(Path.Combine(FindWorkspace(), "tests", "ProjMap.Tests", "obj", "fixtures")) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture cleanup path.");
        if (!Directory.Exists(full)) return;
        foreach (var path in Directory.GetFiles(full, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(full, true);
    }
}
