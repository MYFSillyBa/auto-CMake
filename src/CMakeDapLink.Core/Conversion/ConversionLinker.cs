using System.Globalization;
using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

internal static class ConversionLinker
{
    internal static ulong Number(string text)
    {
        text = text.Trim();
        var multiplier = text.EndsWith('K') || text.EndsWith('k') ? 1024UL : text.EndsWith('M') || text.EndsWith('m') ? 1024UL * 1024 : 1;
        if (multiplier != 1) text = text[..^1];
        return checked((text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : ulong.Parse(text, CultureInfo.InvariantCulture)) * multiplier);
    }

    public static ConversionMemory? ReadScatter(string path, List<ConversionIssue> issues)
    {
        var text = Regex.Replace(File.ReadAllText(path), @";[^\r\n]*", "");
        var regions = Regex.Matches(text, @"\b(?<name>[A-Za-z_]\w*)\s+(?<address>0x[0-9a-fA-F]+|\d+)\s+(?<size>0x[0-9a-fA-F]+|\d+)\s*\{");
        if (regions.Count != 3 || Regex.Matches(text, @"\.ANY\s*\([^)]*\+RW", RegexOptions.IgnoreCase).Count != 1)
        {
            issues.Add(ConversionPaths.Block(path, "当前散布布局包含多个装载/读写区或特殊地址表达式；不能保留 .ANY 在多个 RAM 间的分配语义。",
                "为所选目标提供单个 FLASH 和单个 RAM 的显式 .sct，或先在现有 GCC 工程中确认每个命名段的地址，再使用该 GCC 工程。"));
            return null;
        }
        var residue = Regex.Replace(text, @"\b[A-Za-z_]\w*\s+(?:0x[0-9a-fA-F]+|\d+)\s+(?:0x[0-9a-fA-F]+|\d+)\s*\{", "");
        var ramFunctionSelector = Regex.Match(text, @"\*\.o\s*\(\s*\.RamFunc\s*,\s*\.RamFunc\*\s*\)");
        if (ramFunctionSelector.Success && ramFunctionSelector.Index < regions[2].Index)
        {
            issues.Add(ConversionPaths.Block(path, ".RamFunc 选择器未位于 RAM 执行区。", "将 RAM 函数放入同一 RW_IRAM 执行区并保留 FLASH 装载数据。")); return null;
        }
        residue = Regex.Replace(residue, @"\*\.o\s*\(\s*\.RamFunc\s*,\s*\.RamFunc\*\s*\)", "");
        residue = Regex.Replace(residue, @"\*\.o\s*\(\s*RESET\s*,\s*\+First\s*\)|\*\s*\(\s*InRoot\$\$Sections\s*\)|\.ANY\s*\(\s*\+RO\s*\)|\.ANY\s*\(\s*\+XO\s*\)|\.ANY\s*\(\s*\+RW\s+\+ZI\s*\)", "", RegexOptions.IgnoreCase);
        if (!string.IsNullOrWhiteSpace(residue.Replace("}", "")) || !text.Contains("RESET", StringComparison.Ordinal) || !text.Contains("+RO", StringComparison.Ordinal))
        {
            issues.Add(ConversionPaths.Block(path, "存在对象/库/命名段选择器或非标准执行区属性。", "将特殊段保留在手工校核的 GCC 链接脚本中；自动转换支持 RESET、InRoot$$Sections 和 .ANY 的简单 RO/RW/ZI 布局。"));
            return null;
        }
        var load = regions[0]; var rom = regions[1]; var ram = regions[2];
        var flashStart = Number(rom.Groups["address"].Value); var flashSize = Number(rom.Groups["size"].Value);
        if (Number(load.Groups["address"].Value) != flashStart || Number(load.Groups["size"].Value) != flashSize)
        {
            issues.Add(ConversionPaths.Block(path, "装载地址与执行地址不同。", "使用相同装载/执行地址的 FLASH 布局，或手工保留重定位逻辑。")); return null;
        }
        return new(flashStart, flashSize, Number(ram.Groups["address"].Value), Number(ram.Groups["size"].Value), RamFunctions: ramFunctionSelector.Success);
    }

