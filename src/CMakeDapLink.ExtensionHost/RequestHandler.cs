using System.Text.Json;
using System.Text.RegularExpressions;
using CMakeDapLink.Core;

namespace CMakeDapLink.ExtensionHost;

internal sealed partial class RequestHandler(string root, JsonSerializerOptions json, Action<int, string, string?> progress, Func<string, string> display)
{
    public static string[] Commands { get; } = ["capabilities", "inspect", "previewTasks", "applyChanges", "verify", "build", "flash", "memory", "scanSources", "previewSources", "previewImport", "importFiles", "inspectConversion", "previewConversion", "history", "previewRestore", "restore", "tools", "selectTool", "repair", "importTool", "activateTool", "previewClangd", "previewDebug", "prepareCompileDatabase", "probeCheck", "rtos", "analyzeFirmware", "hardFault"];
    private static string? Text(JsonElement payload, string key) => payload.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Flag(JsonElement payload, string key, bool fallback = false) => payload.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
    private static string Required(JsonElement payload, string key) => Text(payload, key) is { Length: > 0 } value ? value : throw new ArgumentException("请提供 " + key + "。");
    private T Read<T>(JsonElement payload, string key) => payload.GetProperty(key).Deserialize<T>(json) ?? throw new ArgumentException(key + " 数据为空。");
    private static string[] Strings(JsonElement payload, string key) => payload.GetProperty(key).EnumerateArray().Select(x => x.GetString() ?? throw new ArgumentException(key + " 含无效条目。")).ToArray();
    private static string Configuration(JsonElement payload)
    {
        var value = Text(payload, "configuration") ?? "Debug";
        return CMakeConfigurations.Validate(value);
    }

    public async Task<object> ExecuteAsync(string command, JsonElement payload, CancellationToken cancellation)
    {
        if (!Commands.Contains(command, StringComparer.Ordinal)) throw new ArgumentException("不支持的命令：" + command);
        if (command == "flash" && !Flag(payload, "explicitFlash")) throw new ArgumentException("烧录需要明确确认，请使用烧录操作并确认目标与固件。");
        using var lease = command is "applyChanges" or "verify" or "build" or "flash" or "importFiles" or "restore" or "inspectConversion" or "repair" or "importTool" or "activateTool" or "prepareCompileDatabase" or "probeCheck" or "rtos"
            ? WorkspaceSafety.WriteLease(root) : null;
        cancellation.ThrowIfCancellationRequested();
        progress(0, "准备操作", null);
        object result = command switch
        {
            "inspect" => await InspectAsync(payload, cancellation),
            "previewTasks" => await PreviewTasksAsync(payload, cancellation),
            "applyChanges" => Apply(payload),
            "verify" => await VerifyAsync(payload, cancellation),
            "build" => await BuildAsync(payload, cancellation, "build"),
            "flash" => await BuildAsync(payload, cancellation, "flash"),
            "memory" => await MemoryAsync(payload, cancellation),
            "previewClangd" => await PreviewClangdAsync(payload, cancellation),
            "previewDebug" => await PreviewDebugAsync(payload, cancellation),
            "prepareCompileDatabase" => await PrepareCompileDatabaseAsync(payload, cancellation),
            "probeCheck" => await ProbeCheckAsync(payload, cancellation),
            "rtos" => await RtosAsync(payload, cancellation),
            "analyzeFirmware" => await AnalyzeFirmwareAsync(payload, cancellation),
            "hardFault" => await HardFaultAsync(payload, cancellation),
            "scanSources" => ScanSources(payload),
            "previewSources" => PreviewSources(payload),
            "previewImport" => PreviewImport(payload),
            "importFiles" => await ImportFilesAsync(payload, cancellation),
            "inspectConversion" => await InspectConversionAsync(payload, cancellation),
            "previewConversion" => PreviewConversion(payload),
            "history" => new { records = ChangeHistory.ListMetadata(root) },
            "previewRestore" => PreviewRestore(payload),
            "restore" => Restore(payload),
            "tools" => await ToolsAsync(payload, cancellation),
            "selectTool" => await SelectToolAsync(payload, cancellation),
            "repair" => await RepairAsync(payload, cancellation),
            "importTool" => await ImportToolAsync(payload, cancellation),
            "activateTool" => await ActivateToolAsync(payload, cancellation),
            _ => throw new ArgumentException("不支持的命令。")
        };
        progress(100, "操作完成", null);
        return result;
    }

