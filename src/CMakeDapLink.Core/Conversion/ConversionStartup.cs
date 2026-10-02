using System.Text;
using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

internal static class ConversionStartup
{
    public static IReadOnlyList<string>? ReadVectors(string path, bool arm, List<ConversionIssue> issues)
    {
        var text = File.ReadAllText(path);
        var clean = arm ? Regex.Replace(text, @";[^\r\n]*", "") : Regex.Replace(Regex.Replace(text, @"/\*[\s\S]*?\*/", ""), @"//[^\r\n]*", "");
        if (arm) clean = Regex.Replace(clean, @"\\[ \t]*\r?\n", " ");
        var block = arm
            ? Regex.Match(clean, @"(?ms)^\s*__Vectors\s+DCD\s+(?<body>.*?)^\s*__Vectors_End\b")
            : Regex.Match(clean, @"(?ms)^\s*g_pfnVectors\s*:\s*(?<body>.*?)(?:^\s*\.size\s+g_pfnVectors|^\s*\.section|^\s*/\*)");
        if (!block.Success && !arm) block = Regex.Match(clean, @"(?ms)^\s*g_pfnVectors\s*:\s*(?<body>.*)");
        var body = block.Groups["body"].Value;
        var entries = arm
            ? Regex.Matches("DCD " + body, @"\bDCD\s+(?<value>[^\r\n]+)").SelectMany(x => x.Groups["value"].Value.Split(',')).Select(x => x.Trim()).ToArray()
            : Regex.Matches(body, @"(?m)^\s*\.word\s+(?<value>[^\r\n]+)").SelectMany(x => x.Groups["value"].Value.Split(',')).Select(x => x.Trim()).ToArray();
        if (!block.Success || entries.Length < 16 || entries[1] != "Reset_Handler" || entries[0] != (arm ? "__initial_sp" : "_estack") ||
            entries.Skip(2).Any(x => !Regex.IsMatch(x, @"^(?:0|[A-Za-z_]\w*)$")))
        {
            issues.Add(ConversionPaths.Block(path, "未识别标准 CubeMX 向量表，或存在地址/表达式向量。", "提供标准 __Vectors/DCD 或 g_pfnVectors/.word 向量表，并保留中断顺序。")); return null;
        }
        if (arm)
        {
            var reset = Regex.Match(clean, @"(?ms)^\s*Reset_Handler\s+PROC(?<body>.*?)^\s*ENDP");
            var normalized = Compact(Regex.Replace(reset.Groups["body"].Value, @"(?m)^\s*(?:EXPORT|IMPORT)\s+[^\r\n]*", ""));
            const string armReset = "LDRR0,=SystemInitBLXR0LDRR0,=__mainBXR0";
            if (!reset.Success || normalized != armReset && normalized != "LDRR0,=ExitRun0ModeBLXR0" + armReset)
            {
                issues.Add(ConversionPaths.Block(path, "Reset_Handler 含非标准启动代码。", "将自定义复位逻辑显式移入 SystemInit，或为 GCC 提供保持同一向量表的已校核 startup。")); return null;
            }
            var handlers = entries.Skip(2).Where(x => x != "0").Distinct(StringComparer.Ordinal).ToArray();
            var defaults = new HashSet<string>(StringComparer.Ordinal);
            foreach (var handler in handlers)
                if (!Regex.IsMatch(clean, @"\bEXPORT\s+" + Regex.Escape(handler) + @"\s+\[WEAK\]", RegexOptions.IgnoreCase))
                    issues.Add(ConversionPaths.Block(path, $"向量处理函数 {handler} 在 startup 中不是标准 weak 定义。", "保留处理函数实现到独立源码，并提供标准 weak startup。"));
            foreach (Match procedure in Regex.Matches(clean, @"(?ms)^\s*(?<name>\w+)\s+PROC(?<body>.*?)^\s*ENDP"))
            {
                var name = procedure.Groups["name"].Value;
                if (name == "Reset_Handler") continue;
                if (name != "Default_Handler" && !handlers.Contains(name, StringComparer.Ordinal))
                { issues.Add(ConversionPaths.Block(path, $"startup 包含额外函数 {name}。", "把实现保留到独立源文件或提供匹配的 GCC startup。")); continue; }
                var bodyWithoutExports = Regex.Replace(procedure.Groups["body"].Value, @"(?m)^\s*EXPORT\s+[^\r\n]*", "");
                foreach (var handler in handlers) bodyWithoutExports = Regex.Replace(bodyWithoutExports, @"(?m)^\s*" + Regex.Escape(handler) + @"\s*$", "");
                bodyWithoutExports = Regex.Replace(bodyWithoutExports, @"(?mi)^\s*B\s+(?:\.|Default_Handler)\s*$", "");
                if (!string.IsNullOrWhiteSpace(bodyWithoutExports))
                    issues.Add(ConversionPaths.Block(path, $"{name} 含自定义处理逻辑，不能替换为默认 weak handler。", "保留该处理函数实现到独立源码，或提供含相同逻辑的 GCC startup。"));
                else
                {
                    if (name != "Default_Handler") defaults.Add(name);
                    foreach (var handler in handlers)
                        if (Regex.IsMatch(procedure.Groups["body"].Value, @"(?m)^\s*" + Regex.Escape(handler) + @"\s*$")) defaults.Add(handler);
                }
            }
            foreach (var handler in handlers.Where(x => !defaults.Contains(x)))
                issues.Add(ConversionPaths.Block(path, $"无法确认 {handler} 是标准默认循环实现。", "保留自定义处理逻辑到独立源码；默认 handler 必须位于标准 PROC 中。"));
            foreach (Match export in Regex.Matches(clean, @"(?m)^\s*EXPORT\s+(?<name>\w+)"))
            {
                var name = export.Groups["name"].Value;
                if (!handlers.Contains(name, StringComparer.Ordinal) && !new[] { "Reset_Handler", "Default_Handler", "__Vectors", "__Vectors_End", "__Vectors_Size", "__initial_sp", "__heap_base", "__heap_limit", "__user_initial_stackheap" }.Contains(name, StringComparer.Ordinal))
                    issues.Add(ConversionPaths.Block(path, $"startup 额外导出 {name}，不能省略。", "保留该实现到独立源码或提供已校核的对应 startup。"));
            }
        }
        else
        {
            var reset = Regex.Match(clean, @"(?ms)^\s*Reset_Handler\s*:(?<body>.*?)(?:^\s*\.size\s+Reset_Handler|^\s*\.section)");
            if (!reset.Success || !StandardGccReset(reset.Groups["body"].Value))
                issues.Add(ConversionPaths.Block(path, "GCC Reset_Handler 含不属于完整标准初始化模板的指令/控制流，不能丢弃。", "保留自定义复位指令到独立 SystemInit 逻辑，或提供经校核的对应 Arm Compiler 6 startup；仅自动转换完整标准数据/BSS/构造初始化流程。"));
            foreach (var handler in entries.Skip(2).Where(x => x != "0").Distinct(StringComparer.Ordinal))
                if (!Regex.IsMatch(clean, @"(?m)^\s*\.weak\s+" + Regex.Escape(handler) + @"\b") ||
                    !Regex.IsMatch(clean, @"(?m)^\s*\.thumb_set\s+" + Regex.Escape(handler) + @"\s*,\s*Default_Handler\b") ||
                    Regex.IsMatch(clean, @"(?m)^\s*" + Regex.Escape(handler) + @"\s*:"))
                    issues.Add(ConversionPaths.Block(path, $"向量处理函数 {handler} 不是标准 weak Default_Handler 别名。", "保留实现到独立源码，并使用标准 CubeMX weak 向量 startup。"));
            var defaultBody = Regex.Match(clean, @"(?ms)^\s*Default_Handler\s*:(?<body>.*?)(?:^\s*\.size\s+Default_Handler|^\s*\.section)").Groups["body"].Value;
            if (defaultBody.Length == 0 || !Regex.IsMatch(defaultBody, @"(?m)^\s*b\s+(?:Default_Handler|Infinite_Loop|\.)\s*$", RegexOptions.IgnoreCase) ||
                Regex.Matches(defaultBody, @"(?m)^\s*(?<instruction>[a-zA-Z]+)\s+[^\r\n]*").Any(x => x.Groups["instruction"].Value is not "b" and not "B"))
                issues.Add(ConversionPaths.Block(path, "Default_Handler 含非标准处理逻辑。", "保留默认异常实现到独立源码，或手工迁移相同处理逻辑。"));
            if (Regex.IsMatch(clean, @"(?m)^\s*#\s*(?:if|ifdef|ifndef|elif|else|endif)\b") ||
                Regex.Matches(clean, @"(?m)^\s*\.type\s+(?<name>\w+)\s*,\s*%function").Any(x => x.Groups["name"].Value is not "Reset_Handler" and not "Default_Handler"))
                issues.Add(ConversionPaths.Block(path, "startup 含条件汇编或额外函数。", "先展开所选配置的条件并保留额外函数到独立源文件，再转换标准 startup。"));
        }
        return entries;
    }

