using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace CMakeDapLink.Core;

public static class BundledOpenOcdScripts
{
    private const string Resource = "CMakeDapLink.Core.Resources.OpenOcdScripts.zip";
    private const int MaximumEntries = 4096;
    private const long MaximumBytes = 32 * 1024 * 1024;

    public static async Task<string> GetDirectoryAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        using var resource = typeof(BundledOpenOcdScripts).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidDataException("程序缺少内置 OpenOCD 脚本资源，请重新安装完整程序。");
        if (resource.Length > 8 * 1024 * 1024) throw new InvalidDataException("内置脚本压缩包超出限制。");
        var contentHash = Convert.ToHexString(await SHA256.HashDataAsync(resource, cancellation)).ToLowerInvariant();
        resource.Position = 0;
        using var archive = new ZipArchive(resource, ZipArchiveMode.Read);
        if (archive.Entries.Count > MaximumEntries) throw new InvalidDataException("内置脚本文件数量超出限制。");
        var inventoryEntry = archive.GetEntry("bundle-manifest.json") ?? throw new InvalidDataException("内置脚本清单缺失。");
        if (inventoryEntry.Length > 1024 * 1024) throw new InvalidDataException("内置脚本清单超出限制。");
        using var inventoryStream = inventoryEntry.Open();
        using var inventoryData = new MemoryStream();
        await CopyBoundedAsync(inventoryStream, inventoryData, inventoryEntry.Length, cancellation); inventoryData.Position = 0;
        using var inventory = await JsonDocument.ParseAsync(inventoryData, cancellationToken: cancellation);
        var expected = new Dictionary<string, (long Length, string Hash)>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var file in inventory.RootElement.GetProperty("files").EnumerateArray())
        {
            var path = file.GetProperty("path").GetString()!; ValidateRelativePath(path);
            var length = file.GetProperty("length").GetInt64(); var hash = file.GetProperty("sha256").GetString()!;
            if (length < 0 || length > 4 * 1024 * 1024 || (bytes += length) > MaximumBytes ||
                hash.Length != 64 || !hash.All(Uri.IsHexDigit) || !expected.TryAdd(path, (length, hash)))
                throw new InvalidDataException("内置脚本清单无效。");
        }
        if (expected.Count + 1 != archive.Entries.Count) throw new InvalidDataException("内置脚本清单与压缩包不一致。");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            ValidateRelativePath(entry.FullName);
            if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || !entries.TryAdd(entry.FullName, entry))
                throw new InvalidDataException("内置脚本不能包含链接或重复路径。");
            if (entry.FullName != "bundle-manifest.json" && (!expected.TryGetValue(entry.FullName, out var item) || item.Length != entry.Length))
                throw new InvalidDataException("内置脚本文件与清单不一致。");
        }
        var cache = Path.Combine(ManagedTools.UserDataRoot, "OpenOcdScripts");
        ToolVersionManager.EnsureOrdinaryPath(cache); Directory.CreateDirectory(cache);
        var root = Path.Combine(cache, contentHash);
        var lease = Path.Combine(cache, contentHash + ".lock");
        await using var operation = await AcquireLockAsync(lease, cancellation);
        ToolVersionManager.EnsureOrdinaryPath(root); Directory.CreateDirectory(root);
        foreach (var (relative, item) in expected)
        {
            cancellation.ThrowIfCancellationRequested();
            var destination = ResolveChild(root, relative);
            ToolVersionManager.EnsureOrdinaryPath(destination);
            if (await MatchesAsync(destination, item.Length, item.Hash, cancellation)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var input = entries[relative].Open())
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    await CopyBoundedAsync(input, output, item.Length, cancellation);
                if (!await MatchesAsync(temporary, item.Length, item.Hash, cancellation)) throw new InvalidDataException("内置脚本内容校验失败：" + relative);
                cancellation.ThrowIfCancellationRequested();
                File.Move(temporary, destination, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        // Keep the original source/license inventory next to the extracted tree for attribution.
        var manifestPath = ResolveChild(root, "bundle-manifest.json");
        ToolVersionManager.EnsureOrdinaryPath(manifestPath);
        await using (var input = inventoryEntry.Open())
        {
            using var data = new MemoryStream(); await CopyBoundedAsync(input, data, inventoryEntry.Length, cancellation);
            var hash = Convert.ToHexString(SHA256.HashData(data.ToArray()));
            if (!await MatchesAsync(manifestPath, data.Length, hash, cancellation))
            {
                var temporary = manifestPath + ".tmp-" + Guid.NewGuid().ToString("N");
                try { await File.WriteAllBytesAsync(temporary, data.ToArray(), cancellation); cancellation.ThrowIfCancellationRequested(); File.Move(temporary, manifestPath, true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        var allowed = expected.Keys.Append("bundle-manifest.json").ToHashSet(StringComparer.OrdinalIgnoreCase);
        RemoveExtraFiles(root, allowed, cancellation);
        return Path.Combine(root, "scripts");
    }

    private static void ValidateRelativePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':') || relative.StartsWith('/') ||
            relative.Split('/').Any(p => p is "" or "." or "..")) throw new InvalidDataException("内置脚本包含越界路径。");
    }

    private static string ResolveChild(string root, string relative)
    {
        ValidateRelativePath(relative);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("内置脚本缓存路径越界。");
        return path;
    }

    private static async Task<bool> MatchesAsync(string path, long length, string hash, CancellationToken cancellation)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != length) return false;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long expectedLength, CancellationToken cancellation)
    {
        var buffer = new byte[81920]; long total = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellation); if (count == 0) break;
            if ((total += count) > expectedLength) throw new InvalidDataException("内置脚本解压长度超出清单限制。");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
        }
        if (total != expectedLength) throw new InvalidDataException("内置脚本解压长度与清单不一致。");
    }

    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken cancellation)
    {
        ToolVersionManager.EnsureOrdinaryPath(path);
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { await Task.Delay(50, cancellation); }
        }
    }

    private static void RemoveExtraFiles(string root, HashSet<string> allowed, CancellationToken cancellation)
    {
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((root, 0)); var count = 0;
        while (pending.TryPop(out var item))
        {
            cancellation.ThrowIfCancellationRequested(); ToolVersionManager.EnsureOrdinaryPath(item.Path);
            if (item.Depth > 16) throw new IOException("内置脚本缓存目录层级异常。");
            foreach (var entry in Directory.EnumerateFileSystemEntries(item.Path))
            {
                if (++count > MaximumEntries * 2) throw new IOException("内置脚本缓存条目数量异常。");
                var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                var contained = ResolveChild(root, relative); ToolVersionManager.EnsureOrdinaryPath(contained);
                if (Directory.Exists(contained)) pending.Push((contained, item.Depth + 1));
                else if (!allowed.Contains(relative)) File.Delete(contained);
            }
        }
    }
}
