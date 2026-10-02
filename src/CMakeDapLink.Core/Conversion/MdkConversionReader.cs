using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CMakeDapLink.Core;

internal static class MdkConversionReader
{
    private static string Value(XElement? element, string name) => element?.Element(name)?.Value.Trim() ?? "";
    public static IReadOnlyList<string> Targets(string project) => XDocument.Load(project).Descendants("Target")
        .Select(x => Value(x, "TargetName")).Where(x => x.Length > 0).ToArray();

    public static ConversionProject Read(string root, string chip, string project, string targetName)
    {
        var result = new ConversionProject { Root = root, Chip = chip, Name = targetName };
        var portIncludes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directory = Path.GetDirectoryName(project)!;
        var matches = XDocument.Load(project).Descendants("Target").Where(x => Value(x, "TargetName") == targetName).ToArray();
        if (matches.Length != 1) { result.Issues.Add(ConversionPaths.Block(project, $"所选目标 {targetName} 不唯一或不存在。", "明确选择工程文件及目标。")); return result; }
        var target = matches[0]; var options = target.Element("TargetOption"); var common = options?.Element("TargetCommonOption");
        var ads = options?.Element("TargetArmAds"); var misc = ads?.Element("ArmAdsMisc"); var compiler = ads?.Element("Cads"); var assembler = ads?.Element("Aads"); var linker = ads?.Element("LDads");
        if (Value(target, "ToolsetName") != "ARM-ADS" || ads == null)
            result.Issues.Add(ConversionPaths.Block(project, "不是 MDK5 ARM-ADS 工程。", "选择 CubeMX 生成的 MDK5 STM32 .uvprojx。"));
        var device = Value(common, "Device").ToUpperInvariant();
        if (device.Length < 8 || !chip.StartsWith(device.TrimEnd('X'), StringComparison.OrdinalIgnoreCase))
            result.Issues.Add(ConversionPaths.Block(project, $"目标 Device={device} 与 .ioc 芯片 {chip} 不匹配。", "选择对应 .ioc 的 MDK 目标。"));
        result.OutputName = Value(common, "OutputName"); if (result.OutputName.Length == 0) result.OutputName = targetName;
        if (Value(common, "CreateLib") == "1") result.Issues.Add(ConversionPaths.Block(project, "所选目标生成静态库。", "选择可执行固件目标；静态库需要独立 ABI 校核。"));
        var cpuText = Value(misc, "AdsCpuType"); if (cpuText.Length == 0) cpuText = Regex.Match(Value(common, "Cpu"), "CPUTYPE\\(\"([^\"]+)\"\\)").Groups[1].Value;
        result.Cpu = cpuText.Trim('"').ToLowerInvariant();
        if (!new[] { "cortex-m0", "cortex-m0plus", "cortex-m3", "cortex-m4", "cortex-m7", "cortex-m33" }.Contains(result.Cpu, StringComparer.Ordinal))
            result.Issues.Add(ConversionPaths.Block(project, $"不支持或缺少 CPU 设置 {cpuText}。", "提供明确的单核 Cortex-M 目标配置。"));
        var fp = Value(misc, "RvdsVP");
        if (fp is "1" or "2" or "3")
        {
            result.Fpu = fp == "3" ? "fpv5-d16" : result.Cpu == "cortex-m7" ? "fpv5-sp-d16" : result.Cpu == "cortex-m33" ? "fpv5-sp-d16" : "fpv4-sp-d16";
            result.FloatAbi = "hard";
        }
        else if (fp is not "" and not "0") result.Issues.Add(ConversionPaths.Block(project, $"不支持 FPU 选择 RvdsVP={fp}。", "选择已校核的无 FPU、单精度或双精度配置。"));
        foreach (var hook in common?.Elements().Where(x => x.Name.LocalName is "BeforeCompile" or "BeforeMake" or "AfterMake") ?? [])
            for (var i = 1; i <= 2; i++) if (Value(hook, "RunUserProg" + i) == "1" && Value(hook, "UserProg" + i + "Name").Length > 0)
                result.Issues.Add(ConversionPaths.Block(project, $"{hook.Name} 包含命令 {Value(hook, "UserProg" + i + "Name")}。", "把生成源码/后处理步骤明确迁移到 CMake，再进行转换。"));
        void controls(XElement? element, List<string> includes, List<string> defines)
        {
            var c = element?.Element("VariousControls");
            includes.AddRange(Value(c, "IncludePath").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => ConversionPaths.Resolve(directory, x)));
            defines.AddRange(SplitDefines(Value(c, "Define")));
            foreach (var name in new[] { "MiscControls", "Undefine" })
                if (Value(c, name).Length > 0) result.Issues.Add(ConversionPaths.Block(project, $"{element?.Name}/{name}={Value(c, name)} 无法可靠映射。", "将编译器专用参数改为等效的可移植设置，或手工保留对应 GCC 参数。"));
        }
        controls(compiler, result.Includes, result.Defines);
        var asmIncludes = new List<string>(); var asmDefines = new List<string>(); controls(assembler, asmIncludes, asmDefines);
        foreach (var element in new[] { compiler, assembler, linker })
            foreach (var name in new[] { "Ropi", "Rwpi", "useXO", "v6Lto", "uLtcg", "noStLib", "useUlib" })
                if (Value(element, name) == "1") result.Issues.Add(ConversionPaths.Block(project, $"{element?.Name}/{name} 已启用。", "关闭位置无关、LTO、特殊运行库或仅执行代码选项后转换，或手工迁移。"));
        foreach (var name in new[] { "IncludeLibs", "IncludeLibsPath", "Misc", "LinkerInputFile" })
            if (Value(linker, name).Length > 0) result.Issues.Add(ConversionPaths.Block(project, $"LDads/{name}={Value(linker, name)} 包含专用库或链接设置。", "使用相同源码构建 GCC 库；不要直接链接 ARMCC/ArmClang 二进制库。"));
        foreach (var group in target.Element("Groups")?.Elements("Group") ?? [])
        {
            var groupOptions = group.Element("GroupOption");
            if (Excluded(groupOptions)) continue;
            CheckOptions(groupOptions, project + " / " + Value(group, "GroupName"), compiler, assembler, result.Issues);
            foreach (var file in group.Element("Files")?.Elements("File") ?? [])
            {
                var fileOptions = file.Element("FileOption"); if (Excluded(fileOptions)) continue;
                var raw = Value(file, "FilePath"); var path = ConversionPaths.Resolve(directory, raw);
                CheckOptions(fileOptions, path, compiler, assembler, result.Issues);
                var type = Value(file, "FileType"); var extension = Path.GetExtension(path).ToLowerInvariant();
                if (type is "3" or "4" || extension is ".lib" or ".a" or ".o" or ".obj")
                {
                    var library = ConversionLibraries.CmsisDsp(path, result, true);
                    if (library != null) result.Libraries.Add(library);
                    continue;
                }
                if (type == "6") { result.Issues.Add(ConversionPaths.Block(path, "启用了自定义文件构建步骤。", "将该文件的自定义构建步骤显式迁移到 CMake。")); continue; }
                if (type == "5") continue;
                if (extension is ".h" or ".hpp" or ".txt") continue;
                var language = extension switch { ".c" => "C", ".cpp" or ".cxx" or ".cc" => "CXX", ".s" => "ASM", _ => "" };
                if (type == "8" && language == "C") language = "CXX";
                if (language.Length == 0) { result.Issues.Add(ConversionPaths.Block(path, "启用文件的类型不能自动编译。", "明确该文件是源码、头文件或自定义构建输入，并迁移构建步骤。")); continue; }
                if (!File.Exists(path)) { result.Issues.Add(ConversionPaths.Block(path, "启用源文件不存在。", "补齐所选目标的源文件。")); continue; }
                var adaptedPort = ConversionCompatibility.FreeRtosPort(path, result, portIncludes);
                if (adaptedPort == null) continue;
                path = adaptedPort;
                var sourceIncludes = new List<string>(); var sourceDefines = new List<string>(); var sourceFlags = new List<string>();
                if (language == "ASM") { sourceIncludes.AddRange(asmIncludes); sourceDefines.AddRange(asmDefines); }
                foreach (var sourceOptions in new[] { groupOptions, fileOptions })
                {
                    var section = sourceOptions?.Elements().FirstOrDefault(x => x.Name.LocalName is "GroupArmAds" or "FileArmAds");
                    var sourceCompiler = section?.Element(language == "ASM" ? "Aads" : "Cads");
                    var c = sourceCompiler?.Element("VariousControls");
                    sourceIncludes.AddRange(Value(c, "IncludePath").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => ConversionPaths.Resolve(directory, x)));
                    sourceDefines.AddRange(SplitDefines(Value(c, "Define")));
                    sourceFlags.AddRange(CMakeConversionReader.Tokens(Value(c, "MiscControls")));
                    if (sourceOptions?.Element("CommonProperty")?.Element("UseCPPCompiler")?.Value == "1") language = "CXX";
                    if (language != "ASM" && Value(sourceCompiler, "Optim") is { } optimization && optimization.Length > 0 && optimization != "0")
                        sourceFlags.Add(optimization switch { "1" => "-O0", "2" => "-O1", "3" => "-O2", "4" => "-O3", _ => "-Os" });
                }
                if (language != "ASM")
                {
                    string effective(string name)
                    {
                        var value = Value(compiler, name);
                        foreach (var sourceOptions in new[] { groupOptions, fileOptions })
                        {
                            var cads = sourceOptions?.Elements().FirstOrDefault(x => x.Name.LocalName is "GroupArmAds" or "FileArmAds")?.Element("Cads");
                            var local = Value(cads, name); if (local is not "" and not "2") value = local;
                        }
                        return value;
                    }
                    if (Value(target, "uAC6") != "1" && effective("EnumInt") == "0" || effective("vShortEn") == "1") sourceFlags.Add("-fshort-enums");
                    if (effective("PlainCh") is "0" or "1") sourceFlags.Add(effective("PlainCh") == "1" ? "-fsigned-char" : "-funsigned-char");
                    if (effective("vShortWch") == "1") sourceFlags.Add("-fshort-wchar");
                    if (language == "CXX" && Value(target, "uAC6") == "1" && effective("v6Rtti") == "0") sourceFlags.Add("-fno-rtti");
                }
                result.Sources.Add(new(path, Value(group, "GroupName"), language, sourceIncludes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), sourceDefines.Distinct(StringComparer.Ordinal).ToArray()) { CompilerFlags = sourceFlags.Distinct(StringComparer.Ordinal).ToArray() });
            }
        }
        for (var i = 0; i < result.Includes.Count; i++)
        {
            result.Includes[i] = ConversionCompatibility.MapInclude(result.Includes[i], portIncludes);
        }
        for (var i = 0; i < result.Sources.Count; i++)
            result.Sources[i] = result.Sources[i] with { Includes = result.Sources[i].Includes.Select(x => ConversionCompatibility.MapInclude(x, portIncludes)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
        ConversionPaths.Distinct(result.Includes); ConversionPaths.Distinct(result.Defines);
        foreach (var include in result.Includes.Concat(asmIncludes.Select(x => ConversionCompatibility.MapInclude(x, portIncludes))).Concat(result.Sources.SelectMany(x => x.Includes)))
            ConversionCompatibility.CheckInclude(include, result.Issues);
        var startups = result.Sources.Where(x => x.Language == "ASM" && Path.GetFileName(x.Path).StartsWith("startup_", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (startups.Length == 1) result.Startup = startups[0].Path;
        else result.Issues.Add(ConversionPaths.Block(project, $"启用源中找到 {startups.Length} 个 startup。", "选择只有一个标准 CubeMX startup 的单核目标。"));
        foreach (var source in result.Sources.Where(x => x.Language == "ASM" && x.Path != result.Startup))
        {
            var assembly = File.ReadAllText(source.Path);
            if (!Regex.IsMatch(assembly, @"(?m)^\s*\.syntax\s+unified\b") || Regex.IsMatch(assembly, @"(?mi)^\s*(?:AREA|EXPORT|IMPORT|THUMB|[A-Za-z_]\w*\s+(?:DCD|PROC))\b"))
                result.Issues.Add(ConversionPaths.Block(source.Path, "额外汇编源不是可直接交给 GCC 的 GNU unified 汇编。", "提供项目内对应 GCC 汇编源码；自动转换仅转换标准 CubeMX startup。"));
        }
        var scatter = Value(linker, "ScatterFile");
        if (Value(linker, "umfTarg") != "1" && scatter.Length > 0)
        {
            result.Linker = ConversionPaths.Resolve(directory, scatter);
            if (File.Exists(result.Linker)) result.Memory = ConversionLinker.ReadScatter(result.Linker, result.Issues);
            else result.Issues.Add(ConversionPaths.Block(result.Linker, "显式 scatter 文件不存在。", "补齐文件后转换。"));
        }
        else result.Memory = ActiveMemory(misc, project, result.Issues);
        if (result.Memory != null && result.Startup != null) result.Memory = ConversionStartup.WithStack(result.Startup, result.Memory);
        return result;
    }

    internal static IReadOnlyList<string> SplitDefines(string text) => Regex.Matches(text, "(?:[^,\\s\"]|\"[^\"]*\")+").Select(x => x.Value).ToArray();
    private static bool Excluded(XElement? options) => options?.Descendants("IncludeInBuild").Any(x => x.Value.Trim() == "0") == true;
    private static void CheckOptions(XElement? options, string path, XElement? compiler, XElement? assembler, List<ConversionIssue> issues)
    {
        if (options == null) return;
        foreach (var section in options.Elements().Where(x => x.Name.LocalName != "CommonProperty"))
        {
            if (section.Name.LocalName is not "GroupArmAds" and not "FileArmAds")
            { issues.Add(ConversionPaths.Block(path, $"存在 {section.Name} 的组/文件级专用配置。", "手工迁移该配置。")); continue; }
            foreach (var c in section.Elements())
            {
                if (c.Name.LocalName is not "Cads" and not "Aads") { issues.Add(ConversionPaths.Block(path, $"不支持 {c.Name} 覆盖。", "手工迁移该覆盖设置。")); continue; }
                foreach (var setting in c.Elements().Where(x => x.Name.LocalName != "VariousControls"))
                    if (setting.Name.LocalName is not "Optim" and not "wLevel" and not "EnumInt" and not "PlainCh" and not "vShortEn" and not "vShortWch" and not "v6Rtti" && setting.Value.Trim() is not "2" and not "" &&
                        !(setting.Value.Trim() == "0" && (setting.Name.LocalName is "v6Lang" or "v6LangP" || Value(c.Name.LocalName == "Cads" ? compiler : assembler, setting.Name.LocalName) is "0" or "")) &&
                        setting.Value.Trim() != Value(c.Name.LocalName == "Cads" ? compiler : assembler, setting.Name.LocalName))
                        issues.Add(ConversionPaths.Block(path, $"{c.Name}/{setting.Name}={setting.Value} 有编译覆盖。", "仅可自动迁移继承值、优化、宏/包含目录及常见 -std/-O 参数；手工迁移此设置。"));
                var controls = c.Element("VariousControls");
                if (Value(controls, "Undefine").Length > 0 || CMakeConversionReader.Tokens(Value(controls, "MiscControls")).Any(x => !Regex.IsMatch(x, @"^-(?:O[0123sg]|f(?:short-enums|short-wchar|signed-char|unsigned-char|rtti|no-rtti|no-exceptions|no-threadsafe-statics)|std=(?:c99|gnu99|c11|gnu11|c17|gnu17|c\+\+(?:11|14|17)|gnu\+\+(?:11|14|17)))$")))
                    issues.Add(ConversionPaths.Block(path, "组/文件级专用编译参数或取消宏不能自动迁移。", "显式迁移这些参数到 GCC。"));
            }
        }
        foreach (var option in options.Element("CommonProperty")?.Elements() ?? [])
            if (option.Name.LocalName is not "IncludeInBuild" and not "StopOnExitCode" and not "ComprImg" && option.Value.Trim() is not "" and not "0" and not "2" && option.Name.LocalName != "UseCPPCompiler")
                issues.Add(ConversionPaths.Block(path, $"组/文件选项 {option.Name}={option.Value} 不能忽略。", "移除或显式迁移该覆盖设置后转换。"));
    }
    private static ConversionMemory? ActiveMemory(XElement? misc, string path, List<ConversionIssue> issues)
    {
        var memories = misc?.Element("OnChipMemories");
        var flash = new List<XElement>(); var ram = new List<XElement>();
        foreach (var (flag, region, isFlash) in new[] { ("Im1Chk", "OCR_RVCT4", true), ("Im2Chk", "OCR_RVCT5", true), ("Ir1Chk", "OCR_RVCT9", false), ("Ir2Chk", "OCR_RVCT10", false), ("Ro1Chk", "OCR_RVCT1", true), ("Ro2Chk", "OCR_RVCT2", true), ("Ro3Chk", "OCR_RVCT3", true), ("Ra1Chk", "OCR_RVCT6", false), ("Ra2Chk", "OCR_RVCT7", false), ("Ra3Chk", "OCR_RVCT8", false) })
            if (Value(misc, flag) == "1" && memories?.Element(region) is { } memory && Value(memory, "Size") is not "" and not "0x0" and not "0") (isFlash ? flash : ram).Add(memory);
        if (flash.Count != 1 || ram.Count != 1)
        {
            issues.Add(ConversionPaths.Block(path, $"活动 MDK 内存选择为 {flash.Count} 个 ROM / {ram.Count} 个 RAM：{string.Join(", ", flash.Concat(ram).Select(x => x.Name + "=" + Value(x, "StartAddress") + "+" + Value(x, "Size")))}。",
                "为目标配置一个实际使用的 ROM 和 RAM，或提供保留命名段分配的已校核 GCC 工程；不会使用 Device pack 的最大容量替代活动设置。")); return null;
        }
        return new(ConversionLinker.Number(Value(flash[0], "StartAddress")), ConversionLinker.Number(Value(flash[0], "Size")), ConversionLinker.Number(Value(ram[0], "StartAddress")), ConversionLinker.Number(Value(ram[0], "Size")));
    }
}
