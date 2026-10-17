using System.Globalization;
using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

internal static class FirmwareGnuMapReader
{
    private static readonly Regex RegionLine = new(@"^\s*(?<name>[^\s]+)\s+(?<address>0x[0-9a-fA-F]+)\s+(?<size>0x[0-9a-fA-F]+)(?:\s+\S+)?\s*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex SectionLine = new(@"^(?<indent>\s*)(?<name>\.[^\s]+|COMMON)\s+(?<address>0x[0-9a-fA-F]+)\s+(?<size>0x[0-9a-fA-F]+)(?:\s+(?<tail>.*))?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex NameLine = new(@"^(?<indent>\s*)(?<name>\.[^\s]+|COMMON)\s*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Continuation = new(@"^\s+(?<address>0x[0-9a-fA-F]+)\s+(?<size>0x[0-9a-fA-F]+)(?:\s+(?<tail>.*))?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static FirmwareAnalysisReport Read(string path, int top, CancellationToken cancellation)
    {
        var full = Path.GetFullPath(path);
        if (new FileInfo(full).Length > 32 * 1024 * 1024) throw new InvalidDataException("MAP 文件超过 32 MB 分析范围。");
        var regions = new List<(string Name, ulong Origin, ulong Length)>();
        var outputs = new List<FirmwareMemorySection>(); var contributions = new List<FirmwareMapSection>();
        var memory = false; var map = false; var recognized = false; var active = false; var lines = 0;
        (string Name, bool Input)? pending = null;
        foreach (var line in File.ReadLines(full))
        {
            if ((lines++ & 255) == 0) cancellation.ThrowIfCancellationRequested();
            if (lines > 500000 || line.Length > 16384) throw new InvalidDataException("MAP 行数或行长度超过分析范围。");
            if (line.Trim() == "Memory Configuration") { memory = true; continue; }
            if (line.Trim() == "Linker script and memory map") { memory = false; map = recognized = true; continue; }
            if (line.Trim() == "Cross Reference Table") break;
            if (memory)
            {
                var match = RegionLine.Match(line);
                if (match.Success && match.Groups["name"].Value != "*default*")
                {
                    var origin = Number(match, "address"); var size = Number(match, "size");
                    ValidateSpan(origin, size);
                    if (regions.Count >= 256) throw new InvalidDataException("MAP 内存区域数量超过分析范围。");
                    regions.Add((match.Groups["name"].Value, origin, size));
                }
                continue;
            }
            if (!map) continue; // Discarded sections and archive-selection lists are not memory usage.
            var row = SectionLine.Match(line);
            if (row.Success)
            {
                Add(row.Groups["name"].Value, row.Groups["indent"].Length != 0, row); pending = null; continue;
            }
            var name = NameLine.Match(line);
            if (name.Success) { pending = (name.Groups["name"].Value, name.Groups["indent"].Length != 0); continue; }
            if (pending is { } split && Continuation.Match(line) is { Success: true } continuation)
            { Add(split.Name, split.Input, continuation); pending = null; continue; }
            if (!string.IsNullOrWhiteSpace(line)) pending = null;
            if (line.StartsWith("/DISCARD/", StringComparison.Ordinal)) active = false;
        }
        if (!recognized || outputs.Count == 0) throw new InvalidDataException("未识别到 GNU MAP 中的有效分配段；不支持此 MAP 格式或文件没有已分配段。");
        // Parent output sections describe allocation; input contributions only explain ownership.
        var resultRegions = regions.Select(region =>
        {
            var assigned = outputs.Where(section => section.Address >= region.Origin && section.Size <= region.Length && section.Address - region.Origin <= region.Length - section.Size).ToArray();
            var used = Union(assigned);
            return new FirmwareMemoryRegion(region.Name, region.Origin, region.Length, used, region.Length - used,
                region.Length > 0 ? (double)used / region.Length * 100 : null, assigned);
        }).ToArray();
        var notes = new List<string> { "GNU MAP 区域用量按输出段的运行地址并集统计；输入目标文件贡献不会再次累计，调试段和 load address 行不计入。", "MAP 的初始化数据装载映像未单独计入区域用量；完整 Flash 静态占用请分析对应 ELF。" };
        if (regions.Count == 0) notes.Add("MAP 未提供可识别的 Memory Configuration；区域容量和剩余空间未知。");
        if (outputs.Any(section => !regions.Any(region => section.Address >= region.Origin && section.Size <= region.Length && section.Address - region.Origin <= region.Length - section.Size)))
            notes.Add("部分 MAP 输出段不属于已知内存区域；区域汇总不包含这些段。");
        if (contributions.Count == 0) notes.Add("MAP 没有可识别的目标文件贡献；仅列出输出段大小。");
        var listed = contributions.Count == 0 ? outputs.Select(section => new FirmwareMapSection(section.Name, Address(section.Address), section.Size, null)) : contributions;
        if (listed.Count() > top) notes.Add("仅显示指定数量的最大 MAP 贡献；区域用量仍根据全部输出段统计。");
        return new(null, full, [], resultRegions, listed.OrderByDescending(section => section.Size).ThenBy(section => section.Name, StringComparer.Ordinal).Take(top).ToArray(), notes);

        void Add(string name, bool input, Match row)
        {
            var address = Number(row, "address"); var size = Number(row, "size"); ValidateSpan(address, size);
            if (!input)
            {
                active = !Excluded(name); if (!active || size == 0) return;
                if (outputs.Count >= 20000) throw new InvalidDataException("MAP 输出段数量超过分析范围。");
                outputs.Add(new(name, address, size)); return;
            }
            if (!active || Excluded(name) || size == 0) return;
            var owner = row.Groups["tail"].Value.Trim();
            if (owner.Length == 0 || owner.StartsWith("load address", StringComparison.Ordinal) || !Regex.IsMatch(owner, @"\.(?:o|obj)(?:\)|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) return;
            if (contributions.Count >= 100000) throw new InvalidDataException("MAP 目标文件贡献数量超过分析范围。");
            contributions.Add(new(name, Address(address), size, owner));
        }
    }

    private static bool Excluded(string name) => name.StartsWith(".debug", StringComparison.Ordinal) || name.StartsWith(".zdebug", StringComparison.Ordinal) ||
        name.StartsWith(".stab", StringComparison.Ordinal) || name is ".comment" or ".ARM.attributes" or ".gnu.attributes" or ".symtab" or ".strtab" or ".shstrtab";
    private static ulong Number(Match row, string group) => ulong.TryParse(row.Groups[group].Value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
        ? value : throw new InvalidDataException("MAP 地址或大小无效。");
    private static string Address(ulong value) => "0x" + value.ToString(value > uint.MaxValue ? "X16" : "X8");
    private static void ValidateSpan(ulong address, ulong size) { if (size > ulong.MaxValue - address) throw new InvalidDataException("MAP 段地址或大小溢出。"); }
    private static ulong Union(IEnumerable<FirmwareMemorySection> sections)
    {
        ulong total = 0, end = 0;
        foreach (var section in sections.OrderBy(section => section.Address))
        {
            var nextEnd = section.Address + section.Size;
            if (nextEnd > end) total = checked(total + nextEnd - Math.Max(end, section.Address));
            end = Math.Max(end, nextEnd);
        }
        return total;
    }
}