    private static string Compact(string text) => Regex.Replace(text, @"\s+", "");
    private static bool StandardGccReset(string body)
    {
        var actual = Compact(body);
        const string stack = "ldrsp,=_estack";
        if (actual.StartsWith(stack, StringComparison.Ordinal)) actual = actual[stack.Length..];
        const string exitRun = "blExitRun0Mode";
        if (actual.StartsWith(exitRun, StringComparison.Ordinal)) actual = actual[exitRun.Length..];
        const string generated = """
bl SystemInit
ldr r0, =_sdata
ldr r1, =_edata
ldr r2, =_sidata
1:
cmp r0, r1
bcs 2f
ldr r3, [r2]
str r3, [r0]
adds r2, #4
adds r0, #4
b 1b
2:
ldr r0, =_sbss
ldr r1, =_ebss
movs r2, #0
3:
cmp r0, r1
bcs 4f
str r2, [r0]
adds r0, #4
b 3b
4:
bl __libc_init_array
bl main
5:
b 5b
""";
        const string cubeMx = """
bl SystemInit
ldr r0, =_sdata
ldr r1, =_edata
ldr r2, =_sidata
movs r3, #0
b LoopCopyDataInit
CopyDataInit:
ldr r4, [r2, r3]
str r4, [r0, r3]
adds r3, r3, #4
LoopCopyDataInit:
adds r4, r0, r3
cmp r4, r1
bcc CopyDataInit
ldr r2, =_sbss
ldr r4, =_ebss
movs r3, #0
b LoopFillZerobss
FillZerobss:
str r3, [r2]
adds r2, r2, #4
LoopFillZerobss:
cmp r2, r4
bcc FillZerobss
bl __libc_init_array
bl main
""";
        // Compare the entire executable body; accepting an instruction whitelist would
        // still permit inline register writes or changed initialization branches.
        var stock = Compact(cubeMx);
        return actual == Compact(generated) || actual == stock + "bxlr" || actual == stock + "LoopForever:bLoopForever";
    }

