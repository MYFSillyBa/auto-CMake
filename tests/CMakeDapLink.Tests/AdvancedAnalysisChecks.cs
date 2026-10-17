using System.Reflection;
using System.Text;
using System.Text.Json;
using CMakeDapLink.Core;

internal static class AdvancedAnalysisChecks
{
    public static void Run(string root, Action<string, Action> check)
    {
        check("ELF ranking reads real function and variable sizes and excludes debug/undefined symbols", () =>
        {
            var path = Path.Combine(root, "analysis.elf"); WriteElf(path);
            using var result = Invoke("FirmwareAdvancedAnalyzer", "AnalyzeElf", path, 100, CancellationToken.None);
            var symbols = result.RootElement.GetProperty("Symbols").EnumerateArray().ToArray();
            Assert(symbols.Length == 2 && symbols[0].GetProperty("Name").GetString() == "buffer", "ranked symbols differ");
            Assert(symbols[0].GetProperty("Size").GetUInt64() == 64 && symbols[1].GetProperty("Kind").GetString() == "function", "symbol metadata differs");
        });
        check("ELF ranking accepts stripped firmware with a clear note", () =>
        {
            var path = Path.Combine(root, "stripped.elf"); WriteElf(path, stripped: true);
            using var result = Invoke("FirmwareAdvancedAnalyzer", "AnalyzeElf", path, 100, CancellationToken.None);
            Assert(result.RootElement.GetProperty("Symbols").GetArrayLength() == 0 && result.RootElement.GetProperty("Notes").GetArrayLength() > 0, "stripped status is missing");
        });
        check("GNU MAP ignores discarded/debug sections and does not sum child contributions twice", () =>
        {
            var path = Path.Combine(root, "analysis.map");
            File.WriteAllText(path, """
Discarded input sections
 .text.dead     0x08000000 0x400 dead.o
Memory Configuration
Name             Origin             Length             Attributes
FLASH            0x08000000         0x00000100         xr
RAM              0x20000000         0x00000100         xrw
*default*        0x00000000         0xffffffff
Linker script and memory map
.text            0x08000000         0x40
 .text.start     0x08000000         0x10 startup.o
 .text.function
                 0x08000010         0x30 libapp.a(main.o)
.data            0x20000000         0x10 load address 0x08000040
 .data.value     0x20000000         0x10 main.o
.debug_info      0x00000000         0x1000
 .debug_info     0x00000000         0x1000 main.o
OUTPUT(app.elf elf32-littlearm)
""");
            using var result = Invoke("FirmwareAdvancedAnalyzer", "AnalyzeMap", path, 100, CancellationToken.None);
            var sections = result.RootElement.GetProperty("MapSections").EnumerateArray().ToArray();
            Assert(sections.Length == 3 && sections.Any(item => item.GetProperty("ObjectFile").GetString() == "libapp.a(main.o)"), "map contributions differ");
            var regions = result.RootElement.GetProperty("Regions").EnumerateArray().ToArray();
            Assert(regions.Length == 2 && regions[0].GetProperty("Used").GetUInt64() == 64 && regions[1].GetProperty("Used").GetUInt64() == 16, "MAP region usage double counts input or load rows");
        });
        check("analysis limits are bounded and malformed input is rejected", () =>
        {
            var path = Path.Combine(root, "invalid.map"); File.WriteAllText(path, "not a GNU map file");
            Expect<InvalidDataException>(() => Invoke("FirmwareAdvancedAnalyzer", "AnalyzeMap", path, 100, CancellationToken.None));
            WriteElf(path);
            Expect<ArgumentOutOfRangeException>(() => Invoke("FirmwareAdvancedAnalyzer", "AnalyzeElf", path, 101, CancellationToken.None));
            File.WriteAllBytes(path, [0x7f, 0x45, 0x4c, 0x46]);
            Expect<InvalidDataException>(() => Invoke("FirmwareAdvancedAnalyzer", "AnalyzeElf", path, 100, CancellationToken.None));
        });
        check("HardFault fault addresses require their corresponding valid bits", () =>
        {
            using var invalid = Decode(new() { ["CFSR"] = 2, ["MMFAR"] = 0x20000004, ["BFAR"] = 0x40000000 });
            Assert(!Codes(invalid).Contains("MMFAR") && !Codes(invalid).Contains("BFAR"), "invalid address was trusted");
            using var valid = Decode(new() { ["CFSR"] = 0x8282, ["MMFAR"] = 0x20000004, ["BFAR"] = 0x40000000, ["HFSR"] = 0x40000000 });
            Assert(Codes(valid).Contains("MMFAR") && Codes(valid).Contains("BFAR") && Codes(valid).Contains("FORCED"), "valid fault address or escalation missing");
        });
        check("Cortex-M0 does not fabricate unavailable CFSR/HFSR diagnoses", () =>
        {
            using var result = Decode(new() { ["CFSR"] = uint.MaxValue, ["HFSR"] = uint.MaxValue }, "Cortex-M0+");
            Assert(!Codes(result).Contains("DIVBYZERO") && !Codes(result).Contains("FORCED") && result.RootElement.GetProperty("Notes").GetArrayLength() > 0, "M0 registers were decoded");
        });
        check("HardFault EXC_RETURN and imprecise faults retain uncertainty", () =>
        {
            using var result = Decode(new() { ["CFSR"] = 1u << 10, ["LR"] = 0xfffffffdu, ["PC"] = 0x08000042 });
            Assert(Codes(result).Contains("EXC_RETURN") && Codes(result).Contains("IMPRECISERR"), "EXC_RETURN or imprecise diagnosis missing");
            Assert(result.RootElement.GetProperty("Notes").EnumerateArray().Any(item => item.GetString()!.Contains("PC", StringComparison.Ordinal)), "stacked PC guidance missing");
        });
    }

