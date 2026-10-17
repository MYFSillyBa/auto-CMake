using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CMakeDapLink.Core;

namespace CMakeDapLink.ExtensionHost;

internal sealed partial class RequestHandler
{
    private async Task<object> AnalyzeFirmwareAsync(JsonElement payload, CancellationToken cancellation)
    {
        var top = 30;
        if (payload.TryGetProperty("top", out var requested) && (requested.ValueKind != JsonValueKind.Number || !requested.TryGetInt32(out top)))
            throw new ArgumentException("排行数量必须为 1–100。");
        if (top is < 1 or > 100) throw new ArgumentException("排行数量必须为 1–100。");
        cancellation.ThrowIfCancellationRequested();
        FirmwareAnalysisReport? mapReport = null;
        if (Text(payload, "mapFilePath") is { Length: > 0 } selectedMap)
        {
            var map = AdvancedArtifact(selectedMap, ".map");
            progress(40, "分析 GNU MAP 文件贡献", map);
            mapReport = await Task.Run(() => FirmwareAdvancedAnalyzer.AnalyzeMap(map, top, cancellation), cancellation);
            if (string.IsNullOrWhiteSpace(Text(payload, "firmwareElfPath"))) return mapReport;
        }
        var elf = AdvancedElf(payload, out var build);
        progress(40, "分析 ELF 函数和变量", elf);
        var notes = new List<string>(); IReadOnlyList<string> scripts = [];
        if (MemoryWithin(build, elf) && Directory.Exists(build))
        {
            ValidateMemoryCache(build);
            var fragments = MemoryLinkFragments(build, elf, Configuration(payload), notes, out var workingDirectory);
            scripts = MemoryScriptPaths(build, workingDirectory, fragments, notes);
        }
        else notes.Add("所选 ELF 不属于当前构建目录；未推测链接脚本和内存区域容量。");
        if (mapReport != null) notes.Add("同时选择 ELF 与 MAP 时，符号和区域用量来自 ELF，目标文件贡献来自 MAP；请确认两个文件来自同次构建。");
        return await Task.Run<object>(() =>
        {
            var ranking = FirmwareAdvancedAnalyzer.AnalyzeElf(elf, top, cancellation);
            cancellation.ThrowIfCancellationRequested();
            var memory = FirmwareMemoryAnalyzer.Analyze(elf, scripts);
            cancellation.ThrowIfCancellationRequested();
            return ranking with
            {
                MapFilePath = mapReport?.MapFilePath,
                Regions = memory.Regions,
                MapSections = mapReport?.MapSections ?? [],
                Notes = notes.Concat(ranking.Notes).Concat(memory.Notes).Concat(mapReport?.Notes ?? []).Distinct().ToArray()
            };
        }, cancellation);
    }

