using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

internal static class ConversionCompatibility
{
    public static void CheckInclude(string path, List<ConversionIssue> issues)
    {
        if (Directory.Exists(path)) return;
        var message = $"{Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))}: 包含目录不存在，保留原路径。";
        if (!issues.Any(x => x.Message.Equals(message, StringComparison.OrdinalIgnoreCase)))
            issues.Add(new(message, "可能是未使用模块的遗留路径，不阻止生成。实际编译如报告缺少头文件，请补齐对应模块或修正包含路径；程序不会创建空目录或删除此配置。", false));
    }

    public static string? FreeRtosPort(string path, ConversionProject project, IDictionary<string, string> includeMappings)
    {
        if (!Regex.IsMatch(path, @"[/\\]portable[/\\]RVDS[/\\]", RegexOptions.IgnoreCase) || !Path.GetFileName(path).Equals("port.c", StringComparison.OrdinalIgnoreCase)) return path;
        var currentDirectory = Path.GetDirectoryName(path)!;
        if (GnuPort(path) && MatchingKernel(path))
        {
            project.Issues.Add(new($"{path}: 内容为 GNU FreeRTOS port，保留现有位置。", "目录名 RVDS 不决定编译器。生成后实际编译检查 GNU 语法、头文件及 CPU/FPU 参数；软件验证不确认调度运行。", false));
            return path;
        }
        var gcc = Regex.Replace(path, @"([/\\])RVDS([/\\])", "$1GCC$2", RegexOptions.IgnoreCase);
        if (File.Exists(gcc) && GnuPort(gcc) && MatchingKernel(gcc) && SameVersion(path, gcc))
        {
            includeMappings[currentDirectory] = Path.GetDirectoryName(gcc)!;
            project.Issues.Add(new($"{path}: 使用工程内同版本 GNU FreeRTOS port → {gcc}", "已同步对应包含路径；生成后实际编译检查核心/FPU 配置。", false));
            return gcc;
        }
        project.Issues.Add(ConversionPaths.Block(path, "未找到可确认语法及版本的 GNU FreeRTOS port。", $"提供与当前内核版本及核心/FPU 相同的 {gcc} 和 portmacro.h；不会仅按目录名替换或下载其他版本。"));
        return null;
    }

    public static string MapInclude(string path, IReadOnlyDictionary<string, string> mappings)
    {
        foreach (var pair in mappings)
            if (Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(pair.Key)), StringComparison.OrdinalIgnoreCase)) return pair.Value;
        return path;
    }

    private static string WithoutComments(string text) => Regex.Replace(text, @"/\*[\s\S]*?\*/|//[^\r\n]*", "");
    private static bool GnuPort(string path)
    {
        var macro = Path.Combine(Path.GetDirectoryName(path)!, "portmacro.h");
        if (!File.Exists(macro)) return false;
        var body = WithoutComments(File.ReadAllText(path)); var header = WithoutComments(File.ReadAllText(macro));
        return Regex.IsMatch(body, @"\b__attribute__\s*\(") && Regex.IsMatch(body, @"\b(?:__asm|__asm__)\s+(?:__volatile__|volatile)\s*\(") &&
            Regex.IsMatch(header, @"\b(?:__attribute__|__asm|__asm__)\b") &&
            !Regex.IsMatch(body + "\n" + header, @"\b(?:__asm\s*\{|__forceinline)\b|(?m)^\s*__asm\s+(?:void|BaseType_t)\b");
    }
    private static string Version(string path) => Regex.Match(File.ReadAllText(path), @"FreeRTOS(?:\s+Kernel)?\s+V?(\d+\.\d+\.\d+)", RegexOptions.IgnoreCase).Groups[1].Value;
    private static bool MatchingKernel(string path)
    {
        var portable = Directory.GetParent(Path.GetDirectoryName(path)!)?.Parent;
        if (portable?.Name != "portable") return false;
        var task = Path.Combine(portable.Parent!.FullName, "include", "task.h");
        if (!File.Exists(task)) return false;
        var kernel = Regex.Match(File.ReadAllText(task), "(?m)^\\s*#\\s*define\\s+tskKERNEL_VERSION_NUMBER\\s+\"V?(\\d+\\.\\d+\\.\\d+)\"").Groups[1].Value;
        var version = Version(path); return kernel.Length > 0 && version == kernel;
    }
    private static bool SameVersion(string left, string right) => Version(left).Length > 0 && Version(left) == Version(right);
}