    private ProjectInfo Project(JsonElement payload)
    {
        var usePresets = !(payload.TryGetProperty("configurePreset", out var preset) && (preset.ValueKind == JsonValueKind.Null || preset.ValueKind == JsonValueKind.String && preset.GetString() == ""));
        var project = ProjectInspector.Inspect(root, Text(payload, "configurePreset"), usePresets);
        if (project.BuildDirectory != null) WorkspaceSafety.Child(root, project.BuildDirectory);
        return project;
    }

    private async Task<ToolPaths> FindToolsAsync(JsonElement payload, CancellationToken cancellation)
        => (await DiscoverToolsAsync(payload, cancellation)).Selected;

    private async Task<object> InspectAsync(JsonElement payload, CancellationToken cancellation)
    {
        var project = Project(payload);
        var discovery = await DiscoverToolsAsync(payload, cancellation, enumerateAll: true);
        var tools = discovery.Selected;
        if (project.TargetScript != null) project = project with { TargetScript = OpenOcdScripts.AvailableTarget(tools.Scripts, project.TargetScript) };
        var pending = PendingTools(discovery);
        var versions = (await VersionsAsync(tools, cancellation)).Select(version => pending.FirstOrDefault(item => item.Key == version.Key) is { } choice
            ? version with { Version = "待选择", Problem = choice.Title } : version).ToArray();
        var issues = EnvironmentReport.Create(project, tools, Text(payload, "targetScript") ?? project.TargetScript ?? "", versions.Where(x => x.Problem != null).ToDictionary(x => x.Key, x => x.Problem!));
        return new { project, tools, versions, candidates = discovery.Candidates, pendingTools = pending,
            issues = issues.Select(issue => pending.FirstOrDefault(item => item.Key == issue.Key) is { } choice
                ? new { issue.Key, choice.Title, action = choice.Instructions, issue.BlocksConfiguration }
                : new { issue.Key, issue.Title, action = issue.Instructions, issue.BlocksConfiguration }),
            targets = SourceFolderPlanner.FindTargets(root), configurations = CMakeConfigurations.Discover(project,
                Text(payload, "buildDirectory") is { } selectedBuild ? WorkspaceSafety.Child(root, selectedBuild) : null), toolsRoot = ManagedTools.Root };
    }

    private sealed record VersionInfo(string Key, string? Path, string Version, bool Available, string? Problem);
    private async Task<IReadOnlyList<VersionInfo>> VersionsAsync(ToolPaths tools, CancellationToken cancellation)
    {
        var versions = new List<VersionInfo>();
        foreach (var key in ToolVersionManager.Keys)
        {
            cancellation.ThrowIfCancellationRequested();
            var path = ToolVersionManager.GetPath(tools, key);
            if (!File.Exists(path)) { versions.Add(new(key, path, "未安装", false, "可执行工具未找到。")); continue; }
            progress(10 + versions.Count * 15, "检测 " + ToolVersionManager.DisplayName(key), path);
            try
            {
                var run = await HostProcess.RunAsync(new(path!, ["--version"], root, ToolPrefix(tools)), TimeSpan.FromSeconds(8), cancellation, display);
                var line = run.Output.Split('\n').FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? "";
                var pattern = key switch
                {
                    "CMake" => @"(?i)\bcmake version\s+\d+\.\d+",
                    "Ninja" => @"\A\d+\.\d+(?:\.\d+)?(?:[.\w+-]*)\z",
                    "Compiler" => @"(?i)\barm-none-eabi-gcc\b.*?\d+\.\d+",
                    _ => @"(?i)Open On-Chip Debugger\s+\d+\.\d+"
                };
                var valid = run.ExitCode == 0 && Regex.IsMatch(line, pattern);
                versions.Add(new(key, path, line, valid, valid ? null : "版本输出不匹配：" + run.Output));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception ex) { versions.Add(new(key, path, "未通过版本验证", false, ex.Message)); }
        }
        return versions;
    }

