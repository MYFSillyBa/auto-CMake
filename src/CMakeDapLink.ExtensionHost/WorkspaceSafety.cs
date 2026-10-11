using System.Security.Cryptography;
using System.Text;
using CMakeDapLink.Core;

namespace CMakeDapLink.ExtensionHost;

internal static class WorkspaceSafety
{
    public static string Root(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("请选择本机工程的完整目录。");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(root) || root.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root)!), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择现有工程文件夹，不能使用磁盘根目录。");
        Ordinary(root);
        return root;
    }

    public static string Child(string root, string path, bool allowRoot = false)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("路径不能为空。");
        var full = Path.GetFullPath(path, root);
        if (!(allowRoot && full.Equals(root, StringComparison.OrdinalIgnoreCase)) &&
            !full.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("路径必须位于当前工程内：" + path);
        var relative = Path.GetRelativePath(root, full);
        if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part.Contains(':')))
            throw new ArgumentException("路径不能包含数据流或设备名称：" + path);
        Ordinary(full);
        return full;
    }

    public static void Ordinary(string path)
    {
        for (var item = Path.GetFullPath(path); item != null; item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("不能通过符号链接或联接目录访问工程或工具：" + item);
    }

    public static void Changes(string root, IReadOnlyList<FileChange> changes)
    {
        if (changes.Count > 4096) throw new ArgumentException("一次预览的文件数量超过限制。");
        long size = 0;
        foreach (var change in changes)
        {
            Child(root, change.Path);
            size += (long)(change.Before?.Length ?? 0) + (change.After?.Length ?? 0);
            if (size > 64 * 1024 * 1024) throw new ArgumentException("一次预览的文件总大小超过 64 MB。");
        }
    }

    public static string ToolKey(string key) => ToolVersionManager.Keys.FirstOrDefault(x => x.Equals(key, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException("未知工具：" + key);

    public static void Tools(ToolPaths tools)
    {
        foreach (var (key, name) in new[] { ("CMake", "cmake.exe"), ("Ninja", "ninja.exe"), ("Compiler", "arm-none-eabi-gcc.exe"), ("OpenOcd", "openocd.exe") })
        {
            var path = ToolVersionManager.GetPath(tools, key);
            if (path == null) continue;
            if (!Path.IsPathFullyQualified(path) || !Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("请选择 " + name + " 的完整路径。");
            Ordinary(path);
        }
        if (tools.Scripts != null) Ordinary(tools.Scripts);
    }

    public static FileStream WriteLease(string root)
    {
        var directory = Path.Combine(ManagedTools.UserDataRoot, "extension-locks");
        Ordinary(directory); Directory.CreateDirectory(directory);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
        try { return new FileStream(Path.Combine(directory, key + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("当前工程已有操作正在运行，请等待完成后再试。", ex); }
    }
}
