using CMakeDapLink.Core;

namespace CMakeDapLink.ExtensionHost;

internal static class RelocatedCache
{
    public static string? Recover(string root, string buildDirectory, Action<int, string, string?> progress)
    {
        var cache = WorkspaceSafety.Child(root, Path.Combine(buildDirectory, "CMakeCache.txt"));
        if (!File.Exists(cache)) return null;
        if (new FileInfo(cache).Length > 8 * 1024 * 1024) throw new IOException("CMakeCache.txt 超过检查大小限制。");
        string? home = null, cachedBuild = null;
        foreach (var line in File.ReadLines(cache))
        {
            if (line.StartsWith("CMAKE_HOME_DIRECTORY:INTERNAL=", StringComparison.Ordinal)) home = line[(line.IndexOf('=') + 1)..];
            if (line.StartsWith("CMAKE_CACHEFILE_DIR:INTERNAL=", StringComparison.Ordinal)) cachedBuild = line[(line.IndexOf('=') + 1)..];
        }
        static bool Same(string? first, string second) => first == null || Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);
        if (Same(home, root) && Same(cachedBuild, buildDirectory)) return null;
        var files = WorkspaceSafety.Child(root, Path.Combine(buildDirectory, "CMakeFiles"));
        if (File.Exists(files)) throw new IOException("CMakeFiles 已被文件占用，请核对当前构建目录。");
        var backup = WorkspaceSafety.Child(root, Path.Combine(buildDirectory, ".cmake-daplink-cache-backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(backup);
        var savedCache = WorkspaceSafety.Child(root, Path.Combine(backup, "CMakeCache.txt"));
        var savedFiles = WorkspaceSafety.Child(root, Path.Combine(backup, "CMakeFiles"));
        File.Move(cache, savedCache);
        try { if (Directory.Exists(files)) Directory.Move(files, savedFiles); }
        catch { File.Move(savedCache, cache); throw; }
        var message = "恢复移动后的构建缓存：旧 CMakeCache.txt/CMakeFiles 已保留到 " + backup;
        progress(10, "恢复移动后的构建缓存", message);
        return message;
    }
}
