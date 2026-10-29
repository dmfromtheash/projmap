namespace ProjMap.App;

/// Command line: projmap [folder] [--test] [--cdp-port N] [--action-log file]
///  --test        window on the non-primary (laptop) display, full screen; shell actions
///                (open in editor, reveal, folder picker) are only logged, never executed
///  --cdp-port    expose the embedded browser over the Chrome DevTools Protocol (automation)
///  --action-log  where --test writes the shell actions it skipped
sealed record LaunchOptions(string? Root, bool Test, int? CdpPort, string? ActionLog);

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(Parse(args)));
    }

    static LaunchOptions Parse(string[] args)
    {
        bool test = false; int? port = null; string? log = null, folder = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--test": test = true; break;
                case "--cdp-port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p): port = p; i++; break;
                case "--action-log" when i + 1 < args.Length: log = args[++i]; break;
                default: if (!args[i].StartsWith('-')) folder ??= args[i]; break;
            }
        }
        return new LaunchOptions(ResolveRoot(folder), test, port, log);
    }

    /// Explicit folder argument wins; otherwise the git root above the current directory.
    /// Returns null when launched outside any project (double-click on the exe) — the start screen is shown then.
    static string? ResolveRoot(string? arg)
    {
        if (arg != null && Directory.Exists(arg))
            return FindGitRoot(Path.GetFullPath(arg)) ?? Path.GetFullPath(arg);

        var cwd = Environment.CurrentDirectory;
        if (string.Equals(Path.TrimEndingDirectorySeparator(cwd), Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
            return null;
        return FindGitRoot(cwd);
    }

    internal static string? FindGitRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
        return null;
    }
}
