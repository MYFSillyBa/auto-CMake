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
