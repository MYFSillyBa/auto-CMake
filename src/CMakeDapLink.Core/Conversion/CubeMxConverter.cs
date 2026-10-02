using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CMakeDapLink.Core;

public static class CubeMxConverter
{
    public static ConversionInspection Inspect(string root, ConversionDirection direction)
    {
        root = Path.GetFullPath(root); var issues = new List<ConversionIssue>(); var targets = new List<string>(); var projects = new List<string>(); var chip = "";
        try
        {
            chip = ReadChip(root, issues);
            if (direction == ConversionDirection.MdkToCMake)
            {
                projects.AddRange(ProjectFiles(root));
                foreach (var project in projects) targets.AddRange(MdkConversionReader.Targets(project));
                if (projects.Count == 0) issues.Add(ConversionPaths.Block(root, "未找到 MDK5 .uvprojx。", "选择包含 .ioc 和 MDK-ARM 工程的 CubeMX 工程根目录。"));
            }
            else
            {
                var cmake = Path.Combine(root, "CMakeLists.txt");
                if (!File.Exists(cmake)) issues.Add(ConversionPaths.Block(cmake, "根目录没有 CMake 工程。", "选择 CubeMX ARM GCC CMake 工程根目录。"));
                else
                {
                    projects.Add(cmake);
                    var info = ProjectInspector.Inspect(root);
                    foreach (var preset in info.ConfigurePresets)
                    {
                        if (preset.BinaryDirectory == null || !Directory.Exists(Path.Combine(preset.BinaryDirectory, ".cmake", "api", "v1", "reply"))) continue;
                        try { targets.AddRange(CMakeConversionReader.Targets(root, preset.BinaryDirectory, preset.ConfigureConfiguration ?? preset.BuildConfiguration ?? "Debug")); }
                        catch (InvalidDataException ex) { issues.Add(new(ex.Message, "重新配置所选预设后刷新目标。", false)); }
                    }
                    if (targets.Count == 0) issues.Add(new("目标将在所选 CMake 预设/配置完成 File API 配置后读取。", "选择预设并读取构建目标。", false));
                }
            }
        }
        catch (Exception ex) when (Expected(ex)) { issues.Add(ConversionPaths.Block(root, ex.Message, "检查工程文件是否可读且格式正确，然后重新读取。")); }
        return new(root, chip, targets.Distinct(StringComparer.Ordinal).ToArray(), projects, issues);
    }