    public static ConversionMemory? ReadGcc(string path, List<ConversionIssue> issues)
    {
        var text = Regex.Replace(File.ReadAllText(path), @"/\*[\s\S]*?\*/", "");
        if (Regex.IsMatch(text, @"\b(?:INCLUDE|OVERLAY|PHDRS)\b") || Regex.IsMatch(text, @"\.\s*=\s*(?:0x[\da-fA-F]+|\d+)\b") ||
            Regex.IsMatch(text, @"(?m)^\s*\.[\w.]+\s+(?:0x[\da-fA-F]+|\d+)\s*[:(]"))
        {
            issues.Add(ConversionPaths.Block(path, "链接脚本包含 INCLUDE、OVERLAY、PHDRS 或固定段地址。", "展开并校核链接布局后，提供简单 FLASH/RAM 链接脚本。")); return null;
        }
        var memory = Regex.Match(text, @"\bMEMORY\s*\{(?<body>[^}]+)\}");
        const string regionPattern = @"(?<name>[A-Za-z_]\w*)\s*\([^)]*\)\s*:\s*ORIGIN\s*=\s*(?<start>0x[\da-fA-F]+|\d+)\s*,\s*LENGTH\s*=\s*(?<size>0x[\da-fA-F]+|\d+[kKmM]?)(?![\w.])";
        var memoryBody = memory.Groups["body"].Value;
        var regions = Regex.Matches(memoryBody, regionPattern);
        var memoryResidue = Regex.Replace(memoryBody, regionPattern, "");
        if (!memory.Success || regions.Count == 0 || !string.IsNullOrWhiteSpace(memoryResidue.Replace(";", "")) || regions.Select(x => x.Groups["name"].Value).Distinct(StringComparer.Ordinal).Count() != regions.Count)
        {
            issues.Add(ConversionPaths.Block(path, "MEMORY 地址/容量包含非完整数字常量、表达式或重复区域名，不能按数字前缀推测容量。", "将每个 ORIGIN/LENGTH 显式写为完整十进制/十六进制数字或 K/M 容量常量；先准确计算并保留所有保留区。")); return null;
        }
        var assignments = Regex.Matches(text, @">\s*(?<name>[A-Za-z_]\w*)").Select(x => x.Groups["name"].Value).Distinct(StringComparer.Ordinal).ToArray();
        var used = regions.Where(x => assignments.Contains(x.Groups["name"].Value, StringComparer.Ordinal)).ToArray();
        var flashName = Regex.Match(text, @"\.isr_vector\s*:[\s\S]*?\}\s*>\s*(?<name>\w+)").Groups["name"].Value;
        var dataName = Regex.Match(text, @"\.data\s*:[\s\S]*?\}\s*>\s*(?<name>\w+)").Groups["name"].Value;
        var bssName = Regex.Match(text, @"\.bss(?:\s*\([^)]*\))?\s*:[\s\S]*?\}\s*>\s*(?<name>\w+)").Groups["name"].Value;
        var flash = used.FirstOrDefault(x => x.Groups["name"].Value == flashName);
        var ram = used.FirstOrDefault(x => x.Groups["name"].Value == dataName);
        if (used.Length != 2 || flash == null || ram == null || dataName != bssName || flashName == dataName)
        {
            issues.Add(ConversionPaths.Block(path, "无法准确映射向量、数据和 BSS 到单个 FLASH/单个 RAM；可能有多个实际使用的区域。",
                "将所选目标的向量/RO 放入同一 FLASH，数据/BSS/栈放入同一 RAM；特殊区域请提供并校核手工 MDK scatter。")); return null;
        }
        if (!Regex.IsMatch(text, @"_estack\s*=\s*ORIGIN\s*\(\s*" + Regex.Escape(dataName) + @"\s*\)\s*\+\s*LENGTH\s*\(\s*" + Regex.Escape(dataName) + @"\s*\)\s*;"))
        {
            issues.Add(ConversionPaths.Block(path, "栈顶不是实际数据 RAM 的末端，存在自定义栈布局。", "提供同一 RAM 区末端的标准 CubeMX _estack，或手工校核 MDK 栈位置。")); return null;
        }
        var allowed = new HashSet<string>(StringComparer.Ordinal) { ".isr_vector", ".text", ".rodata", ".ARM.extab", ".ARM", ".ARM.exidx", ".preinit_array", ".init_array", ".fini_array", ".data", ".bss", "._user_heap_stack" };
        var sectionHeaders = Regex.Matches(text, @"(?m)^\s*(?<name>\.[A-Za-z_][\w.]*)\s*(?:\([^)]*\))?\s*:");
        var unknown = sectionHeaders.Select(x => x.Groups["name"].Value).Where(x => !allowed.Contains(x)).ToArray();
        var selectors = Regex.Matches(text, @"\*\s*\(\s*(?<name>\.[\w.]+)\*?\s*\)").Select(x => x.Groups["name"].Value)
            .Where(x => !allowed.Contains(x) && !new[] { ".glue_7", ".glue_7t", ".eh_frame", ".init", ".fini", ".RamFunc" }.Contains(x, StringComparer.Ordinal)).ToArray();
        if (unknown.Length > 0 || selectors.Length > 0 || Regex.IsMatch(text, @"\b(?:AT\s*\(|ABSOLUTE\s*\(|NOLOAD\s*\)[^:]*:[^{]*0x)"))
        {
            issues.Add(ConversionPaths.Block(path, $"包含特殊段或显式定位：{string.Join(", ", unknown.Concat(selectors).Distinct())}。", "手工建立 MDK 命名执行区并校核初始化行为；自动转换仅支持标准 CubeMX C/C++ 段布局。")); return null;
        }
        var data = Regex.Match(text, @"\.data\s*:\s*\{(?<body>[^}]+)\}\s*>\s*" + Regex.Escape(dataName) + @"\s+AT\s*>\s*" + Regex.Escape(flashName) + @"\b");
        const string ramSelectorPattern = @"\*\s*\(\s*\.RamFunc\*?\s*\)";
        var textSection = Regex.Match(text, @"\.text\s*:\s*\{(?<body>[^}]+)\}\s*>\s*" + Regex.Escape(flashName) + @"\b");
        var ramSelectorCount = data.Success ? Regex.Matches(data.Groups["body"].Value, ramSelectorPattern).Count : 0;
        var roSelectorCount = textSection.Success ? Regex.Matches(textSection.Groups["body"].Value, ramSelectorPattern).Count : 0;
        var allSelectorCount = Regex.Matches(text, ramSelectorPattern).Count;
        var ramFunctions = ramSelectorCount > 0;
        if (allSelectorCount != ramSelectorCount + roSelectorCount || ramSelectorCount > 0 && roSelectorCount > 0)
        {
            issues.Add(ConversionPaths.Block(path, ".RamFunc 不在标准 .data RAM 执行/FLASH 装载区中。", "为 RAM 函数提供并校核同一 RAM 执行区和 FLASH 装载/初始化映射。")); return null;
        }
        ulong size(string symbol, ulong fallback)
        {
            var match = Regex.Match(text, Regex.Escape(symbol) + @"\s*=\s*(0x[\da-fA-F]+|\d+)\s*;"); return match.Success ? Number(match.Groups[1].Value) : fallback;
        }
        return new(Number(flash.Groups["start"].Value), Number(flash.Groups["size"].Value), Number(ram.Groups["start"].Value), Number(ram.Groups["size"].Value), size("_Min_Stack_Size", 0x400), size("_Min_Heap_Size", 0x200), ramFunctions);
    }

