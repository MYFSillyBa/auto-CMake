using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

public sealed record ToolRepairProgress(int Percent, string Stage, string? Message = null)
{
    public long? ReceivedBytes { get; init; }
    public long? TotalBytes { get; init; }
    public double? BytesPerSecond { get; init; }
}
public sealed record ToolRepairResult(ToolPaths Tools, IReadOnlyList<string> Installed, IReadOnlyList<string> Problems);

public sealed class ToolRepairInstaller : IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler {
        ConnectTimeout = TimeSpan.FromSeconds(15), AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true }) { Timeout = Timeout.InfiniteTimeSpan };

    private readonly string _root;

    public ToolRepairInstaller(string? root = null)
    {
        _root = new ToolVersionManager(root).Root;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CMakeDapLink/1.0");
    }

    public async Task<ToolRepairResult> RepairAsync(ToolPaths current, IReadOnlyCollection<string> brokenKeys,
        string? target, IProgress<ToolRepairProgress>? progress = null, CancellationToken cancellation = default,
        bool openOcdOnly = false, bool allowTargetAlias = false)
    {
        target = string.IsNullOrWhiteSpace(target) ? null : target.Trim().Replace('\\', '/');
        if (openOcdOnly && (target == null || !OpenOcdScripts.ValidTarget(target)))
            return new(current, [], ["请先选择匹配芯片的有效 target/*.cfg，再补全 OpenOCD 脚本。"]);
        var bundledScripts = await BundledOpenOcdScripts.GetDirectoryAsync(cancellation);
        current = current with { Scripts = bundledScripts };
        if (target != null)
        {
            target = allowTargetAlias ? OpenOcdScripts.AvailableTarget(bundledScripts, target) : target;
            var absent = OpenOcdScripts.MissingFiles(bundledScripts, target);
            if (absent.Count != 0)
                return new(current, [], ["内置 OpenOCD 脚本尚不支持所选 Target：" + target + "；缺少：" + string.Join("、", absent) +
                    "。请核对芯片型号或选择内置的匹配 target/*.cfg；不会为缺失脚本下载工具包。"]);
        }
        Directory.CreateDirectory(_root);
        ToolVersionManager.EnsureOrdinaryPath(_root);
        if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException(_root + " 不能是链接目录，请使用普通文件夹。");
        var settings = ManagedTools.LoadSettings();
        if (!File.Exists(ManagedTools.SettingsPath)) ManagedTools.SaveSettings(settings);
        var percent = 0;
        void Log(string message) => progress?.Report(new(percent, openOcdOnly ? "修复 OpenOCD 可执行工具" : "自动修复构建环境", message));
        var catalog = new ToolDownloadCatalog(_http, settings, Log);
        var installed = new List<string>(); var problems = new List<string>();
        var keys = openOcdOnly ? new[] { "OpenOcd" } : new[] { "CMake", "Ninja", "Compiler", "OpenOcd" };
        var missing = keys.Where(key => !File.Exists(GetPath(current, key)) || brokenKeys.Contains(key)).ToArray();
        await using (var initialOperation = await ManagedTools.AcquirePathsLockAsync(cancellation))
        {
            current = ManagedTools.ReconcilePathsLocked(current, missing, bundledScripts);
        }
        Log("使用程序内置的完整 Target、接口和 Tcl 脚本，不搜索本机脚本目录、不下载目标脚本。");
        Log("安装目录：" + _root + "；只补齐缺失或无法运行的工具，保留已有可用工具。");
        for (var i = 0; i < missing.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var key = missing[i]; percent = 5 + i * 80 / Math.Max(1, missing.Length);
            var pending = missing.Skip(i + 1).ToHashSet(StringComparer.Ordinal);
            var validationTools = new ToolPaths(pending.Contains("CMake") ? null : current.CMake,
                pending.Contains("Ninja") ? null : current.Ninja, pending.Contains("Compiler") ? null : current.Compiler,
                pending.Contains("OpenOcd") ? null : current.OpenOcd, pending.Contains("OpenOcd") ? null : current.Scripts);
            progress?.Report(new(percent, "查询 " + DisplayName(key) + " 最新稳定版", "正在查询发布方的最新稳定版本。"));
            try
            {
                ToolInstallation location;
                try
                {
                    var package = await catalog.OfficialAsync(key, cancellation);
                    location = await InstallForTargetAsync(package, validationTools, target, percent, 80 / missing.Length, progress, cancellation, allowTargetAlias);
                }
                catch (Exception ex) when (!cancellation.IsCancellationRequested && ex is not UnauthorizedAccessException)
                {
                    Log(DisplayName(key) + " 官方下载暂不可用：" + ex.Message + "；查询备用发行版。");
                    var package = await catalog.AlternativeAsync(key, cancellation);
                    location = await InstallForTargetAsync(package, validationTools, target, percent, 80 / missing.Length, progress, cancellation, allowTargetAlias);
                }
                if (key == "OpenOcd") location = location with { Scripts = bundledScripts };
                current = await new ToolVersionManager(_root).ActivateForRepairAsync(location, current, pending, cancellation, bundledScripts);
                current = current with { Scripts = bundledScripts };
                percent = 5 + (i + 1) * 80 / Math.Max(1, missing.Length);
                installed.Add(DisplayName(key)); Log(DisplayName(key) + " 安装并验证完成：" + location.Executable);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception ex) { problems.Add(DisplayName(key) + "：" + ex.Message); Log(problems[^1]); }
        }
        await using var operation = await ManagedTools.AcquirePathsLockAsync(cancellation);
        current = ManagedTools.ReconcilePathsLocked(current, [], bundledScripts);
        percent = 90; progress?.Report(new(percent, openOcdOnly ? "验证 OpenOCD Target 与 CMSIS-DAP 配置" : "验证工具链与 CMSIS-DAP 配置"));
        foreach (var key in keys)
        {
            var path = GetPath(current, key);
            if (path == null || !File.Exists(path)) continue;
            try { Log(await RunAsync(path, ["--version"], _root, current, cancellation)); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception ex) { problems.Add(DisplayName(key) + " 验证：" + ex.Message); }
        }
        if (!openOcdOnly && File.Exists(current.CMake) && File.Exists(current.Ninja) && File.Exists(current.Compiler))
        {
            try { await VerifyBuildAsync(current, cancellation, _root); Log("CMake + Ninja + ARM GCC 实际编译通过（ARM ELF）。"); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception ex) { problems.Add("工具链联合验证：" + ex.Message); Log(problems[^1]); }
        }
        var openOcdValidated = false;
        if (File.Exists(current.OpenOcd) && EnvironmentScanner.IsScripts(current.Scripts))
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(target))
                {
                    var actualTarget = allowTargetAlias ? OpenOcdScripts.AvailableTarget(current.Scripts, target) : target;
                    await OpenOcdScripts.ValidateAsync(current, actualTarget, cancellation);
                    Log("OpenOCD " + actualTarget + " 与 CMSIS-DAP/SWD 脚本解析通过（未连接硬件）。");
                }
                else
                    await RunAsync(current.OpenOcd!, ["-s", current.Scripts!, "-f", "interface/cmsis-dap.cfg", "-c", "transport select swd", "-c", "shutdown"], _root, current, cancellation);
                openOcdValidated = true;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception ex) { problems.Add("OpenOCD 配置验证：" + ex.Message); Log(problems[^1]); }
        }
        cancellation.ThrowIfCancellationRequested();
        if (target == null || openOcdValidated) ManagedTools.SavePathsLocked(current);
        progress?.Report(new(100, problems.Count == 0 ? "自动修复完成，路径已保存" : "自动修复结束，查看待补全项",
            installed.Count == 0 ? "本机工具已可用，无需下载。" : "已补齐：" + string.Join("、", installed)));
        return new(current, installed, problems);
    }

    public Task<ToolInstallation> InstallPackageAsync(ToolPackage package, ToolPaths current,
        IProgress<ToolRepairProgress>? progress = null, CancellationToken cancellation = default)
        => InstallAsync(package, current, 0, 100, progress, cancellation);

    private async Task<ToolInstallation> InstallForTargetAsync(ToolPackage package, ToolPaths current, string? target,
        int start, int span, IProgress<ToolRepairProgress>? progress, CancellationToken cancellation, bool allowTargetAlias)
    {
        var installation = await InstallAsync(package, current, start, span, progress, cancellation);
        if (package.Key == "OpenOcd" && !string.IsNullOrWhiteSpace(target))
            await OpenOcdScripts.ValidateAsync(current with { OpenOcd = installation.Executable },
                allowTargetAlias ? OpenOcdScripts.AvailableTarget(current.Scripts, target) : target, cancellation);
        return installation;
    }

    private async Task<ToolInstallation> InstallAsync(ToolPackage package, ToolPaths current, int start, int span,
        IProgress<ToolRepairProgress>? progress, CancellationToken cancellation)
    {
        if (!Regex.IsMatch(package.Sha256, @"\A[0-9a-fA-F]{64}\z") || Path.GetFileName(package.FileName) != package.FileName ||
            !package.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装包名称或 SHA-256 无效。");
        void Report(int amount, string stage, string? message = null) => progress?.Report(new(start + span * amount / 100, stage, message));
        Report(0, "下载 " + DisplayName(package.Key), $"{package.Publisher} · {package.Version} · {package.FileName}");
        var cache = Path.Combine(_root, "_downloads"); Directory.CreateDirectory(cache);
        ToolVersionManager.EnsureOrdinaryPath(cache);
        var archive = Path.Combine(cache, package.Sha256[..12].ToLowerInvariant() + "-" + package.FileName);
        ToolVersionManager.EnsureOrdinaryPath(archive);
        if (!File.Exists(archive) || !await HashMatchesAsync(archive, package.Sha256, cancellation))
        {
            Exception? lastError = null; var downloaded = false;
            foreach (var url in package.Urls.Distinct())
            {
                cancellation.ThrowIfCancellationRequested();
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") continue;
                Report(0, "下载 " + DisplayName(package.Key), "下载源：" + url);
                var partial = archive + ".part-" + Guid.NewGuid().ToString("N");
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    timeout.CancelAfter(TimeSpan.FromMinutes(20));
                    using var connection = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token); connection.CancelAfter(TimeSpan.FromSeconds(40));
                    using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, connection.Token);
                    response.EnsureSuccessStatusCode();
                    var total = response.Content.Headers.ContentLength; long received = 0;
                    await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                    await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                    {
                        var buffer = new byte[131072]; var last = Stopwatch.StartNew(); var elapsed = Stopwatch.StartNew();
                        while (true)
                        {
                            using var idle = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token); idle.CancelAfter(TimeSpan.FromSeconds(45));
                            var count = await input.ReadAsync(buffer, idle.Token); if (count == 0) break;
                            await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token); received += count;
                            if (received > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("安装包超出 2 GB 限制。");
                            if (last.ElapsedMilliseconds >= 250)
                            {
                                var pct = total is > 0 ? Math.Min(65, (int)(received * 65 / total.Value)) : 20;
                                var speed = received / Math.Max(.001, elapsed.Elapsed.TotalSeconds);
                                progress?.Report(new(start + span * pct / 100,
                                    $"下载 {DisplayName(package.Key)} · {received / 1048576.0:F1} MB" + (total is > 0 ? $" / {total / 1048576.0:F1} MB" : " / 大小未知") + $" · {speed / 1048576.0:F2} MB/s")
                                    { ReceivedBytes = received, TotalBytes = total, BytesPerSecond = speed }); last.Restart();
                            }
                        }
                        var finalSpeed = received / Math.Max(.001, elapsed.Elapsed.TotalSeconds);
                        progress?.Report(new(start + span * 65 / 100,
                            $"下载 {DisplayName(package.Key)} · {received / 1048576.0:F1} MB" + (total is > 0 ? $" / {total / 1048576.0:F1} MB" : " / 大小未知") + $" · {finalSpeed / 1048576.0:F2} MB/s")
                            { ReceivedBytes = received, TotalBytes = total, BytesPerSecond = finalSpeed });
                    }
                    if (total.HasValue && received != total.Value) throw new InvalidDataException("下载大小与服务器提供的长度不匹配。");
                    Report(66, "校验安装包 SHA-256");
                    if (!await HashMatchesAsync(partial, package.Sha256, timeout.Token)) throw new InvalidDataException("SHA-256 与发布方不一致。");
                    File.Move(partial, archive, true); downloaded = true; break;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception ex) { lastError = ex; Report(0, "切换备用下载源", "当前源不可用：" + ex.Message); }
                finally { if (File.Exists(partial)) File.Delete(partial); }
            }
            if (!downloaded) throw new IOException("所有下载源均未完成下载：" + lastError?.Message, lastError);
        }
        else Report(66, "使用已校验的缓存安装包");
        var extractionProgress = new MappedProgress(item => progress?.Report(item with { Percent = start + span * item.Percent / 100 }));
        return await new ToolVersionManager(_root).ImportVerifiedZipAsync(package, archive, current, extractionProgress, cancellation);
    }

    private sealed class MappedProgress(Action<ToolRepairProgress> report) : IProgress<ToolRepairProgress>
    {
        public void Report(ToolRepairProgress value) => report(value);
    }

    private static async Task<bool> HashMatchesAsync(string file, string expected, CancellationToken cancellation)
    {
        await using var stream = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task VerifyBuildAsync(ToolPaths tools, CancellationToken cancellation, string? installRoot = null)
    {
        var ownedRoot = installRoot ?? ManagedTools.Root;
        ToolVersionManager.EnsureOrdinaryPath(ownedRoot);
        var root = Path.Combine(ownedRoot, "_verification", Guid.NewGuid().ToString("N"));
        ToolVersionManager.EnsureOrdinaryPath(root); Directory.CreateDirectory(root);
        try
        {
            var compiler = tools.Compiler!.Replace('\\', '/').Replace("\"", "\\\"");
            await File.WriteAllTextAsync(Path.Combine(root, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.20)\nset(CMAKE_SYSTEM_NAME Generic)\nset(CMAKE_TRY_COMPILE_TARGET_TYPE STATIC_LIBRARY)\nset(CMAKE_C_COMPILER \"" + compiler + "\")\nproject(ToolCheck C)\nadd_executable(probe main.c)\nset_target_properties(probe PROPERTIES SUFFIX .elf)\ntarget_link_options(probe PRIVATE -nostdlib -Wl,-e,main -Wl,-Ttext=0x08000000)\n", cancellation);
            await File.WriteAllTextAsync(Path.Combine(root, "main.c"), "int main(void) { return 0; }\n", cancellation);
            await RunAsync(tools.CMake!, ["-S", root, "-B", Path.Combine(root, "build"), "-G", "Ninja", "-DCMAKE_MAKE_PROGRAM=" + tools.Ninja], root, tools, cancellation, 90);
            await RunAsync(tools.CMake!, ["--build", Path.Combine(root, "build")], root, tools, cancellation, 90);
            var bytes = await File.ReadAllBytesAsync(Path.Combine(root, "build", "probe.elf"), cancellation);
            if (bytes.Length < 20 || bytes[0] != 0x7f || bytes[1] != 'E' || bytes[2] != 'L' || bytes[3] != 'F' || bytes[18] != 40)
                throw new InvalidDataException("没有生成 ARM ELF。");
        }
        finally { RemoveOwnedDirectory(root, ownedRoot); }
    }

    internal static async Task<string> RunAsync(string executable, IEnumerable<string> args, string directory,
        ToolPaths tools, CancellationToken cancellation, int seconds = 20)
    {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        var dirs = new[] { tools.CMake, tools.Ninja, tools.Compiler, tools.OpenOcd, executable }.Where(x => x != null).Select(x => Path.GetDirectoryName(x!));
        info.Environment["PATH"] = string.Join(Path.PathSeparator, dirs) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        using var process = Process.Start(info) ?? throw new IOException("无法启动 " + executable);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var output = (await stdout + "\n" + await stderr).Trim();
        if (process.ExitCode != 0) throw new InvalidOperationException(Path.GetFileName(executable) + "：" + output);
        return output.Split('\n').FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? Path.GetFileName(executable);
    }

    private static void RemoveOwnedDirectory(string path, string root)
    {
        var absolute = Path.GetFullPath(path);
        if (!absolute.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("清理路径不在工具安装目录内。");
        if (Directory.Exists(absolute))
        {
            ToolVersionManager.EnsureOrdinaryPath(absolute);
            foreach (var entry in Directory.EnumerateFileSystemEntries(absolute, "*", SearchOption.AllDirectories)) ToolVersionManager.EnsureOrdinaryPath(entry);
            Directory.Delete(absolute, true);
        }
    }
    private static string? GetPath(ToolPaths tools, string key) => key switch { "CMake" => tools.CMake, "Ninja" => tools.Ninja, "Compiler" => tools.Compiler, _ => tools.OpenOcd };
    private static string DisplayName(string key) => key switch { "Compiler" => "ARM GCC", "OpenOcd" => "OpenOCD", _ => key };
    public void Dispose() => _http.Dispose();
}
