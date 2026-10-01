using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

public sealed record ToolRepairProgress(int Percent, string Stage, string? Message = null);
public sealed record ToolRepairResult(ToolPaths Tools, IReadOnlyList<string> Installed, IReadOnlyList<string> Problems);

public sealed class ToolRepairInstaller : IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler {
        ConnectTimeout = TimeSpan.FromSeconds(15), AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true }) { Timeout = Timeout.InfiniteTimeSpan };

    public ToolRepairInstaller()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CMakeDapLink/1.0");
    }

    public async Task<ToolRepairResult> RepairAsync(ToolPaths current, IReadOnlyCollection<string> brokenKeys,
        string? target, IProgress<ToolRepairProgress>? progress = null, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(ManagedTools.Root);
        if ((File.GetAttributes(ManagedTools.Root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("D:\\CMake_Tools 不能是链接目录，请使用普通文件夹。");
        var settings = ManagedTools.LoadSettings();
        if (!File.Exists(ManagedTools.SettingsPath)) ManagedTools.SaveSettings(settings);
        var percent = 0;
        void Log(string message) => progress?.Report(new(percent, "自动修复构建环境", message));
        var catalog = new ToolDownloadCatalog(_http, settings, Log);
        var installed = new List<string>(); var problems = new List<string>();
        var missing = new[] { "CMake", "Ninja", "Compiler", "OpenOcd" }.Where(key =>
            !File.Exists(GetPath(current, key)) || brokenKeys.Contains(key) || key == "OpenOcd" && !EnvironmentScanner.IsScripts(current.Scripts)).ToArray();
        Log("安装目录：" + ManagedTools.Root + "；只补齐缺失或无法运行的工具，保留已有可用工具。");
        for (var i = 0; i < missing.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var key = missing[i]; percent = 5 + i * 80 / Math.Max(1, missing.Length);
            progress?.Report(new(percent, "查询 " + DisplayName(key) + " 最新稳定版", "正在查询发布方的最新稳定版本。"));
            try
            {
                (string Executable, string? Scripts) location;
                try
                {
                    var package = await catalog.OfficialAsync(key, cancellation);
                    location = await InstallAsync(package, percent, 80 / missing.Length, progress, cancellation);
                }
                catch (Exception ex) when (!cancellation.IsCancellationRequested && ex is not UnauthorizedAccessException)
                {
                    Log(DisplayName(key) + " 官方下载暂不可用：" + ex.Message + "；查询备用发行版。");
                    var package = await catalog.AlternativeAsync(key, cancellation);
                    location = await InstallAsync(package, percent, 80 / missing.Length, progress, cancellation);
                }
                current = key switch {
                    "CMake" => current with { CMake = location.Executable },
                    "Ninja" => current with { Ninja = location.Executable },
                    "Compiler" => current with { Compiler = location.Executable },
                    _ => current with { OpenOcd = location.Executable, Scripts = location.Scripts }
                };
                ManagedTools.SavePaths(current);
                percent = 5 + (i + 1) * 80 / Math.Max(1, missing.Length);
                installed.Add(DisplayName(key)); Log(DisplayName(key) + " 安装并验证完成：" + location.Executable);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception ex) { problems.Add(DisplayName(key) + "：" + ex.Message); Log(problems[^1]); }
        }
        percent = 90; progress?.Report(new(percent, "验证工具链与 CMSIS-DAP 配置"));
        foreach (var key in new[] { "CMake", "Ninja", "Compiler", "OpenOcd" })
        {
            var path = GetPath(current, key);
            if (path == null || !File.Exists(path)) continue;
            try { Log(await RunAsync(path, ["--version"], ManagedTools.Root, current, cancellation)); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception ex) { problems.Add(DisplayName(key) + " 验证：" + ex.Message); }
        }
        if (File.Exists(current.CMake) && File.Exists(current.Ninja) && File.Exists(current.Compiler))
        {
            try { await VerifyBuildAsync(current, cancellation); Log("CMake + Ninja + ARM GCC 实际编译通过（ARM ELF）。"); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception ex) { problems.Add("工具链联合验证：" + ex.Message); Log(problems[^1]); }
        }
        if (File.Exists(current.OpenOcd) && EnvironmentScanner.IsScripts(current.Scripts))
        {
            try
            {
                var args = new List<string> { "-s", current.Scripts!, "-f", "interface/cmsis-dap.cfg", "-c", "transport select swd" };
                if (!string.IsNullOrWhiteSpace(target) && Regex.IsMatch(target, @"\Atarget/[a-zA-Z0-9_.-]+\.cfg\z") && File.Exists(Path.Combine(current.Scripts!, target)))
                    args.AddRange(["-f", target]);
                args.AddRange(["-c", "shutdown"]);
                await RunAsync(current.OpenOcd!, args, ManagedTools.Root, current, cancellation);
                Log("OpenOCD CMSIS-DAP/SWD 脚本解析通过。");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception ex) { problems.Add("OpenOCD 配置验证：" + ex.Message); Log(problems[^1]); }
        }
        ManagedTools.SavePaths(current);
        progress?.Report(new(100, problems.Count == 0 ? "自动修复完成，路径已保存" : "自动修复结束，查看待补全项",
            installed.Count == 0 ? "本机工具已可用，无需下载。" : "已补齐：" + string.Join("、", installed)));
        return new(current, installed, problems);
    }

    private async Task<(string Executable, string? Scripts)> InstallAsync(ToolPackage package, int start, int span,
        IProgress<ToolRepairProgress>? progress, CancellationToken cancellation)
    {
        if (!Regex.IsMatch(package.Sha256, @"\A[0-9a-fA-F]{64}\z") || Path.GetFileName(package.FileName) != package.FileName ||
            !package.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装包名称或 SHA-256 无效。");
        void Report(int amount, string stage, string? message = null) => progress?.Report(new(start + span * amount / 100, stage, message));
        Report(0, "下载 " + DisplayName(package.Key), $"{package.Publisher} · {package.Version} · {package.FileName}");
        var cache = Path.Combine(ManagedTools.Root, "_downloads"); Directory.CreateDirectory(cache);
        var archive = Path.Combine(cache, package.FileName);
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
                        var buffer = new byte[131072]; var last = Stopwatch.StartNew();
                        while (true)
                        {
                            using var idle = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token); idle.CancelAfter(TimeSpan.FromSeconds(45));
                            var count = await input.ReadAsync(buffer, idle.Token); if (count == 0) break;
                            await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token); received += count;
                            if (received > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("安装包超出 2 GB 限制。");
                            if (last.ElapsedMilliseconds >= 250)
                            {
                                var pct = total is > 0 ? Math.Min(65, (int)(received * 65 / total.Value)) : 20;
                                Report(pct, $"下载 {DisplayName(package.Key)} · {received / 1048576.0:F1} MB" + (total is > 0 ? $" / {total / 1048576.0:F1} MB" : "")); last.Restart();
                            }
                        }
                    }
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
        var stage = Path.Combine(ManagedTools.Root, ".install-" + Guid.NewGuid().ToString("N"));
        try
        {
            Report(70, "解压 " + DisplayName(package.Key));
            Directory.CreateDirectory(stage);
            using (var zip = ZipFile.OpenRead(archive))
            {
                long size = 0; var count = 0;
                foreach (var entry in zip.Entries)
                {
                    cancellation.ThrowIfCancellationRequested(); size += entry.Length;
                    if (size > 6L * 1024 * 1024 * 1024) throw new InvalidDataException("解压大小超出限制。");
                    if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000 || entry.FullName.Contains(':')) throw new InvalidDataException("安装包包含链接或无效文件名。");
                    var destination = Path.GetFullPath(Path.Combine(stage, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!destination.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装包包含越界路径。");
                    if (entry.Name.Length == 0) { Directory.CreateDirectory(destination); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await using var input = entry.Open();
                    await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
                    await input.CopyToAsync(output, cancellation);
                    if (++count % 80 == 0) Report(70 + count * 20 / zip.Entries.Count, "解压 " + DisplayName(package.Key));
                }
            }
            var expected = package.Key switch { "CMake" => "cmake.exe", "Ninja" => "ninja.exe", "Compiler" => "arm-none-eabi-gcc.exe", _ => "openocd.exe" };
            var executable = Directory.EnumerateFiles(stage, expected, SearchOption.AllDirectories).OrderBy(x => x.Length).FirstOrDefault()
                ?? throw new InvalidDataException("安装包中没有 " + expected);
            Report(92, "运行 " + DisplayName(package.Key) + " 版本验证");
            await RunAsync(executable, ["--version"], stage, new(null, null, null, null, null), cancellation);
            if (package.Key == "Compiler")
                foreach (var companion in new[] { "arm-none-eabi-g++.exe", "arm-none-eabi-objcopy.exe", "arm-none-eabi-ld.exe" })
                    if (!File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, companion))) throw new InvalidDataException("ARM GCC 包缺少 " + companion);
            var scripts = package.Key == "OpenOcd" ? EnvironmentScanner.FindScripts(executable) : null;
            if (package.Key == "OpenOcd" && scripts == null) throw new InvalidDataException("OpenOCD 包缺少完整 CMSIS-DAP 脚本。");
            var parent = Path.Combine(ManagedTools.Root, package.Key); Directory.CreateDirectory(parent);
            var final = Path.Combine(parent, Regex.Replace(package.Version, @"[^a-zA-Z0-9_.-]", "_") + "-" + Guid.NewGuid().ToString("N")[..8]);
            var exeRelative = Path.GetRelativePath(stage, executable); var scriptsRelative = scripts == null ? null : Path.GetRelativePath(stage, scripts);
            Directory.Move(stage, final);
            Report(100, DisplayName(package.Key) + " 安装完成");
            return (Path.Combine(final, exeRelative), scriptsRelative == null ? null : Path.Combine(final, scriptsRelative));
        }
        finally { RemoveOwnedDirectory(stage); }
    }

    private static async Task<bool> HashMatchesAsync(string file, string expected, CancellationToken cancellation)
    {
        await using var stream = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task VerifyBuildAsync(ToolPaths tools, CancellationToken cancellation)
    {
        var root = Path.Combine(ManagedTools.Root, "_verification", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
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
        finally { RemoveOwnedDirectory(root); }
    }

    private static async Task<string> RunAsync(string executable, IEnumerable<string> args, string directory,
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
        return output.Split('\n').FirstOrDefault()?.Trim() ?? Path.GetFileName(executable);
    }

    private static void RemoveOwnedDirectory(string path)
    {
        var absolute = Path.GetFullPath(path);
        if (!absolute.StartsWith(Path.GetFullPath(ManagedTools.Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("清理路径不在工具安装目录内。");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
    }
    private static string? GetPath(ToolPaths tools, string key) => key switch { "CMake" => tools.CMake, "Ninja" => tools.Ninja, "Compiler" => tools.Compiler, _ => tools.OpenOcd };
    private static string DisplayName(string key) => key switch { "Compiler" => "ARM GCC", "OpenOcd" => "OpenOCD", _ => key };
    public void Dispose() => _http.Dispose();
}