    private static string ToolPrefix(ToolPaths tools) => string.Join(Path.PathSeparator, new[] { tools.CMake, tools.Ninja, tools.Compiler, tools.OpenOcd }.Where(x => x != null).Select(x => Path.GetDirectoryName(x!)).Distinct(StringComparer.OrdinalIgnoreCase));

    private async Task<SetupOptions> OptionsAsync(JsonElement payload, CancellationToken cancellation, bool requireTarget = false, bool useFirmware = false)
    {
        var project = Project(payload);
        if (!project.IsCMakeProject) throw new ArgumentException("当前工程没有根目录 CMakeLists.txt。");
        var discovery = await DiscoverToolsAsync(payload, cancellation);
        var tools = discovery.Selected;
        var pending = PendingTools(discovery).Where(item => requireTarget || item.Key != "OpenOcd").ToArray();
        if (pending.Length != 0) throw new ArgumentException(string.Join("；", pending.Select(item => item.Title)) + "。请先在工程配置或工具管理中选择版本，再重新预览。");
        foreach (var key in new[] { "CMake", "Ninja", "Compiler" })
            if (!File.Exists(ToolVersionManager.GetPath(tools, key))) throw new FileNotFoundException("请先补齐 " + ToolVersionManager.DisplayName(key) + "，再配置工程。");
        var target = Text(payload, "targetScript") ?? project.TargetScript ?? "";
        if (target.Length != 0)
        {
            target = target.Replace('\\', '/');
            if (!Regex.IsMatch(target, @"\Atarget/[a-zA-Z0-9_.-]+\.cfg\z"))
            {
                if (requireTarget) throw new ArgumentException("Target 必须是 target/xxx.cfg。");
                target = "";
            }
            if (target.Length != 0) target = OpenOcdScripts.AvailableTarget(tools.Scripts, target);
        }
        if (requireTarget && target.Length == 0) throw new ArgumentException("请确认芯片并选择匹配的 OpenOCD Target。");
        if (requireTarget && !File.Exists(tools.OpenOcd)) throw new FileNotFoundException("请先补齐 OpenOCD 工具，再预览任务。");
        var buildDirectory = WorkspaceSafety.Child(root, Text(payload, "buildDirectory") ?? project.BuildDirectory ?? Path.Combine(root, "build", "daplink-debug"));
        if (project.ConfigurePreset != null && project.BuildDirectory != null && !buildDirectory.Equals(Path.GetFullPath(project.BuildDirectory), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("预设构建目录与所选目录不一致，请使用该预设的 binaryDir。");
        var buildPreset = Text(payload, "buildPreset") ?? project.BuildPreset;
        if (buildPreset != null && !project.ConfigurePresets.Any(x => x.Name == project.ConfigurePreset && x.BuildPresets.Any(p => p.Name == buildPreset)))
            throw new ArgumentException("编译预设必须属于当前配置预设。");
        var configuration = Configuration(payload);
        var preset = project.ConfigurePresets.FirstOrDefault(x => x.Name == project.ConfigurePreset);
        var configured = preset?.ConfigureConfiguration;
        var built = preset?.BuildPresets.FirstOrDefault(x => x.Name == buildPreset)?.Configuration;
        foreach (var (kind, fixedConfiguration) in new[] { ("配置", configured), ("编译", built) })
            if (!string.IsNullOrWhiteSpace(fixedConfiguration) && !fixedConfiguration.Equals(configuration, StringComparison.Ordinal))
                throw new ArgumentException($"当前{kind}预设固定使用 {fixedConfiguration}，与所选 {configuration} 不一致。请选择 {fixedConfiguration} 配置或切换对应预设，然后重新预览。");
        var firmware = useFirmware ? Text(payload, "firmwareElfPath") : null;
        if (string.IsNullOrWhiteSpace(firmware)) firmware = null;
        if (firmware != null) firmware = WorkspaceSafety.Child(buildDirectory, WorkspaceSafety.Child(root, firmware));
        var speed = 1000;
        if (payload.TryGetProperty("adapterSpeed", out var speedValue) && (speedValue.ValueKind != JsonValueKind.Number || !speedValue.TryGetInt32(out speed)))
            throw new ArgumentException("探针速度必须为 1–50000 kHz。");
        var options = new SetupOptions(root, tools.CMake!, tools.Ninja!, tools.Compiler!, tools.OpenOcd ?? "", tools.Scripts!, target, project.ConfigurePreset,
            buildPreset, buildDirectory, project.ToolchainFile, firmware) { BuildConfiguration = configuration, TaskLanguage = Text(payload, "language") == "en" ? "en" : "zh-CN",
            ProbeType = Text(payload, "probeType") ?? "cmsis-dap", AdapterSpeed = speed, ResetMode = Text(payload, "resetMode") ?? "default" };
        OpenOcdProbe.Validate(options);
        return options;
    }

    private async Task<object> PreviewTasksAsync(JsonElement payload, CancellationToken cancellation)
    {
        var buildOnly = Flag(payload, "buildOnly");
        var options = await OptionsAsync(payload, cancellation, !buildOnly, !buildOnly);
        if (!buildOnly)
        {
            var absent = OpenOcdScripts.MissingFiles(options.Scripts, options.TargetScript, OpenOcdProbe.Resolve(options).InterfaceScript);
            if (absent.Count != 0) throw new InvalidDataException("内置脚本缺少：" + string.Join("、", absent));
        }
        var changes = TaskPreview.Create(options, BuildPlan(options), buildOnly);
        WorkspaceSafety.Changes(root, changes);
        return new { changes, label = buildOnly ? "配置 VS Code 编译任务" : "配置 VS Code 编译与烧录任务", canonicalLabel = buildOnly ? "配置 VS Code 编译任务" : "配置 VS Code 编译与烧录任务", summary = buildOnly ?
            "生成 CMake 配置和编译任务。预览不会运行编译或连接硬件。" : "生成 CMake 配置、编译和显式固件烧录任务。预览不会运行编译或连接硬件。", issues = Array.Empty<object>() };
    }

    private object Apply(JsonElement payload)
    {
        var changes = Read<List<FileChange>>(payload, "changes");
        WorkspaceSafety.Changes(root, changes);
        var record = ChangeHistory.Apply(root, DisplayLocalization.CanonicalActionLabel(Text(payload, "label") ?? "应用预览修改"), changes);
        return new { record, changedFiles = record?.Changes.Select(x => WorkspaceSafety.Child(root, x.Path)).ToArray() ?? [] };
    }

    private Task<object> VerifyAsync(JsonElement payload, CancellationToken cancellation) => BuildAsync(payload, cancellation, "verify");

    private async Task<object> BuildAsync(JsonElement payload, CancellationToken cancellation, string operation)
    {
        var flashing = operation == "flash";
        var options = await OptionsAsync(payload, cancellation, flashing, flashing);
        var plan = BuildPlan(options);
        var output = new List<string>(); var problems = new List<BuildProblem>(); var externalProblems = new HashSet<BuildProblem>(); bool openOcdChecked = false, flashed = false;
        async Task<bool> Run(ToolCommand command, string stage, TimeSpan limit, bool openOcd = false)
        {
            var run = await HostProcess.RunAsync(command, limit, cancellation, display);
            output.Add(display(stage) + Environment.NewLine + run.Output);
            var parsed = openOcd ? OpenOcdDiagnostics.Parse(run.Output, root) : BuildDiagnostics.Parse(run.Output, root, stage);
            problems.AddRange(parsed); externalProblems.UnionWith(parsed);
            if (run.ExitCode == 0 && !parsed.Any(x => x.Severity == "错误")) return true;
            if (!parsed.Any(x => x.Severity == "错误")) problems.Add(new(stage, "错误", "工具退出代码：" + run.ExitCode, null, null, null, "展开完整日志并修正工程配置。"));
            return false;
        }
        var recovery = await PrepareConfigureAsync(plan, cancellation);
        if (recovery != null) output.Add(recovery);
        progress(15, "CMake 配置", plan.BuildDirectory);
        var success = await Run(plan.Configure, "CMake 配置", TimeSpan.FromMinutes(5));
        if (success)
        {
            progress(45, "编译工程", null);
            success = await Run(plan.Build, "编译", TimeSpan.FromMinutes(15));
        }
        IReadOnlyList<string> firmware = [];
        if (success)
        {
            try { firmware = CMakeBuildPlan.FindElfs(plan.BuildDirectory, options.BuildConfiguration, root).Select(x => WorkspaceSafety.Child(plan.BuildDirectory, x)).ToArray(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
            { success = false; problems.Add(new("固件识别", "错误", ex.Message, null, null, null, "重新配置并核对当前构建目标。")); }
        }
        if (success && (flashing || operation == "verify" && Flag(payload, "checkOpenOcd", true)))
        {
            progress(85, flashing ? "准备烧录固件" : "软件检查 OpenOCD 脚本", flashing ? options.FirmwareElfPath : "未连接硬件。");
            try
            {
                if (!File.Exists(options.OpenOcd)) throw new FileNotFoundException("OpenOCD 工具未找到。");
                var probe = OpenOcdProbe.Resolve(options);
                var absent = OpenOcdScripts.MissingFiles(options.Scripts, options.TargetScript, probe.InterfaceScript);
                if (absent.Count != 0) throw new InvalidDataException("脚本或依赖缺少：" + string.Join("、", absent));
                string? selected = null;
                if (flashing)
                {
                    selected = options.FirmwareElfPath;
                    if (selected != null && !firmware.Contains(selected, StringComparer.OrdinalIgnoreCase))
                        throw new InvalidOperationException("所选固件不是当前配置的目标产物，请重新选择。");
                    if (selected == null && firmware.Count != 1)
                        throw new InvalidOperationException($"当前配置找到 {firmware.Count} 个 ELF 产物，请明确选择要烧录的固件。");
                    selected ??= firmware[0];
                    progress(90, "烧录固件", selected);
                }
                var args = probe.Arguments(options).Concat(["-c", flashing ? "program " + OpenOcdProbe.TclPath(selected!) + " verify reset exit" : "shutdown"]).ToArray();
                success = await Run(new(options.OpenOcd, args, root, plan.Configure.PathPrefix), flashing ? "OpenOCD 烧录" : "OpenOCD 脚本", flashing ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(20), true);
                if (flashing) flashed = success; else openOcdChecked = success;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception ex)
            { success = false; problems.Add(new(flashing ? "OpenOCD 烧录" : "OpenOCD 脚本", "错误", ex.Message, null, null, null,
                flashing ? "核对所选固件、探针、复位模式和 OpenOCD Target。" : "核对 Target 和 OpenOCD 版本。脚本检查不会连接硬件。")); }
        }
        return new { success, firmware, problems = problems.Select(p => new { p.Stage, p.Severity, p.Message, p.FilePath, p.Line, p.Column, p.Suggestion, rawMessage = externalProblems.Contains(p) }), output = string.Join(Environment.NewLine, output), openOcdChecked, flashed };
    }

    private CMakeBuildPlan BuildPlan(SetupOptions options)
    {
        var plan = CMakeBuildPlan.Create(options);
        WorkspaceSafety.Child(root, plan.BuildDirectory);
        return plan;
    }

    private async Task<string?> PrepareConfigureAsync(CMakeBuildPlan plan, CancellationToken cancellation)
    {
        var recovery = RelocatedCache.Recover(root, plan.BuildDirectory, progress);
        WorkspaceSafety.Child(root, Path.Combine(plan.BuildDirectory, ".cmake", "api", "v1", "query", "client-cmake-daplink", "codemodel-v2"));
        var messages = new List<string>();
        if (recovery != null) messages.Add(recovery);
        var preparation = plan.PrepareToolSelection();
        if (preparation != null)
        {
            progress(12, "准备所选编译工具", null);
            var result = await HostProcess.RunAsync(preparation, TimeSpan.FromSeconds(30), cancellation, display);
            if (!string.IsNullOrWhiteSpace(result.Output)) messages.Add(result.Output);
            if (result.ExitCode != 0) throw new IOException("准备 CMake 工具选择失败：" + result.Output);
        }
        CurrentFirmwareArtifacts.PrepareQuery(plan.BuildDirectory);
        return messages.Count == 0 ? null : string.Join(Environment.NewLine, messages);
    }

    private SourceFolderPlan SourcePlan(JsonElement payload, bool selected)
    {
        var folder = WorkspaceSafety.Child(root, Required(payload, "folder"), true);
        var files = selected ? Strings(payload, "selectedFiles") : null;
        if (files != null) foreach (var path in files) WorkspaceSafety.Child(root, path);
        return SourceFolderPlanner.Preview(root, folder, Text(payload, "target"), files);
    }
    private object ScanSources(JsonElement payload)
    {
        var plan = SourcePlan(payload, false);
        return new { plan.CandidateFiles, plan.AlreadyRegisteredFiles, selectedFiles = plan.SourceFiles.Concat(plan.HeaderFiles).ToArray(), targets = SourceFolderPlanner.FindTargets(root), plan.Notes };
    }
    private object PreviewSources(JsonElement payload)
    {
        var plan = SourcePlan(payload, true);
        var changes = SourceFolderPlanner.Changes(plan); WorkspaceSafety.Changes(root, changes);
        return new { changes, label = "更新 CMake 源文件：" + plan.Target, canonicalLabel = "更新 CMake 源文件：" + plan.Target, summary = $"{plan.Target} · {plan.SourceFiles.Count} 个源文件 · {plan.HeaderFiles.Count} 个头文件" };
    }
    private object PreviewImport(JsonElement payload)
    {
        var plan = ProjectFileImporter.Preview(root, Required(payload, "folderName"), Strings(payload, "sourceFiles"));
        foreach (var file in plan.Files) WorkspaceSafety.Ordinary(file.SourcePath);
        return new { plan, summary = $"{plan.Files.Count} 个文件 → {Path.GetFileName(plan.FolderPath)}；重复名称自动编号。" };
    }
    private async Task<object> ImportFilesAsync(JsonElement payload, CancellationToken cancellation)
    {
        var plan = Read<FileImportPlan>(payload, "plan");
        if (!Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.Root)).Equals(root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("导入预览不属于当前工程。");
        WorkspaceSafety.Child(root, plan.FolderPath);
        if (plan.Files.Count > 4096) throw new ArgumentException("一次导入不能超过 4096 个文件。");
        foreach (var file in plan.Files) { WorkspaceSafety.Child(root, file.DestinationPath); WorkspaceSafety.Ordinary(file.SourcePath); }
        var expected = ProjectFileImporter.Preview(root, Path.GetFileName(plan.FolderPath), plan.Files.Select(x => x.SourcePath).ToArray());
        if (expected.FolderExisted != plan.FolderExisted || expected.Files.Count != plan.Files.Count ||
            expected.Files.Where((file, index) => file != plan.Files[index]).Any())
            throw new IOException("导入计划与当前文件清单不一致，请重新预览。");
        var files = await CancellableFileImport.ApplyAsync(root, plan, Flag(payload, "confirmedExistingFolder"), progress, cancellation);
        return new { files, plan.FolderPath };
    }

    private static ConversionDirection Direction(JsonElement payload)
    {
        var value = Required(payload, "direction");
        return Enum.TryParse<ConversionDirection>(value, true, out var direction) && Enum.IsDefined(direction) ? direction : throw new ArgumentException("请选择 MDK 与 CMake 的转换方向。");
    }
    private async Task<object> InspectConversionAsync(JsonElement payload, CancellationToken cancellation)
    {
        var direction = Direction(payload);
        var inspection = CubeMxConverter.Inspect(root, direction);
        string? buildDirectory = null;
        IReadOnlyList<string> targets = inspection.Targets;
        if (direction == ConversionDirection.CMakeToMdk && !inspection.Issues.Any(x => x.Blocking))
        {
            var common = payload.TryGetProperty("commonOptions", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : payload;
            var options = await OptionsAsync(common, cancellation);
            var plan = BuildPlan(options);
            await PrepareConfigureAsync(plan, cancellation);
            progress(30, "解析 CMake 构建目标", "执行配置，不执行编译。");
            var result = await HostProcess.RunAsync(plan.Configure, TimeSpan.FromMinutes(5), cancellation, display);
            if (result.ExitCode != 0) throw new InvalidOperationException("CMake 配置未通过：" + result.Output);
            buildDirectory = plan.BuildDirectory;
            targets = ConfiguredTargets.Read(root, buildDirectory, options.BuildConfiguration);
            inspection = inspection with { Targets = targets, Issues = inspection.Issues.Where(x => !x.Message.Contains("目标将在所选 CMake", StringComparison.Ordinal)).ToArray() };
        }
        return new { inspection, buildDirectory, targets, inspection.SourceProjects };
    }
    private object PreviewConversion(JsonElement payload)
    {
        var buildDirectory = Text(payload, "buildDirectory");
        if (buildDirectory != null) buildDirectory = WorkspaceSafety.Child(root, buildDirectory);
        var sourceProject = Text(payload, "sourceProject");
        if (sourceProject != null) sourceProject = WorkspaceSafety.Child(root, sourceProject);
        var direction = Direction(payload);
        var plan = CubeMxConverter.CreatePlan(new(root, direction, Required(payload, "target"), sourceProject, buildDirectory, Configuration(payload)));
        if (direction == ConversionDirection.MdkToCMake)
            plan = plan with { Issues = plan.Issues.Append(new ConversionIssue("MDK 转 CMake 生成 Debug 和 Release 预设；其他自定义配置请在生成后按工程需求添加。", "生成后检查优化与调试参数，再配置并编译所需配置。", false)).ToArray() };
        WorkspaceSafety.Changes(root, plan.Changes);
        return new { plan.Changes, label = "工程转换：" + Required(payload, "target"), canonicalLabel = "工程转换：" + Required(payload, "target"), plan.Summary, plan.Issues, plan.CanApply, plan.OutputProject, plan.SourceCount, plan.IncludeCount };
    }

    private object PreviewRestore(JsonElement payload)
    {
        var id = Required(payload, "id");
        var changes = ChangeHistory.GetRestoreChanges(root, id); WorkspaceSafety.Changes(root, changes);
        var conflicts = ChangeHistory.GetRestoreConflicts(root, id).Select(x => x.Path).ToArray();
        var original = ChangeHistory.ListMetadata(root).First(x => x.Id == id);
        return new { changes, label = "恢复 · " + DisplayLocalization.CanonicalActionLabel(original.Label), canonicalLabel = "恢复 · " + DisplayLocalization.CanonicalActionLabel(original.Label), conflicts };
    }
    private object Restore(JsonElement payload)
    {
        var id = Required(payload, "id");
        var changes = ChangeHistory.GetRestoreChanges(root, id); WorkspaceSafety.Changes(root, changes);
        var reviewed = payload.TryGetProperty("changes", out _) ? Read<List<FileChange>>(payload, "changes") : null;
        if (reviewed != null) WorkspaceSafety.Changes(root, reviewed);
        // The extension supplies this flag only after its explicit native confirmation dialog.
        var record = ChangeHistory.Restore(root, id, Flag(payload, "overwriteChangedFiles"), reviewed);
        return new { record, changedFiles = record.Changes.Select(x => WorkspaceSafety.Child(root, x.Path)).ToArray() };
    }

    private ToolVersionManager Manager(JsonElement payload) => new(Text(payload, "installationRoot"));
    private async Task<object> ToolsAsync(JsonElement payload, CancellationToken cancellation)
    {
        var discovery = await DiscoverToolsAsync(payload, cancellation, enumerateAll: true);
        var tools = discovery.Selected;
        var manager = Manager(payload);
        return new { installations = await manager.ListAsync(tools, cancellation, discovery), tools, root = manager.Root };
    }
    private IProgress<ToolRepairProgress> ToolProgress() => new ImmediateProgress<ToolRepairProgress>(item => progress(item.Percent, item.Stage, item.Message));
    private async Task<object> RepairAsync(JsonElement payload, CancellationToken cancellation)
    {
        var discovery = await DiscoverToolsAsync(payload, cancellation);
        var tools = discovery.Selected;
        var pending = PendingTools(discovery);
        if (pending.Length != 0)
        {
            progress(100, "需要选择已安装工具", "待选择工具：" + string.Join("；", pending.Select(item => item.Title)));
            return new { tools, installed = Array.Empty<string>(), problems = pending.Select(item => item.Instructions).ToArray(), selectionRequired = pending };
        }
        var versions = await VersionsAsync(tools, cancellation);
        using var installer = new ToolRepairInstaller(Manager(payload).Root);
        var target = Text(payload, "targetScript") ?? Project(payload).TargetScript;
        if (target != null && !Regex.IsMatch(target.Replace('\\', '/'), @"\Atarget/[a-zA-Z0-9_.-]+\.cfg\z")) throw new ArgumentException("Target 必须是 target/xxx.cfg。");
        return await installer.RepairAsync(tools, versions.Where(x => !x.Available).Select(x => x.Key).ToArray(), target, ToolProgress(), cancellation, savePaths: false);
    }
    private async Task<object> ImportToolAsync(JsonElement payload, CancellationToken cancellation)
    {
        var archive = Required(payload, "archive");
        if (!Path.IsPathFullyQualified(archive) || !Path.GetExtension(archive).Equals(".zip", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择完整工具 ZIP 安装包。");
        WorkspaceSafety.Ordinary(archive);
        var tools = await FindToolsAsync(payload, cancellation);
        var installation = await Manager(payload).ImportZipAsync(WorkspaceSafety.ToolKey(Required(payload, "key")), archive, tools, ToolProgress(), cancellation);
        return new { installation };
    }
    private async Task<object> ActivateToolAsync(JsonElement payload, CancellationToken cancellation)
    {
        var requested = Read<ToolInstallation>(payload, "installation");
        var tools = await FindToolsAsync(payload, cancellation);
        var manager = Manager(payload);
        var key = WorkspaceSafety.ToolKey(requested.Key);
        // Only an installation freshly discovered by Core can be activated; client metadata is not trusted.
        var installations = await manager.ListAsync(tools, cancellation);
        var installation = installations.FirstOrDefault(x => x.Key == key && x.Executable.Equals(requested.Executable, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("所选工具版本不在当前安装清单中，请重新读取工具列表。");
        WorkspaceSafety.Tools(new(key == "CMake" ? installation.Executable : null, key == "Ninja" ? installation.Executable : null,
            key == "Compiler" ? installation.Executable : null, key == "OpenOcd" ? installation.Executable : null, installation.Scripts));
        return await ValidateToolAsync(key, installation.Executable, manager, cancellation);
    }

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
