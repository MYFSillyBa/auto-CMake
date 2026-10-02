using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

internal static class ConversionLibraries
{
    public static string? CmsisDsp(string path, ConversionProject project, bool toGcc)
    {
        var name = Path.GetFileName(path);
        var match = Regex.Match(name, toGcc ? @"^arm_(cortexM(?:0l|3l|4l|4lf|7l|7lfsp|7lfdp))_math\.lib$" : @"^libarm_(cortexM(?:0l|3l|4l|4lf|7l|7lfsp|7lfdp))_math\.a$", RegexOptions.IgnoreCase);
        var parent = Path.GetDirectoryName(path)!;
        var expected = project.Cpu switch
        {
            "cortex-m0" or "cortex-m0plus" when project.Fpu == null && project.FloatAbi == "soft" => "cortexM0l",
            "cortex-m3" when project.Fpu == null && project.FloatAbi == "soft" => "cortexM3l",
            "cortex-m4" when project.Fpu == null && project.FloatAbi == "soft" => "cortexM4l",
            "cortex-m4" when project.Fpu == "fpv4-sp-d16" && project.FloatAbi == "hard" => "cortexM4lf",
            "cortex-m7" when project.Fpu == null && project.FloatAbi == "soft" => "cortexM7l",
            "cortex-m7" when project.Fpu == "fpv5-sp-d16" && project.FloatAbi == "hard" => "cortexM7lfsp",
            "cortex-m7" when project.Fpu == "fpv5-d16" && project.FloatAbi == "hard" => "cortexM7lfdp",
            _ => ""
        };
        if (!match.Success || !match.Groups[1].Value.Equals(expected, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(parent).Equals(toGcc ? "ARM" : "GCC", StringComparison.OrdinalIgnoreCase))
        {
            project.Issues.Add(ConversionPaths.Block(path, "二进制库不能自动匹配当前 CPU/FPU 的 CMSIS DSP 版本。", "提供对应编译器、核心、大小端及浮点 ABI 的库，并手工确认链接；不会把 ARM 专用库直接交给 GCC。")); return null;
        }
        var counterpart = Path.Combine(Path.GetDirectoryName(parent)!, toGcc ? "GCC" : "ARM", toGcc ? "libarm_" + match.Groups[1].Value + "_math.a" : "arm_" + match.Groups[1].Value + "_math.lib");
        if (!File.Exists(path) || !File.Exists(counterpart) || !ArmArchive(toGcc ? counterpart : path) || !ArmArchive(toGcc ? path : counterpart))
        {
            project.Issues.Add(ConversionPaths.Block(path, "缺少可读的对应 CMSIS DSP ARM ELF 库。", $"补齐 {counterpart}，使用同一 CMSIS DSP 版本、核心和浮点配置。")); return null;
        }
        project.Issues.Add(new($"CMSIS DSP: {path} → {counterpart}", "已按 CPU/FPU 匹配库名并检查 ARM ELF 容器，保留链接顺序。仍需实际编译验证 ABI 和符号；请确保两种库来自同一 CMSIS DSP 版本。", false));
        return counterpart;
    }

    // Ordinary ar archives only; do not accept thin archives, bitcode or non-ARM objects.
    private static bool ArmArchive(string path)
    {
        using var stream = File.OpenRead(path); var magic = new byte[8];
        if (stream.Read(magic) != 8 || Encoding.ASCII.GetString(magic) != "!<arch>\n") return false;
        var header = new byte[60]; var elf = new byte[52]; var count = 0;
        while (stream.Position < stream.Length)
        {
            if (stream.Length - stream.Position < header.Length) return false;
            stream.ReadExactly(header);
            if (header[58] != '`' || header[59] != '\n' || !long.TryParse(Encoding.ASCII.GetString(header, 48, 10).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size > stream.Length - stream.Position) return false;
            var next = stream.Position + size; var name = Encoding.ASCII.GetString(header, 0, 16).Trim();
            if (name is not "/" and not "//" and not "/SYM64/")
            {
                if (size < elf.Length || name.StartsWith("#1/", StringComparison.Ordinal)) return false;
                stream.ReadExactly(elf);
                if (elf[0] != 0x7f || elf[1] != 'E' || elf[2] != 'L' || elf[3] != 'F' || elf[4] != 1 || elf[5] != 1 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(16)) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(18)) != 40 ||
                    (BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan(36)) >> 24) != 5) return false;
                count++;
            }
            stream.Position = next + (size & 1);
            if (stream.Position > stream.Length) return false;
        }
        return count > 0;
    }
}
