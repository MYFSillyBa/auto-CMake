namespace CMakeDapLink.Core;

public sealed record HardFaultFinding(string Code, string Title, string Detail);
public sealed record HardFaultLocation(string Register, string Address, string? Function, string? File, int? Line);
public sealed record HardFaultReport(IReadOnlyList<HardFaultFinding> Findings, IReadOnlyList<HardFaultLocation> Locations, IReadOnlyList<string> Notes);

/// <summary>Interprets supplied fault evidence; never reads hardware or invents a register snapshot.</summary>
public static class HardFaultDecoder
{
    public static HardFaultReport Decode(IReadOnlyDictionary<string, uint> registers, string? core)
    {
        var values = registers.ToDictionary(pair => pair.Key.ToUpperInvariant(), pair => pair.Value, StringComparer.Ordinal);
        var findings = new List<HardFaultFinding>();
        var notes = new List<string>();
        var architecture = (core ?? "").Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        var baseline = architecture.Contains("M0", StringComparison.Ordinal) || architecture.Contains("M1", StringComparison.Ordinal) || architecture.Contains("M23", StringComparison.Ordinal) || architecture.Contains("ARMV6", StringComparison.Ordinal);
        if (baseline)
            notes.Add("Cortex-M0/M0+ 等基线内核没有这里使用的 CFSR、HFSR、MMFAR、BFAR；已忽略这些值，请结合异常栈帧和反汇编分析。");
        else
        {
            if (string.IsNullOrWhiteSpace(core)) notes.Add("未指定内核，按 Cortex-M3/M4/M7 通用故障位解释；可选扩展位以芯片手册为准，Cortex-M0/M0+ 不适用。");
            if (values.TryGetValue("CFSR", out var cfsr))
            {
                foreach (var bit in Bits)
                    if ((cfsr & (1u << bit.Bit)) != 0) findings.Add(new(bit.Code, bit.Title, bit.Detail));
                var known = Bits.Aggregate(0u, (mask, bit) => mask | (1u << bit.Bit)) | (1u << 7) | (1u << 15);
                if ((cfsr & ~known) != 0) notes.Add("CFSR 包含未解释的保留位或内核扩展位；请查阅所选内核与芯片手册。");
                if ((cfsr & (1u << 7)) != 0)
                {
                    if (values.TryGetValue("MMFAR", out var address)) findings.Add(new("MMFAR", "有效内存管理故障地址", "MMARVALID 已置位，MMFAR = " + Address(address) + "。"));
                    else notes.Add("MMARVALID 已置位，但未提供 MMFAR，故障地址未知。");
                }
                else if (values.ContainsKey("MMFAR")) notes.Add("MMARVALID 未置位，MMFAR 可能是旧值，未将其作为故障地址。");
                if ((cfsr & (1u << 15)) != 0)
                {
                    if (values.TryGetValue("BFAR", out var address)) findings.Add(new("BFAR", "有效总线故障地址", "BFARVALID 已置位，BFAR = " + Address(address) + "。"));
                    else notes.Add("BFARVALID 已置位，但未提供 BFAR，故障地址未知。");
                }
                else if (values.ContainsKey("BFAR")) notes.Add("BFARVALID 未置位，BFAR 可能是旧值，未将其作为故障地址。");
            }
            else notes.Add("未提供 CFSR，无法确认 MemManage、BusFault 或 UsageFault 的状态位及地址有效性。");
            if (values.TryGetValue("HFSR", out var hfsr))
            {
                if ((hfsr & (1u << 1)) != 0) findings.Add(new("VECTTBL", "异常向量读取总线错误", "异常入口读取向量表时发生总线错误；检查 VTOR、向量表地址和可访问性。"));
                if ((hfsr & (1u << 30)) != 0) findings.Add(new("FORCED", "可配置故障升级为 HardFault", "故障处理被禁用或当前优先级不能处理原故障；结合 CFSR 确认具体原因。"));
                if ((hfsr & (1u << 31)) != 0) findings.Add(new("DEBUGEVT", "调试事件触发 HardFault", "调试事件无法进入调试状态；检查断点、BKPT 和半主机调用，并结合 DFSR。"));
                if ((hfsr & ~0xc0000002u) != 0) notes.Add("HFSR 包含未解释的保留位；请核对寄存器来源和内核型号。");
            }
        }
        if (values.TryGetValue("EXC_RETURN", out var exceptionReturn) || values.TryGetValue("LR", out exceptionReturn))
        {
            if ((exceptionReturn is 0xfffffff1 or 0xfffffff9 or 0xfffffffd or 0xffffffe1 or 0xffffffe9 or 0xffffffed) &&
                (!baseline || (exceptionReturn & 16) != 0))
            {
                var stack = (exceptionReturn & 4) == 0 ? "MSP" : "PSP";
                var mode = (exceptionReturn & 8) == 0 ? "Handler" : "Thread";
                var frame = (exceptionReturn & 16) == 0 ? "扩展浮点栈帧" : "基本栈帧";
                findings.Add(new("EXC_RETURN", "异常返回上下文", "EXC_RETURN = " + Address(exceptionReturn) + "；使用 " + stack + "，返回 " + mode + " 模式，" + frame + "。"));
                notes.Add("EXC_RETURN 是异常入口时处理函数 LR 的编码，不能当作调用地址；栈帧内保存的 LR 才可能是被中断函数的返回地址。");
            }
            else if (exceptionReturn >= 0xffffff80)
                notes.Add("LR/EXC_RETURN 位于异常返回编码范围，但不属于本解码器支持的通用值；可能涉及安全扩展或损坏，未猜测栈帧。");
        }
        if (values.TryGetValue("SP", out var sp) && (sp & 3) != 0)
            findings.Add(new("SP_ALIGNMENT", "栈指针未按字对齐", "所提供 SP = " + Address(sp) + "；核对该值是否来自异常发生时的实际栈指针。"));
        if (values.TryGetValue("XPSR", out var xpsr) && (xpsr & (1u << 24)) == 0)
            findings.Add(new("THUMB_STATE", "xPSR 的 Thumb 状态位为零", "Cortex-M 需要 Thumb 状态；若这是异常栈帧中的 xPSR，检查栈损坏和异常返回上下文。"));
        if (findings.Count == 0) notes.Add("所提供寄存器没有可确认的故障原因；零状态位不证明固件正常，也可能在捕获前已被清除。");
        notes.Add("PC 应提供异常入口保存的 stacked PC；在 HardFault 处理函数内读取的当前 PC 只定位处理函数，不能确认原故障指令。");
        notes.Add("地址映射依赖与崩溃固件完全匹配且保留调试信息的 ELF；LR 通常是返回点，单个 PC/LR 不能重建完整调用栈。");
        return new(findings, [], notes);
    }

