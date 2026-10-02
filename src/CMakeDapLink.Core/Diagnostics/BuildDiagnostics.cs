using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

public sealed record BuildProblem(string Stage, string Severity, string Message, string? FilePath,
    int? Line, int? Column, string Suggestion);

public static class BuildDiagnostics
{
    private static readonly Regex Ansi = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex Compiler = new(@"^(?<file>.+?):(?<line>\d+)(?::(?<column>\d+))?:\s*(?<severity>fatal error|error|warning):\s*(?<message>.+)$", RegexOptions.Compiled);
    private static readonly Regex CMake = new(@"^CMake (?<severity>Error|Warning)(?: \([^)]*\))? at (?<file>.+?):(?<line>\d+)(?: \([^)]*\))?:?", RegexOptions.Compiled);
    private static readonly Regex LinkLocation = new(@"^(?<file>.+?):(?<line>\d+):\s*(?<message>.*(?:undefined reference|multiple definition).*)$", RegexOptions.Compiled);
    private static readonly Regex LinkDiagnostic = new(@"^.+[/\\](?:ld(?:\.exe)?|ld\.lld|lld(?:\.exe)?):\s*(?<severity>warning|error):\s*(?<message>.+)$", RegexOptions.Compiled);

    public static IReadOnlyList<BuildProblem> Parse(string output, string root, string stage)
    {
        var lines = Ansi.Replace(output, "").Replace("\r\n", "\n").Split('\n');
        var result = new List<BuildProblem>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            var match = Compiler.Match(line);
            if (match.Success)
            {
                Add(match.Groups["message"].Value, match.Groups["severity"].Value.Contains("error") ? "错误" : "警告",
                    match.Groups["file"].Value, Number(match, "line"), Number(match, "column"), stage);
                continue;
            }
            match = CMake.Match(line);
            if (match.Success)
            {
                var details = new List<string>();
                for (var j = i + 1; j < lines.Length && j <= i + 8; j++)
                {
                    if (string.IsNullOrWhiteSpace(lines[j])) { if (details.Count > 0) break; continue; }
                    if (!char.IsWhiteSpace(lines[j][0])) break;
                    details.Add(lines[j].Trim());
                }
                Add(details.Count == 0 ? line : string.Join(" ", details), match.Groups["severity"].Value == "Error" ? "错误" : "警告",
                    match.Groups["file"].Value, Number(match, "line"), null, "CMake 配置");
                continue;
            }
            match = LinkLocation.Match(line);
            if (match.Success)
            {
                Add(match.Groups["message"].Value, "错误", match.Groups["file"].Value, Number(match, "line"), null, "链接");
                continue;
            }
            match = LinkDiagnostic.Match(line);
            if (match.Success)
            {
                Add(match.Groups["message"].Value, match.Groups["severity"].Value == "warning" ? "警告" : "错误", null, null, null, "链接");
                continue;
            }
            if (line.Contains("undefined reference", StringComparison.OrdinalIgnoreCase) || line.Contains("multiple definition", StringComparison.OrdinalIgnoreCase)
                || line.Contains("overflowed", StringComparison.OrdinalIgnoreCase) || line.Contains("will not fit", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("collect2:", StringComparison.OrdinalIgnoreCase))
                Add(line, "错误", null, null, null, "链接");
            else if (line.StartsWith("CMake Error", StringComparison.Ordinal))
                Add(line, "错误", null, null, null, "CMake 配置");
            else if (line.Contains("error:", StringComparison.OrdinalIgnoreCase) && !line.StartsWith("collect2:"))
                Add(line, "错误", null, null, null, stage);
        }
        return result.Distinct().OrderBy(x => x.Severity == "错误" ? 0 : 1).ToArray();

        void Add(string message, string severity, string? path, int? line, int? column, string problemStage)
        {
            string? fullPath = null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                try { fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path)); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
            }
            result.Add(new(problemStage, severity, message, fullPath, line, column, Advice(message)));
        }
    }

    public static string Advice(string message)
    {
        if (message.Contains("conflicting types", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(message, @"\b(?:u?int(?:8|16|32|64)_t)\b"))
            return "工程重复定义了标准整数类型。GCC 与 Keil 对 int/long 的 typedef 选择可能不同，即使均为 32 位也会冲突。将该头文件中的 int8_t～int64_t、uint8_t～uint64_t 自定义 typedef 改为 #include <stdint.h>，保留 bool_t、fp32 等自定义别名；检查所有引用是否使用同一标准声明。";
        if (message.Contains("conflicting types", StringComparison.OrdinalIgnoreCase))
            return "同一函数或变量的声明与实现类型不同。核对报错位置和日志中的 previous declaration；若从 MDK 迁移且自定义了 uint32_t/int32_t，先统一使用 <stdint.h>，再核对 HAL 回调与 CMSIS 声明。";
        if (message.Contains("uses VFP register arguments", StringComparison.OrdinalIgnoreCase) || message.Contains("VFP register argument", StringComparison.OrdinalIgnoreCase))
            return "参与链接的对象/库浮点 ABI 不一致。统一 -mfloat-abi（hard/soft/softfp）和 -mfpu，选择对应 CPU/FPU 的 GCC 库，并重新编译所有参与链接的对象；改文件名不能修正 ABI。";
        if (message.Contains("wchar_t", StringComparison.OrdinalIgnoreCase) || message.Contains("variable-size enums", StringComparison.OrdinalIgnoreCase) || message.Contains("small enums", StringComparison.OrdinalIgnoreCase))
            return "对象与预编译库的 wchar_t/枚举 ABI 不同。转换保留了原 MDK 的短 wchar_t/短枚举设置，请核对 -fshort-wchar/-fshort-enums 与库配置；跨对象传递这些类型时不能忽略。使用统一 ABI 重编库，或在确认接口不依赖旧布局后调整工程参数。";
        if (message.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase) || message.Contains("Cannot find source file", StringComparison.OrdinalIgnoreCase))
            return "可能原因：文件不存在、名称大小写不一致或搜索目录未加入。检查文件路径；头文件需要将所在目录加入 target_include_directories。";
        if (message.Contains("undefined reference", StringComparison.OrdinalIgnoreCase))
            return "可能原因：实现该符号的源文件或库未参与链接，也可能是 C/C++ 链接声明不一致。检查 target_sources、target_link_libraries 及 extern \"C\"。";
        if (message.Contains("multiple definition", StringComparison.OrdinalIgnoreCase) || message.Contains("redefinition", StringComparison.OrdinalIgnoreCase))
            return "可能原因：重复加入实现文件，或在头文件中定义了全局变量/函数。检查重复源码和定义位置；确认是否同时加入多个 main。";
        if (message.Contains("overflowed", StringComparison.OrdinalIgnoreCase) || message.Contains("will not fit", StringComparison.OrdinalIgnoreCase))
            return "可能原因：代码或数据超出链接脚本定义的存储区域。核对芯片型号、链接脚本的 FLASH/RAM 容量及实际使用情况。";
        if (message.Contains("not declared", StringComparison.OrdinalIgnoreCase) || message.Contains("implicit declaration", StringComparison.OrdinalIgnoreCase))
            return "检查对应声明是否被包含、声明是否受条件编译宏限制，以及函数名是否一致。";
        if (message.Contains("toolchain", StringComparison.OrdinalIgnoreCase) || message.Contains("compiler", StringComparison.OrdinalIgnoreCase))
            return "核对所选预设和 toolchain 文件，检查编译器路径及工具版本；配置有变化时使用独立构建目录。";
        return "先处理列表中的首个错误，核对该位置附近的代码和构建配置；展开完整日志查看上下文。";
    }

    private static int? Number(Match match, string name) => int.TryParse(match.Groups[name].Value, out var number) ? number : null;
}
