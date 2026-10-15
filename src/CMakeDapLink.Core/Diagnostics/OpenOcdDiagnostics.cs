using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

/// <summary>Explains reported OpenOCD failures without probing a device or claiming a repair.</summary>
public static class OpenOcdDiagnostics
{
    private static readonly Regex Ansi = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex Location = new(@"^(?<file>.+?\.(?:cfg|tcl)):(?<line>\d+):", RegexOptions.Compiled);

    public static IReadOnlyList<BuildProblem> Parse(string output, string root)
    {
        var result = new List<BuildProblem>();
        foreach (var raw in Ansi.Replace(output, "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || !(Regex.IsMatch(line, @"(?i)\b(?:error|fatal)\b") ||
                line.Contains("Can't find", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("unable to", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("unexpected idcode", StringComparison.OrdinalIgnoreCase))) continue;
            var location = Location.Match(line);
            string? path = null;
            if (location.Success)
                try { path = Path.GetFullPath(location.Groups["file"].Value, root); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
            result.Add(new("OpenOCD", "错误", raw, path,
                location.Success && int.TryParse(location.Groups["line"].Value, out var number) ? number : null,
                null, Advice(line)));
        }
        return result.Distinct().ToArray();
    }

    private static string Advice(string message)
    {
        if (Has("can't find", "couldn't read file", "no such file", "invalid command name", "syntax error"))
            return "可能是脚本路径、依赖或 OpenOCD 版本不匹配。核对 -s 脚本目录、interface/target 脚本和原始报错位置；这条信息不能证明硬件连接状态。";
        if (Has("no device found", "unable to open", "libusb", "cmsis-dap", "j-link", "jlink", "st-link", "stlink", "adapter", "usb"))
            return "可能是探针未连接、被其他程序占用、驱动不匹配或探针类型选择错误。核对所选探针、USB 连接和驱动，并关闭占用探针的调试会话。";
        if (Has("unexpected idcode", "expected id", "device id", "idcode"))
            return "目标标识与脚本预期可能不一致。根据实际芯片完整型号核对 target 脚本，并检查 SWD/JTAG 接线和目标供电；不要仅凭这条信息修改预期标识。";
        if (Has("protected", "protection", "locked"))
            return "目标可能启用了读写保护。先查阅芯片保护状态及解锁步骤，确认数据保留要求；部分解锁操作会擦除芯片，不能自动执行。";
        if (Has("verify", "verification", "checksum", "writ", "flash", "eras", "image"))
            return "可能是 Flash 驱动、目标容量、镜像地址或保护状态不匹配。核对实际固件、链接地址、芯片型号及完整日志，再决定是否重新烧录。";
        if (Has("reset", "halt", "timeout", "timed out", "target not examined", "init mode", "swd", "jtag", "voltage"))
            return "可能是目标供电、调试接线、接口速度或复位方式不合适。核对供电与地线、SWD/JTAG 引脚，尝试较低接口速度，并按硬件条件选择复位方式。";
        return "OpenOCD 报告了异常。先核对原始日志中的首个错误、探针类型和芯片脚本；软件日志不足以确认具体硬件故障。";

        bool Has(params string[] values) => values.Any(value => message.Contains(value, StringComparison.OrdinalIgnoreCase));
    }
}
