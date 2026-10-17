using System.Text.Json;
using CMakeDapLink.Core;

namespace CMakeDapLink.ExtensionHost;

internal sealed partial class RequestHandler
{
    private async Task<object> PreviewClangdAsync(JsonElement payload, CancellationToken cancellation)
    {
        var options = await OptionsAsync(payload, cancellation);
        if (Text(payload, "compilationDatabaseDirectory") is { Length: > 0 } databaseDirectory)
            options = options with { BuildDirectory = WorkspaceSafety.Child(root, databaseDirectory, allowRoot: true) };
        foreach (var path in new[] { ".clangd", ".vscode/settings.json" }) WorkspaceSafety.Child(root, path);
        var changes = DevelopmentConfiguration.PreviewClangd(options);
        WorkspaceSafety.Changes(root, changes);
        var database = WorkspaceSafety.Child(root, Path.Combine(options.BuildDirectory!, "compile_commands.json"));
        return new
        {
            changes, label = "配置 clangd", canonicalLabel = "配置 clangd",
            summary = "预览 clangd 编译数据库、编译器查询与 IntelliSense 设置；应用前不会修改文件。",
            compileDatabaseFound = File.Exists(database), compileDatabasePath = database,
            notes = File.Exists(database) ? Array.Empty<string>() : ["当前构建目录缺少 compile_commands.json；请显式执行导出编译数据库，再刷新 clangd。"]
        };
    }

    private async Task<object> PreviewDebugAsync(JsonElement payload, CancellationToken cancellation)
    {
        var options = await OptionsAsync(payload, cancellation, requireTarget: true, useFirmware: true);
        options = options with { FirmwareElfPath = DevelopmentElf(options) };
        var gdb = DevelopmentGdb(options.Compiler, Text(payload, "gdbPath"));
        var launch = WorkspaceSafety.Child(root, ".vscode/launch.json");
        var changes = DevelopmentConfiguration.PreviewDebugNamed(options, gdb, Text(payload, "debugName"));
        WorkspaceSafety.Changes(root, changes);
        var bytes = changes.FirstOrDefault(x => x.Path.Equals(launch, StringComparison.OrdinalIgnoreCase))?.After ?? File.ReadAllBytes(launch);
        return new
        {
            changes, configurationName = DevelopmentConfiguration.ConfigurationName(bytes), configuration = DevelopmentConfiguration.Configuration(bytes),
            label = "配置 Cortex-Debug 调试", canonicalLabel = "配置 Cortex-Debug 调试",
            summary = "生成使用所选探针、芯片脚本与 ELF 的 Cortex-Debug 连接配置；预览不会连接硬件。",
            notes = new[] { "调试配置使用 attach，只连接目标与加载符号；启动后可能暂停或复位目标，不会自动下载固件。", "需要安装 Cortex-Debug 扩展，并使用当前 ELF 对应的目标固件。" }
        };
    }

    private async Task<object> ProbeCheckAsync(JsonElement payload, CancellationToken cancellation)
    {
        if (!Flag(payload, "explicitHardware")) throw new ArgumentException("硬件检查需要明确确认，可能暂停或复位目标。");
        var options = await OptionsAsync(payload, cancellation, requireTarget: true);
        OpenOcdProbe probe;
        try { probe = OpenOcdProbe.Resolve(options); }
        catch (Exception error) when (error is FileNotFoundException or InvalidDataException)
        { return new { scriptFound = false, probeFound = false, chipConnected = false, output = error.Message, notes = new[] { "OpenOCD 脚本或依赖缺失，未连接硬件。" } }; }
        var missing = OpenOcdScripts.MissingFiles(options.Scripts, options.TargetScript, probe.InterfaceScript);
        if (missing.Count > 0) return new { scriptFound = false, probeFound = false, chipConnected = false, output = string.Join(Environment.NewLine, missing), notes = new[] { "OpenOCD 脚本或依赖缺失，未连接硬件。" } };
        var notes = new List<string> { DevelopmentHardware.ProbeWarning };
        progress(25, "连接探针与芯片", options.TargetScript);
        var run = await DevelopmentProcess.RunAsync(new(options.OpenOcd, DevelopmentHardware.ProbeArguments(options), root, ToolPrefix(new(options.CMake, options.Ninja, options.Compiler, options.OpenOcd, options.Scripts))), TimeSpan.FromSeconds(15), cancellation);
        var evidence = DevelopmentHardware.ParseProbeCheck(run.Output, run.ExitCode);
        if (run.TimedOut) notes.Add("硬件检查超时，诊断进程已结束；请确认目标当前状态。");
        else if (!evidence.ProbeFound) notes.Add("日志没有确认探针已打开；请检查 USB、驱动、探针类型及是否被占用。");
        else if (!evidence.ChipConnected) notes.Add("已检测到探针，但芯片连接尚未确认；请检查供电、SWD 接线、速度与芯片脚本。");
        var diagnostics = OpenOcdDiagnostics.Parse(run.Output, root).Select(x => new { x.Severity, x.Message, x.Suggestion, rawMessage = true }).ToArray();
        return new { scriptFound = true, probeFound = evidence.ProbeFound, chipConnected = evidence.ChipConnected, output = run.Output, notes, diagnostics, exitCode = run.ExitCode, timedOut = run.TimedOut };
    }

