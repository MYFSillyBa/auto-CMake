using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

public sealed record ToolInstallation(string Key, string Version, string Executable, string? Scripts,
    string InstallDirectory, long SizeBytes, bool IsManaged, bool IsActive);
public sealed record ToolUpdate(string Key, string CurrentVersion, ToolPackage Latest);
public sealed record ToolCleanupEntry(string Name, string Path, long SizeBytes, bool IsDirectory);

public sealed class ToolVersionManager
{
    public static IReadOnlyList<string> Keys { get; } = ["CMake", "Ninja", "Compiler", "OpenOcd"];
    private const string ManifestName = "installation.json";
    private readonly string _root;
    public string Root => _root;

    public ToolVersionManager(string? root = null)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root ?? ManagedTools.Root));
        if (_root == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(_root)!)) throw new ArgumentException("工具目录不能是磁盘根目录。");
        EnsureOrdinaryPath(_root);
    }

    public static string? GetPath(ToolPaths tools, string key) => key switch
    {
        "CMake" => tools.CMake, "Ninja" => tools.Ninja, "Compiler" => tools.Compiler,
        "OpenOcd" => tools.OpenOcd, _ => throw new ArgumentException("未知工具：" + key)
    };
    public static string DisplayName(string key) => key switch { "Compiler" => "ARM GCC", "OpenOcd" => "OpenOCD", _ => key };
    private static string ExecutableName(string key) => key switch
    {
        "CMake" => "cmake.exe", "Ninja" => "ninja.exe", "Compiler" => "arm-none-eabi-gcc.exe",
        "OpenOcd" => "openocd.exe", _ => throw new ArgumentException("未知工具：" + key)
    };

    public async Task<IReadOnlyList<ToolInstallation>> ListAsync(ToolPaths current, CancellationToken cancellation = default)
    {
        var result = new List<ToolInstallation>();
        foreach (var key in Keys)
        {
            cancellation.ThrowIfCancellationRequested();
            var parent = Path.Combine(_root, key);
            if (Directory.Exists(parent))
            {
                EnsureOrdinaryPath(parent);
                foreach (var directory in Directory.EnumerateDirectories(parent).Order(StringComparer.OrdinalIgnoreCase))
                {
                    EnsureOrdinaryTree(directory);
                    var manifest = ReadManifest(directory, key);
                    var executable = manifest == null ? FindExecutable(directory, key) : ResolveChild(directory, manifest.Executable);
                    if (executable == null || !File.Exists(executable)) continue;
                    var scripts = manifest?.Scripts is { } relative ? ResolveChild(directory, relative) : key == "OpenOcd" ? EnvironmentScanner.FindScripts(executable) : null;
                    var version = manifest?.Version ?? await DescribeVersionAsync(key, executable, cancellation);
                    result.Add(new(key, version, executable, scripts, directory, Size(directory), manifest != null, SamePath(executable, GetPath(current, key))));
                }
            }
            var active = GetPath(current, key);
            if (File.Exists(active) && !result.Any(x => SamePath(x.Executable, active)))
                result.Add(new(key, await DescribeVersionAsync(key, active!, cancellation), Path.GetFullPath(active!),
                    key == "OpenOcd" ? EnvironmentScanner.FindScripts(active, current.Scripts) : null,
                    Path.GetDirectoryName(Path.GetFullPath(active!))!, new FileInfo(active!).Length, false, true));
        }
        return result;
    }

    public async Task<ToolInstallation> ImportZipAsync(string key, string archive, ToolPaths current,
        IProgress<ToolRepairProgress>? progress = null, CancellationToken cancellation = default)
    {
        _ = ExecutableName(key);
        await using var input = File.OpenRead(archive);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellation));
        input.Close();
        return await ImportVerifiedZipAsync(new(key, "", Path.GetFileName(archive), hash, "本地 ZIP", []), archive, current, progress, cancellation);
    }

    internal async Task<ToolInstallation> ImportVerifiedZipAsync(ToolPackage package, string archive, ToolPaths current,
        IProgress<ToolRepairProgress>? progress, CancellationToken cancellation)
    {
        _ = ExecutableName(package.Key);
        EnsureOrdinaryPath(_root);
        Directory.CreateDirectory(_root);
        var stage = Path.Combine(_root, ".install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            progress?.Report(new(70, "解压 " + DisplayName(package.Key), "先在临时目录验证，完成后保存为独立版本。"));
            await using (var archiveStream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
            {
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(archiveStream, cancellation));
                if (!actualHash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装包内容已变化，SHA-256 验证未通过。");
                archiveStream.Position = 0;
                using var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read, true);
                long size = 0;
                if (zip.Entries.Count > 100000) throw new InvalidDataException("安装包文件数量超出限制。");
                foreach (var entry in zip.Entries)
                {
                    cancellation.ThrowIfCancellationRequested();
                    size = checked(size + entry.Length);
                    if (size > 6L * 1024 * 1024 * 1024) throw new InvalidDataException("解压大小超出限制。");
                    if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000 ||
                        (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || entry.FullName.Contains(':'))
                        throw new InvalidDataException("安装包包含链接或无效文件名。");
                    var destination = ResolveChild(stage, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    if (entry.Name.Length == 0) { Directory.CreateDirectory(destination); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await using var source = entry.Open();
                    await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
                    await source.CopyToAsync(output, cancellation);
                }
            }
            var executable = FindExecutable(stage, package.Key) ?? throw new InvalidDataException("安装包中没有 " + ExecutableName(package.Key));
            progress?.Report(new(90, "验证 " + DisplayName(package.Key) + " 版本"));
            var version = await ReadVersionAsync(package.Key, executable, cancellation);
            var scripts = package.Key == "OpenOcd" ? EnvironmentScanner.FindScripts(executable) : null;
            if (scripts != null && !IsWithin(scripts, stage)) throw new InvalidDataException("OpenOCD 脚本必须来自安装包内部。");
            var candidate = new ToolInstallation(package.Key, version, executable, scripts, stage, 0, true, false);
            await VerifyAsync(candidate, current, cancellation);
            var directoryVersion = string.IsNullOrWhiteSpace(package.Version) ? version : package.Version;
            var parent = Path.Combine(_root, package.Key);
            EnsureOrdinaryPath(parent); Directory.CreateDirectory(parent);
            var final = Path.Combine(parent, Regex.Replace(directoryVersion, @"[^a-zA-Z0-9_.-]", "_") + "-" + package.Sha256[..12].ToLowerInvariant());
            var relativeExe = Path.GetRelativePath(stage, executable);
            var relativeScripts = scripts == null ? null : Path.GetRelativePath(stage, scripts);
            var manifest = new InstallationManifest(package.Key, version, relativeExe, relativeScripts, package.Sha256, package.Publisher);
            await File.WriteAllTextAsync(Path.Combine(stage, ManifestName), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), cancellation);
            await using var operation = await ManagedTools.AcquirePathsLockAsync(cancellation);
            var finalExists = Directory.Exists(final);
            if (finalExists)
            {
                EnsureOrdinaryTree(final);
                var existingManifest = ReadManifest(final, package.Key);
                if (existingManifest == null || !existingManifest.Sha256.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase) || existingManifest.Executable != relativeExe)
                    throw new IOException("同名版本目录已存在且记录不匹配，请保留或清理后重试。");
            }
            // An unhealthy copy may still be active. Publish a verified sibling rather
            // than modifying it. Reuse healthy repair copies even after the original is cleaned.
            var existingDirectories = (finalExists ? new[] { final } : Array.Empty<string>())
                .Concat(Directory.EnumerateDirectories(parent, Path.GetFileName(final) + "-repair-*"));
            foreach (var directory in existingDirectories)
            {
                EnsureOrdinaryTree(directory);
                var installedManifest = ReadManifest(directory, package.Key);
                if (installedManifest == null || !installedManifest.Sha256.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase) || installedManifest.Executable != relativeExe) continue;
                var existing = candidate with { Executable = ResolveChild(directory, installedManifest.Executable), Scripts = installedManifest.Scripts == null ? null : ResolveChild(directory, installedManifest.Scripts), InstallDirectory = directory, SizeBytes = Size(directory) };
                try
                {
                    await VerifyAsync(existing, current, cancellation);
                    return existing;
                }
                catch (Exception ex) when (!cancellation.IsCancellationRequested && ex is IOException or InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
                {
                    // The new staged copy already passed validation. Retain this old copy.
                }
            }
            if (finalExists) final += "-repair-" + Guid.NewGuid().ToString("N");
            cancellation.ThrowIfCancellationRequested();
            Directory.Move(stage, final);
            progress?.Report(new(100, DisplayName(package.Key) + " 已安装，选择后可启用", "版本验证通过，既有版本已保留。"));
            return candidate with { Executable = ResolveChild(final, relativeExe), Scripts = relativeScripts == null ? null : ResolveChild(final, relativeScripts), InstallDirectory = final, SizeBytes = Size(final) };
        }
        finally { DeleteOwnedDirectory(stage); }
    }

    public Task<ToolPaths> ActivateAsync(ToolInstallation installation, ToolPaths current, bool savePaths = true, CancellationToken cancellation = default)
        => ActivateCoreAsync(installation, current, [], savePaths, cancellation);

    internal Task<ToolPaths> ActivateForRepairAsync(ToolInstallation installation, ToolPaths current, IReadOnlyCollection<string> pendingKeys, CancellationToken cancellation, string? requiredScripts = null)
        => ActivateCoreAsync(installation, current, pendingKeys, true, cancellation, requiredScripts);

    private async Task<ToolPaths> ActivateCoreAsync(ToolInstallation installation, ToolPaths current, IReadOnlyCollection<string> pendingKeys, bool savePaths, CancellationToken cancellation, string? requiredScripts = null)
    {
        await using var operation = await ManagedTools.AcquirePathsLockAsync(cancellation);
        var latest = savePaths ? ManagedTools.ReconcilePathsLocked(current, [installation.Key], requiredScripts) : current;
        var validationTools = new ToolPaths(pendingKeys.Contains("CMake") ? null : latest.CMake,
            pendingKeys.Contains("Ninja") ? null : latest.Ninja, pendingKeys.Contains("Compiler") ? null : latest.Compiler,
            pendingKeys.Contains("OpenOcd") ? null : latest.OpenOcd, pendingKeys.Contains("OpenOcd") ? null : latest.Scripts);
        await VerifyAsync(installation, validationTools, cancellation);
        cancellation.ThrowIfCancellationRequested();
        var result = WithTool(latest, installation);
        if (savePaths) ManagedTools.SavePathsLocked(result);
        return result;
    }

    private async Task VerifyAsync(ToolInstallation installation, ToolPaths current, CancellationToken cancellation)
    {
        EnsureOrdinaryPath(installation.Executable);
        await ReadVersionAsync(installation.Key, installation.Executable, cancellation);
        if (installation.Key == "Compiler")
            foreach (var name in new[] { "arm-none-eabi-g++.exe", "arm-none-eabi-objcopy.exe", "arm-none-eabi-ld.exe" })
                if (!File.Exists(Path.Combine(Path.GetDirectoryName(installation.Executable)!, name))) throw new InvalidDataException("ARM GCC 包缺少 " + name);
        var tools = WithTool(current, installation);
        if (installation.Key == "OpenOcd")
        {
            if (!EnvironmentScanner.IsScripts(installation.Scripts)) throw new InvalidDataException("OpenOCD 包缺少完整 CMSIS-DAP 脚本。");
            await ToolRepairInstaller.RunAsync(installation.Executable,
                ["-s", installation.Scripts!, "-f", "interface/cmsis-dap.cfg", "-c", "transport select swd", "-c", "shutdown"],
                Path.GetDirectoryName(installation.Executable)!, tools, cancellation);
        }
        if (installation.Key != "OpenOcd" && File.Exists(tools.CMake) && File.Exists(tools.Ninja) && File.Exists(tools.Compiler))
            await ToolRepairInstaller.VerifyBuildAsync(tools, cancellation, _root);
    }

    public async Task<ToolUpdate> CheckUpdateAsync(string key, ToolPaths current, CancellationToken cancellation = default)
    {
        _ = ExecutableName(key);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CMakeDapLink/1.0");
        var catalog = new ToolDownloadCatalog(http, ManagedTools.LoadSettings(), _ => { });
        var latest = await catalog.OfficialAsync(key, cancellation);
        var path = GetPath(current, key);
        return new(key, File.Exists(path) ? await DescribeVersionAsync(key, path!, cancellation) : "未安装", latest);
    }

    public async Task<ToolInstallation> InstallAsync(ToolPackage package, ToolPaths current,
        IProgress<ToolRepairProgress>? progress = null, CancellationToken cancellation = default)
    {
        using var installer = new ToolRepairInstaller(_root);
        return await installer.InstallPackageAsync(package, current, progress, cancellation);
    }

    public IReadOnlyList<ToolCleanupEntry> ListCleanup(ToolPaths current)
    {
        using var operation = ManagedTools.AcquirePathsLock();
        return ListCleanupLocked(current, ManagedTools.LoadPaths());
    }

    private IReadOnlyList<ToolCleanupEntry> ListCleanupLocked(ToolPaths current, ToolPaths saved)
    {
        var result = new List<ToolCleanupEntry>();
        foreach (var key in Keys)
        {
            var parent = Path.Combine(_root, key);
            if (!Directory.Exists(parent)) continue;
            EnsureOrdinaryPath(parent);
            foreach (var directory in Directory.EnumerateDirectories(parent))
            {
                EnsureOrdinaryTree(directory);
                if (!HasActivePath(directory, current) && !HasActivePath(directory, saved) && ReadManifest(directory, key) != null)
                    result.Add(new(DisplayName(key) + " / " + Path.GetFileName(directory), directory, Size(directory), true));
            }
        }
        var cache = Path.Combine(_root, "_downloads");
        if (Directory.Exists(cache))
        {
            EnsureOrdinaryPath(cache);
            foreach (var file in Directory.EnumerateFiles(cache))
            {
                EnsureOrdinaryPath(file);
                if (!HasActivePath(file, current) && !HasActivePath(file, saved)) result.Add(new("下载缓存 / " + Path.GetFileName(file), file, new FileInfo(file).Length, false));
            }
        }
        return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void Cleanup(IReadOnlyCollection<ToolCleanupEntry> confirmed, ToolPaths current)
    {
        using var operation = ManagedTools.AcquirePathsLock();
        var saved = ManagedTools.LoadPaths();
        var allowed = ListCleanupLocked(current, saved).ToDictionary(x => Path.GetFullPath(x.Path), StringComparer.OrdinalIgnoreCase);
        var selected = confirmed.Select(x => allowed.TryGetValue(Path.GetFullPath(x.Path), out var allowedItem) && allowedItem.IsDirectory == x.IsDirectory
            ? allowedItem : throw new IOException("清理目标已变化或正在启用，请重新查看清单。")).ToArray();
        foreach (var entry in selected)
        {
            if (!IsWithin(entry.Path, _root) || HasActivePath(entry.Path, current) || HasActivePath(entry.Path, saved)) throw new IOException("不能清理当前启用的工具或外部路径。");
            EnsureOrdinaryPath(entry.Path);
            if (entry.IsDirectory) { EnsureOrdinaryTree(entry.Path); Directory.Delete(entry.Path, true); }
            else File.Delete(entry.Path);
        }
    }

    private static ToolPaths WithTool(ToolPaths current, ToolInstallation installation) => installation.Key switch
    {
        "CMake" => current with { CMake = installation.Executable }, "Ninja" => current with { Ninja = installation.Executable },
        "Compiler" => current with { Compiler = installation.Executable },
        "OpenOcd" => current with { OpenOcd = installation.Executable, Scripts = installation.Scripts }, _ => throw new ArgumentException("未知工具")
    };
    private static async Task<string> DescribeVersionAsync(string key, string path, CancellationToken cancellation)
    {
        try { return await ReadVersionAsync(key, path, cancellation); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception) { return "未通过版本验证"; }
    }
    private static async Task<string> ReadVersionAsync(string key, string path, CancellationToken cancellation)
    {
        var output = await ToolRepairInstaller.RunAsync(path, ["--version"], Path.GetDirectoryName(path)!, new(null, null, null, null, null), cancellation);
        var pattern = key switch
        {
            "Ninja" => @"\A\d+\.\d+(?:\.\d+)?(?:[.\w+-]*)\z", "CMake" => @"(?i)\bcmake version\s+(\d+\.\d+(?:\.\d+)?[.\w+-]*)",
            "Compiler" => @"(?i)\barm-none-eabi-gcc\b.*?\b(\d+\.\d+(?:\.\d+)?[.\w+-]*)",
            "OpenOcd" => @"(?i)Open On-Chip Debugger\s+(\d+\.\d+(?:\.\d+)?[.\w+-]*)", _ => throw new ArgumentException("未知工具")
        };
        var match = Regex.Match(output, pattern);
        if (!match.Success) throw new InvalidDataException(DisplayName(key) + " 的 --version 输出不匹配。");
        return key == "Ninja" ? match.Value : match.Groups[1].Value;
    }
    private static string? FindExecutable(string directory, string key) => Files(directory).Where(x => Path.GetFileName(x).Equals(ExecutableName(key), StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Length).FirstOrDefault();
    private static IEnumerable<string> Files(string directory)
    {
        EnsureOrdinaryPath(directory);
        foreach (var file in Directory.EnumerateFiles(directory)) { EnsureOrdinaryPath(file); yield return file; }
        foreach (var child in Directory.EnumerateDirectories(directory)) foreach (var file in Files(child)) yield return file;
    }
    private static long Size(string directory) => Files(directory).Sum(x => new FileInfo(x).Length);
    internal static void EnsureOrdinaryPath(string path)
    {
        var absolute = Path.GetFullPath(path);
        for (var item = new DirectoryInfo(absolute); item != null; item = item.Parent)
            if ((Directory.Exists(item.FullName) || File.Exists(item.FullName)) && (File.GetAttributes(item.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("工具目录不能包含链接：" + item.FullName);
    }
    private static void EnsureOrdinaryTree(string directory) { foreach (var _ in Files(directory)) { } }
    internal static bool SamePath(string? a, string? b) => a != null && b != null && Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    private static bool IsWithin(string child, string parent) => Path.GetFullPath(child).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool HasActivePath(string directory, ToolPaths current) => new[] { current.CMake, current.Ninja, current.Compiler, current.OpenOcd, current.Scripts }
        .Any(path => path != null && (SamePath(path, directory) || IsWithin(path, directory)));
    private static string ResolveChild(string directory, string child)
    {
        var absolute = Path.GetFullPath(Path.Combine(directory, child));
        if (!IsWithin(absolute, directory)) throw new InvalidDataException("安装包或记录包含越界路径。");
        return absolute;
    }
    private void DeleteOwnedDirectory(string path)
    {
        if (!IsWithin(path, _root)) throw new IOException("清理路径不在工具目录内。");
        if (!Directory.Exists(path)) return;
        EnsureOrdinaryTree(path); Directory.Delete(path, true);
    }
    private static InstallationManifest? ReadManifest(string directory, string key)
    {
        var path = Path.Combine(directory, ManifestName);
        if (!File.Exists(path)) return null;
        var manifest = JsonSerializer.Deserialize<InstallationManifest>(File.ReadAllText(path));
        if (manifest?.Key != key) throw new InvalidDataException("版本记录的工具名称不匹配。");
        if (string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.Executable) ||
            !Regex.IsMatch(manifest.Sha256 ?? "", @"\A[0-9a-fA-F]{64}\z") ||
            !Path.GetFileName(manifest.Executable).Equals(ExecutableName(key), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("版本安装记录不完整，不能认定为受管理目录。");
        _ = ResolveChild(directory, manifest.Executable);
        if (manifest.Scripts != null) _ = ResolveChild(directory, manifest.Scripts);
        return manifest;
    }
    private sealed record InstallationManifest(string Key, string Version, string Executable, string? Scripts, string Sha256, string Publisher);
}