    public static ConversionPlan CreatePlan(ConversionRequest request)
    {
        var root = Path.GetFullPath(request.Root); var issues = new List<ConversionIssue>(); var output = request.Direction == ConversionDirection.MdkToCMake ? Path.Combine(root, "CMakeLists.txt") : Path.Combine(root, "MDK-ARM", ConversionPaths.SafeName(request.Target) + ".uvprojx");
        try
        {
            var chip = ReadChip(root, issues);
            if (issues.Any(x => x.Blocking)) return new(root, output, [], issues, 0, 0, "工程识别需要处理。");
            ConversionProject project;
            if (request.Direction == ConversionDirection.MdkToCMake)
            {
                var candidates = ProjectFiles(root).Where(x => request.SourceProject == null || Path.GetFullPath(request.SourceProject, root).Equals(x, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (candidates.Length != 1)
                {
                    issues.Add(ConversionPaths.Block(root, $"找到 {candidates.Length} 个候选 MDK 工程。", "明确选择 .uvprojx 文件；多个工程不能只根据同名目标猜测。")); return new(root, output, [], issues, 0, 0, "请选择源工程。");
                }
                project = MdkConversionReader.Read(root, chip, candidates[0], request.Target);
            }
            else
            {
                if (request.BuildDirectory == null)
                {
                    issues.Add(ConversionPaths.Block(root, "没有所选配置的构建目录。", "先对所选 CMake 预设执行 File API 配置，并传入其构建目录。")); return new(root, output, [], issues, 0, 0, "请读取 CMake 构建目标。");
                }
                project = CMakeConversionReader.Read(root, chip, Path.GetFullPath(request.BuildDirectory, root), request.Configuration, request.Target);
            }
            issues.AddRange(project.Issues.Distinct());
            if (project.Memory is { } memory && (memory.FlashSize == 0 || memory.RamSize == 0 || memory.StackSize + memory.HeapSize > memory.RamSize))
                issues.Add(ConversionPaths.Block(output, "活动内存大小无效，或堆/栈超过 RAM。", "检查所选目标的显式内存配置。"));
            IReadOnlyList<string>? vectors = null;
            if (project.Startup != null) vectors = ConversionStartup.ReadVectors(project.Startup, request.Direction == ConversionDirection.MdkToCMake, issues);
            if (issues.Any(x => x.Blocking) || vectors == null || project.Memory == null)
                return new(root, output, [], issues, project.Sources.Count, project.Includes.Count, "当前目标含需要显式处理的设置。");
            var changes = request.Direction == ConversionDirection.MdkToCMake ? GccChanges(project, vectors) : MdkChanges(project, vectors);
            issues.Add(new("转换保留源码与原构建格式；生成文件需要通过预览确认。", "配置并编译生成的目标以验证工具链行为。", false));
            return new(root, output, changes, issues, project.Sources.Count, project.Includes.Count,
                $"{chip} · {request.Target} · {project.Cpu}{(project.Fpu == null ? "" : " / " + project.Fpu + " / " + project.FloatAbi)} · FLASH 0x{project.Memory.FlashStart:X}+0x{project.Memory.FlashSize:X} · RAM 0x{project.Memory.RamStart:X}+0x{project.Memory.RamSize:X} · {project.Sources.Count} 个源文件 · {project.Libraries.Count} 个库。");
        }
        catch (Exception ex) when (Expected(ex)) { issues.Add(ConversionPaths.Block(root, ex.Message, "补齐工程文件并重新配置所选目标后再转换。")); return new(root, output, [], issues, 0, 0, "无法读取转换输入。"); }
    }
    private static bool Expected(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException or KeyNotFoundException;
    private static string ReadChip(string root, List<ConversionIssue> issues)
    {
        if (!Directory.Exists(root)) { issues.Add(ConversionPaths.Block(root, "工程目录不存在。", "选择现有 CubeMX 工程目录。")); return ""; }
        var iocs = Directory.EnumerateFiles(root, "*.ioc", SearchOption.TopDirectoryOnly).ToArray();
        if (iocs.Length != 1) { issues.Add(ConversionPaths.Block(root, $"根目录找到 {iocs.Length} 个 .ioc，无法唯一识别 CubeMX 工程。", "选择只有一个对应 .ioc 的工程根目录。")); return ""; }
        var text = File.ReadAllText(iocs[0]);
        var chip = Regex.Match(text, @"(?m)^Mcu\.CPN\s*=\s*(STM32[A-Za-z0-9]+)\s*$").Groups[1].Value.ToUpperInvariant();
        if (chip.Length == 0) issues.Add(ConversionPaths.Block(iocs[0], "缺少准确的 STM32 Mcu.CPN。", "使用保留完整 Mcu.CPN 元数据的 CubeMX .ioc；不会根据名称猜测芯片。"));
        if (!Regex.IsMatch(text, @"(?m)^ProjectManager\.ProjectName=.+") || !Regex.IsMatch(text, @"(?m)^ProjectManager\.FirmwarePackage=STM32Cube") ||
            (!Directory.Exists(Path.Combine(root, "Core")) && !Directory.Exists(Path.Combine(root, "Drivers"))))
            issues.Add(ConversionPaths.Block(iocs[0], "缺少 CubeMX 固件生成元数据或 Core/Drivers 源目录。", "选择保留 .ioc、CubeMX 固件包元数据和生成源目录的项目。"));
        if (Regex.IsMatch(chip, @"^STM32H7[45][57]") || Regex.IsMatch(text, @"(?m)^ProjectManager\.ProjectStructure=.*(?:Dual|Multi)", RegexOptions.IgnoreCase))
            issues.Add(ConversionPaths.Block(iocs[0], "双核/多核工程的目标隔离和内存不能自动映射。", "为每个核心建立独立源根及明确内存配置后手工迁移。"));
        return chip;
    }
    private static IEnumerable<string> ProjectFiles(string root)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var file in Directory.EnumerateFiles(root, "*.uvprojx")) yield return Path.GetFullPath(file);
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (new[] { ".git", ".vs", ".vscode", "build", "bin", "obj", "Drivers", "Middlewares", "node_modules" }.Contains(Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase)) continue;
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (var file in ProjectFiles(dir)) yield return file;
        }
    }

