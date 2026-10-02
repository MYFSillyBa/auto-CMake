using System.Text.Json;
using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

internal static class CMakeConversionReader
{
    private static string Text(JsonElement element, string property) => element.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";
    private static JsonElement[] Array(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];
    private static JsonDocument ReadReply(string directory, JsonElement reference)
    {
        var name = Text(reference, "jsonFile");
        if (name.Length == 0 || name != Path.GetFileName(name)) throw new InvalidDataException("CMake 返回了无效的 File API 引用文件。");
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, name)));
    }
    private static (string Reply, JsonElement Model) Load(string root, string buildDirectory)
    {
        var reply = Path.Combine(buildDirectory, ".cmake", "api", "v1", "reply");
        var indexPath = Directory.Exists(reply) ? Directory.EnumerateFiles(reply, "index-*.json").OrderDescending(StringComparer.Ordinal).FirstOrDefault() : null;
        if (indexPath == null) throw new InvalidDataException($"{buildDirectory}: 未找到 codemodel-v2；先对所选预设/配置执行 CMake 配置。 ");
        using var index = JsonDocument.Parse(File.ReadAllText(indexPath));
        var modelReference = Array(index.RootElement, "objects").FirstOrDefault(x => Text(x, "kind") == "codemodel" && x.GetProperty("version").GetProperty("major").GetInt32() == 2);
        if (modelReference.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException($"{indexPath}: 缺少 codemodel-v2，请重新配置。 ");
        using var model = ReadReply(reply, modelReference); var data = model.RootElement;
        var paths = data.GetProperty("paths");
        if (!PathEquals(Text(paths, "source"), root) || !PathEquals(Text(paths, "build"), buildDirectory))
            throw new InvalidDataException($"{indexPath}: File API 工程/构建目录与当前选择不匹配，请重新配置。 ");
        return (reply, data.Clone());
    }
    private static bool PathEquals(string left, string right) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
    private static JsonElement SelectConfiguration(JsonElement model, string configuration)
    {
        var configurations = Array(model, "configurations");
        var selected = configurations.FirstOrDefault(x => Text(x, "name").Equals(configuration, StringComparison.OrdinalIgnoreCase));
        if (selected.ValueKind == JsonValueKind.Undefined && configurations.Length == 1 && Text(configurations[0], "name").Length == 0) selected = configurations[0];
        if (selected.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException($"File API 中没有所选配置 {configuration}；请配置该配置。 ");
        return selected;
    }
    public static IReadOnlyList<string> Targets(string root, string buildDirectory, string configuration)
    {
        var (reply, model) = Load(root, buildDirectory); var selected = SelectConfiguration(model, configuration);
        var names = new List<string>();
        foreach (var reference in Array(selected, "targets"))
        {
            using var data = ReadReply(reply, reference); if (Text(data.RootElement, "type") == "EXECUTABLE") names.Add(Text(data.RootElement, "name"));
        }
        return names;
    }

    internal static IReadOnlyList<string> Tokens(string command) => Regex.Matches(command, "\"(?<q>[^\"]*)\"|'(?<s>[^']*)'|(?<u>[^\\s\"']+)")
        .Select(x => x.Groups["q"].Success ? x.Groups["q"].Value : x.Groups["s"].Success ? x.Groups["s"].Value : x.Groups["u"].Value).ToArray();
    public static ConversionProject Read(string root, string chip, string buildDirectory, string configuration, string targetName)
    {
        var result = new ConversionProject { Root = root, Chip = chip, Name = targetName, OutputName = targetName };
        var (reply, model) = Load(root, buildDirectory); var selected = SelectConfiguration(model, configuration);
        var references = Array(selected, "targets").ToDictionary(x => Text(x, "id"), StringComparer.Ordinal);
        var matches = references.Values.Where(x => Text(x, "name") == targetName).ToArray();
        if (matches.Length != 1) { result.Issues.Add(ConversionPaths.Block(buildDirectory, $"找不到唯一目标 {targetName}。", "选择当前配置中明确的可执行目标。")); return result; }
        using var primaryDoc = ReadReply(reply, matches[0]); var primary = primaryDoc.RootElement;
        if (Text(primary, "type") != "EXECUTABLE") { result.Issues.Add(ConversionPaths.Block(targetName, "所选目标不是可执行固件。", "选择 EXECUTABLE 目标。")); return result; }
        var artifact = Array(primary, "artifacts").Select(x => Text(x, "path")).FirstOrDefault();
        if (artifact != null) result.OutputName = Path.GetFileNameWithoutExtension(artifact);
        var seen = new HashSet<string>(StringComparer.Ordinal); var cpus = new HashSet<string>(StringComparer.Ordinal); var fpus = new HashSet<string>(StringComparer.Ordinal); var abis = new HashSet<string>(StringComparer.Ordinal);
        void readTarget(JsonElement target)
        {
            if (!seen.Add(Text(target, "id"))) return;
            var objectDependencies = new List<JsonElement>();
            foreach (var dependency in Array(target, "dependencies"))
            {
                if (!references.TryGetValue(Text(dependency, "id"), out var reference)) continue;
                using var dependencyDoc = ReadReply(reply, reference); var data = dependencyDoc.RootElement;
                if (Text(data, "type") == "OBJECT_LIBRARY") { objectDependencies.Add(data.Clone()); readTarget(data); }
                else result.Issues.Add(ConversionPaths.Block(Text(data, "name"), $"目标依赖 {Text(data, "type")}；直接展开会改变静态库选择或自定义构建语义。", "为依赖保留独立的 Arm Compiler 6 库工程/构建步骤，再手工链接；自动转换仅展开对象库源码。"));
            }
            var groups = Array(target, "compileGroups");
            foreach (var group in groups)
            {
                var flags = Array(group, "compileCommandFragments").SelectMany(x => Tokens(Text(x, "fragment"))).ToArray();
                for (var flagIndex = 0; flagIndex < flags.Length; flagIndex++)
                {
                    var flag = flags[flagIndex];
                    if (flag == "-D" && flagIndex + 1 < flags.Length) { flagIndex++; continue; }
                    if (flag.StartsWith("-D", StringComparison.Ordinal) && flag.Length > 2) continue;
                    if (flag == "-x" && flagIndex + 1 < flags.Length && flags[flagIndex + 1] == "assembler-with-cpp" && Text(group, "language") == "ASM") { flagIndex++; continue; }
                    if (flag is "-MMD" or "-MP" or "-fstack-usage")
                    {
                        var message = $"{Text(target, "name")}: {flag} 为 GCC 依赖/栈使用报告选项，生成的 MDK 使用自己的构建报告。";
                        if (!result.Issues.Any(x => x.Message == message)) result.Issues.Add(new(message, "如需 .d/.su 文件，请在 MDK 单独启用等效报告。", false));
                        continue;
                    }
                    if (flag.StartsWith("-mcpu=", StringComparison.Ordinal)) cpus.Add(flag[6..]);
                    else if (flag.StartsWith("-mfpu=", StringComparison.Ordinal)) fpus.Add(flag[6..]);
                    else if (flag.StartsWith("-mfloat-abi=", StringComparison.Ordinal)) abis.Add(flag[12..]);
                    else if (!AcceptedFlag(flag)) result.Issues.Add(ConversionPaths.Block(Text(target, "name"), $"{Text(group, "language")} 编译参数 {flag} 无法安全映射到 Arm Compiler 6。", "移除或明确迁移该参数到 MDK 相应的组/文件设置。"));
                }
            }
            foreach (var source in Array(target, "sources"))
            {
                if (!source.TryGetProperty("compileGroupIndex", out var groupIndex))
                {
                    var uncompiled = Text(source, "path");
                    if (Path.GetExtension(uncompiled).Equals(".o", StringComparison.OrdinalIgnoreCase) && objectDependencies.Count > 0) continue;
                    if (Path.GetExtension(uncompiled) is ".c" or ".cpp" or ".s" or ".S" or ".o" or ".a")
                        result.Issues.Add(ConversionPaths.Block(uncompiled, "目标包含无编译组的对象/自定义源码。", "保留其生成或对象库依赖语义，再手工建立 MDK 构建步骤。"));
                    continue;
                }
                var group = groups[groupIndex.GetInt32()]; var language = Text(group, "language");
                var generated = source.TryGetProperty("isGenerated", out var isGenerated) && isGenerated.GetBoolean();
                var path = ConversionPaths.Resolve(generated ? buildDirectory : root, Text(source, "path"));
                if (generated || !File.Exists(path)) { result.Issues.Add(ConversionPaths.Block(path, "源码由构建生成或当前不存在。", "先将生成过程迁移为可重现的 MDK BeforeMake 步骤；自动转换不把临时产物当作普通源码。")); continue; }
                if (language is not "C" and not "CXX" and not "ASM") { result.Issues.Add(ConversionPaths.Block(path, $"不支持语言 {language}。", "使用 C/C++/标准 startup ASM 源码。")); continue; }
                var includes = Array(group, "includes").Select(x => ConversionPaths.Resolve(root, Text(x, "path"))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var flagsForDefines = Array(group, "compileCommandFragments").SelectMany(x => Tokens(Text(x, "fragment"))).ToArray();
                var fragmentDefines = new List<string>();
                for (var flagIndex = 0; flagIndex < flagsForDefines.Length; flagIndex++)
                {
                    var flag = flagsForDefines[flagIndex];
                    if (flag == "-D" && flagIndex + 1 < flagsForDefines.Length) fragmentDefines.Add(flagsForDefines[++flagIndex]);
                    else if (flag.StartsWith("-D", StringComparison.Ordinal) && flag.Length > 2) fragmentDefines.Add(flag[2..]);
                }
                var defines = Array(group, "defines").Select(x => Text(x, "define")).Concat(fragmentDefines).Distinct(StringComparer.Ordinal).ToArray();
                foreach (var include in includes) ConversionCompatibility.CheckInclude(include, result.Issues);
                var semanticFlags = Array(group, "compileCommandFragments").SelectMany(x => Tokens(Text(x, "fragment"))).Where(x =>
                    x.StartsWith("-std=", StringComparison.Ordinal) || Regex.IsMatch(x, @"^-(?:O[0123sg]|f(?:short-enums|short-wchar|signed-char|unsigned-char|rtti|no-rtti|no-exceptions|no-threadsafe-statics))$")).ToArray();
                var duplicate = result.Sources.FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (duplicate != null)
                {
                    var different = duplicate.Language != language || !duplicate.CompilerFlags.SequenceEqual(semanticFlags) || !duplicate.Includes.SequenceEqual(includes) || !duplicate.Defines.SequenceEqual(defines);
                    result.Issues.Add(ConversionPaths.Block(path, different ? "同一路径的独立对象编译具有不同语言/宏/包含目录/语义参数，不能合并。" : "同一路径产生多个独立对象；合并可能改变 weak 符号选择或构造函数调用次数。", "保留独立对象库构建及对象链接；自动转换不去重独立编译对象。"));
                }
                else result.Sources.Add(new(path, Text(target, "name") + " / " + language, language, includes, defines) { CompilerFlags = semanticFlags });
                result.Includes.AddRange(includes); result.Defines.AddRange(defines);
            }
        }
        readTarget(primary);
        if (cpus.Count != 1 || fpus.Count > 1 || abis.Count > 1)
            result.Issues.Add(ConversionPaths.Block(targetName, "缺少一致的 -mcpu，或编译组核心/FPU/浮点 ABI 不一致。", "对所选固件和对象库统一 CPU/FPU/ABI 并重新配置。"));
        result.Cpu = cpus.FirstOrDefault() ?? ""; result.Fpu = fpus.FirstOrDefault(); result.FloatAbi = abis.FirstOrDefault() ?? "soft";
        if (!new[] { "cortex-m0", "cortex-m0plus", "cortex-m3", "cortex-m4", "cortex-m7", "cortex-m33" }.Contains(result.Cpu, StringComparer.Ordinal))
            result.Issues.Add(ConversionPaths.Block(targetName, $"不支持 CPU {result.Cpu}。", "选择已校核的单核 Cortex-M 配置。"));
        if (result.Fpu != null && result.Fpu is not "fpv4-sp-d16" and not "fpv5-sp-d16" and not "fpv5-d16" || result.FloatAbi is not "soft" and not "hard")
            result.Issues.Add(ConversionPaths.Block(targetName, $"浮点配置 {result.Fpu}/{result.FloatAbi} 不支持可靠转换。", "采用明确的 soft 无 FPU 或 hard 单/双精度设置；softfp 需要手工 ABI 校核。"));
        ConversionPaths.Distinct(result.Includes); ConversionPaths.Distinct(result.Defines);
        var startups = result.Sources.Where(x => x.Language == "ASM" && Path.GetFileName(x.Path).StartsWith("startup_", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (startups.Length == 1) result.Startup = startups[0].Path;
        else result.Issues.Add(ConversionPaths.Block(targetName, $"所选源中有 {startups.Length} 个 startup。", "选择仅含一个标准 CubeMX startup 的单核固件目标。"));
        foreach (var other in result.Sources.Where(x => x.Language == "ASM" && x.Path != result.Startup))
            result.Issues.Add(ConversionPaths.Block(other.Path, "额外 GCC 汇编源码不能自动变为 ARMASM。", "提供对应 Arm Compiler 6 汇编源码或手工配置集成汇编器。"));
        var link = primary.GetProperty("link"); var linkTokens = Array(link, "commandFragments").SelectMany(x => Tokens(Text(x, "fragment"))).ToArray();
        var scripts = new List<string>();
        for (var i = 0; i < linkTokens.Length; i++)
        {
            var token = linkTokens[i];
            if (token == "-D" && i + 1 < linkTokens.Length) { i++; continue; }
            if (token.StartsWith("-D", StringComparison.Ordinal) && token.Length > 2) continue;
            if (token == "-T" && i + 1 < linkTokens.Length) scripts.Add(ConversionPaths.Resolve(buildDirectory, linkTokens[++i]));
            else if (token.StartsWith("-T", StringComparison.Ordinal) && token.Length > 2) scripts.Add(ConversionPaths.Resolve(buildDirectory, token[2..]));
            else if (token.StartsWith("-Wl,-T,", StringComparison.Ordinal)) scripts.Add(ConversionPaths.Resolve(buildDirectory, token[7..]));
            else if (Path.GetExtension(token).Equals(".a", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(token).Equals(".lib", StringComparison.OrdinalIgnoreCase))
            {
                var library = ConversionLibraries.CmsisDsp(ConversionPaths.Resolve(buildDirectory, token), result, false);
                if (library != null) result.Libraries.Add(library);
            }
            else if (!AcceptedLinkFlag(token)) result.Issues.Add(ConversionPaths.Block(targetName, $"链接参数/库 {token} 不能直接用于 Arm Compiler 6。", "保留库构建/链接顺序并验证 ABI，或明确迁移该链接选项。"));
        }
        scripts = scripts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (scripts.Count != 1 || !File.Exists(scripts[0])) result.Issues.Add(ConversionPaths.Block(targetName, "File API 中没有唯一可读的 -T 链接脚本。", "给所选固件显式指定现有 GCC .ld 并重新配置。"));
        else { result.Linker = scripts[0]; result.Memory = ConversionLinker.ReadGcc(scripts[0], result.Issues); }
        return result;
    }
    private static bool AcceptedFlag(string flag) => flag is "-mthumb" or "-ffunction-sections" or "-fdata-sections" or "-fno-common" or "-MMD" or "-MP" or "-fstack-usage" || Regex.IsMatch(flag, @"^-(?:g[0-3]?|gdwarf-[2345]|O[0123sg]|f(?:short-enums|short-wchar|signed-char|unsigned-char|rtti|no-rtti|no-exceptions|no-threadsafe-statics)|std=(?:c99|gnu99|c11|gnu11|c17|gnu17|c\+\+(?:11|14|17)|gnu\+\+(?:11|14|17))|W(?:all|extra|pedantic|no-unused-parameter))$");
    private static bool AcceptedLinkFlag(string flag) => AcceptedFlag(flag) || flag.StartsWith("-mcpu=", StringComparison.Ordinal) || flag.StartsWith("-mfpu=", StringComparison.Ordinal) || flag.StartsWith("-mfloat-abi=", StringComparison.Ordinal) ||
        flag is "" or "-nostartfiles" or "--specs=nano.specs" or "--specs=nosys.specs" or "-specs=nano.specs" or "-specs=nosys.specs" or "-Wl,--gc-sections" or "-Wl,--print-memory-usage" or "-Wl,--start-group" or "-Wl,--end-group" or "-lm" or "-lc" or "-lstdc++" or "-lsupc++" || flag.StartsWith("-Wl,-Map=", StringComparison.Ordinal);
}