    public static ConversionMemory WithStack(string path, ConversionMemory memory)
    {
        var text = File.ReadAllText(path);
        ulong read(string name, ulong fallback)
        {
            var found = Regex.Match(text, @"(?mi)^\s*" + name + @"\s+EQU\s+(0x[\da-fA-F]+|\d+)");
            return found.Success ? ConversionLinker.Number(found.Groups[1].Value) : fallback;
        }
        return memory with { StackSize = read("Stack_Size", memory.StackSize), HeapSize = read("Heap_Size", memory.HeapSize) };
    }

    private static string Notices(string path, bool armOutput)
    {
        var text = File.ReadAllText(path);
        var comments = Regex.Matches(text, @"(?m)^\s*;(?<text>[^\r\n]*)").Select(x => x.Groups["text"].Value).ToList();
        foreach (Match match in Regex.Matches(text, @"/\*(?<text>[\s\S]*?)\*/")) comments.AddRange(match.Groups["text"].Value.Split('\n'));
        return armOutput ? string.Join("\n", comments.Select(x => ";" + x.TrimEnd('\r'))) + "\n" : "/*\n" + string.Join("\n", comments).Replace("*/", "* /") + "\n*/\n";
    }

    public static string Gcc(string original, IReadOnlyList<string> vectors, string cpu)
    {
        var result = new StringBuilder(Notices(original, false));
        result.Append($".syntax unified\n.cpu {cpu}\n.thumb\n.global g_pfnVectors\n.global Reset_Handler\n.section .isr_vector,\"a\",%progbits\n.type g_pfnVectors,%object\ng_pfnVectors:\n");
        result.AppendLine(" .word _estack"); foreach (var entry in vectors.Skip(1)) result.AppendLine(" .word " + entry);
        result.Append(".size g_pfnVectors, .-g_pfnVectors\n.section .text.Reset_Handler,\"ax\",%progbits\n.type Reset_Handler,%function\n.thumb_func\nReset_Handler:\n");
        if (HasExitRun0(original)) result.Append(" bl ExitRun0Mode\n");
        result.Append(" bl SystemInit\n ldr r0, =_sdata\n ldr r1, =_edata\n ldr r2, =_sidata\n1:\n cmp r0, r1\n bcs 2f\n ldr r3, [r2]\n str r3, [r0]\n adds r2, #4\n adds r0, #4\n b 1b\n2:\n ldr r0, =_sbss\n ldr r1, =_ebss\n movs r2, #0\n3:\n cmp r0, r1\n bcs 4f\n str r2, [r0]\n adds r0, #4\n b 3b\n4:\n bl __libc_init_array\n bl main\n5:\n b 5b\n.size Reset_Handler, .-Reset_Handler\n.section .text.Default_Handler,\"ax\",%progbits\n.thumb_func\nDefault_Handler:\n b Default_Handler\n");
        foreach (var handler in vectors.Skip(2).Where(x => x != "0").Distinct(StringComparer.Ordinal)) result.Append($".weak {handler}\n.thumb_set {handler}, Default_Handler\n");
        result.Append(".section .note.GNU-stack,\"\",%progbits\n"); return result.ToString();
    }