    private static JsonDocument Decode(Dictionary<string, uint> registers, string? core = null) => Invoke("HardFaultDecoder", "Decode", registers, core);
    private static string[] Codes(JsonDocument result) => result.RootElement.GetProperty("Findings").EnumerateArray().Select(item => item.GetProperty("Code").GetString()!).ToArray();
    private static JsonDocument Invoke(string type, string method, params object?[] arguments)
    {
        var implementation = typeof(ProjectInspector).Assembly.GetType("CMakeDapLink.Core." + type);
        Assert(implementation != null, type + " implementation is missing");
        try { return JsonDocument.Parse(JsonSerializer.Serialize(implementation!.GetMethod(method, BindingFlags.Static | BindingFlags.Public)!.Invoke(null, arguments))); }
        catch (TargetInvocationException exception) when (exception.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw; }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void WriteElf(string path, bool stripped = false)
    {
        var names = Encoding.UTF8.GetBytes("\0.text\0.bss\0.symtab\0.strtab\0.shstrtab\0.debug_info\0");
        var strings = Encoding.UTF8.GetBytes("\0function\0buffer\0undefined\0debug\0");
        using var stream = File.Create(path); using var writer = new BinaryWriter(stream);
        writer.Write(new byte[] { 0x7f, 0x45, 0x4c, 0x46, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        writer.Write((ushort)2); writer.Write((ushort)40); writer.Write(1u); writer.Write(0x08000001u); writer.Write(0u); writer.Write(512u);
        writer.Write(0u); writer.Write((ushort)52); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)40); writer.Write((ushort)8); writer.Write((ushort)6);
        stream.Position = 80; writer.Write(new byte[48]); stream.Position = 128;
        Symbol(0, 0, 0, 0, 0); Symbol(1, 0x08000001, 48, 0x12, 1); Symbol(10, 0x20000000, 64, 0x11, 2);
        Symbol(17, 0, 100, 0x12, 0); Symbol(27, 0, 4096, 0x11, 7);
        stream.Position = 224; writer.Write(strings); stream.Position = 288; writer.Write(names);
        stream.Position = 512;
        Section(0, 0, 0, 0, 0, 0, 0, 0); Section(1, 1, 6, 0x08000000, 80, 48, 0, 0); Section(7, 8, 3, 0x20000000, 128, 64, 0, 0);
        Section(12, stripped ? 0u : 2u, 0, 0, 128, 80, 4, 16); Section(20, 3, 0, 0, 224, (uint)strings.Length, 0, 0);
        Section(0, 0, 0, 0, 0, 0, 0, 0); Section(28, 3, 0, 0, 288, (uint)names.Length, 0, 0); Section(38, 1, 0, 0, 80, 48, 0, 0);
        void Symbol(uint name, uint address, uint size, byte info, ushort section) { writer.Write(name); writer.Write(address); writer.Write(size); writer.Write(info); writer.Write((byte)0); writer.Write(section); }
        void Section(uint name, uint type, uint flags, uint address, uint offset, uint size, uint link, uint entrySize)
        { writer.Write(name); writer.Write(type); writer.Write(flags); writer.Write(address); writer.Write(offset); writer.Write(size); writer.Write(link); writer.Write(0u); writer.Write(1u); writer.Write(entrySize); }
    }
}
