using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProjMap.App;

/// Per-user state under %LOCALAPPDATA%\projmap: recent projects and cached graphs.
/// Scanned repositories are never written to. PROJMAP_HOME moves the whole store elsewhere, so an
/// automated run (perf, e2e) never shares state with the owner's open window.
static class AppStore
{
    public static readonly string Dir = Environment.GetEnvironmentVariable("PROJMAP_HOME") is { Length: > 0 } home
        ? Path.GetFullPath(home)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "projmap");
    static readonly string RecentFile = Path.Combine(Dir, "recent.json");

    public sealed record RecentProject(string Root, string Name, DateTimeOffset OpenedAt);

    public static List<RecentProject> LoadRecent()
    {
        try
        {
            return File.Exists(RecentFile)
                ? JsonSerializer.Deserialize<List<RecentProject>>(File.ReadAllText(RecentFile)) ?? []
                : [];
        }
        catch { return []; }
    }

    public static void TouchRecent(string root)
    {
        var list = LoadRecent().Where(r => !string.Equals(r.Root, root, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Insert(0, new RecentProject(root, Path.GetFileName(Path.TrimEndingDirectorySeparator(root)), DateTimeOffset.Now));
        Directory.CreateDirectory(Dir);
        File.WriteAllText(RecentFile, JsonSerializer.Serialize(list.Where(r => Directory.Exists(r.Root)).Take(12).ToList()));
    }

    public static string CachePath(string root) => Path.Combine(Dir, "cache", Key(root) + ".json");

    static string Key(string root) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant())))[..16];

    /// The owner's tier/feature corrections for one project; kept here, never in the scanned repository.
    static string ViewsOverridesPath(string root) => Path.Combine(Dir, "views", Key(root) + ".json");

    public static ProjMap.Core.ViewsOverrides? LoadViewsOverrides(string root)
    {
        try
        {
            var path = ViewsOverridesPath(root);
            return File.Exists(path) ? JsonSerializer.Deserialize<ProjMap.Core.ViewsOverrides>(File.ReadAllText(path), ProjMap.Core.GraphJson.Options) : null;
        }
        catch { return null; }
    }

    /// Atomic replace: a crash mid-write never leaves a half-written corrections file.
    public static void SaveViewsOverrides(string root, ProjMap.Core.ViewsOverrides overrides)
    {
        var path = ViewsOverridesPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(overrides, ProjMap.Core.GraphJson.Options));
        File.Move(temp, path, overwrite: true);
    }
}