    public static string Arm(string original, IReadOnlyList<string> vectors, ConversionMemory memory)
    {
        var result = new StringBuilder(Notices(original, true));
        result.Append($"Stack_Size EQU 0x{memory.StackSize:X}\n AREA STACK, NOINIT, READWRITE, ALIGN=3\nStack_Mem SPACE Stack_Size\n__initial_sp\nHeap_Size EQU 0x{memory.HeapSize:X}\n AREA HEAP, NOINIT, READWRITE, ALIGN=3\n__heap_base\nHeap_Mem SPACE Heap_Size\n__heap_limit\n PRESERVE8\n THUMB\n AREA RESET, DATA, READONLY\n EXPORT __Vectors\n EXPORT __Vectors_End\n EXPORT __Vectors_Size\n__Vectors DCD __initial_sp\n");
        foreach (var entry in vectors.Skip(1)) result.AppendLine(" DCD " + entry);
        result.Append("__Vectors_End\n__Vectors_Size EQU __Vectors_End - __Vectors\n AREA |.text|, CODE, READONLY\nReset_Handler PROC\n EXPORT Reset_Handler [WEAK]\n IMPORT SystemInit\n IMPORT __main\n");
        if (HasExitRun0(original)) result.Append(" IMPORT ExitRun0Mode\n LDR R0, =ExitRun0Mode\n BLX R0\n");
        result.Append(" LDR R0, =SystemInit\n BLX R0\n LDR R0, =__main\n BX R0\n ENDP\nDefault_Handler PROC\n");
        foreach (var handler in vectors.Skip(2).Where(x => x != "0").Distinct(StringComparer.Ordinal)) result.AppendLine(" EXPORT " + handler + " [WEAK]");
        foreach (var handler in vectors.Skip(2).Where(x => x != "0").Distinct(StringComparer.Ordinal)) result.AppendLine(handler);
        result.Append(" B .\n ENDP\n ALIGN\n IF :DEF:__MICROLIB\n EXPORT __initial_sp\n EXPORT __heap_base\n EXPORT __heap_limit\n ELSE\n IMPORT __use_two_region_memory\n EXPORT __user_initial_stackheap\n__user_initial_stackheap\n LDR R0, =Heap_Mem\n LDR R1, =(Stack_Mem + Stack_Size)\n LDR R2, =(Heap_Mem + Heap_Size)\n LDR R3, =Stack_Mem\n BX LR\n ALIGN\n ENDIF\n END\n"); return result.ToString();
    }
    private static bool HasExitRun0(string path) => Regex.IsMatch(File.ReadAllText(path), @"(?mi)^\s*(?:LDR\s+R0\s*,\s*=|bl\s+)ExitRun0Mode\b");
    public static string? GccCounterpart(ConversionProject project, IReadOnlyList<string> vectors)
    {
        var directory = Path.Combine(project.Root, "Drivers", "CMSIS", "Device", "ST");
        if (!Directory.Exists(directory)) return null;
        foreach (var candidate in Directory.EnumerateFiles(directory, Path.GetFileName(project.Startup!), SearchOption.AllDirectories).Where(x => x.Contains(Path.DirectorySeparatorChar + "gcc" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            var issues = new List<ConversionIssue>(); var candidateVectors = ReadVectors(candidate, false, issues);
            if (!issues.Any(x => x.Blocking) && candidateVectors != null && candidateVectors.Skip(1).SequenceEqual(vectors.Skip(1)) && HasExitRun0(candidate) == HasExitRun0(project.Startup!)) return candidate;
        }
        return null;
    }
}