    private static IReadOnlyList<FileChange> GccChanges(ConversionProject project, IReadOnlyList<string> vectors)
    {
        var root = project.Root; var name = ConversionPaths.SafeName(project.Name); var changes = new List<FileChange>();
        var startup = "cmake/converted/startup_" + name + ".S"; var linker = "cmake/converted/" + name + ".ld";
        var counterpart = ConversionStartup.GccCounterpart(project, vectors);
        if (counterpart != null) startup = ConversionPaths.Relative(root, counterpart);
        else changes.Add(ConversionPaths.Change(root, startup, ConversionStartup.Gcc(project.Startup!, vectors, project.Cpu)));
        changes.Add(ConversionPaths.Change(root, linker, ConversionLinker.Gcc(project.Memory!)));
        changes.Add(ConversionPaths.Change(root, "cmake/converted/runtime.c", "__attribute__((weak)) void _init(void) {}\n__attribute__((weak)) void _fini(void) {}\n__attribute__((weak)) void *__dso_handle;\n"));
        changes.Add(ConversionPaths.Change(root, "cmake/gcc-arm-none-eabi.cmake", """
set(CMAKE_SYSTEM_NAME Generic)
set(CMAKE_SYSTEM_PROCESSOR arm)
set(CMAKE_TRY_COMPILE_TARGET_TYPE STATIC_LIBRARY)
find_program(ARM_GCC arm-none-eabi-gcc REQUIRED)
find_program(ARM_GXX arm-none-eabi-g++ REQUIRED)
find_program(ARM_OBJCOPY arm-none-eabi-objcopy REQUIRED)
find_program(ARM_SIZE arm-none-eabi-size REQUIRED)
set(CMAKE_C_COMPILER "${ARM_GCC}")
set(CMAKE_CXX_COMPILER "${ARM_GXX}")
set(CMAKE_ASM_COMPILER "${ARM_GCC}")
set(CMAKE_OBJCOPY "${ARM_OBJCOPY}" CACHE FILEPATH "")
set(CMAKE_SIZE "${ARM_SIZE}" CACHE FILEPATH "")

"""));
        var cmake = new StringBuilder($"cmake_minimum_required(VERSION 3.22)\nproject({name} LANGUAGES C CXX ASM)\nset(CMAKE_C_STANDARD 11)\nset(CMAKE_C_EXTENSIONS ON)\nset(CMAKE_CXX_STANDARD 17)\nadd_executable({name}\n");
        foreach (var source in project.Sources.Where(x => x.Path != project.Startup)) cmake.AppendLine("  " + ConversionPaths.Quote(ConversionPaths.Relative(root, source.Path)));
        cmake.AppendLine("  " + ConversionPaths.Quote(startup)); cmake.AppendLine("  cmake/converted/runtime.c\n)");
        cmake.AppendLine($"set_target_properties({name} PROPERTIES OUTPUT_NAME {ConversionPaths.Quote(project.OutputName)} SUFFIX .elf)");
        cmake.AppendLine($"target_include_directories({name} PRIVATE"); foreach (var include in project.Includes) cmake.AppendLine("  " + CMakePath(root, include)); cmake.AppendLine(")");
        cmake.AppendLine($"target_compile_definitions({name} PRIVATE"); foreach (var define in project.Defines) cmake.AppendLine("  " + ConversionPaths.Quote(define)); cmake.AppendLine(")");
        foreach (var source in project.Sources.Where(x => x.Language == "CXX" && Path.GetExtension(x.Path).Equals(".c", StringComparison.OrdinalIgnoreCase)))
            cmake.AppendLine($"set_source_files_properties({ConversionPaths.Quote(ConversionPaths.Relative(root, source.Path))} PROPERTIES LANGUAGE CXX)");
        foreach (var source in project.Sources.Where(x => x.Path != project.Startup && (x.Includes.Count > 0 || x.Defines.Count > 0 || x.CompilerFlags.Count > 0)))
        {
            var src = ConversionPaths.Quote(ConversionPaths.Relative(root, source.Path));
            if (source.Includes.Count > 0) cmake.AppendLine($"set_property(SOURCE {src} APPEND PROPERTY INCLUDE_DIRECTORIES {string.Join(" ", source.Includes.Select(x => CMakePath(root, x)))})");
            if (source.Defines.Count > 0) cmake.AppendLine($"set_property(SOURCE {src} APPEND PROPERTY COMPILE_DEFINITIONS {string.Join(" ", source.Defines.Select(ConversionPaths.Quote))})");
            if (source.CompilerFlags.Count > 0) cmake.AppendLine($"set_property(SOURCE {src} APPEND PROPERTY COMPILE_OPTIONS {string.Join(" ", source.CompilerFlags.Select(ConversionPaths.Quote))})");
        }
        var machine = "-mcpu=" + project.Cpu + " -mthumb" + (project.Fpu == null ? "" : " -mfpu=" + project.Fpu + " -mfloat-abi=" + project.FloatAbi);
        cmake.AppendLine($"target_compile_options({name} PRIVATE {machine} -ffunction-sections -fdata-sections $<$<CONFIG:Debug>:-O0> $<$<CONFIG:Debug>:-g3> $<$<CONFIG:Release>:-Os>)");
        cmake.AppendLine($"target_link_options({name} PRIVATE {machine} -nostartfiles --specs=nano.specs --specs=nosys.specs \"-T${{CMAKE_CURRENT_SOURCE_DIR}}/{linker}\" -Wl,--gc-sections \"-Wl,-Map=${{CMAKE_CURRENT_BINARY_DIR}}/{name}.map\")");
        cmake.AppendLine($"set_property(TARGET {name} APPEND PROPERTY LINK_DEPENDS \"${{CMAKE_CURRENT_SOURCE_DIR}}/{linker}\")");
        if (project.Libraries.Count > 0)
        {
            cmake.AppendLine($"target_link_libraries({name} PRIVATE");
            foreach (var library in project.Libraries)
            {
                cmake.AppendLine("  " + CMakePath(root, library));
            }
            cmake.AppendLine("  m\n)");
        }
        cmake.AppendLine($"add_custom_command(TARGET {name} POST_BUILD\n  COMMAND \"${{CMAKE_OBJCOPY}}\" -O ihex \"$<TARGET_FILE:{name}>\" \"$<TARGET_FILE_DIR:{name}>/$<TARGET_FILE_BASE_NAME:{name}>.hex\"\n  COMMAND \"${{CMAKE_OBJCOPY}}\" -O binary \"$<TARGET_FILE:{name}>\" \"$<TARGET_FILE_DIR:{name}>/$<TARGET_FILE_BASE_NAME:{name}>.bin\"\n  COMMAND \"${{CMAKE_SIZE}}\" \"$<TARGET_FILE:{name}>\"\n  VERBATIM)\n");
        changes.Add(ConversionPaths.Change(root, "CMakeLists.txt", cmake.ToString()));
        var presets = new
        {
            version = 4,
            configurePresets = new[] { "Debug", "Release" }.Select(configuration => new
            {
                name = configuration, generator = "Ninja", binaryDir = "${sourceDir}/build/" + configuration,
                toolchainFile = "${sourceDir}/cmake/gcc-arm-none-eabi.cmake",
                cacheVariables = new Dictionary<string, string> { ["CMAKE_BUILD_TYPE"] = configuration, ["CMAKE_EXPORT_COMPILE_COMMANDS"] = "ON" }
            }),
            buildPresets = new[] { "Debug", "Release" }.Select(configuration => new { name = configuration, configurePreset = configuration })
        };
        changes.Add(ConversionPaths.Change(root, "CMakePresets.json", JsonSerializer.Serialize(presets, new JsonSerializerOptions { WriteIndented = true }) + "\n"));
        return changes;
    }

