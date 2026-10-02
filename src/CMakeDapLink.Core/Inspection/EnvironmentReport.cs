namespace CMakeDapLink.Core;

public sealed record SetupIssue(string Key, string Title, string Instructions, bool BlocksConfiguration = true);

public static class EnvironmentReport
{
    public static IReadOnlyList<SetupIssue> Create(ProjectInfo? project, ToolPaths tools, string target,
        IReadOnlyDictionary<string, string> runFailures)
    {
        var issues = new List<SetupIssue>();
        if (project == null || !project.IsCMakeProject)
            issues.Add(new("Project", "未选择有效的 CMake 工程", "在“工程配置”选择含 CMakeLists.txt 的工程根目录。"));
        foreach (var (key, name, path, executable, install) in new[]
        {
            ("CMake", "CMake", tools.CMake, "cmake.exe", "安装 CMake 或 STM32CubeCLT"),
            ("Ninja", "Ninja", tools.Ninja, "ninja.exe", "安装 Ninja 或 STM32CubeCLT"),
            ("Compiler", "ARM GCC", tools.Compiler, "arm-none-eabi-gcc.exe", "安装 Arm GNU Toolchain 或 STM32CubeCLT"),
            ("OpenOcd", "OpenOCD", tools.OpenOcd, "openocd.exe", "安装带 CMSIS-DAP 支持的 OpenOCD 完整包")
        })
        {
            if (path == null || !File.Exists(path))
                issues.Add(new(key, name + " 未找到", $"点击“自动修复”，程序会下载完整工具包到 {ManagedTools.Root} 并补齐路径。可在“工具管理”更换安装目录。也可{install}，然后在配置详情的 {name} 行点“选择”，指定 {executable}。"));
            else if (runFailures.TryGetValue(key, out var failure))
                issues.Add(new(key, name + " 无法运行", $"当前文件：{path}\n检测结果：{failure}\n点击“自动修复”安装完整工具包，或手动选择可正常运行 --version 的 {executable}。"));
        }
        if (!EnvironmentScanner.IsScripts(tools.Scripts))
            issues.Add(new("Scripts", "CMSIS-DAP 脚本目录缺失", "点击“自动修复”补齐 OpenOCD 完整包；或者在“脚本目录”行选择同时含 interface/cmsis-dap.cfg 和 target 文件夹的 scripts 目录，通常在 share/openocd/scripts 或 openocd/scripts 下。"));
        target = target.Trim().Replace('\\', '/');
        var mapped = project?.Chip == null ? null : OpenOcdScripts.TargetForChip(project.Chip);
        if (string.IsNullOrWhiteSpace(target) || !OpenOcdScripts.ValidTarget(target))
            issues.Add(new("Target", mapped == null ? "芯片 Target 需要手动匹配" : "OpenOCD Target 尚未补全", mapped == null
                ? "先确认芯片具体型号。该型号尚无可靠自动映射，请手动选择与芯片匹配的 target/*.cfg 和配套完整 OpenOCD 工具包。"
                : "已识别芯片对应 " + mapped + "；程序会先查找本机配套完整 OpenOCD 包，缺失时自动下载。可点击“自动修复”重试。"));
        else
        {
            var missing = OpenOcdScripts.MissingFiles(tools.Scripts, target);
            if (missing.Count != 0)
                issues.Add(new("Target", missing.Contains(target, StringComparer.OrdinalIgnoreCase) ? "OpenOCD Target 脚本缺失" : "OpenOCD 脚本依赖不完整",
                    "当前 Target：" + target + "\n缺少或无法读取：" + string.Join("、", missing) + "\n点击“自动修复”查找或下载配套完整 OpenOCD 包后重试。"));
            else if (runFailures.TryGetValue("Target", out var parseFailure) || runFailures.TryGetValue("Scripts", out parseFailure))
                issues.Add(new("Target", "OpenOCD Target 脚本解析未通过", "当前 Target：" + target + "\n检测结果：" + parseFailure + "\n点击“自动修复”下载匹配的完整工具包后重试；脚本验证不连接硬件。"));
        }
        if (project?.IsCMakeProject == true)
        {
            if (project.Chip == null)
                issues.Add(new("Chip", "芯片型号需要手动核对", "确认目标芯片的具体型号；CubeMX 工程可核对 .ioc 中的 Mcu.CPN，其他工程可核对 CMake 的芯片编译宏。根据该型号填写 OpenOCD Target 脚本。", false));
            foreach (var note in project.Notes.Where(x => x.Contains("CMakePresets.json", StringComparison.Ordinal) || x.Contains("binaryDir", StringComparison.Ordinal)))
                issues.Add(new("Preset", "构建预设需要补全", note + "\n在工程 CMakePresets.json 中修正 JSON，并为配置预设提供可解析的 binaryDir，或为它设置对应的 buildPreset，然后重新选择工程。"));
        }
        return issues;
    }
}