    private static string Address(uint value) => "0x" + value.ToString("X8");
    private sealed record FaultBit(int Bit, string Code, string Title, string Detail);
    // Arm DUI 0552A sections 2.3 and 4.3; FP lazy-preservation bits are optional on FP-capable cores.
    private static readonly FaultBit[] Bits =
    [
        new(0, "IACCVIOL", "指令访问违反内存保护", "处理器从禁止执行或受 MPU 保护的区域取指；检查函数指针、执行权限和返回地址。"),
        new(1, "DACCVIOL", "数据访问违反内存保护", "数据读写违反内存保护；仅在 MMARVALID 置位时使用 MMFAR 定位地址。"),
        new(3, "MUNSTKERR", "异常返回出栈内存保护错误", "异常返回读取栈帧时发生内存保护错误；栈帧内容可能不可靠。"),
        new(4, "MSTKERR", "异常入口压栈内存保护错误", "异常入口保存栈帧时发生内存保护错误；检查栈边界和 MPU，保存的 PC 可能不可靠。"),
        new(5, "MLSPERR", "浮点延迟保存内存保护错误", "具有浮点扩展的内核在延迟保存浮点状态时触发内存保护错误；检查栈空间和权限。"),
        new(8, "IBUSERR", "指令总线错误", "处理器取指时发生总线错误；检查代码地址、存储器映射和执行区域。"),
        new(9, "PRECISERR", "精确数据总线错误", "数据访问发生精确总线错误；若无压栈错误，stacked PC 通常对应相关指令，BFAR 仍需 BFARVALID。"),
        new(10, "IMPRECISERR", "非精确数据总线错误", "总线错误可能来自较早的缓冲写入；stacked PC 不一定是故障指令，不能据此确认单行代码。"),
        new(11, "UNSTKERR", "异常返回出栈总线错误", "异常返回读取栈帧发生总线错误；检查 SP、栈存储器和栈边界，栈帧可能不可靠。"),
        new(12, "STKERR", "异常入口压栈总线错误", "异常入口保存栈帧发生总线错误；栈帧可能不完整，不能直接信任保存的 PC/LR。"),
        new(13, "LSPERR", "浮点延迟保存总线错误", "具有浮点扩展的内核在延迟保存浮点状态时发生总线错误；检查栈存储器和可用空间。"),
        new(16, "UNDEFINSTR", "未定义指令", "解码到未定义指令；检查跳转目标、代码损坏和固件是否适合当前内核。"),
        new(17, "INVSTATE", "无效执行状态", "执行状态不合法；检查 Thumb 状态、函数指针最低位和异常栈帧。"),
        new(18, "INVPC", "无效异常返回", "异常返回完整性检查失败；检查 EXC_RETURN、栈帧和返回上下文。"),
        new(19, "NOCP", "协处理器不可用", "执行了不可用或未启用的协处理器指令；检查 FPU 支持、使能状态和编译浮点选项。"),
        new(24, "UNALIGNED", "未对齐内存访问", "发生被内核禁止或由 UNALIGN_TRP 捕获的未对齐访问；检查指针和数据结构对齐。"),
        new(25, "DIVBYZERO", "整数除零", "启用除零陷阱时执行了整数除零；检查除数和 CCR.DIV_0_TRP 设置。")
    ];
}