    private async Task<object> HardFaultAsync(JsonElement payload, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!payload.TryGetProperty("registers", out var supplied) || supplied.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("请提供 HardFault 寄存器对象。");
        var registers = new Dictionary<string, uint>(StringComparer.Ordinal);
        var registerCount = 0;
        foreach (var item in supplied.EnumerateObject())
        {
            if (++registerCount > 40 || item.Name.Length > 32) throw new ArgumentException("HardFault 寄存器数量或名称长度超过限制。");
            var name = item.Name.ToUpperInvariant();
            if (name.StartsWith("SCB_", StringComparison.Ordinal)) name = name[4..];
            if (!Regex.IsMatch(name, @"\A(?:CFSR|HFSR|MMFAR|BFAR|DFSR|AFSR|SHCSR|CCR|XPSR|CONTROL|EXC_RETURN|MSP|PSP|PC|LR|SP|R(?:[0-9]|1[0-5]))\z", RegexOptions.CultureInvariant))
                throw new ArgumentException("不支持的寄存器名称：" + item.Name);
            name = name switch { "R13" => "SP", "R14" => "LR", "R15" => "PC", _ => name };
            var number = AdvancedRegister(item.Value);
            if (registers.TryGetValue(name, out var existing) && existing != number) throw new ArgumentException("同一寄存器提供了不一致的值：" + name);
            registers[name] = number;
        }
        if (registers.Count == 0) throw new ArgumentException("请至少提供一个实际捕获的 HardFault 寄存器。");
        var core = Text(payload, "core") ?? Text(payload, "cpu");
        string? assumedChip = null;
        if (string.IsNullOrWhiteSpace(core))
        {
            var chip = Project(payload).Chip?.ToUpperInvariant();
            foreach (var (family, candidateCore) in new[]
            {
                ("F0", "Cortex-M0"), ("C0", "Cortex-M0+"), ("G0", "Cortex-M0+"), ("L0", "Cortex-M0+"), ("U0", "Cortex-M0+"),
                ("F1", "Cortex-M3"), ("F2", "Cortex-M3"), ("L1", "Cortex-M3"),
                ("F3", "Cortex-M4"), ("F4", "Cortex-M4"), ("G4", "Cortex-M4"), ("L4", "Cortex-M4"),
                ("F7", "Cortex-M7"), ("H7", "Cortex-M7"), ("L5", "Cortex-M33"), ("U5", "Cortex-M33"), ("H5", "Cortex-M33"), ("WBA", "Cortex-M33"), ("N6", "Cortex-M55")
            })
                if (chip?.StartsWith("STM32" + family, StringComparison.Ordinal) == true) { core = candidateCore; assumedChip = chip; break; }
        }
        var report = HardFaultDecoder.Decode(registers, core);
        var notes = report.Notes.ToList(); var locations = new List<HardFaultLocation>();
        if (assumedChip != null) notes.Add("根据当前工程芯片 " + assumedChip + " 推断 " + core + "；请确认寄存器来自同一内核，多核芯片还需核对实际故障内核。");
        var candidates = new[] { "PC", "LR" }.Where(registers.ContainsKey)
            .Select(name => (Register: name, Address: registers[name])).Where(item => item.Address < 0xffffff80 && item.Address != 0).ToArray();
        if (candidates.Length == 0) return report with { Notes = notes };
        string? elf = null;
        if (Text(payload, "firmwareElfPath") is { Length: > 0 } selectedElf) elf = AdvancedArtifact(selectedElf, ".elf");
        else
        {
            var project = Project(payload);
            var build = WorkspaceSafety.Child(root, Text(payload, "buildDirectory") ?? project.BuildDirectory ?? Path.Combine(root, "build", "daplink-debug"));
            if (Directory.Exists(build))
            {
                var available = CMakeBuildPlan.FindElfs(build, Configuration(payload));
                if (available.Count == 1) elf = AdvancedArtifact(available[0], ".elf");
            }
        }
        if (elf == null)
        {
            notes.Add("未选择唯一的 ELF；已完成寄存器解码，PC/LR 源码位置未知。");
            return report with { Notes = notes };
        }
        // Reject unsupported or malformed images before passing their paths to an external reader.
        await Task.Run(() => FirmwareAdvancedAnalyzer.AnalyzeElf(elf, 1, cancellation), cancellation);
        ToolPaths tools;
        if (payload.TryGetProperty("tools", out var explicitTools) && explicitTools.ValueKind == JsonValueKind.Object)
        {
            tools = explicitTools.Deserialize<ToolPaths>(json) ?? throw new ArgumentException("工具选择数据为空。");
            WorkspaceSafety.Tools(tools);
        }
        else tools = await FindToolsAsync(payload, cancellation);
        var compiler = tools.Compiler;
        if (!File.Exists(compiler))
        {
            notes.Add("未选择有效的 Arm GCC；已完成寄存器解码，源码位置需要同一工具链的 arm-none-eabi-addr2line.exe。");
            return report with { Notes = notes };
        }
        var addr2line = Path.Combine(Path.GetDirectoryName(compiler)!, "arm-none-eabi-addr2line.exe");
        WorkspaceSafety.Ordinary(addr2line);
        if (!File.Exists(addr2line))
        {
            notes.Add("所选 GCC 目录缺少 arm-none-eabi-addr2line.exe；PC/LR 源码位置未知。");
            return report with { Notes = notes };
        }
        foreach (var candidate in candidates)
        {
            cancellation.ThrowIfCancellationRequested();
            var address = candidate.Address & ~1u; // ELF instruction addresses do not carry the Thumb pointer bit.
            progress(60, "映射 HardFault 源码位置", candidate.Register + " = 0x" + address.ToString("X8"));
            try
            {
                var result = await HostProcess.RunAsync(new(addr2line, ["-f", "-C", "-e", elf, "0x" + address.ToString("X8")], root, ToolPrefix(tools)),
                    TimeSpan.FromSeconds(10), cancellation);
                if (result.ExitCode != 0) { notes.Add("addr2line 未能解析所选 ELF；请确认工具链与固件匹配并保留调试信息。"); continue; }
                var lines = result.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 2) { notes.Add("addr2line 输出无法识别；没有生成推测的源码位置。"); continue; }
                var function = lines[0] == "??" ? null : lines[0];
                var source = Regex.Match(lines[1], @"\A(?<file>.+):(?<line>\d+)(?:\s+\(discriminator\s+\d+\))?\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                string? file = null; int? line = null;
                if (source.Success && source.Groups["file"].Value is not ("??" or "?"))
                {
                    file = source.Groups["file"].Value;
                    if (int.TryParse(source.Groups["line"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedLine) && parsedLine > 0) line = parsedLine;
                }
                if (function == null && file == null) { notes.Add("部分 PC/LR 地址没有函数或源码信息；请核对 ELF 版本以及编译调试选项。"); continue; }
                locations.Add(new(candidate.Register, "0x" + address.ToString("X8"), function, file, line));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is IOException or TimeoutException or System.ComponentModel.Win32Exception)
            { notes.Add("源码地址映射工具运行失败；寄存器解码结果仍然有效，请检查所选 GCC 工具链。"); }
        }
        return report with { Locations = locations, Notes = notes.Distinct().ToArray() };
    }

    private string AdvancedElf(JsonElement payload, out string build)
    {
        var project = Project(payload);
        build = WorkspaceSafety.Child(root, Text(payload, "buildDirectory") ?? project.BuildDirectory ?? Path.Combine(root, "build", "daplink-debug"));
        var selected = Text(payload, "firmwareElfPath");
        if (string.IsNullOrWhiteSpace(selected))
        {
            var available = Directory.Exists(build) ? CMakeBuildPlan.FindElfs(build, Configuration(payload)) : [];
            if (available.Count != 1) throw new ArgumentException("请选择一个实际编译生成的 ELF，再分析函数和变量。");
            selected = available[0];
        }
        return AdvancedArtifact(selected, ".elf");
    }

    private string AdvancedArtifact(string selected, string extension)
    {
        var path = WorkspaceSafety.Child(root, selected);
        if (!File.Exists(path) || !Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择当前工程内现有的 " + extension + " 文件。");
        if (new FileInfo(path).Length > (extension == ".elf" ? 256L : 32L) * 1024 * 1024)
            throw new InvalidDataException("固件分析文件大小超过读取范围。");
        return path;
    }

    private static bool MemoryWithin(string directory, string path) => path.StartsWith(directory.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static uint AdvancedRegister(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= 64 } text)
        {
            text = text.Trim();
            var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (uint.TryParse(hex ? text.AsSpan(2) : text.AsSpan(), hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out number)) return number;
        }
        throw new ArgumentException("寄存器值必须为 32 位无符号十进制数或 0x 开头的十六进制数。");
    }
}
