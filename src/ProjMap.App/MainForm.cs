using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using ProjMap.Analysis;
using ProjMap.Core;

namespace ProjMap.App;

/// Native shell: a window hosting the viewer (WebView2), the scanner, live file watching
/// and the bridge actions the viewer cannot do itself (open in editor, reveal, pick folder).
sealed class MainForm : Form
{
    const string Host = "projmap.local";
    static readonly string[] IgnoredDirs = ["bin", "obj", "node_modules", ".vs", ".idea", ".projmap", "dist", "build", "__pycache__", ".venv", "artifacts", "TestResults"];

    readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(14, 17, 23) };
    readonly Dictionary<string, string> _resources;
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 1500 };
    readonly HashSet<string> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);

    readonly LaunchOptions _opt;
    string? _root;
    byte[]? _graphJson;
    string? _graphHash;
    int _graphVersion;
    FileSystemWatcher? _watcher;
    CancellationTokenSource? _scanCts;
    bool _live = true;
    bool _scanning;
    bool _rescanQueued;
    ScanState? _scanState;
    readonly HashSet<string> _queuedScanPaths = new(StringComparer.OrdinalIgnoreCase);

    public MainForm(LaunchOptions opt)
    {
        _opt = opt;
        _root = opt.Root;
        Text = "projmap";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        BackColor = Color.FromArgb(28, 32, 39);
        MinimumSize = new Size(900, 600);
        if (opt.Test)
        {
            // automated runs never touch the owner's main screen: laptop display, full screen
            var screen = Screen.AllScreens.FirstOrDefault(s => !s.Primary) ?? Screen.PrimaryScreen!;
            StartPosition = FormStartPosition.Manual;
            Bounds = screen.WorkingArea;
            FormBorderStyle = FormBorderStyle.None;
            Load += (_, _) => { Bounds = screen.Bounds; WindowState = FormWindowState.Maximized; };
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Size = new Size((int)(area.Width * 0.85), (int)(area.Height * 0.85));
        }
        Controls.Add(_web);

        _resources = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(n => n.StartsWith("web", StringComparison.Ordinal))
            .ToDictionary(n => n.Replace('\\', '/'), n => n, StringComparer.OrdinalIgnoreCase);

        _debounce.Tick += (_, _) => { _debounce.Stop(); OnFilesSettled(); };
        Load += async (_, _) => await InitWebAsync();
        FormClosing += (_, _) => { _scanCts?.Cancel(); _watcher?.Dispose(); };
    }

    async Task InitWebAsync()
    {
        var envOptions = new CoreWebView2EnvironmentOptions(
            _opt.CdpPort is int port ? $"--remote-debugging-port={port}" : null);
        // a test run gets its own profile so it never shares state with the owner's window
        var profile = Path.Combine(AppStore.Dir, _opt.Test ? "webview-test" : "webview");
        var env = await CoreWebView2Environment.CreateAsync(null, profile, envOptions);
        await _web.EnsureCoreWebView2Async(env);
        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = Debugger.IsAttached;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = true;
        core.AddWebResourceRequestedFilter($"https://{Host}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnResourceRequested;
        core.WebMessageReceived += OnWebMessage;
        core.Navigate($"https://{Host}/web/index.html");
    }

    void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var path = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
        var env = _web.CoreWebView2.Environment;
        Stream? body = null;
        string type = "application/octet-stream";

        if (path.StartsWith("graph.json", StringComparison.OrdinalIgnoreCase) && _graphJson != null)
        {
            body = new MemoryStream(_graphJson, writable: false);
            type = "application/json";
        }
        else if (path.Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            // source text for the built-in file viewer: only inside the project, only reasonable sizes
            body = ReadProjectFile(System.Web.HttpUtility.ParseQueryString(new Uri(e.Request.Uri).Query)["p"]);
            type = "text/plain; charset=utf-8";
        }
        else if (_resources.TryGetValue(path, out var name))
        {
            body = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            type = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".html" => "text/html; charset=utf-8",
                ".js" => "text/javascript; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".svg" => "image/svg+xml",
                ".json" => "application/json",
                ".woff2" => "font/woff2",
                _ => type,
            };
        }

        e.Response = body == null
            ? env.CreateWebResourceResponse(null, 404, "Not Found", "")
            : env.CreateWebResourceResponse(body, 200, "OK", $"Content-Type: {type}\nCache-Control: no-store");
    }

    Stream? ReadProjectFile(string? rel)
    {
        if (_root == null || string.IsNullOrEmpty(rel)) return null;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, rel));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return null;
        if (new FileInfo(full).Length > 3 * 1024 * 1024) return new MemoryStream("(файл больше 3 МБ — откройте его в редакторе)"u8.ToArray());
        try { return new MemoryStream(File.ReadAllBytes(full), writable: false); }
        catch (IOException) { return null; }
    }

    // ---- viewer → host ----

    void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var m = doc.RootElement;
        string Str(string key) => m.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

        switch (Str("type"))
        {
            case "ready":
                Post(new { type = "recent", items = AppStore.LoadRecent() });
                if (_root != null) OpenProject(_root);
                else Post(new { type = "start" });
                break;
            case "openProject":
                OpenProject(Str("root"));
                break;
            case "pickFolder" when _opt.Test:
                LogAction("pickFolder", "");
                break;
            case "pickFolder":
                using (var dlg = new FolderBrowserDialog { Description = "Папка проекта", UseDescriptionForTitle = true })
                    if (dlg.ShowDialog(this) == DialogResult.OK) OpenProject(dlg.SelectedPath);
                break;
            case "rescan":
                _ = ScanAsync();
                break;
            case "loaded":
                StartDeferredScan();
                break;
            case "setLive":
                _live = m.TryGetProperty("on", out var on) && on.GetBoolean();
                break;
            case "open":
                OpenInEditor(Str("path"), m.TryGetProperty("line", out var ln) && ln.ValueKind == JsonValueKind.Number ? ln.GetInt32() : 0);
                break;
            case "reveal":
                Reveal(Str("path"));
                break;
            case "title":
                Text = Str("text");
                break;
        }
    }

    void Post(object message) => _web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, GraphJson.Options));

    // ---- project lifecycle ----

    void OpenProject(string root)
    {
        if (!Directory.Exists(root)) { Post(new { type = "error", message = $"Папка не найдена: {root}" }); return; }
        root = Program.FindGitRoot(Path.GetFullPath(root)) ?? Path.GetFullPath(root);
        AppStore.TouchRecent(root);
        // the same project again (e.g. from «Недавние»): just show what we have, no new scan
        if (string.Equals(root, _root, StringComparison.OrdinalIgnoreCase) && _graphJson != null)
        {
            Post(new { type = "graph", version = ++_graphVersion, root, cached = false });
            return;
        }
        _scanCts?.Cancel();
        _root = root;
        _scanState = null;
        _queuedScanPaths.Clear();
        _graphJson = null;
        _graphHash = null;
        Text = $"projmap — {Path.GetFileName(root)}";
        StartWatching(root);

        var cache = AppStore.CachePath(root);
        if (File.Exists(cache))
        {
            // show the cached picture first; the background rescan starts once the viewer has it
            // (a scan running at the same time would delay serving 30 MB to the viewer by minutes)
            _graphJson = File.ReadAllBytes(cache);
            _scanAfterLoad = true;
            Post(new { type = "graph", version = ++_graphVersion, root, cached = true });
            _ = Task.Delay(15000).ContinueWith(_ => BeginInvoke(StartDeferredScan)); // fallback if the viewer never reports
        }
        else
        {
            Post(new { type = "project", root });
            _ = ScanAsync();
        }
    }

    bool _scanAfterLoad;
    void StartDeferredScan()
    {
        if (!_scanAfterLoad) return;
        _scanAfterLoad = false;
        _ = ScanAsync();
    }

    async Task ScanAsync(IEnumerable<string>? changedPaths = null)
    {
        if (_root == null) return;
        if (changedPaths != null) _queuedScanPaths.UnionWith(changedPaths);
        if (_scanning) { _rescanQueued = true; return; }
        _scanning = true;
        _scanCts = new CancellationTokenSource();
        var root = _root;
        var paths = _queuedScanPaths.ToArray();
        _queuedScanPaths.Clear();
        var progress = new Progress<ScanProgress>(p => Post(new { type = "progress", stage = p.Stage, fraction = p.Fraction, detail = p.Detail }));
        try
        {
            var sw = Stopwatch.StartNew();
            var result = await Task.Run(() => ProjectScanner.ScanAsync(new ScanOptions(root) { Progress = progress }, _scanState, paths, _scanCts.Token));
            if (root != _root) return;
            _scanState = result.State;
            var graph = result.Graph;
            // the scan time stamp changes every run; compare the content without it
            var (json, hash) = await Task.Run(() =>
            {
                var bytes = GraphJson.SerializeCompact(graph);
                var stable = GraphJson.Serialize(graph with { Project = graph.Project with { ScannedAt = default, ScanMilliseconds = 0, Timings = null } });
                return (bytes, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(stable))));
            });
            Directory.CreateDirectory(Path.GetDirectoryName(AppStore.CachePath(root))!);
            await File.WriteAllBytesAsync(AppStore.CachePath(root), json);
            if (hash == _graphHash)
            {
                // nothing changed in the project: don't make the viewer reload 30 MB and redraw
                Post(new { type = "unchanged", ms = sw.ElapsedMilliseconds });
                return;
            }
            _graphHash = hash;
            _graphJson = json;
            Post(new { type = "graph", version = ++_graphVersion, root, cached = false, ms = sw.ElapsedMilliseconds });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Post(new { type = "error", message = "Сканирование не удалось: " + ex.Message });
        }
        finally
        {
            _scanning = false;
            if (_rescanQueued) { _rescanQueued = false; _ = ScanAsync(); }
        }
    }

    // ---- live sync ----

    void StartWatching(string root)
    {
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            InternalBufferSize = 64 * 1024,
        };
        FileSystemEventHandler h = (_, e) => OnFsEvent(e.FullPath);
        _watcher.Changed += h;
        _watcher.Created += h;
        _watcher.Deleted += h;
        _watcher.Renamed += (_, e) => { OnFsEvent(e.OldFullPath); OnFsEvent(e.FullPath); };
        _watcher.Error += (_, _) => OnFsEvent(root);
        _watcher.EnableRaisingEvents = true;
    }

    void OnFsEvent(string fullPath)
    {
        if (!_live || _root == null || !IsRelevant(_root, fullPath)) return;
        BeginInvoke(() =>
        {
            _pendingChanges.Add(Path.GetRelativePath(_root, fullPath).Replace('\\', '/'));
            _debounce.Stop();
            _debounce.Start();
        });
    }

    static bool IsRelevant(string root, string fullPath)
    {
        var rel = Path.GetRelativePath(root, fullPath);
        var parts = rel.Split(Path.DirectorySeparatorChar);
        if (parts[0] == ".git")
            return parts.Length > 1 && (parts[1] == "HEAD" || parts[1] == "refs" || parts[1] == "packed-refs"); // commits and branch switches
        return !parts.Any(p => IgnoredDirs.Contains(p, StringComparer.OrdinalIgnoreCase));
    }

    void OnFilesSettled()
    {
        var paths = _pendingChanges.ToArray();
        var changed = paths.Where(p => !p.StartsWith(".git/")).Take(50).ToArray();
        _pendingChanges.Clear();
        Post(new { type = "changed", paths = changed });
        _ = ScanAsync(paths);
    }

    // ---- shell actions ----

    string? Full(string rel) => _root == null || string.IsNullOrEmpty(rel) ? _root : Path.GetFullPath(Path.Combine(_root, rel));

    void OpenInEditor(string rel, int line)
    {
        var path = Full(rel);
        if (path == null || !File.Exists(path)) { Reveal(rel); return; }
        if (_opt.Test) { LogAction("open", $"{path}:{line}"); return; }
        // off the UI thread: waiting for VS Code must never freeze the window
        _ = Task.Run(() =>
        {
            try
            {
                // VS Code if available (code.cmd on PATH), otherwise the default app.
                var psi = new ProcessStartInfo("cmd.exe", $"/c code -g \"{path}:{Math.Max(1, line)}\"")
                { CreateNoWindow = true, UseShellExecute = false };
                using var p = Process.Start(psi)!;
                if (!p.WaitForExit(8000) || p.ExitCode != 0) throw new InvalidOperationException();
            }
            catch
            {
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                catch (Exception ex) { BeginInvoke(() => Post(new { type = "error", message = "Не удалось открыть файл: " + ex.Message })); }
            }
        });
    }

    void Reveal(string rel)
    {
        var path = Full(rel);
        if (path == null) return;
        if (_opt.Test) { LogAction("reveal", path); return; }
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch (Exception ex) { Post(new { type = "error", message = "Не удалось открыть папку: " + ex.Message }); }
    }

    void LogAction(string action, string arg)
    {
        var line = $"{DateTimeOffset.Now:O}\t{action}\t{arg}";
        if (_opt.ActionLog != null) File.AppendAllText(_opt.ActionLog, line + Environment.NewLine);
        Post(new { type = "testAction", action, arg });
    }
}
