using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProjMap.App;

/// Per-user state under %LOCALAPPDATA%\projmap: recent projects and cached graphs.
/// Scanned repositories are never written to.
static class AppStore
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "projmap");
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

    public static string CachePath(string root)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant())))[..16];
        return Path.Combine(Dir, "cache", hash + ".json");
    }
}