    private async Task<object> RtosAsync(JsonElement payload, CancellationToken cancellation)
    {
        if (!Flag(payload, "explicitHardware")) throw new ArgumentException("RTOS 读取需要明确确认，连接时可能暂停目标。");
        var port = 3333;
        if (payload.TryGetProperty("gdbPort", out var selectedPort) && (selectedPort.ValueKind != JsonValueKind.Number || !selectedPort.TryGetInt32(out port))) throw new ArgumentException("GDB 端口必须为 1–65535。");
        var endpoint = DevelopmentHardware.LocalEndpoint(Text(payload, "gdbHost"), port);
        var mode = Text(payload, "gdbTargetMode") ?? "remote";
        if (mode is not ("remote" or "extended-remote")) throw new ArgumentException("GDB 连接模式必须是 remote 或 extended-remote。");
        var options = await OptionsAsync(payload, cancellation, useFirmware: true);
        var elf = DevelopmentElf(options);
        var gdb = DevelopmentGdb(options.Compiler, Text(payload, "gdbPath"));
        progress(25, "读取 RTOS 任务", endpoint);
        var run = await DevelopmentProcess.RunAsync(new(gdb, DevelopmentHardware.GdbArguments(elf, endpoint, mode == "extended-remote"), root, Path.GetDirectoryName(gdb)!), TimeSpan.FromSeconds(20), cancellation);
        var snapshot = DevelopmentHardware.ParseRtos(run.Output);
        var notes = new List<string> { DevelopmentHardware.RtosWarning };
        notes.AddRange(snapshot.Notes);
        if (run.TimedOut) notes.Add("RTOS 读取超时，GDB 进程已结束；无法确认分离是否完成，请检查目标状态。");
        else if (!snapshot.Detached) notes.Add("GDB 未确认已分离，请在调试器中检查目标状态。");
        return new { tasks = snapshot.Tasks, notes, output = run.Output, freeRtosAvailable = snapshot.FreeRtosAvailable, detached = snapshot.Detached, endpoint, exitCode = run.ExitCode, timedOut = run.TimedOut };
    }

    private string DevelopmentElf(SetupOptions options)
    {
        var build = WorkspaceSafety.Child(root, options.BuildDirectory!);
        var artifacts = CMakeBuildPlan.FindElfs(build, options.BuildConfiguration, root).Select(x => WorkspaceSafety.Child(build, x)).ToArray();
        if (options.FirmwareElfPath is { } selected)
        {
            if (!File.Exists(selected) || !artifacts.Contains(selected, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("所选 ELF 不是当前配置的目标产物，请先编译并重新选择。");
            return WorkspaceSafety.Child(build, selected);
        }
        if (artifacts.Length != 1) throw new ArgumentException("请先编译当前配置，并明确选择要调试的 ELF 固件。");
        return artifacts[0];
    }

    private static string DevelopmentGdb(string compiler, string? selected)
    {
        var path = selected ?? Path.Combine(Path.GetDirectoryName(compiler)!, "arm-none-eabi-gdb.exe");
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new FileNotFoundException("未找到 GDB；请选择 arm-none-eabi-gdb.exe 的完整路径。", path);
        var name = Path.GetFileName(path);
        if (!new[] { "arm-none-eabi-gdb.exe", "gdb-multiarch.exe", "gdb.exe" }.Contains(name, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("请选择 arm-none-eabi-gdb.exe、gdb-multiarch.exe 或 gdb.exe。");
        WorkspaceSafety.Ordinary(path);
        return Path.GetFullPath(path);
    }
}