    public static string Scatter(ConversionMemory m) => $"LR_IROM1 0x{m.FlashStart:X8} 0x{m.FlashSize:X8} {{\n  ER_IROM1 0x{m.FlashStart:X8} 0x{m.FlashSize:X8} {{\n    *.o (RESET, +First)\n    *(InRoot$$Sections)\n    .ANY (+RO)\n    .ANY (+XO)\n  }}\n  RW_IRAM1 0x{m.RamStart:X8} 0x{m.RamSize:X8} {{\n{(m.RamFunctions ? "    *.o (.RamFunc, .RamFunc*)\n" : "")}    .ANY (+RW +ZI)\n  }}\n}}\n";

    public static string Gcc(ConversionMemory m) => $$"""
ENTRY(Reset_Handler)
MEMORY
{
  FLASH (rx) : ORIGIN = 0x{{m.FlashStart:X8}}, LENGTH = 0x{{m.FlashSize:X8}}
  RAM (xrw) : ORIGIN = 0x{{m.RamStart:X8}}, LENGTH = 0x{{m.RamSize:X8}}
}
_estack = ORIGIN(RAM) + LENGTH(RAM);
_Min_Stack_Size = 0x{{m.StackSize:X}};
_Min_Heap_Size = 0x{{m.HeapSize:X}};
SECTIONS
{
  .isr_vector : { . = ALIGN(4); KEEP(*(.isr_vector)) . = ALIGN(4); } >FLASH
  .text : { *(.text*) {{(m.RamFunctions ? "" : "*(.RamFunc*)")}} *(.glue_7) *(.glue_7t) *(.eh_frame) KEEP(*(.init)) KEEP(*(.fini)) . = ALIGN(4); _etext = .; } >FLASH
  .rodata : { *(.rodata*) . = ALIGN(4); } >FLASH
  .ARM.extab : { *(.ARM.extab* .gnu.linkonce.armextab.*) } >FLASH
  .ARM.exidx : { __exidx_start = .; *(.ARM.exidx*) __exidx_end = .; } >FLASH
  .preinit_array : { PROVIDE_HIDDEN(__preinit_array_start = .); KEEP(*(.preinit_array*)) PROVIDE_HIDDEN(__preinit_array_end = .); } >FLASH
  .init_array : { PROVIDE_HIDDEN(__init_array_start = .); KEEP(*(SORT(.init_array.*))) KEEP(*(.init_array*)) PROVIDE_HIDDEN(__init_array_end = .); } >FLASH
  .fini_array : { PROVIDE_HIDDEN(__fini_array_start = .); KEEP(*(SORT(.fini_array.*))) KEEP(*(.fini_array*)) PROVIDE_HIDDEN(__fini_array_end = .); } >FLASH
  _sidata = LOADADDR(.data);
  .data : { . = ALIGN(4); _sdata = .; *(.data*) {{(m.RamFunctions ? "*(.RamFunc*)" : "")}} . = ALIGN(4); _edata = .; } >RAM AT> FLASH
  .bss (NOLOAD) : { . = ALIGN(4); _sbss = .; __bss_start__ = .; *(.bss*) *(COMMON) . = ALIGN(4); _ebss = .; __bss_end__ = .; } >RAM
  ._user_heap_stack (NOLOAD) : { . = ALIGN(8); PROVIDE(end = .); PROVIDE(_end = .); . += _Min_Heap_Size; . += _Min_Stack_Size; . = ALIGN(8); } >RAM
  ASSERT(SIZEOF(._user_heap_stack) + ADDR(._user_heap_stack) <= _estack, "RAM overflow: heap/stack")
}

""";
}
