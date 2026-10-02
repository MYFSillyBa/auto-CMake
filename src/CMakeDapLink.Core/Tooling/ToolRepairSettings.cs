using System.Text.Json;

namespace CMakeDapLink.Core;

public sealed record ToolRepairSettings(string GitHubMirror = "https://ghfast.top/{url}");

public static class ManagedTools
{
    public static string UserDataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CMakeDapLink");
    public static string PathsPath => Path.Combine(UserDataRoot, "tool-paths.json");
    public static string SettingsPath => Path.Combine(UserDataRoot, "download-sources.json");
    public static string Root
    {
        get
        {
            try
            {
                var root = JsonSerializer.Deserialize<RootSettings>(File.ReadAllText(Path.Combine(UserDataRoot, "tool-root.json")))?.Root;
                if (!string.IsNullOrWhiteSpace(root) && Path.IsPathFullyQualified(root)) return Path.GetFullPath(root);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException) { }
            return Directory.Exists(@"D:\") ? @"D:\CMake_Tools" : Path.Combine(UserDataRoot, "Tools");
        }
    }
    public static void SetRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) throw new ArgumentException("请选择完整的工具安装目录。");
        var absolute = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (absolute == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(absolute)!)) throw new ArgumentException("请选择磁盘下的工具专用文件夹。");
        ToolVersionManager.EnsureOrdinaryPath(absolute);
        Directory.CreateDirectory(absolute);
        var previousPaths = LoadPaths();
        var previousSettings = LoadSettings();
        if (!File.Exists(PathsPath)) SavePaths(previousPaths);
        if (!File.Exists(SettingsPath)) SaveSettings(previousSettings);
        SaveJson(Path.Combine(UserDataRoot, "tool-root.json"), new RootSettings(absolute));
    }
    private sealed record RootSettings(string Root);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static ToolPaths LoadPaths()
    {
        try { return JsonSerializer.Deserialize<ToolPaths>(File.ReadAllText(File.Exists(PathsPath) ? PathsPath : Path.Combine(Root, "tool-paths.json"))) ?? new(null, null, null, null, null); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(null, null, null, null, null); }
    }
    public static void SavePaths(ToolPaths paths)
    {
        using var operation = AcquirePathsLock();
        SavePathsLocked(paths);
    }
    internal static void SavePathsLocked(ToolPaths paths) => SaveJson(PathsPath, paths);

    // The caller owns only the keys it is changing. A dialog or a repair can have
    // an older snapshot of the other tools while another process activates them.
    internal static ToolPaths ReconcilePathsLocked(ToolPaths current, IReadOnlyCollection<string> changedKeys, string? requiredScripts = null)
    {
        var saved = LoadPaths();
        string? KeepLatest(string key, string? previous, string? latest)
        {
            if (changedKeys.Contains(key) || string.IsNullOrWhiteSpace(latest)) return previous;
            if (!ToolVersionManager.SamePath(previous, latest) && !File.Exists(latest))
                throw new IOException(ToolVersionManager.DisplayName(key) + " 的已保存路径已变化但文件不存在，请刷新工具选择后重试：" + latest);
            return latest;
        }
        var openOcd = KeepLatest("OpenOcd", current.OpenOcd, saved.OpenOcd);
        var scripts = requiredScripts ?? (changedKeys.Contains("OpenOcd") || string.IsNullOrWhiteSpace(saved.OpenOcd) ? current.Scripts : saved.Scripts);
        if (requiredScripts == null && !changedKeys.Contains("OpenOcd") && !string.IsNullOrWhiteSpace(saved.OpenOcd) &&
            (!ToolVersionManager.SamePath(current.OpenOcd, saved.OpenOcd) ||
                current.Scripts != saved.Scripts && !ToolVersionManager.SamePath(current.Scripts, saved.Scripts)) &&
            !EnvironmentScanner.IsScripts(scripts))
            throw new IOException("OpenOCD 的已保存脚本路径已变化但配置不完整，请刷新工具选择后重试。");
        return new(KeepLatest("CMake", current.CMake, saved.CMake), KeepLatest("Ninja", current.Ninja, saved.Ninja),
            KeepLatest("Compiler", current.Compiler, saved.Compiler), openOcd, scripts);
    }

    // Paths are shared across installation roots. Keep their saves, activation validation,
    // and cleanup under one cross-process lease; file leases may span async continuations.
    internal static async Task<FileStream> AcquirePathsLockAsync(CancellationToken cancellation)
    {
        Directory.CreateDirectory(UserDataRoot);
        var path = Path.Combine(UserDataRoot, "tool-operation.lock");
        ToolVersionManager.EnsureOrdinaryPath(path);
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                await Task.Delay(50, cancellation).ConfigureAwait(false);
            }
        }
    }
    internal static FileStream AcquirePathsLock()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return AcquirePathsLockAsync(timeout.Token).GetAwaiter().GetResult();
    }
    public static ToolRepairSettings LoadSettings()
    {
        var path = File.Exists(SettingsPath) ? SettingsPath : Path.Combine(Root, "download-sources.json");
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<ToolRepairSettings>(File.ReadAllText(path)) ?? new();
    }
    public static void SaveSettings(ToolRepairSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.GitHubMirror) &&
            (!settings.GitHubMirror.Contains("{url}", StringComparison.Ordinal) ||
             !Uri.TryCreate(settings.GitHubMirror.Replace("{url}", "https://github.com/example/file.zip"), UriKind.Absolute, out var uri) || uri.Scheme != "https"))
            throw new ArgumentException("镜像源需使用 HTTPS，并包含 {url}，例如 https://ghfast.top/{url}；留空则关闭加速镜像。");
        SaveJson(SettingsPath, settings);
    }
    private static void SaveJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temp, path, true);
    }
}
