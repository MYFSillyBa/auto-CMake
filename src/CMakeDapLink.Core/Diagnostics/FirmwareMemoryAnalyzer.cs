namespace CMakeDapLink.Core;

public sealed record FirmwareMemorySection(string Name, ulong Address, ulong Size);
public sealed record FirmwareMemoryRegion(string Name, ulong Origin, ulong? Length, ulong Used, ulong? Remaining,
    double? Percent, IReadOnlyList<FirmwareMemorySection> Sections);
public sealed record MemoryAnalysisReport(string FirmwareElfPath, IReadOnlyList<FirmwareMemoryRegion> Regions,
    IReadOnlyList<FirmwareMemorySection> Sections, ulong? TotalFlash, ulong? TotalRam, IReadOnlyList<string> Notes);

/// <summary>Static allocated memory, including relocated initialization data; never runtime heap/stack consumption.</summary>
public static class FirmwareMemoryAnalyzer
{
    private sealed record Placement(FirmwareMemorySection Section, string? Region, bool LoadImage);

    public static MemoryAnalysisReport Analyze(string firmwareElfPath, IEnumerable<string> linkerScripts)
    {
        var firmware = Path.GetFullPath(firmwareElfPath);
        var sections = FirmwareElfReader.Read(firmware);
        var notes = new List<string>();
        var layout = FirmwareLinkerMemory.Read(linkerScripts, notes);
        var placements = new List<Placement>();
        foreach (var section in sections)
        {
            layout.Assignments.TryGetValue(section.Name, out var assignment);
            placements.Add(new(new(section.Name, section.Address, section.Size), assignment?.Runtime, false));
            if (section.LoadAddress is { } load && load != section.Address && section.Size > 0)
                placements.Add(new(new(section.Name + " [LMA]", load, section.Size), assignment?.Load, true));
        }
        var groups = layout.Regions.ToDictionary(region => region.Name, _ => new List<Placement>(), StringComparer.Ordinal);
        var unknown = new List<Placement>();
        foreach (var placement in placements)
        {
            var span = placement.Section;
            var named = layout.Regions.FirstOrDefault(region => region.Name == placement.Region &&
                span.Address >= region.Origin && (!region.Length.HasValue || Contains(region.Origin, region.Length.Value, span.Address, span.Size)));
            var candidates = layout.Regions.Where(region => region.Length.HasValue && Contains(region.Origin, region.Length.Value, span.Address, span.Size)).ToArray();
            var selected = named ?? (candidates.Length == 1 ? candidates[0] : null);
            if (selected == null) { if (span.Size > 0) unknown.Add(placement); } else groups[selected.Name].Add(placement);
        }
        var regions = new List<FirmwareMemoryRegion>();
        var flash = new List<FirmwareMemorySection>(); var ram = new List<FirmwareMemorySection>();
        var unknownKind = false;
        foreach (var region in layout.Regions)
        {
            var placed = groups[region.Name];
            var used = UnionSize(placed.Select(item => item.Section));
            regions.Add(new(region.Name, region.Origin, region.Length, used,
                region.Length is { } length && used <= length ? length - used : null,
                region.Length is > 0 ? (double)used / region.Length.Value * 100 : null,
                placed.Select(item => item.Section).OrderBy(item => item.Address).ToArray()));
            // Classification comes from the linked region's name/attributes and actual runtime/load placement.
            var kind = FirmwareLinkerMemory.Kind(region);
            if (kind == "Flash") flash.AddRange(placed.Select(item => item.Section));
            else if (kind == "RAM") ram.AddRange(placed.Select(item => item.Section));
            else if (used > 0) unknownKind = true;
        }
        if (unknown.Count > 0)
        {
            var actual = unknown.Select(item => item.Section).OrderBy(item => item.Address).ToArray();
            regions.Add(new("ELF", actual.Min(item => item.Address), null, UnionSize(actual), null, null, actual));
            if (actual.Any(item => item.Size > 0)) unknownKind = true;
            notes.Add("部分分配段无法唯一映射到链接脚本区域；容量、剩余空间和分类汇总显示为未知。");
        }
        var missingLoadImage = sections.Any(section => section.Size > 0 && section.FileBacked && section.LoadAddress == null);
        if (missingLoadImage)
            notes.Add("未发现独立的初始化数据装载映像；若 ELF 缺少对应程序头，Flash 初始化数据用量无法确认。");
        if (HasOverlap(placements.Select(item => item.Section)))
            notes.Add("分配段或装载映像存在重叠；用量按地址并集统计，同一字节不会重复累计。");
        notes.Add("仅统计 ELF 的静态分配及初始化装载映像，不包含调试信息；堆栈预留计入静态占用，不代表运行时峰值。");
        return new(firmware, regions, sections.Select(section => new FirmwareMemorySection(section.Name, section.Address, section.Size)).ToArray(),
            unknownKind || missingLoadImage ? null : UnionSize(flash), unknownKind ? null : UnionSize(ram), notes.Distinct().ToArray());
    }

    private static bool Contains(ulong origin, ulong length, ulong address, ulong size)
        => address >= origin && size <= length && address - origin <= length - size;

    private static bool HasOverlap(IEnumerable<FirmwareMemorySection> sections)
    {
        ulong end = 0; var seen = false;
        foreach (var section in sections.Where(section => section.Size > 0).OrderBy(section => section.Address))
        {
            if (seen && section.Address < end) return true;
            end = checked(section.Address + section.Size); seen = true;
        }
        return false;
    }

    private static ulong UnionSize(IEnumerable<FirmwareMemorySection> sections)
    {
        ulong total = 0, start = 0, end = 0; var seen = false;
        foreach (var section in sections.Where(section => section.Size > 0).OrderBy(section => section.Address))
        {
            var nextEnd = checked(section.Address + section.Size);
            if (!seen) { start = section.Address; end = nextEnd; seen = true; }
            else if (section.Address <= end) end = Math.Max(end, nextEnd);
            else { total = checked(total + (end - start)); start = section.Address; end = nextEnd; }
        }
        return seen ? checked(total + (end - start)) : 0;
    }
}