    private static IReadOnlyList<FileChange> MdkChanges(ConversionProject project, IReadOnlyList<string> vectors)
    {
        var root = project.Root; var safeName = ConversionPaths.SafeName(project.Name); var outputDirectory = Path.Combine(root, "MDK-ARM"); var memory = project.Memory!;
        var startup = "converted/startup_" + safeName + ".s"; var scatter = "converted/" + safeName + ".sct";
        var options = XElement.Parse(MdkProjectTemplate.TargetOptions);
        void set(XElement parent, string name, string value) { var element = parent.Element(name); if (element == null) throw new InvalidDataException("MDK template missing " + name); element.Value = value; }
        var common = options.Element("TargetCommonOption")!; var ads = options.Element("TargetArmAds")!; var misc = ads.Element("ArmAdsMisc")!;
        var compiler = ads.Element("Cads")!; var assembler = ads.Element("Aads")!; var ld = ads.Element("LDads")!;
        var device = Regex.IsMatch(project.Chip, @"[A-Z][0-9]$") && project.Chip.Length > 11 ? project.Chip[..^2] : project.Chip;
        set(common, "Device", device); set(common, "Vendor", "STMicroelectronics");
        var armCpu = project.Cpu.Replace("cortex-m", "Cortex-M", StringComparison.Ordinal);
        set(common, "Cpu", $"IRAM(0x{memory.RamStart:X}-0x{memory.RamStart + memory.RamSize - 1:X}) IROM(0x{memory.FlashStart:X}-0x{memory.FlashStart + memory.FlashSize - 1:X}) CPUTYPE(\"{armCpu}\"){(project.Fpu == null ? "" : project.Fpu == "fpv5-d16" ? " FPU3(DFPU)" : " FPU2")}");
        set(common, "OutputDirectory", ".\\Objects\\" + safeName + "\\"); set(common, "OutputName", project.OutputName); set(common, "CreateExecutable", "1"); set(common, "CreateHexFile", "1"); set(common, "DebugInformation", "1");
        set(options.Element("CommonProperty")!, "IncludeInBuild", "1"); set(options.Element("CommonProperty")!, "StopOnExitCode", "3");
        set(misc, "AdsCpuType", "\"" + armCpu + "\""); set(misc, "RvdsVP", project.Fpu == null ? "0" : project.Fpu == "fpv5-d16" ? "3" : "2");
        set(misc, "hadIROM", "1"); set(misc, "hadIRAM", "1"); set(misc, "Im1Chk", "1"); set(misc, "Ir1Chk", "1"); set(misc, "useUlib", "0"); set(misc, "StupSel", "8");
        foreach (var (name, start, size) in new[] { ("IROM", memory.FlashStart, memory.FlashSize), ("IRAM", memory.RamStart, memory.RamSize), ("OCR_RVCT4", memory.FlashStart, memory.FlashSize), ("OCR_RVCT9", memory.RamStart, memory.RamSize) })
        {
            var region = misc.Element("OnChipMemories")!.Element(name)!; set(region, "Type", name.Contains("ROM", StringComparison.Ordinal) || name == "OCR_RVCT4" ? "1" : "0"); set(region, "StartAddress", "0x" + start.ToString("X")); set(region, "Size", "0x" + size.ToString("X"));
        }
        set(compiler, "interw", "1"); set(compiler, "Optim", "1"); set(compiler, "OneElfS", "1"); set(compiler, "uC99", "1"); set(compiler, "v6Lang", "3"); set(compiler, "v6LangP", "4"); set(compiler, "vShortEn", "0"); set(compiler, "vShortWch", "0");
        set(assembler, "interw", "1"); set(assembler, "ClangAsOpt", "0"); set(ld, "useFile", "1"); set(ld, "ScatterFile", scatter.Replace('/', '\\'));
        var target = new XElement("Target", new XElement("TargetName", project.Name), new XElement("ToolsetNumber", "0x4"), new XElement("ToolsetName", "ARM-ADS"), new XElement("pArmCC", ""), new XElement("pCCUsed", ""), new XElement("uAC6", "1"), options);
        var groups = new XElement("Groups"); target.Add(groups);
        var sourceGroups = project.Sources.Where(x => x.Path != project.Startup).GroupBy(x => x.Group + "\n" + string.Join(";", x.Includes) + "\n" + string.Join(";", x.Defines) + "\n" + string.Join(" ", x.CompilerFlags));
        var index = 0;
        foreach (var sources in sourceGroups)
        {
            var sample = sources.First(); var groupCompiler = new XElement(compiler); var groupAssembler = new XElement(assembler);
            if (sample.Language == "CXX") set(groupCompiler, "v6Rtti", sample.CompilerFlags.LastOrDefault(x => x is "-frtti" or "-fno-rtti") == "-fno-rtti" ? "0" : "1");
            var controls = groupCompiler.Element("VariousControls")!;
            set(controls, "IncludePath", string.Join(";", sample.Includes.Select(x => ConversionPaths.Relative(outputDirectory, x).Replace('/', '\\'))));
            set(controls, "Define", string.Join(",", sample.Defines)); set(controls, "MiscControls", string.Join(" ", sample.CompilerFlags));
            var groupCommon = new XElement(options.Element("CommonProperty")!);
            var files = new XElement("Files", sources.Select(source => new XElement("File", new XElement("FileName", Path.GetFileName(source.Path)), new XElement("FileType", source.Language == "CXX" ? "8" : "1"), new XElement("FilePath", ConversionPaths.Relative(outputDirectory, source.Path).Replace('/', '\\')))));
            groups.Add(new XElement("Group", new XElement("GroupName", sample.Group + " " + ++index), new XElement("GroupOption", groupCommon, new XElement("GroupArmAds", groupCompiler, groupAssembler)), files));
        }
        groups.Add(new XElement("Group", new XElement("GroupName", "Startup"), new XElement("Files", new XElement("File", new XElement("FileName", Path.GetFileName(startup)), new XElement("FileType", "2"), new XElement("FilePath", startup.Replace('/', '\\'))))));
        if (project.Libraries.Count > 0)
            groups.Add(new XElement("Group", new XElement("GroupName", "CMSIS DSP Libraries"), new XElement("Files", project.Libraries.Select(path =>
                new XElement("File", new XElement("FileName", Path.GetFileName(path)), new XElement("FileType", "4"), new XElement("FilePath", ConversionPaths.Relative(outputDirectory, path).Replace('/', '\\')))))));
        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement("Project", new XElement("SchemaVersion", "2.1"), new XElement("Header", "### uVision Project, (C) Keil Software"), new XElement("Targets", target)));
        return [ConversionPaths.Change(root, "MDK-ARM/" + startup, ConversionStartup.Arm(project.Startup!, vectors, memory)), ConversionPaths.Change(root, "MDK-ARM/" + scatter, ConversionLinker.Scatter(memory)), ConversionPaths.Change(root, "MDK-ARM/" + safeName + ".uvprojx", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"no\" ?>\n" + doc + "\n")];
    }

    private static string CMakePath(string root, string path)
    {
        var relative = ConversionPaths.Relative(root, path);
        return Path.IsPathRooted(relative) ? ConversionPaths.Quote(path) : $"\"${{CMAKE_CURRENT_SOURCE_DIR}}/{ConversionPaths.Quote(relative)[1..^1]}\"";
    }
}
